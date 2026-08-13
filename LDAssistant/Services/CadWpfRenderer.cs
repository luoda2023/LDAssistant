using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadEntity = ACadSharp.Entities.Entity;
using Point = System.Windows.Point;

namespace LDAssistant.Services
{
    /// <summary>
    /// CAD 矢量渲染：直接遍历 ACadSharp 实体 → WPF Geometry / FormattedText，不经 SVG。
    /// 线条不采用 CAD 线宽（统一细线，缩放不失真、不重叠）；虚线线型按屏幕比例归一化。
    /// 文字支持 D:\ZCODE\LDAssistant\fonts 大字体目录（TTF/OTF 直接加载，SHX 映射为等义中文字体）。
    /// 必须在 STA 线程调用；返回的 DrawingImage 已 Freeze，可跨线程使用。
    /// </summary>
    public static class CadWpfRenderer
    {
        public sealed class Result
        {
            public DrawingImage Image;
            public double Width;
            public double Height;
            // 屏幕变换参数（模型坐标 → 屏幕：sx=(X-MinX)*Scale, sy=(MaxY-Y)*Scale），
            // 供探针/命中测试等外部定位用。
            public double Scale;
            public double MinX, MinY, MaxX, MaxY;
            public double ContentMinX, ContentMinY, ContentMaxX, ContentMaxY;  // 内容密集范围（剔除边缘空白/离群点）
            public double TypicalTextHeight;  // 常见文字字高（模型单位），供初始视图放大参考
            public double TypicalTextX, TypicalTextY;  // 文字密集区中心（插入点中位数），初始视图定位用
            public double InitZoom;     // 初始视图自动放大倍数（供调用方同步缩放状态，避免首次缩放跳变）
            public double InitPanX, InitPanY;  // 初始视图定位平移（模型坐标换算为 pan，供调用方同步）
            public List<double> TextXs, TextYs;  // 文字插入点坐标（按 X 排序，X/Y/Height 三元组配对），OCR 聚类用
            public List<double> TextHeights;  // 与 TextXs 对齐的每文字字高（模型单位），簇级 zoom 计算用
            // 视口渲染：适配比例 + 整图包围盒（窗口计算用）
            public double FitScale;
            public double FullMinX, FullMinY, FullMaxX, FullMaxY;
        }

        private const int MaxDepth = 12;
        private const bool DarkBg = true;   // CAD 预览区深色背景（#2A2A2E）
        // OCR 文字专用白色（textOnly 渲染）。必须 Freeze：该静态 Brush 在类加载线程
        // （UI 线程）创建，而 OCR 分块渲染在独立 STA 线程执行，未冻结的 DispatcherObject
        // 跨线程使用会抛异常 → RenderViewport 吞掉返回 null → OCR 无条带可识别。
        private static readonly SolidColorBrush s_ocrWhite = CreateOcrWhite();
        private static SolidColorBrush CreateOcrWhite()
        {
            var b = new SolidColorBrush(Colors.White);
            b.Freeze();
            return b;
        }

        // ── 渲染阶段计时（性能分析用，渲染完成后由调用方读取）──
        public static System.Collections.Generic.List<(string stage, long ms)> LastTimings;

        private static void Tm(System.Collections.Generic.List<(string, long)> list, string stage, System.Diagnostics.Stopwatch sw)
        {
            sw.Stop();
            list.Add((stage, sw.ElapsedMilliseconds));
            sw.Restart();
        }

        // ── 构建期数据结构 ──
        private sealed class StrokeBucket
        {
            public SolidColorBrush Brush;
            public DashStyle Dash;          // null = 实线
            public StreamGeometry Geo;
            public StreamGeometryContext Ctx;
        }

        private sealed class FillItem
        {
            public Geometry Geo;
            public SolidColorBrush Brush;
            public double Opacity;
        }

        private sealed class TextRunItem
        {
            public FormattedText Formatted;   // 西文字体段（null = SHX 字形段）
            public Geometry ShxGeo;           // SHX 字形几何（冻结，模型坐标，单线）
            public double Advance;            // 段宽（模型坐标）
            public double DrawX;              // FormattedText 段的绘制 x（模型坐标）
            public Typeface Face;             // 重建 FormattedText 用（OCR 白色覆盖）
            public double SizePx;             // 重建 FormattedText 用（像素字号）
        }

        private sealed class TextItem
        {
            public double X, Y, FontSizeModel, Rot;
            public SolidColorBrush Brush;
            public string Text;
            public FontFamily Family;       // null = 默认
            public string Anchor;
            // 模型构建时一次性生成（与缩放无关，组变换负责缩放），重烘焙直接复用
            public System.Windows.Media.FormattedText Formatted;
            public List<TextRunItem> Runs;  // 非 null = 分段渲染（SHX 大字体字形 + 西文字体段）
            public ShxFont BigShx;          // 大字体 SHX（中文）
            public ShxFont PrimaryShx;      // 主字体 SHX（西文）
            public double DX, DY;           // 绘制位置（模型坐标，含锚点偏移）
            public double BMinX, BMinY, BMaxX, BMaxY;  // 字形包围盒（模型坐标，未旋转；供视口裁剪）
        }

        private sealed class BuildCtx
        {
            public readonly Dictionary<string, StrokeBucket> Buckets = new();
            public readonly List<FillItem> Fills = new();
            public readonly List<TextItem> Texts = new();
            public readonly List<(double x, double y, SolidColorBrush brush)> Points = new();
            public double Scale;
            public double MinX, MinY, MaxX, MaxY;   // 过滤范围（包围盒外扩）
            public bool InBounds(double x, double y)
                => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
        }

        /// <summary>模型空间内容（缩放无关，供重烘焙复用）：几何/填充/文字均在模型坐标，
        /// 只有最终组装（组变换 + 线宽 + 点半径）随缩放变化。</summary>
        private sealed class ModelCtx
        {
            public Dictionary<string, StrokeBucket> Buckets;
            public List<FillItem> Fills;
            public List<TextItem> Texts;
            public List<(double x, double y, SolidColorBrush brush)> Points;
            public double MinX, MinY, MaxX, MaxY;
            public double ContentMinX, ContentMinY, ContentMaxX, ContentMaxY;  // 内容密集范围（剔除边缘空白/离群点）
            public double TypicalTextHeight;  // 常见文字字高（模型单位），供初始视图放大参考
            public double TypicalTextX, TypicalTextY;  // 文字密集区中心（滑动窗口最密处），初始视图定位用
            public List<double> TextXs, TextYs;         // 文字插入点坐标（排序后），滑动窗口定位用
            public List<double> TextYsPaired;           // 与 TextXs 配对的 Y（OCR 聚类用）
            public List<double> TextHeights;            // 与 TextXs 配对的字高（簇级 zoom 用）
            public List<(double x, double y)> SamplePts;  // 内容采样点（降采样，窗口内容验证用）
        }

        // 模型缓存：同一文件+页 重烘焙时直接复用（首次构建 ~1s，之后每次组装 ~0.1s）
        private static (string key, ModelCtx ctx) _modelCache;
        private static readonly object _modelCacheLock = new();

        /// <summary>释放模型缓存（切换文件/退出时调用，及时归还大图纸几何内存）。</summary>
        public static void ClearModelCache()
        {
            lock (_modelCacheLock) { _modelCache = default; }
        }

        /// <summary>
        /// 渲染 CAD 页面为 WPF 矢量图（按视口尺寸缩放，线宽=1 屏幕像素细线）。失败返回 null。
        /// zoom：相对视口适配的缩放倍数（用于缩放重烘焙），烘焙总比例 = 适配比例 × zoom，
        /// 线宽仍为 1 屏幕像素 —— 这样缩放时线条永远细线，不会变粗重叠。
        /// </summary>
        public static Result Render(IList<ACadEntity> entities, double viewW, double viewH, double zoom = 1.0, string cacheKey = null)
        {
            try
            {
                if (entities == null || entities.Count == 0) return null;
                if (zoom <= 0) zoom = 1.0;
                var tm = new System.Collections.Generic.List<(string, long)>();
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // 模型缓存：同一文件+页 的几何/填充/文字与缩放无关，重烘焙直接复用
                ModelCtx model = null;
                if (cacheKey != null)
                {
                    lock (_modelCacheLock)
                    {
                        if (_modelCache.key == cacheKey) model = _modelCache.ctx;
                    }
                }
                if (model == null)
                {
                    model = BuildModel(entities, viewW, viewH, tm, sw);
                    if (model == null) return null;
                    if (cacheKey != null)
                    {
                        lock (_modelCacheLock) { _modelCache = (cacheKey, model); }
                    }
                }
                else
                {
                    tm.Add(("model-cache-hit", 0));
                }

                double minX = model.MinX, minY = model.MinY, maxX = model.MaxX, maxY = model.MaxY;

                // 缩放：适配比例 × zoom（线宽=1 屏幕像素，模型坐标单位 = 1/scale）
                const double MAX_DIM = 16000.0;
                double fitScale = Math.Min((viewW - 40) / (maxX - minX), (viewH - 40) / (maxY - minY));
                if (fitScale <= 0) fitScale = Math.Min(MAX_DIM / (maxX - minX), MAX_DIM / (maxY - minY));
                fitScale = Math.Min(fitScale, 40.0);
                double scale = fitScale * zoom;
                // 防御：总画布边长不超过 4000 万设备像素，防止极端缩放下坐标溢出 milcore 渲染边界
                double rangeMax = Math.Max(maxX - minX, maxY - minY);
                if (rangeMax > 0 && scale * rangeMax > 40_000_000.0)
                    scale = 40_000_000.0 / rangeMax;

                // 组装（Y 翻转：CAD 上正 → 屏幕下正；线宽/点半径随缩放，保证永远细线）
                var group = new DrawingGroup();
                group.Transform = new MatrixTransform(new System.Windows.Media.Matrix(scale, 0, 0, -scale, -minX * scale, maxY * scale));
                double penW = 1.0 / scale;
                using (var dc = group.Open())
                {
                    foreach (var f in model.Fills)
                    {
                        dc.PushOpacity(f.Opacity);
                        dc.DrawGeometry(f.Brush, null, f.Geo);
                        dc.Pop();
                    }
                    Tm(tm, "asm-fills", sw);
                    foreach (var kv in model.Buckets)
                    {
                        var b = kv.Value;
                        dc.DrawGeometry(null, new Pen(b.Brush, penW) { DashStyle = b.Dash }, b.Geo);
                    }
                    Tm(tm, "asm-strokes", sw);
                    if (model.Points != null)
                    {
                        double r = 3.0 / scale;   // 约 3 屏幕像素
                        foreach (var pt in model.Points)
                        {
                            var eg = new EllipseGeometry(new Point(pt.x, pt.y), r, r);
                            eg.Freeze();
                            dc.DrawGeometry(pt.brush, null, eg);
                        }
                    }
                    Tm(tm, "asm-points", sw);
                    foreach (var t in model.Texts) DrawText(dc, t, penW);
                    Tm(tm, "asm-texts", sw);
                }

                var image = new DrawingImage(group);
                image.Freeze();
                sw.Stop();
                tm.Add(("total", sw.ElapsedMilliseconds));
                LastTimings = tm;
                return new Result { Image = image, Width = (maxX - minX) * scale, Height = (maxY - minY) * scale, Scale = scale, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// 视口渲染（AutoCAD 式）：画布恒等于视口尺寸，只烘焙当前可见区域并做视口裁剪。
        /// 平移/缩放只改变可见窗口（zoom/panX/panY），画布大小不变 ——
        /// 因此位图缓存永远可用、拖动永远顺滑；缩放重烘焙只重绘可见实体，快且不卡。
        /// 线宽仍为 1 屏幕像素细线；文字/填充/描边全部视口裁剪（含余量防贴边截断）。
        /// </summary>
        public static Result RenderViewport(IList<ACadEntity> entities, double viewW, double viewH,
            double zoom, double panX, double panY, string cacheKey = null, bool allowInitFit = true,
            bool textOnly = false)
        {
            try
            {
                if (entities == null || entities.Count == 0) return null;
                if (zoom <= 0) zoom = 1.0;
                if (viewW < 100) viewW = 100;
                if (viewH < 100) viewH = 100;
                var tm = new System.Collections.Generic.List<(string, long)>();
                var sw = System.Diagnostics.Stopwatch.StartNew();

                ModelCtx model = null;
                if (cacheKey != null)
                {
                    lock (_modelCacheLock)
                    {
                        if (_modelCache.key == cacheKey) model = _modelCache.ctx;
                    }
                }
                if (model == null)
                {
                    model = BuildModel(entities, viewW, viewH, tm, sw);
                    if (model == null) return null;
                    if (cacheKey != null)
                    {
                        lock (_modelCacheLock) { _modelCache = (cacheKey, model); }
                    }
                }

                double fullMinX = model.MinX, fullMinY = model.MinY, fullMaxX = model.MaxX, fullMaxY = model.MaxY;
                // 适配基础统一用内容密集范围（剔除边缘空白/离群垃圾）：打开与缩放/平移共用同一
                // 比例基准，避免视图跳变；初始视图（zoom=1 且 pan=0）再按典型字高自动放大并
                // 定位到文字密集区，放大/定位结果经 InitZoom/InitPan 同步给调用方。
                bool initFit = allowInitFit && zoom == 1.0 && panX == 0 && panY == 0;
                double baseMinX = model.ContentMinX;
                double baseMinY = model.ContentMinY;
                double baseMaxX = model.ContentMaxX;
                double baseMaxY = model.ContentMaxY;
                double fitScale = Math.Min((viewW - 40) / (baseMaxX - baseMinX), (viewH - 40) / (baseMaxY - baseMinY));
                if (fitScale <= 0 || double.IsInfinity(fitScale))
                    fitScale = Math.Min(16000.0 / (baseMaxX - baseMinX), 16000.0 / (baseMaxY - baseMinY));
                fitScale = Math.Min(fitScale, 40.0);
                if (fitScale <= 0) fitScale = 1e-6;
                // 初始字高适配：内容 fit 后常见文字 < 30px 时自动放大（上限 100 倍，与滚轮一致），
                // 打开即清晰可读；超大图纸按典型字高放大后只显示局部，用户可再缩小看全貌。
                double initZoom = 1.0;
                if (initFit && model.TypicalTextHeight > 0)
                {
                    double onScreen = model.TypicalTextHeight * fitScale;
                    if (onScreen > 0 && onScreen < 30.0)
                        initZoom = Math.Min(30.0 / onScreen, 100.0);
                }
                double scale = fitScale * zoom * initZoom;
                if (scale <= 0) scale = 1e-6;
                // 可见窗口（模型坐标）：画布恒为视口，窗口随 缩放×平移 移动。
                // 初始视图放大后窗口中心定位到文字密集区（中位数），避免放大到内容几何中心
                // 却落在无文字的空白区（超大图纸尤为明显）。
                double winCX = (baseMinX + baseMaxX) / 2.0;
                double winCY = (baseMinY + baseMaxY) / 2.0;
                if (initFit && initZoom > 1.0 && model.TextXs != null && model.TextXs.Count > 0)
                {
                    // 定位循环：滑动窗口找文字最密集处 → 三候选（原始 best/clamp/内容中心）
                    // 用内容采样点+文字锚点验证；若所有候选窗口都无内容（超大稀疏图纸），
                    // 逐步降低放大倍数直到窗口内有内容或缩回整图 —— 保证初始视图永不空白。
                    while (true)
                    {
                        double winW = viewW / scale, winH = viewH / scale;
                        // 滑动窗口：找包含文字最多的窗口中心（视口宽/高已知）
                        double bestX = winCX; int bestN = -1;
                        var xs = model.TextXs; int j = 0;
                        for (int i = 0; i < xs.Count; i++)
                        {
                            while (j < xs.Count && xs[j] <= xs[i] + winW) j++;
                            if (j - i > bestN) { bestN = j - i; bestX = (xs[i] + xs[j - 1]) / 2.0; }
                        }
                        double bestY = winCY; bestN = -1; j = 0;
                        var ys = model.TextYs;
                        for (int i = 0; i < ys.Count; i++)
                        {
                            while (j < ys.Count && ys[j] <= ys[i] + winH) j++;
                            if (j - i > bestN) { bestN = j - i; bestY = (ys[i] + ys[j - 1]) / 2.0; }
                        }
                        // 候选窗口：原始 bestX/bestY（可能落在内容范围外，如离群文字块）、
                        // clamp 到内容范围内、内容中心。
                        double x0 = bestX - winW / 2, x1 = bestX + winW / 2;
                        double y0 = bestY - winH / 2, y1 = bestY + winH / 2;
                        double cxC = Math.Max(baseMinX, Math.Min(baseMaxX, bestX));
                        double cyC = Math.Max(baseMinY, Math.Min(baseMaxY, bestY));
                        double x2 = cxC - winW / 2, x3 = cxC + winW / 2;
                        double y2 = cyC - winH / 2, y3 = cyC + winH / 2;
                        double cxM = (baseMinX + baseMaxX) / 2.0;
                        double cyM = (baseMinY + baseMaxY) / 2.0;
                        double x4 = cxM - winW / 2, x5 = cxM + winW / 2;
                        double y4 = cyM - winH / 2, y5 = cyM + winH / 2;
                        int n1 = 0, n2 = 0, n3 = 0;
                        // 内容采样点：保证窗口落在有内容处
                        if (model.SamplePts != null)
                        {
                            foreach (var sp in model.SamplePts)
                            {
                                if (sp.x >= x0 && sp.x <= x1 && sp.y >= y0 && sp.y <= y1) n1++;
                                if (sp.x >= x2 && sp.x <= x3 && sp.y >= y2 && sp.y <= y3) n2++;
                                if (sp.x >= x4 && sp.x <= x5 && sp.y >= y4 && sp.y <= y5) n3++;
                            }
                        }
                        // 文字锚点：OCR 主要看文字，权重高（×8）
                        foreach (var t in model.Texts)
                        {
                            if (t.X >= x0 && t.X <= x1 && t.Y >= y0 && t.Y <= y1) n1 += 8;
                            if (t.X >= x2 && t.X <= x3 && t.Y >= y2 && t.Y <= y3) n2 += 8;
                            if (t.X >= x4 && t.X <= x5 && t.Y >= y4 && t.Y <= y5) n3 += 8;
                        }
                        if (n1 + n2 + n3 > 0 || initZoom <= 1.0)
                        {
                            if (initZoom <= 1.0) { winCX = cxM; winCY = cyM; }
                            else if (n1 >= n2 && n1 >= n3) { winCX = bestX; winCY = bestY; }
                            else if (n2 >= n3) { winCX = cxC; winCY = cyC; }
                            else { winCX = cxM; winCY = cyM; }
                            break;
                        }
                        // 降级：放大倍数减半，窗口变大后重试定位
                        initZoom = Math.Max(1.0, initZoom / 2.0);
                        scale = fitScale * zoom * initZoom;
                        if (scale <= 0) scale = 1e-6;
                    }
                }
                double initPanX = 0, initPanY = 0;
                if (initFit && initZoom > 1.0 && model.TextXs != null && model.TextXs.Count > 0)
                {
                    // 把定位中心换算为 pan（与下方统一公式一致），供调用方同步缩放/平移状态
                    initPanX = scale * (baseMinX - winCX) + viewW / 2.0;
                    initPanY = scale * (winCY - baseMaxY) + viewH / 2.0;
                }
                double effPanX = initFit ? initPanX : panX;
                double effPanY = initFit ? initPanY : panY;
                double winMinX = baseMinX - effPanX / scale;
                double winMaxY = baseMaxY + effPanY / scale;
                double winMaxX = winMinX + viewW / scale;
                double winMinY = winMaxY - viewH / scale;

                // 裁剪外扩余量（约 12 屏幕像素，防贴边线条/文字被裁掉）
                double m = Math.Max(12.0 / scale, 1e-9);
                double cMinX = winMinX - m, cMaxX = winMaxX + m;
                double cMinY = winMinY - m, cMaxY = winMaxY + m;

                var group = new DrawingGroup();
                group.Transform = new MatrixTransform(new System.Windows.Media.Matrix(scale, 0, 0, -scale, -winMinX * scale, winMaxY * scale));
                double penW = 1.0 / scale;
                using (var dc = group.Open())
                {
                    if (!textOnly)
                    {
                        // 填充/剖切面：几何包围盒视口裁剪
                        foreach (var f in model.Fills)
                        {
                            var b = f.Geo.Bounds;
                            if (b.IsEmpty || b.Right < cMinX || b.Left > cMaxX || b.Bottom < cMinY || b.Top > cMaxY) continue;
                            dc.PushOpacity(f.Opacity);
                            dc.DrawGeometry(f.Brush, null, f.Geo);
                            dc.Pop();
                        }
                        Tm(tm, "vp-fills", sw);
                        foreach (var kv in model.Buckets)
                        {
                            var b = kv.Value;
                            var bnd = b.Geo.Bounds;
                            if (bnd.IsEmpty || bnd.Right < cMinX || bnd.Left > cMaxX || bnd.Bottom < cMinY || bnd.Top > cMaxY) continue;
                            dc.DrawGeometry(null, new Pen(b.Brush, penW) { DashStyle = b.Dash }, b.Geo);
                        }
                        Tm(tm, "vp-strokes", sw);
                        if (model.Points != null)
                        {
                            double r = 3.0 / scale;
                            foreach (var pt in model.Points)
                            {
                                if (pt.x < cMinX || pt.x > cMaxX || pt.y < cMinY || pt.y > cMaxY) continue;
                                var eg = new EllipseGeometry(new Point(pt.x, pt.y), r, r);
                                eg.Freeze();
                                dc.DrawGeometry(pt.brush, null, eg);
                            }
                        }
                        Tm(tm, "vp-points", sw);
                    }
                    if (model.Texts != null)
                    {
                        foreach (var t in model.Texts)
                        {
                            // 用字形真实包围盒裁剪：中心 + 旋转包络半径（覆盖任意旋转/多行/锚点偏移）
                            double w = t.BMaxX - t.BMinX, h = t.BMaxY - t.BMinY;
                            if (w <= 0 || h <= 0) continue;
                            double cx = (t.BMinX + t.BMaxX) / 2.0;
                            double cy = (t.BMinY + t.BMaxY) / 2.0;
                            double r = 0.5 * Math.Sqrt(w * w + h * h) + m;
                            // DrawText 内层镜像：字形在模型空间以 y=t.Y 为轴翻转后再被旋转
                            cy = 2 * t.Y - cy;
                            if (cx + r < cMinX || cx - r > cMaxX || cy + r < cMinY || cy - r > cMaxY) continue;
                            DrawText(dc, t, penW, textOnly ? s_ocrWhite : null);
                        }
                    }
                    Tm(tm, "vp-texts", sw);
                }

                var image = new DrawingImage(group);
                image.Freeze();
                sw.Stop();
                tm.Add(("total", sw.ElapsedMilliseconds));
                LastTimings = tm;

                return new Result
                {
                    Image = image,
                    Width = viewW,
                    Height = viewH,
                    Scale = scale,
                    MinX = winMinX, MinY = winMinY, MaxX = winMaxX, MaxY = winMaxY,
                    ContentMinX = model.ContentMinX, ContentMinY = model.ContentMinY,
                    ContentMaxX = model.ContentMaxX, ContentMaxY = model.ContentMaxY,
                    TypicalTextHeight = model.TypicalTextHeight,
                    TypicalTextX = model.TypicalTextX, TypicalTextY = model.TypicalTextY,
                    InitZoom = initZoom, InitPanX = initPanX, InitPanY = initPanY,
                    TextXs = model.TextXs, TextYs = model.TextYsPaired,
                    TextHeights = model.TextHeights,
                    FitScale = fitScale,
                    FullMinX = fullMinX, FullMinY = fullMinY, FullMaxX = fullMaxX, FullMaxY = fullMaxY,
                };
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>构建模型空间内容（与缩放无关）：包围盒 + 填充/描边/文字几何，全部冻结。</summary>
        private static ModelCtx BuildModel(IList<ACadEntity> entities, double viewW, double viewH,
            System.Collections.Generic.List<(string, long)> tm, System.Diagnostics.Stopwatch sw)
        {
            // 1. 包围盒：遍历采样全部几何点
            var pts = new List<(double x, double y)>();
            Walk(entities, pts, Identity(), 0);
            if (pts.Count == 0) return null;
            Tm(tm, "bounds", sw);
            double minX, minY, maxX, maxY;
            ComputeBounds(pts, out minX, out minY, out maxX, out maxY);
            // 内容采样点（降采样 ≤ 20000）：初始视图窗口验证用，避免窗口落在空白区
            List<(double x, double y)> samplePts = pts;
            if (pts.Count > 20000)
            {
                int stride = (pts.Count + 19999) / 20000;
                samplePts = new List<(double, double)>(20000);
                for (int k = 0; k < pts.Count; k += stride) samplePts.Add(pts[k]);
            }
            if (maxX - minX < 1e-6) { minX -= 1; maxX += 1; }
            if (maxY - minY < 1e-6) { minY -= 1; maxY += 1; }
            double pad = Math.Max(maxX - minX, maxY - minY) * 0.02 + 1;

            // 内容密集范围：按点坐标 1%-99% 分位剔除边缘空白/离群垃圾实体；
            // 至少保留全范围 50%，避免多图拼板图纸被过度裁剪。
            double cMinX = minX, cMinY = minY, cMaxX = maxX, cMaxY = maxY;
            try
            {
                int n = pts.Count;
                if (n > 64)
                {
                    var xs = new double[n]; var ys = new double[n];
                    for (int k = 0; k < n; k++) { xs[k] = pts[k].x; ys[k] = pts[k].y; }
                    Array.Sort(xs); Array.Sort(ys);
                    cMinX = xs[n / 100]; cMaxX = xs[n - 1 - n / 100];
                    cMinY = ys[n / 100]; cMaxY = ys[n - 1 - n / 100];
                    double fullW = maxX - minX, fullH = maxY - minY;
                    if (cMaxX - cMinX < fullW * 0.5) { cMinX = minX + fullW * 0.25; cMaxX = maxX - fullW * 0.25; }
                    if (cMaxY - cMinY < fullH * 0.5) { cMinY = minY + fullH * 0.25; cMaxY = maxY - fullH * 0.25; }
                }
            }
            catch { cMinX = minX; cMinY = minY; cMaxX = maxX; cMaxY = maxY; }
            minX -= pad; minY -= pad; maxX += pad; maxY += pad;

            // 过滤范围：包围盒外扩 3 倍（剔除坐标失控的垃圾实体，防 milcore 渲染毒化）
            var bc = new BuildCtx
            {
                Scale = 1.0,   // 点实体半径在组装时按当前缩放重算，此处占位
                MinX = minX - 3 * (maxX - minX),
                MinY = minY - 3 * (maxY - minY),
                MaxX = maxX + 3 * (maxX - minX),
                MaxY = maxY + 3 * (maxY - minY),
            };

            // 单次遍历：填充/描边/文字分别入集合，组装顺序天然正确（填充→描边→文字）
            DrawList(entities, bc, Identity(), null, 0);
            Tm(tm, "pass-walk", sw);

            // 关闭并冻结所有桶几何
            foreach (var kv in bc.Buckets)
            {
                kv.Value.Ctx.Close();
                kv.Value.Geo.Freeze();
            }
            foreach (var f in bc.Fills)
            {
                if (f.Geo is StreamGeometry sg) sg.Freeze();
            }

            // 文字一次性成形：字高防御上限（约 300 屏幕像素，按参考适配比例折算到模型单位），
            // 锚点偏移也在此算好 —— 之后重烘焙只做 DrawText，不再重建字形。
            const double MAX_DIM2 = 16000.0;
            double fit0 = Math.Min((viewW - 40) / (maxX - minX), (viewH - 40) / (maxY - minY));
            if (fit0 <= 0) fit0 = Math.Min(MAX_DIM2 / (maxX - minX), MAX_DIM2 / (maxY - minY));
            fit0 = Math.Min(fit0, 40.0);
            double textCap = 300.0 / Math.Max(fit0, 1e-6);
            foreach (var t in bc.Texts)
            {
                try
                {
                    double fs = Math.Min(Math.Max(t.FontSizeModel, 1e-6), textCap);
                    var tf = CadTypeface(t.Family);

                    // SHX 矢量字体分段：中文/大字体内码 → SHX 字形几何，其余 → FormattedText
                    if (t.BigShx != null || t.PrimaryShx != null)
                    {
                        BuildShxRuns(t, fs, tf);
                    }
                    else
                    {
                        t.Formatted = new FormattedText(
                            t.Text,
                            CultureInfo.GetCultureInfo("zh-CN"),
                            FlowDirection.LeftToRight,
                            tf,
                            fs,
                            t.Brush,
                            1.0);
                        double tx = t.X;
                        if (t.Anchor == "middle") tx = t.X - t.Formatted.WidthIncludingTrailingWhitespace / 2.0;
                        else if (t.Anchor == "end") tx = t.X - t.Formatted.WidthIncludingTrailingWhitespace;
                        t.DX = tx;
                        t.DY = t.Y - t.Formatted.Baseline;
                    }
                }
                catch (Exception ex)
                {
                    // 之前这里是空 catch，导致 SHX 字形在冻结对象上设置 Transform 时抛 InvalidOperationException
                    // 被静默吞掉后 t.Runs/t.Formatted 都为 null，文字完全不画。改为 Debug 输出便于排查。
                    System.Diagnostics.Debug.WriteLine($"[BuildModel text fail] '{t.Text}': {ex.GetType().Name}: {ex.Message}");
                    t.Formatted = null;
                    t.Runs = null;
                }
            }

            // 文字字形包围盒（用于视口裁剪）：直接取已生成几何的真实范围，
            // 避免用“锚点+宽度”估算导致旋转/多行/锚点偏移的文字在窗口边缘被误裁丢失。
            foreach (var t in bc.Texts)
            {
                Rect box = Rect.Empty;
                if (t.Runs != null)
                {
                    foreach (var run in t.Runs)
                    {
                        if (run.ShxGeo != null && !run.ShxGeo.Bounds.IsEmpty)
                        {
                            var sbnd = run.ShxGeo.Bounds;
                            box.Union(new Rect(sbnd.X, sbnd.Y, sbnd.Width * CadWidthFactor, sbnd.Height));
                        }
                        else if (run.Formatted != null)
                            box.Union(new Rect(run.DrawX, t.DY,
                                run.Formatted.WidthIncludingTrailingWhitespace * CadWidthFactor, run.Formatted.Height));
                    }
                }
                else if (t.Formatted != null)
                {
                    box.Union(new Rect(t.DX, t.DY,
                        t.Formatted.WidthIncludingTrailingWhitespace * CadWidthFactor, t.Formatted.Height));
                }
                if (box.IsEmpty)
                {
                    // 兜底：锚点附近一个字号范围
                    double fs = Math.Max(t.FontSizeModel, 1e-6);
                    t.BMinX = t.X - fs; t.BMinY = t.Y - fs;
                    t.BMaxX = t.X + fs * 8; t.BMaxY = t.Y + fs;
                }
                else
                {
                    t.BMinX = box.Left; t.BMinY = box.Top;
                    t.BMaxX = box.Right; t.BMaxY = box.Bottom;
                }
            }
            Tm(tm, "text-format", sw);

            // 常见文字字高（模型单位）：取字高分布众数桶，用于初始视图自动放大
            double typicalH = 0;
            try
            {
                var heights = new List<double>();
                foreach (var t2 in bc.Texts) if (t2.FontSizeModel > 0) heights.Add(t2.FontSizeModel);
                if (heights.Count > 0)
                {
                    heights.Sort();
                    double med = heights[heights.Count / 2];
                    // 众数近似：中位数邻域内最密集的字高
                    double bestH = med; int bestCnt = 0;
                    for (int k = 0; k < heights.Count; k++)
                    {
                        double h0 = heights[k];
                        int cnt = 0;
                        for (int m = k; m < heights.Count && heights[m] <= h0 * 1.25; m++) cnt++;
                        if (cnt > bestCnt) { bestCnt = cnt; bestH = h0; }
                    }
                    typicalH = bestH;
                }
            }
            catch { typicalH = 0; }

            // 文字插入点坐标：三元组 (X,Y,字高) 按 X 排序保持配对（OCR 聚类需 X/Y 对应同一文字）
            var textXs = new List<double>(); var textYs = new List<double>(); var textHeights = new List<double>();
            foreach (var t2 in bc.Texts)
            {
                textXs.Add(t2.X); textYs.Add(t2.Y);
                textHeights.Add(t2.FontSizeModel > 0 ? t2.FontSizeModel : 1.0);
            }
            {
                var orderX = Enumerable.Range(0, textXs.Count).OrderBy(i => textXs[i]).ToArray();
                textXs = orderX.Select(i => textXs[i]).ToList();
                textYs = orderX.Select(i => textYs[i]).ToList();
                textHeights = orderX.Select(i => textHeights[i]).ToList();
            }
            // Y 独立排序（渲染器 Y 滑动窗口只需 Y 值；不参与配对）
            var textYsSorted = new List<double>(textYs);
            textYsSorted.Sort();
            textXs.Sort();
            double typicalX = textXs.Count > 0 ? textXs[textXs.Count / 2] : 0;
            double typicalY = textYs.Count > 0 ? textYs[textYs.Count / 2] : 0;

            return new ModelCtx
            {
                Buckets = bc.Buckets,
                Fills = bc.Fills,
                Texts = bc.Texts,
                Points = bc.Points,
                ContentMinX = cMinX, ContentMinY = cMinY, ContentMaxX = cMaxX, ContentMaxY = cMaxY,
                TypicalTextHeight = typicalH,
                TypicalTextX = typicalX, TypicalTextY = typicalY,
                TextXs = textXs, TextYs = textYsSorted,
                TextYsPaired = textYs, TextHeights = textHeights,
                SamplePts = samplePts,
                MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
            };
        }

        /// <summary>按字符分段：大字体内码（中文）用 SHX 字形，其余用西文字体，锚点按总宽对齐。</summary>
        private static void BuildShxRuns(TextItem t, double fs, Typeface tf)
        {
            var runs = new List<TextRunItem>();
            double x = 0;
            string text = t.Text ?? "";

            // 第一遍：预解析每个字符的形状（有墨迹才算可用），避免后面反复解析与死循环
            var shapes = new Dictionary<int, ShxShape>();
            for (int k = 0; k < text.Length; k++)
            {
                char c = text[k];
                if (shapes.ContainsKey(c)) continue;
                ShxShape s = null;
                if (t.BigShx != null) s = t.BigShx.GetLayoutCharShapeUnicode(c, fs);
                if ((s == null || !s.HasInk) && t.PrimaryShx != null)
                    s = t.PrimaryShx.GetLayoutCharShapeUnicode(c, fs);
                shapes[c] = (s != null && s.HasInk) ? s : null;
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                var shape = shapes.TryGetValue(c, out var sh) ? sh : null;
                if (shape != null)
                {
                    var geo = GetShxGeo(t, shape, c, fs);
                    double adv = Math.Max(shape.LastPoint.X, fs * 0.5);
                    runs.Add(new TextRunItem { ShxGeo = geo, Advance = adv });
                    x += adv;
                    i++;
                    continue;
                }

                // 累积连续的非 SHX 字符为 FormattedText 段
                int j = i;
                var sb = new System.Text.StringBuilder();
                while (j < text.Length)
                {
                    char c2 = text[j];
                    if (shapes.TryGetValue(c2, out var s2) && s2 != null) break;
                    sb.Append(c2);
                    j++;
                }
                var ft = new FormattedText(
                    sb.ToString(),
                    CultureInfo.GetCultureInfo("zh-CN"),
                    FlowDirection.LeftToRight,
                    tf,
                    fs,
                    t.Brush,
                    1.0);
                double w = ft.WidthIncludingTrailingWhitespace;
                runs.Add(new TextRunItem { Formatted = ft, Advance = w, Face = tf, SizePx = fs });
                x += w;
                i = j;
            }

            if (runs.Count == 0) { t.Formatted = null; return; }
            if (runs.Count == 1 && runs[0].Formatted != null)
            {
                // 整串无 SHX 字符：退化回单 FormattedText（保持旧渲染一致性）
                t.Formatted = runs[0].Formatted;
                double tx = t.X;
                if (t.Anchor == "middle") tx = t.X - runs[0].Advance / 2.0;
                else if (t.Anchor == "end") tx = t.X - runs[0].Advance;
                t.DX = tx;
                t.DY = t.Y - runs[0].Formatted.Baseline;
                return;
            }

            double total = x;
            double dx0 = t.X;
            if (t.Anchor == "middle") dx0 = t.X - total / 2.0;
            else if (t.Anchor == "end") dx0 = t.X - total;

            double cur = dx0;
            foreach (var run in runs)
            {
                if (run.ShxGeo != null)
                {
                    var tr = new MatrixTransform(new System.Windows.Media.Matrix(1, 0, 0, -1, cur, t.Y));
                    tr.Freeze();
                    run.ShxGeo.Transform = tr;
                    run.ShxGeo.Freeze();
                }
                else
                {
                    run.DrawX = cur;
                }
                cur += run.Advance;
            }
            t.Runs = runs;
            t.DX = dx0;
            t.DY = t.Y - (runs.FirstOrDefault(r => r.Formatted != null)?.Formatted.Baseline ?? 0);
            t.Formatted = null;
        }

        /// <summary>SHX 字形折线 → StreamGeometry（模型坐标，单线描边）。</summary>
        private static Geometry BuildShxGeometry(ShxShape shape)
        {
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                foreach (var pl in shape.Polylines)
                {
                    if (pl == null || pl.Count < 2) continue;
                    ctx.BeginFigure(new Point(pl[0].X, pl[0].Y), false, false);
                    for (int k = 1; k < pl.Count; k++)
                        ctx.LineTo(new Point(pl[k].X, pl[k].Y), true, false);
                }
            }
            return sg;
        }

        /// <summary>SHX 基础字形几何缓存：同字符同字号首次构建后全局复用（text-format 阶段的主要开销）。
        /// 每 run 用 <see cref="Freezable.Clone"/> 取得可变副本再设位移/镜像 Transform ——
        /// 冻结的共享几何本身不能写 Transform，克隆副本开销远小于重建 StreamGeometry。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ShxFont big, ShxFont pri, char ch, int sizeQ), Geometry> ShxBaseCache = new();

        private static Geometry GetShxGeo(TextItem t, ShxShape shape, char c, double fs)
        {
            int sizeQ = (int)Math.Round(fs * 16.0);   // 1/16 字高精度，兼顾命中率与字形尺寸误差
            var key = (t.BigShx, t.PrimaryShx, c, sizeQ);
            if (!ShxBaseCache.TryGetValue(key, out var baseGeo))
            {
                baseGeo = BuildShxGeometry(shape);
                baseGeo.Freeze();
                ShxBaseCache[key] = baseGeo;
            }
            return baseGeo.Clone();   // 可变副本，由 BuildShxRuns 设置 Transform 后 Freeze
        }

        // ═══════════════════════ 实体遍历 ═══════════════════════

        private static void DrawList(IEnumerable<ACadEntity> entities, BuildCtx bc, Matrix m, string parentColor, int depth)
        {
            if (entities == null || depth > MaxDepth) return;
            foreach (var ent in entities)
            {
                if (ent == null) continue;
                try { DrawEntity(ent, bc, m, parentColor, depth); }
                catch { }
            }
        }

        private static void DrawEntity(ACadEntity ent, BuildCtx bc, Matrix m, string parentColor, int depth)
        {
            switch (ent)
            {
                case Insert ins:
                {
                    var block = ins.Block;
                    if (block?.Entities == null) break;
                    double sx = ins.XScale == 0 ? 1 : ins.XScale;
                    double sy = ins.YScale == 0 ? 1 : ins.YScale;
                    var cm = Compose(m, BlockMatrix(ins.InsertPoint.X, ins.InsertPoint.Y, sx, sy, ins.Rotation));
                    string insColor = parentColor;
                    try { if (!ins.Color.IsByBlock) insColor = ResolveColorHex(ins, parentColor); } catch { }
                    DrawList(block.Entities, bc, cm, insColor, depth + 1);
                    try { if (ins.Attributes != null) DrawList(ins.Attributes.Cast<ACadEntity>(), bc, cm, insColor, depth + 1); } catch { }
                    break;
                }
                case Dimension dim:
                {
                    BlockRecord blk = null;
                    try { blk = dim.Block; } catch { }
                    if (blk?.Entities != null && blk.Entities.Count > 0)
                    {
                        string dimColor = ResolveColorHex(dim, parentColor);
                        DrawList(blk.Entities, bc, m, dimColor, depth + 1);
                    }
                    break;
                }
                case Hatch h:
                    DrawHatch(bc, m, h, parentColor);
                    break;
                case Solid sd:
                {
                    string c = ResolveColorHex(ent, parentColor);
                    var p = new[]
                    {
                        T(m, sd.FirstCorner.X, sd.FirstCorner.Y), T(m, sd.SecondCorner.X, sd.SecondCorner.Y),
                        T(m, sd.FourthCorner.X, sd.FourthCorner.Y), T(m, sd.ThirdCorner.X, sd.ThirdCorner.Y),
                    };
                    AddFill(bc, c, p, 1.0);
                    break;
                }
                case Face3D f:
                {
                    string c = ResolveColorHex(ent, parentColor);
                    var p = new[]
                    {
                        T(m, f.FirstCorner.X, f.FirstCorner.Y), T(m, f.SecondCorner.X, f.SecondCorner.Y),
                        T(m, f.ThirdCorner.X, f.ThirdCorner.Y), T(m, f.FourthCorner.X, f.FourthCorner.Y),
                    };
                    AddFill(bc, c, p, 1.0);
                    break;
                }
                case Ray ray:
                {
                    var s = T(m, ray.StartPoint.X, ray.StartPoint.Y);
                    AddSeg(bc, ent, parentColor, m, s, T(m, s.x + ray.Direction.X * 1e4, s.y + ray.Direction.Y * 1e4));
                    break;
                }
                case XLine xl:
                {
                    var s = T(m, xl.FirstPoint.X, xl.FirstPoint.Y);
                    AddSeg(bc, ent, parentColor, m,
                        T(m, s.x + xl.Direction.X * 1e4, s.y + xl.Direction.Y * 1e4),
                        T(m, s.x - xl.Direction.X * 1e4, s.y - xl.Direction.Y * 1e4));
                    break;
                }
                case Line ln:
                    AddSeg(bc, ent, parentColor, m, T(m, ln.StartPoint.X, ln.StartPoint.Y), T(m, ln.EndPoint.X, ln.EndPoint.Y));
                    break;
                case LwPolyline pl:
                    AddPoly(bc, ent, parentColor, m, pl.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)), pl.IsClosed);
                    break;
                case Polyline2D p2:
                    AddPoly(bc, ent, parentColor, m, p2.Vertices.Select(v => (v.Location.X, v.Location.Y, 0.0)), p2.IsClosed);
                    break;
                case Polyline3D p3:
                    AddPoly(bc, ent, parentColor, m, p3.Vertices.Select(v => (v.Location.X, v.Location.Y, 0.0)), p3.IsClosed);
                    break;
                case Spline sp:
                {
                    var list = (sp.FitPoints != null && sp.FitPoints.Count >= 2) ? sp.FitPoints : sp.ControlPoints;
                    if (list != null && list.Count >= 2)
                        AddPoly(bc, ent, parentColor, m, list.Select(p => (p.X, p.Y, 0.0)), sp.IsClosed);
                    break;
                }
                case Leader ld:
                {
                    if (ld.Vertices != null && ld.Vertices.Count >= 2)
                        AddPoly(bc, ent, parentColor, m, ld.Vertices.Select(v => (v.X, v.Y, 0.0)), false);
                    break;
                }
                case Arc a:
                    AddSegArc(bc, ent, parentColor, m, a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle);
                    break;
                case Circle c:
                    AddSegCircle(bc, ent, parentColor, m, c.Center.X, c.Center.Y, c.Radius);
                    break;
                case Ellipse e:
                    AddSegEllipse(bc, ent, parentColor, m, e);
                    break;
                case ACadSharp.Entities.Point ptE:
                {
                    var p = T(m, ptE.Location.X, ptE.Location.Y);
                    if (!bc.InBounds(p.x, p.y)) break;
                    string c = ResolveColorHex(ent, parentColor);
                    var brush = ParseColor(c);
                    if (brush == null) break;
                    // 点实体：半径随缩放（组装时按 3 屏幕像素重算）
                    bc.Points.Add((p.x, p.y, brush));
                    break;
                }
                case MText mt:
                {
                    string txt = CleanMText(mt.Value);
                    if (string.IsNullOrWhiteSpace(txt)) break;
                    txt = WrapMText(txt, mt.RectangleWidth, mt.Height > 0 ? mt.Height : 2.5);
                    var p = T(m, mt.InsertPoint.X, mt.InsertPoint.Y);
                    if (!bc.InBounds(p.x, p.y)) break;
                    double h = mt.Height > 0 ? mt.Height : 2.5;
                    double fontSize = h * MatrixScale(m);
                    double rotDeg = mt.Rotation * 180 / Math.PI;

                    // 多行段落拆分：MText 的换行 \P 已由 CleanMText 转成 \n。
                    // 此前整段文本作为单个 TextItem，SHX 逐字符渲染时 \n 不产生换行，
                    // 导致多行挤成一行、文字拉得极长。这里按 \n 拆行，每行一个 TextItem，
                    // 行方向从上到下（模型坐标 Y 递减）。旋转的多行不做拆分（边缘场景，保持原状）。
                    var lines = txt.Split('\n');
                    if (lines.Length > 1 && Math.Abs(rotDeg) < 0.05)
                    {
                        double lineSpacing = fontSize * 1.5;   // 行距约 1.5 倍字高
                        double firstLineY = p.y - fontSize;    // 第一行基线在插入点下方约一个字高
                        for (int li = 0; li < lines.Length; li++)
                        {
                            string line = lines[li];
                            if (line.Length == 0) continue;
                            double ly = firstLineY - li * lineSpacing;
                            AddText(bc, (p.x, ly), fontSize, 0, ent, line, "start");
                        }
                    }
                    else
                    {
                        // 旋转角直接取 CAD 角度（正）；最终组变换的 Y 翻转会自动修正方向。
                        // （之前取负导致 90° 旋转文字在屏幕上倒转/镜像）
                        AddText(bc, p, fontSize, rotDeg, ent, txt, "start");
                    }
                    break;
                }
                case TextEntity tx:
                {
                    if (tx is AttributeDefinition) break;
                    string txt = CleanMText(tx.Value);
                    if (string.IsNullOrWhiteSpace(txt)) break;
                    double bx = tx.InsertPoint.X, by = tx.InsertPoint.Y;
                    string anchor = "start";
                    try
                    {
                        if (tx.HorizontalAlignment != TextHorizontalAlignment.Left && (tx.AlignmentPoint.X != 0 || tx.AlignmentPoint.Y != 0))
                        { bx = tx.AlignmentPoint.X; by = tx.AlignmentPoint.Y; }
                        if (tx.HorizontalAlignment == TextHorizontalAlignment.Right) anchor = "end";
                        else if (tx.HorizontalAlignment == TextHorizontalAlignment.Center) anchor = "middle";
                    }
                    catch { }
                    var p = T(m, bx, by);
                    if (!bc.InBounds(p.x, p.y)) break;
                    double h = tx.Height > 0 ? tx.Height : 2.5;
                    AddText(bc, p, h * MatrixScale(m), tx.Rotation * 180 / Math.PI, ent, txt, anchor);
                    break;
                }
            }
        }

        // ── 几何输出 ──

        private static StrokeBucket GetBucket(BuildCtx bc, ACadEntity ent, string parentColor)
        {
            string color = ResolveColorHex(ent, parentColor);
            var dash = GetDash(ent);
            string dk = dash == null ? "" : string.Join(",", dash.Dashes.Select(d => d.ToString("F2", CultureInfo.InvariantCulture)));
            string key = color + "|" + dk;
            if (!bc.Buckets.TryGetValue(key, out var b))
            {
                b = new StrokeBucket { Brush = ParseColor(color), Dash = dash, Geo = new StreamGeometry() };
                b.Ctx = b.Geo.Open();
                bc.Buckets[key] = b;
            }
            return b;
        }

        private static void AddSeg(BuildCtx bc, ACadEntity ent, string parentColor, Matrix m, (double x, double y) a, (double x, double y) b)
        {
            if (!bc.InBounds(a.x, a.y) && !bc.InBounds(b.x, b.y)) return;
            var bucket = GetBucket(bc, ent, parentColor);
            if (bucket.Brush == null) return;
            bucket.Ctx.BeginFigure(new Point(a.x, a.y), false, false);
            bucket.Ctx.LineTo(new Point(b.x, b.y), true, false);
        }

        private static void AddPoly(BuildCtx bc, ACadEntity ent, string parentColor, Matrix m, IEnumerable<(double x, double y, double bulge)> verts, bool closed)
        {
            var bucket = GetBucket(bc, ent, parentColor);
            if (bucket.Brush == null) return;
            var ctx = bucket.Ctx;
            bool first = true;
            (double x, double y) firstPt = (0, 0);
            (double x, double y) prevPt = (0, 0);
            double prevBulge = 0;
            bool prevValid = false;
            foreach (var v in verts)
            {
                var cur = T(m, v.x, v.y);
                if (first)
                {
                    ctx.BeginFigure(new Point(cur.x, cur.y), false, false);
                    firstPt = cur;
                    first = false;
                }
                else if (Math.Abs(prevBulge) > 1e-4)
                {
                    var ctrl = BulgeControl(prevPt, cur, prevBulge);
                    ctx.QuadraticBezierTo(new Point(ctrl.x, ctrl.y), new Point(cur.x, cur.y), true, false);
                }
                else ctx.LineTo(new Point(cur.x, cur.y), true, false);
                prevPt = cur;
                prevBulge = v.bulge;
                prevValid = true;
            }
            if (closed && prevValid)
            {
                if (Math.Abs(prevBulge) > 1e-4)
                {
                    var ctrl = BulgeControl(prevPt, firstPt, prevBulge);
                    ctx.QuadraticBezierTo(new Point(ctrl.x, ctrl.y), new Point(firstPt.x, firstPt.y), true, false);
                }
                else ctx.LineTo(new Point(firstPt.x, firstPt.y), true, false);
            }
        }

        private static void AddSegCircle(BuildCtx bc, ACadEntity ent, string parentColor, Matrix m, double cx, double cy, double r)
        {
            const int seg = 64;
            var pts = new List<(double x, double y, double bulge)>();
            for (int i = 0; i <= seg; i++)
            {
                double t = i * 2 * Math.PI / seg;
                pts.Add((cx + r * Math.Cos(t), cy + r * Math.Sin(t), 0.0));
            }
            AddPoly(bc, ent, parentColor, m, pts, true);
        }

        private static void AddSegArc(BuildCtx bc, ACadEntity ent, string parentColor, Matrix m, double cx, double cy, double r, double a0, double a1)
        {
            double sweep = a1 - a0;
            if (sweep <= 0) sweep += 2 * Math.PI;
            if (sweep >= 2 * Math.PI - 0.001) { AddSegCircle(bc, ent, parentColor, m, cx, cy, r); return; }
            int seg = Math.Max(8, Math.Min(64, (int)(sweep / (Math.PI / 24))));
            var pts = new List<(double x, double y, double bulge)>();
            for (int i = 0; i <= seg; i++)
            {
                double t = a0 + sweep * i / seg;
                pts.Add((cx + r * Math.Cos(t), cy + r * Math.Sin(t), 0.0));
            }
            AddPoly(bc, ent, parentColor, m, pts, false);
        }

        private static void AddSegEllipse(BuildCtx bc, ACadEntity ent, string parentColor, Matrix m, Ellipse e)
        {
            double rx = e.MajorAxis, ry = e.MajorAxis * e.RadiusRatio, rot = e.Rotation;
            const int seg = 96;
            var pts = new List<(double x, double y, double bulge)>();
            for (int i = 0; i <= seg; i++)
            {
                double t = i * 2 * Math.PI / seg;
                double ex = e.Center.X + rx * Math.Cos(t) * Math.Cos(rot) - ry * Math.Sin(t) * Math.Sin(rot);
                double ey = e.Center.Y + rx * Math.Cos(t) * Math.Sin(rot) + ry * Math.Sin(t) * Math.Cos(rot);
                pts.Add((ex, ey, 0.0));
            }
            AddPoly(bc, ent, parentColor, m, pts, true);
        }

        private static (double x, double y) BulgeControl((double x, double y) p0, (double x, double y) p1, double bulge)
        {
            double dx = p1.x - p0.x, dy = p1.y - p0.y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-6) return p1;
            double sag = bulge * chord / 2;
            double mx = (p0.x + p1.x) / 2, my = (p0.y + p1.y) / 2;
            double nx = -dy, ny = dx;
            double nl = Math.Sqrt(nx * nx + ny * ny);
            if (nl < 1e-6) return p1;
            nx /= nl; ny /= nl;
            return (mx - nx * sag, my - ny * sag);
        }

        private static void AddFill(BuildCtx bc, string colorHex, (double x, double y)[] pts, double opacity)
        {
            var brush = ParseColor(colorHex);
            if (brush == null) return;
            bool any = pts.Any(p => bc.InBounds(p.x, p.y));
            if (!any) return;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(pts[0].x, pts[0].y), true, true);
                var seg = new List<Point>();
                for (int i = 1; i < pts.Length; i++) seg.Add(new Point(pts[i].x, pts[i].y));
                ctx.PolyLineTo(seg, true, false);
            }
            bc.Fills.Add(new FillItem { Geo = geo, Brush = brush, Opacity = opacity });
        }

        /// <summary>Hatch：填充（非实心 35% 透明度）+ 边界描边。</summary>
        private static void DrawHatch(BuildCtx bc, Matrix m, Hatch h, string parentColor)
        {
            if (h.Paths == null) return;
            string color = ResolveColorHex(h, parentColor);
            bool solid = false;
            try { solid = h.IsSolid || (h.Pattern != null && string.Equals(h.Pattern.Name, "SOLID", StringComparison.OrdinalIgnoreCase)); } catch { }

            // 一次收集所有边界边（按边界路径分组，保持洞/岛镂空正确）；
            // 优先用已解析的 Entities，否则对 Edges 调 ToEntity()（仅此一次，填充与描边共用）。
            var pathEdges = new List<List<ACadEntity>>();
            try
            {
                foreach (var bp in h.Paths)
                {
                    var edges = new List<ACadEntity>();
                    if (bp.Entities != null && bp.Entities.Count > 0) edges.AddRange(bp.Entities);
                    else if (bp.Edges != null)
                    {
                        foreach (var e in bp.Edges)
                        {
                            try { var te = e.ToEntity(); if (te != null) edges.Add(te); } catch { }
                        }
                    }
                    if (edges.Count > 0) pathEdges.Add(edges);
                }
            }
            catch { }
            if (pathEdges.Count == 0) return;

            // ── 填充几何 ──
            var fillGeo = new StreamGeometry();
            bool any = false;
            using (var fctx = fillGeo.Open())
            {
                foreach (var edges in pathEdges)
                {
                    bool started = false;
                    var startPt = new Point();
                    foreach (var e in edges)
                    {
                        switch (e)
                        {
                            case Line l:
                            {
                                var a = T(m, l.StartPoint.X, l.StartPoint.Y);
                                var b2 = T(m, l.EndPoint.X, l.EndPoint.Y);
                                if (!bc.InBounds(a.x, a.y) && !bc.InBounds(b2.x, b2.y)) continue;
                                if (!started) { startPt = new Point(a.x, a.y); fctx.BeginFigure(startPt, true, false); started = true; any = true; }
                                fctx.LineTo(new Point(b2.x, b2.y), true, false);
                                break;
                            }
                            case LwPolyline pl:
                            {
                                foreach (var v in pl.Vertices)
                                {
                                    var p = T(m, v.Location.X, v.Location.Y);
                                    if (!bc.InBounds(p.x, p.y)) continue;
                                    if (!started) { startPt = new Point(p.x, p.y); fctx.BeginFigure(startPt, true, false); started = true; any = true; }
                                    else fctx.LineTo(new Point(p.x, p.y), true, false);
                                }
                                break;
                            }
                            case Polyline2D p2:
                            {
                                foreach (var v in p2.Vertices)
                                {
                                    var p = T(m, v.Location.X, v.Location.Y);
                                    if (!bc.InBounds(p.x, p.y)) continue;
                                    if (!started) { startPt = new Point(p.x, p.y); fctx.BeginFigure(startPt, true, false); started = true; any = true; }
                                    else fctx.LineTo(new Point(p.x, p.y), true, false);
                                }
                                break;
                            }
                            case Arc arc:
                            {
                                double sweep = arc.EndAngle - arc.StartAngle;
                                if (sweep < 0) sweep += 2 * Math.PI;
                                int seg = Math.Max(8, (int)(sweep / (Math.PI / 24)));
                                for (int i = 0; i <= seg; i++)
                                {
                                    double t = arc.StartAngle + sweep * i / seg;
                                    var p = T(m, arc.Center.X + arc.Radius * Math.Cos(t), arc.Center.Y + arc.Radius * Math.Sin(t));
                                    if (!bc.InBounds(p.x, p.y)) continue;
                                    if (!started) { startPt = new Point(p.x, p.y); fctx.BeginFigure(startPt, true, false); started = true; any = true; }
                                    else fctx.LineTo(new Point(p.x, p.y), true, false);
                                }
                                break;
                            }
                            case Circle cc:
                            {
                                const int seg2 = 32;
                                for (int i = 0; i <= seg2; i++)
                                {
                                    double t = i * 2 * Math.PI / seg2;
                                    var p = T(m, cc.Center.X + cc.Radius * Math.Cos(t), cc.Center.Y + cc.Radius * Math.Sin(t));
                                    if (!bc.InBounds(p.x, p.y)) continue;
                                    if (!started) { startPt = new Point(p.x, p.y); fctx.BeginFigure(startPt, true, false); started = true; any = true; }
                                    else fctx.LineTo(new Point(p.x, p.y), true, false);
                                }
                                break;
                            }
                        }
                    }
                    if (started) fctx.LineTo(startPt, true, false);
                }
            }
            if (!any) return;
            var brush = ParseColor(color);
            if (brush != null)
                bc.Fills.Add(new FillItem { Geo = fillGeo, Brush = brush, Opacity = solid ? 1.0 : 0.65 });

            // ── 边界描边（复用已收集的边）──
            var b = GetBucket(bc, h, parentColor);
            if (b.Brush != null && b.Ctx != null)
            {
                foreach (var edges in pathEdges)
                {
                    bool started = false;
                    bool st0 = false;
                    foreach (var e in edges)
                    {
                        switch (e)
                        {
                            case Line l:
                            {
                                var a = T(m, l.StartPoint.X, l.StartPoint.Y);
                                var bb = T(m, l.EndPoint.X, l.EndPoint.Y);
                                if (!bc.InBounds(a.x, a.y) && !bc.InBounds(bb.x, bb.y)) continue;
                                if (!started) { b.Ctx.BeginFigure(new Point(a.x, a.y), false, false); started = true; }
                                b.Ctx.LineTo(new Point(bb.x, bb.y), true, false);
                                break;
                            }
                            case LwPolyline pl:
                            {
                                bool st = false;
                                foreach (var v in pl.Vertices)
                                {
                                    var p = T(m, v.Location.X, v.Location.Y);
                                    if (!bc.InBounds(p.x, p.y)) continue;
                                    if (!st) { b.Ctx.BeginFigure(new Point(p.x, p.y), false, false); st = true; started = true; }
                                    else b.Ctx.LineTo(new Point(p.x, p.y), true, false);
                                }
                                break;
                            }
                            case Polyline2D p2:
                            {
                                bool st2 = false;
                                foreach (var v in p2.Vertices)
                                {
                                    var p = T(m, v.Location.X, v.Location.Y);
                                    if (!bc.InBounds(p.x, p.y)) continue;
                                    if (!st2) { b.Ctx.BeginFigure(new Point(p.x, p.y), false, false); st2 = true; started = true; }
                                    else b.Ctx.LineTo(new Point(p.x, p.y), true, false);
                                }
                                break;
                            }
                            case Arc arc:
                            {
                                double sweep = arc.EndAngle - arc.StartAngle;
                                if (sweep < 0) sweep += 2 * Math.PI;
                                int seg = Math.Max(8, (int)(sweep / (Math.PI / 24)));
                                for (int i = 0; i <= seg; i++)
                                {
                                    double t = arc.StartAngle + sweep * i / seg;
                                    var p = T(m, arc.Center.X + arc.Radius * Math.Cos(t), arc.Center.Y + arc.Radius * Math.Sin(t));
                                    if (!bc.InBounds(p.x, p.y)) continue;
                                    if (!st0) { b.Ctx.BeginFigure(new Point(p.x, p.y), false, false); st0 = true; started = true; }
                                    else b.Ctx.LineTo(new Point(p.x, p.y), true, false);
                                }
                                break;
                            }
                            case Circle cc:
                            {
                                const int seg2 = 32;
                                for (int i = 0; i <= seg2; i++)
                                {
                                    double t = i * 2 * Math.PI / seg2;
                                    var p = T(m, cc.Center.X + cc.Radius * Math.Cos(t), cc.Center.Y + cc.Radius * Math.Sin(t));
                                    if (!bc.InBounds(p.x, p.y)) continue;
                                    if (!st0) { b.Ctx.BeginFigure(new Point(p.x, p.y), false, false); st0 = true; started = true; }
                                    else b.Ctx.LineTo(new Point(p.x, p.y), true, false);
                                }
                                break;
                            }
                        }
                    }
                }
            }
        }

        // ── 文字 ──

        // ═══ 全局字体覆盖（CAD 字体对话框应用后生效） ═══
        // 实体自身文字样式的字体优先；无法解析时用这里的全局选择兜底，
        // 这样用户在大字体里选定的 SHX（如 hztxt）能真正应用到图纸上。
        public static string OverrideShxFont;      // 全局 SHX 主字体（西文）
        public static string OverrideBigShxFont;   // 全局 SHX 大字体（中文）
        public static bool OverrideUseBigFont;     // 是否启用大字体覆盖
        public static double CadWidthFactor = 1.0; // 全局字宽（宽度因子），由 CAD字宽设置 应用

        private static void AddText(BuildCtx bc, (double x, double y) p, double fontSizeModel, double rotDeg, ACadEntity ent, string text, string anchor)
        {
            var brush = ParseColor(ResolveColorHex(ent, null));
            if (brush == null) return;
            var family = ResolveFontFamily(ent);
            var (bigShx, primaryShx) = ResolveShxFonts(ent);
            bc.Texts.Add(new TextItem
            {
                X = p.x, Y = p.y,
                FontSizeModel = Math.Max(fontSizeModel, 1e-6),
                Rot = rotDeg, Brush = brush, Text = text,
                Family = family, Anchor = anchor,
                BigShx = bigShx, PrimaryShx = primaryShx,
            });
        }

        /// <summary>解析文本样式的大字体/主字体 SHX 文件（fonts 目录内有则用矢量字形渲染）。
        /// 规则：图纸里原用宋体/黑体的保留（走 WPF 系统字体渲染）；其它字体——包括 style 未解析到
        /// （TextStyle 为 null，很常见，ACadSharp 有时拿不到默认 Standard 样式）——统一替换为
        /// Tssdeng.shx（主字体/西文）+ HZTXT.shx（大字体/中文），以保证 AutoCAD 大字体显示效果。</summary>
        private static (ShxFont big, ShxFont primary) ResolveShxFonts(ACadEntity ent)
        {
            try
            {
                ACadSharp.Tables.TextStyle style = null;
                if (ent is MText mt) style = mt.Style;
                else if (ent is TextEntity tx) style = tx.Style;

                string primaryFont = null, bigFont = null;
                if (style != null)
                {
                    try
                    {
                        var styleType = style.GetType();
                        var pProp = styleType.GetProperty("Filename") ?? styleType.GetProperty("FontFile") ?? styleType.GetProperty("PrimaryFont");
                        if (pProp != null) primaryFont = pProp.GetValue(style)?.ToString();
                        var bProp = styleType.GetProperty("BigFontFilename") ?? styleType.GetProperty("BigFont");
                        if (bProp != null) bigFont = bProp.GetValue(style)?.ToString();
                    }
                    catch { }
                }

                // 宋体/黑体保留：返回 null → 走 WPF 系统字体（ResolveFontFamily 映射 SimSun/SimHei）。
                // 样式名本身为宋体/黑体/仿宋也要保留（真实图纸常见样式名"宋体"但 Filename 为空的情况）。
                if ((style != null && IsSongHeiFont(style.Name)) || IsSongHeiFont(primaryFont) || IsSongHeiFont(bigFont))
                    return (null, null);

                // 其它字体（含 style == null 的默认情况）：全部替换为 Tssdeng + HZTXT（覆盖优先，缺文件时再兜底固定名）
                ShxFont big = null, primary = null;
                if (OverrideUseBigFont && !string.IsNullOrEmpty(OverrideBigShxFont))
                    big = Fonts.Shx(OverrideBigShxFont);
                if (big == null) big = Fonts.Shx("hztxt");
                if (!string.IsNullOrEmpty(OverrideShxFont))
                    primary = Fonts.Shx(OverrideShxFont);
                if (primary == null) primary = Fonts.Shx("tssdeng");
                return (big, primary);
            }
            catch { return (null, null); }
        }

        /// <summary>判断 CAD 字体名是否为宋体/黑体/仿宋（含 simsun/simhei/simfang/宋体/黑体/仿宋 及 TTF 后缀变体）。
        /// 这些字体保留系统字体渲染，不替换为大字体。</summary>
        private static bool IsSongHeiFont(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return false;
            string name = fontName.Trim();
            string lower = name.ToLowerInvariant();
            if (lower.Contains("simsun") || lower.Contains("simhei") || lower.Contains("simfang")
                || lower.Contains("fangsong") || lower.Contains("fsgb2312") || lower.Contains("仿宋")
                || lower.Contains("fangsong_gb2312") || lower.Contains("仿宋_gb2312")) return true;
            if (name.Contains("宋体") || name.Contains("黑体") || name.Contains("仿宋")) return true;
            return false;
        }

        private static void DrawText(DrawingContext dc, TextItem t, double penW, SolidColorBrush overrideBrush = null)
        {
            // FormattedText/字形几何在模型构建时已生成（含字高防御上限与锚点偏移），缩放由组变换完成。
            // 组变换 (scale,0,0,-scale) 会把文字 Y 镜像（上下颠倒）——必须绕基线再镜像一次抵消。
            // 文字不强制最小屏幕尺寸：缩小视图下若把文字强制放大到固定像素高度，行间距
            // 小于放大后字高时文字会互相重叠、盖住图形（用户反馈"缩小后图形文字重叠"）。
            // 可读性由打开时的智能初始视图（initZoom 放大到典型字高 30px）与滚轮缩放保证。
            dc.PushTransform(new ScaleTransform(1, -1, t.X, t.Y));
            double wf = CadWidthFactor;
            if (Math.Abs(wf - 1.0) > 0.001)
                dc.PushTransform(new ScaleTransform(wf, 1, t.X, t.Y));
            if (Math.Abs(t.Rot) > 0.05)
            {
                // 镜像空间里旋转方向翻转：CAD 逆时针角度要取负才在屏幕上逆时针
                dc.PushTransform(new RotateTransform(-t.Rot, t.X, t.Y));
                DrawTextRuns(dc, t, penW, overrideBrush);
                dc.Pop();
            }
            else
            {
                DrawTextRuns(dc, t, penW, overrideBrush);
            }
            if (Math.Abs(wf - 1.0) > 0.001) dc.Pop();
            dc.Pop();
        }

        private static void DrawTextRuns(DrawingContext dc, TextItem t, double penW, SolidColorBrush overrideBrush = null)
        {
            if (t.Runs != null)
            {
                foreach (var run in t.Runs)
                {
                    if (run.ShxGeo != null)
                    {
                        // SHX 单线字形：细线描边（线宽与几何线一致，1 屏幕像素）。
                        // 几何已自带「Y 镜像 + 平移到基线」变换（几何级，先于 dc 变换栈应用），
                        // 直接绘制即可 —— 不能用 dc.PushTransform：嵌套矩阵与外层
                        // Scale(1,-1) 的复合顺序不等价，二次镜像抵消 → 文字偏移/翻转不显示。
                        dc.DrawGeometry(null, new Pen(overrideBrush ?? t.Brush, penW), run.ShxGeo);
                    }
                    else if (run.Formatted != null)
                    {
                        if (overrideBrush != null)
                        {
                            // OCR 专用：重新构建白色 FormattedText（原 Brush 不可变）
                            var ft = new FormattedText(run.Formatted.Text,
                                CultureInfo.GetCultureInfo("zh-CN"),
                                FlowDirection.LeftToRight,
                                run.Face ?? new Typeface(t.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                                run.SizePx > 0 ? run.SizePx : 12.0,
                                overrideBrush, 1.0);
                            dc.DrawText(ft, new Point(run.DrawX, t.DY));
                        }
                        else
                        {
                            dc.DrawText(run.Formatted, new Point(run.DrawX, t.DY));
                        }
                    }
                }
                return;
            }
            if (t.Formatted == null) return;
            dc.DrawText(t.Formatted, new Point(t.DX, t.DY));
        }

        // ═══════════════════════ 包围盒遍历 ═══════════════════════

        private static void Walk(IList<ACadEntity> entities, List<(double x, double y)> pts, Matrix m, int depth)
        {
            if (entities == null || depth > MaxDepth) return;
            foreach (var ent in entities)
            {
                if (ent == null) continue;
                try { WalkEntity(ent, pts, m, depth); }
                catch { }
            }
        }

        private static void WalkEntity(ACadEntity ent, List<(double x, double y)> pts, Matrix m, int depth)
        {
            switch (ent)
            {
                case Line l: Add(pts, m, l.StartPoint.X, l.StartPoint.Y); Add(pts, m, l.EndPoint.X, l.EndPoint.Y); break;
                case Arc a: SampleArc(pts, m, a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle); break;
                case Circle c: Add(pts, m, c.Center.X + c.Radius, c.Center.Y); Add(pts, m, c.Center.X - c.Radius, c.Center.Y); Add(pts, m, c.Center.X, c.Center.Y + c.Radius); Add(pts, m, c.Center.X, c.Center.Y - c.Radius); break;
                case Ellipse e:
                {
                    double rx = e.MajorAxis, ry = e.MajorAxis * e.RadiusRatio, rot = e.Rotation;
                    for (int i = 0; i < 16; i++)
                    {
                        double t = i * 2 * Math.PI / 16;
                        Add(pts, m, e.Center.X + rx * Math.Cos(t) * Math.Cos(rot) - ry * Math.Sin(t) * Math.Sin(rot),
                                   e.Center.Y + rx * Math.Cos(t) * Math.Sin(rot) + ry * Math.Sin(t) * Math.Cos(rot));
                    }
                    break;
                }
                case LwPolyline pl: foreach (var v in pl.Vertices) Add(pts, m, v.Location.X, v.Location.Y); break;
                case Polyline2D p2: foreach (var v in p2.Vertices) Add(pts, m, v.Location.X, v.Location.Y); break;
                case Polyline3D p3: foreach (var v in p3.Vertices) Add(pts, m, v.Location.X, v.Location.Y); break;
                case Spline s:
                {
                    var list = (s.FitPoints != null && s.FitPoints.Count >= 2) ? s.FitPoints : s.ControlPoints;
                    if (list != null) foreach (var p in list) Add(pts, m, p.X, p.Y);
                    break;
                }
                case Solid sd: Add(pts, m, sd.FirstCorner.X, sd.FirstCorner.Y); Add(pts, m, sd.SecondCorner.X, sd.SecondCorner.Y); Add(pts, m, sd.ThirdCorner.X, sd.ThirdCorner.Y); Add(pts, m, sd.FourthCorner.X, sd.FourthCorner.Y); break;
                case Face3D f: Add(pts, m, f.FirstCorner.X, f.FirstCorner.Y); Add(pts, m, f.SecondCorner.X, f.SecondCorner.Y); Add(pts, m, f.ThirdCorner.X, f.ThirdCorner.Y); Add(pts, m, f.FourthCorner.X, f.FourthCorner.Y); break;
                case Leader ld: if (ld.Vertices != null) foreach (var v in ld.Vertices) Add(pts, m, v.X, v.Y); break;
                case MText mt:
                    Add(pts, m, mt.InsertPoint.X, mt.InsertPoint.Y);
                    Add(pts, m, mt.InsertPoint.X + mt.Height, mt.InsertPoint.Y + mt.Height);
                    break;
                case TextEntity t:
                    Add(pts, m, t.InsertPoint.X, t.InsertPoint.Y);
                    Add(pts, m, t.InsertPoint.X + t.Height, t.InsertPoint.Y + t.Height);
                    break;
                case ACadSharp.Entities.Point p: Add(pts, m, p.Location.X, p.Location.Y); break;
                case Hatch h:
                {
                    if (h.Paths == null) break;
                    foreach (var bp in h.Paths)
                    {
                        IEnumerable<ACadEntity> edges = null;
                        try { edges = bp.Entities ?? bp.Edges?.Select(e => { try { return e.ToEntity(); } catch { return null; } }).Where(e => e != null); } catch { }
                        if (edges == null) continue;
                        foreach (var e in edges)
                        {
                            switch (e)
                            {
                                case Line l2: Add(pts, m, l2.StartPoint.X, l2.StartPoint.Y); Add(pts, m, l2.EndPoint.X, l2.EndPoint.Y); break;
                                case LwPolyline pl2: foreach (var v in pl2.Vertices) Add(pts, m, v.Location.X, v.Location.Y); break;
                                case Arc a2: SampleArc(pts, m, a2.Center.X, a2.Center.Y, a2.Radius, a2.StartAngle, a2.EndAngle); break;
                                case Circle c2: Add(pts, m, c2.Center.X + c2.Radius, c2.Center.Y); Add(pts, m, c2.Center.X - c2.Radius, c2.Center.Y); break;
                            }
                        }
                    }
                    break;
                }
                case Insert ins:
                {
                    var b = ins.Block;
                    if (b?.Entities == null) break;
                    double sx = ins.XScale == 0 ? 1 : ins.XScale, sy = ins.YScale == 0 ? 1 : ins.YScale;
                    Walk(b.Entities.ToList(), pts, Compose(m, BlockMatrix(ins.InsertPoint.X, ins.InsertPoint.Y, sx, sy, ins.Rotation)), depth + 1);
                    break;
                }
                case Dimension dim:
                {
                    BlockRecord blk = null;
                    try { blk = dim.Block; } catch { }
                    if (blk?.Entities != null) Walk(blk.Entities.ToList(), pts, m, depth + 1);
                    break;
                }
                case Ray _: case XLine _: break;
            }
        }

        /// <summary>稳健包围盒：跨度异常（垃圾实体撑爆）时退化为 1%~99% 分位数范围。</summary>
        private static void ComputeBounds(List<(double x, double y)> pts, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = pts.Min(p => p.x); maxX = pts.Max(p => p.x);
            minY = pts.Min(p => p.y); maxY = pts.Max(p => p.y);
            // 百分位裁剪：剔除坐标失控的垃圾点（仅当采样点足够多时才做，防止单点越界）
            if (pts.Count >= 4)
            {
                var xs = pts.Select(p => p.x).OrderBy(v => v).ToList();
                var ys = pts.Select(p => p.y).OrderBy(v => v).ToList();
                int lo = Math.Max(0, xs.Count / 100);
                int hi = Math.Min(xs.Count - 1, Math.Max(lo + 1, xs.Count - xs.Count / 100 - 1));
                double p01x = xs[lo], p99x = xs[hi];
                double p01y = ys[lo], p99y = ys[hi];
                double spanX = Math.Max(p99x - p01x, 1e-6), spanY = Math.Max(p99y - p01y, 1e-6);
                if ((maxX - minX) > 100 * spanX) { minX = p01x; maxX = p99x; }
                if ((maxY - minY) > 100 * spanY) { minY = p01y; maxY = p99y; }
            }
        }

        private static void Add(List<(double x, double y)> pts, Matrix m, double x, double y)
        {
            var (X, Y) = Tf(m, x, y);
            if (double.IsNaN(X) || double.IsNaN(Y) || double.IsInfinity(X) || double.IsInfinity(Y)) return;
            pts.Add((X, Y));
        }

        private static void SampleArc(List<(double x, double y)> pts, Matrix m, double cx, double cy, double r, double a0, double a1)
        {
            double sweep = a1 - a0;
            if (sweep <= 0) sweep += 2 * Math.PI;
            int seg = Math.Max(4, Math.Min(24, (int)(sweep / (Math.PI / 8))));
            for (int i = 0; i <= seg; i++)
            {
                double t = a0 + sweep * i / seg;
                Add(pts, m, cx + r * Math.Cos(t), cy + r * Math.Sin(t));
            }
        }

        // ═══════════════════════ 颜色 / 线型 / 字体 ═══════════════════════

        private static string ResolveColorHex(ACadEntity ent, string parentColor)
        {
            try
            {
                var c = ent.Color;
                if (c.IsByBlock) return parentColor ?? DefHex();
                if (c.IsByLayer && ent.Layer != null) c = ent.Layer.Color;
                if (c.IsByLayer) return parentColor ?? DefHex();
                if (c.IsTrueColor)
                {
                    uint v = unchecked((uint)c.TrueColor);
                    return AdjHex(RgbToHex((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
                }
                int idx = 7;
                try { idx = c.Index; } catch { }
                if (idx == 0 || idx == 256) return parentColor ?? DefHex();
                return AdjHex(AciHex(idx));
            }
            catch { return DefHex(); }
        }

        private static string DefHex() => DarkBg ? "#ffffff" : "#000000";

        private static string AdjHex(string hex)
        {
            var (r, g, b) = HexToRgb(hex);
            double lum = 0.299 * r + 0.587 * g + 0.114 * b;
            if (DarkBg)
            {
                if (lum < 45)
                {
                    if (lum < 12) return "#ffffff";
                    double k = 110.0 / Math.Max(lum, 1);
                    return RgbToHex((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
                }
            }
            return hex;
        }

        private static string AciHex(int idx)
        {
            if (idx <= 0) return "#000000";
            if (idx <= 9)
            {
                return idx switch
                {
                    1 => "#ff0000", 2 => "#ffff00", 3 => "#00ff00", 4 => "#00ffff",
                    5 => "#0000ff", 6 => "#ff00ff", 7 => "#ffffff", 8 => "#808080",
                    9 => "#c0c0c0", _ => "#000000"
                };
            }
            if (idx <= 249)
            {
                int i = idx - 10;
                double hue = (i % 20) * 18.0;
                int row = i / 20;
                double sat = (row < 6) ? 1.0 : 0.5;
                double lum = 1.0 - (row % 6) * 0.15;
                return HslToHex(hue, sat, lum);
            }
            int g = (idx - 250) * 51;
            return RgbToHex((byte)g, (byte)g, (byte)g);
        }

        private static string HslToHex(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360 / 360;
            double r, g, b;
            if (s == 0) { r = g = b = l; }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = Hue2Rgb(p, q, h + 1.0 / 3);
                g = Hue2Rgb(p, q, h);
                b = Hue2Rgb(p, q, h - 1.0 / 3);
            }
            return RgbToHex((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }

        private static double Hue2Rgb(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        private static string RgbToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
        private static (byte r, byte g, byte b) HexToRgb(string hex)
        {
            hex = hex.TrimStart('#');
            return (Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
        }

        private static SolidColorBrush ParseColor(string hex)
        {
            try
            {
                hex = hex.Trim().ToLowerInvariant();
                if (!hex.StartsWith("#") || hex.Length < 4) return null;
                var (r, g, b) = HexToRgb(hex);
                var br = new SolidColorBrush(Color.FromRgb(r, g, b));
                br.Freeze();
                return br;
            }
            catch { return null; }
        }

        /// <summary>线型虚线：按屏幕比例归一化（最长段≈10 屏幕像素），保持线型比例可见。</summary>
        private static DashStyle GetDash(ACadEntity ent)
        {
            try
            {
                var lt = ent.LineType;
                if (lt == null && ent.Layer != null) lt = ent.Layer.LineType;
                if (lt == null) return null;
                string name = lt.Name ?? "";
                if (name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return null;
                var segs = lt.Segments?.ToList();
                if (segs == null || segs.Count < 2) return null;
                var lens = new List<double>();
                foreach (var s in segs)
                {
                    double len = Math.Abs(s.Length);
                    if (len < 1e-4) len = 0.6;
                    lens.Add(len);
                }
                if (lens.Count % 2 != 0) lens.Add(lens[lens.Count - 1]);
                double maxLen = lens.Max();
                if (maxLen <= 0) return null;
                double k = 10.0 / maxLen;
                var ds = new DashStyle(lens.Select(l => Math.Max(l * k, 0.05)).ToList(), 0);
                ds.Freeze();
                return ds;
            }
            catch { return null; }
        }

        // ── 字体：优先 D:\ZCODE\LDAssistant\fonts 目录（TTF/OTF 直接加载；SHX 映射等义中文字体） ──

        // 字体/字形缓存：避免每个文字实体重复构建 FontFamily/Typeface（7000+ 文字时显著提速）
        private static readonly Dictionary<string, FontFamily> FontFamilyCache = new();
        private static readonly Dictionary<FontFamily, Typeface> TypefaceCache = new();
        private static readonly FontFamily DefaultCadFamily = new FontFamily("Microsoft YaHei,SimSun");
        private static Typeface CadTypeface(FontFamily ff)
        {
            if (ff == null) ff = DefaultCadFamily;
            if (!TypefaceCache.TryGetValue(ff, out var tf))
            {
                tf = new Typeface(ff, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                TypefaceCache[ff] = tf;
            }
            return tf;
        }

        private static class Fonts
        {
            private static readonly Dictionary<string, FontFamily> ByName = new(StringComparer.OrdinalIgnoreCase);
            private static readonly Dictionary<string, ShxFont> ShxByName = new(StringComparer.OrdinalIgnoreCase);
            private static readonly FontFamily DefaultChinese = new FontFamily("Microsoft YaHei,SimSun");

            static Fonts()
            {
                try
                {
                    // 部署目录与源码目录都可能存在 fonts；同名文件只解析一次（省一半解析时间与瞬时内存）
                    var dirs = new[]
                    {
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts"),
                        @"D:\ZCODE\LDAssistant\fonts",
                    }.Where(Directory.Exists).Distinct();
                    var loadedTtf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dir in dirs)
                    {
                        foreach (var f in Directory.EnumerateFiles(dir)
                                     .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                                              || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
                        {
                            string key = Path.GetFileNameWithoutExtension(f);
                            if (!loadedTtf.Add(key)) continue;   // 另一目录已有同名文件，跳过
                            try
                            {
                                var gtf = new GlyphTypeface(new Uri(f));
                                string fam = gtf.FamilyNames.Values.FirstOrDefault() ?? key;
                                var baseUri = new Uri(Path.GetDirectoryName(f) + Path.DirectorySeparatorChar);
                                var family = new FontFamily(baseUri, "./" + Path.GetFileName(f) + "#" + fam);
                                ByName[key] = family;
                                ByName[fam] = family;
                            }
                            catch { }
                        }
                    }
                    // SHX 矢量字体：解析字形字节码，按文件名缓存（同名只解析一次）
                    var loadedShx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dir in dirs)
                    {
                        foreach (var f in Directory.EnumerateFiles(dir)
                                     .Where(f => f.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)))
                        {
                            string key = Path.GetFileNameWithoutExtension(f);
                            if (!loadedShx.Add(key)) continue;   // 另一目录已有同名文件，跳过
                            try
                            {
                                var shx = ShxFont.Load(f);
                                if (shx != null)
                                {
                                    ShxByName[key] = shx;
                                    ShxByName[shx.Info?.Trim().ToLowerInvariant() ?? key] = shx;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            /// <summary>按 CAD 字体文件名取 SHX 矢量字体（找不到返回 null）。</summary>
            public static ShxFont Shx(string cadFontName)
            {
                if (string.IsNullOrEmpty(cadFontName)) return null;
                string n = Path.GetFileNameWithoutExtension(cadFontName);
                // 去掉 @~! 等修饰前缀
                n = n.TrimStart('@', '~', '!');
                if (ShxByName.TryGetValue(n, out var f)) return f;
                return null;
            }

            /// <summary>按 CAD 字体名查目录内 TTF/OTF；无则返回中文字体兜底。</summary>
            public static FontFamily For(string cadFontName)
            {
                // 目录内精确匹配：找到返回对应字体，找不到返回 null（让调用方继续走 SHX 映射或默认兜底）
                if (!string.IsNullOrEmpty(cadFontName))
                {
                    string n = Path.GetFileNameWithoutExtension(cadFontName);
                    if (ByName.TryGetValue(n, out var f)) return f;
                }
                return null;
            }

            public static FontFamily Default() => DefaultChinese;
        }

        /// <summary>从实体文本样式解析字体：大字体优先，映射 SHX → 中文字体；目录内 TTF 精确匹配。按样式名缓存。</summary>
        private static FontFamily ResolveFontFamily(ACadEntity ent)
        {
            try
            {
                ACadSharp.Tables.TextStyle style = null;
                if (ent is MText mt) style = mt.Style;
                else if (ent is TextEntity tx) style = tx.Style;
                if (style == null) return Fonts.Default();

                string primaryFont = null, bigFont = null;
                try
                {
                    var styleType = style.GetType();
                    var pProp = styleType.GetProperty("Filename") ?? styleType.GetProperty("FontFile") ?? styleType.GetProperty("PrimaryFont");
                    if (pProp != null) primaryFont = pProp.GetValue(style)?.ToString();
                    var bProp = styleType.GetProperty("BigFontFilename") ?? styleType.GetProperty("BigFont");
                    if (bProp != null) bigFont = bProp.GetValue(style)?.ToString();
                }
                catch { }

                string key = (bigFont ?? "") + "|" + (primaryFont ?? "");
                // 宋体/黑体保留：直接映射系统字体，不走 SHX 替换（样式名含宋体/黑体/仿宋同样保留）
                if ((style != null && IsSongHeiFont(style.Name)) || IsSongHeiFont(primaryFont) || IsSongHeiFont(bigFont))
                {
                    string low = ((style.Name ?? "") + " " + primaryFont + " " + bigFont).ToLowerInvariant();
                    FontFamily sim = low.Contains("simhei") || low.Contains("黑体")
                        ? new FontFamily("SimHei, 黑体, Microsoft YaHei")
                        : new FontFamily("SimSun, 宋体, Microsoft YaHei");
                    lock (FontFamilyCache) { FontFamilyCache[key] = sim; }
                    return sim;
                }

                lock (FontFamilyCache)
                {
                    if (FontFamilyCache.TryGetValue(key, out var cached)) return cached;
                }
                FontFamily result = null;
                // 目录内 TTF 精确匹配（大字体优先）
                foreach (var cand in new[] { bigFont, primaryFont })
                {
                    if (string.IsNullOrEmpty(cand)) continue;
                    if (cand.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                        || cand.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                        || cand.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                    {
                        string ttfName = Path.GetFileNameWithoutExtension(cand);
                        var inDir = Fonts.For(ttfName);
                        if (inDir != null) { result = inDir; break; }
                    }
                    var mapped = MapShxToFont(cand);
                    if (mapped != null) { result = mapped; break; }
                }
                result = result ?? Fonts.Default();
                lock (FontFamilyCache) { FontFamilyCache[key] = result; }
                return result;
            }
            catch { return Fonts.Default(); }
        }

        /// <summary>常见 SHX 大字体 → Windows 中文字体替代（WPF 无法直接渲染 SHX）。</summary>
        private static FontFamily MapShxToFont(string shxName)
        {
            if (string.IsNullOrEmpty(shxName)) return null;
            string name = Path.GetFileNameWithoutExtension(shxName).ToLowerInvariant();
            switch (name)
            {
                // 中文大字体 SHX → 宋体/黑体等（含天正 TSSD 字库与南方 CASS 常用字）
                case "hztxt":
                case "hztxt5":
                case "hz":
                case "hzdx":
                case "chineset":
                case "gbcbig":
                case "gbchinese":
                case "tssdchn":
                case "tssdeng":
                case "xc90":
                    return new FontFamily("SimSun, 宋体, SimHei, Microsoft YaHei");
                case "fs":
                case "hzfs":
                case "fsgb2312":
                case "simfang":
                    return new FontFamily("FangSong, 仿宋, Microsoft YaHei");
                case "ht":
                case "hzht":
                case "simhei":
                    return new FontFamily("SimHei, 黑体, Microsoft YaHei");
                case "khz":
                case "hzkt":
                case "simkai":
                case "ktgb2312":
                    return new FontFamily("KaiTi, 楷体, Microsoft YaHei");
                // 英文 SHX → 英文字体（含南方 CASS 的 rs/rd）
                case "txt":
                case "simplex":
                case "romans":
                case "rs":
                case "rd":
                    return new FontFamily("Arial");
                case "romand":
                case "romantic":
                case "italic":
                case "italict":
                    return new FontFamily("Times New Roman");
                case "scripts":
                case "scriptc":
                    return new FontFamily("Comic Sans MS");
                default:
                    // 未知 SHX：目录内有同名 TTF 则命中，否则中文兜底
                    return Fonts.For(name);
            }
        }

        // ── 文本清洗 / 折行 ──

        /// <summary>解码 AutoCAD \U+XXXX Unicode 转义：钢筋符号 0x82-85 映射到字体码位
        /// （Tssdeng 等专业 SHX 字形），上下标控制码 0x8C-8F 去除，其余解码为对应 Unicode 字符。
        /// 必须在删除 \X 格式代码之前调用，否则 \U 会被当格式码删掉、残留 "+0084" 之类字面文本。</summary>
        private static readonly System.Text.RegularExpressions.Regex _reUni = new System.Text.RegularExpressions.Regex(@"\\U\+([0-9A-Fa-f]{4})", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string DecodeUniEscapes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return _reUni.Replace(s, m =>
            {
                int cp;
                try { cp = Convert.ToInt32(m.Groups[1].Value, 16); }
                catch { return m.Value; }
                switch (cp)
                {
                    case 0x82: return "\u0082";  // 一级钢筋 Φ
                    case 0x83: return "\u0083";  // 二级钢筋
                    case 0x84: return "\u0084";  // 三级钢筋
                    case 0x85: return "\u0085";  // 四级钢筋
                    case 0x8C: case 0x8D: case 0x8E: case 0x8F: return "";  // 上下标起止标记
                    default: return ((char)cp).ToString();
                }
            });
        }

        private static readonly System.Text.RegularExpressions.Regex _reFontFmt = new System.Text.RegularExpressions.Regex(@"\\f[^;]*;", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex _reCodeFmt = new System.Text.RegularExpressions.Regex(@"\\[A-Za-z]+\d*;?", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string CleanMText(string s)
        {
            if (s == null) return "";
            s = s.Replace("\\P", "\n").Replace("\\p", "\n").Replace("\\~", " ");
            // \U+XXXX 先解码（\U 之后会被格式码正则删除，残留 "+0084" 字面文本）
            s = DecodeUniEscapes(s);
            s = _reFontFmt.Replace(s, "");
            s = _reCodeFmt.Replace(s, "");
            s = s.Replace("{}", "");
            // AutoCAD %% 转义：钢筋等级符号 %%130-133 → 字体码位 0x82-0x85（Tssdeng 等含字形），
            // %%c/d/p → Φ/°/±，%%140-143 上下标标记去除，%%% → %
            s = s.Replace("%%130", "\u0082").Replace("%%131", "\u0083")
                 .Replace("%%132", "\u0084").Replace("%%133", "\u0085")
                 .Replace("%%140", "").Replace("%%141", "")
                 .Replace("%%142", "").Replace("%%143", "")
                 .Replace("%%d", "°").Replace("%%D", "°")
                 .Replace("%%c", "Φ").Replace("%%C", "Φ")
                 .Replace("%%p", "±").Replace("%%P", "±")
                 .Replace("%%u", "").Replace("%%U", "")
                 .Replace("%%%", "%");
            return s;
        }

        /// <summary>MText 按参考框宽自动折行：CJK 按 1em、ASCII 按 0.55em 估宽。</summary>
        private static string WrapMText(string text, double boxWidth, double fontHeight)
        {
            if (string.IsNullOrEmpty(text) || boxWidth <= 0 || fontHeight <= 0) return text;
            double maxEm = boxWidth / fontHeight;
            if (maxEm < 2) return text;
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var rawLine in text.Split('\n'))
            {
                if (!first) sb.Append('\n');
                first = false;
                if (rawLine.Length == 0) continue;
                var cur = new System.Text.StringBuilder();
                double w = 0;
                foreach (char ch in rawLine)
                {
                    double cw = IsFullWidthChar(ch) ? 1.0 : 0.55;
                    if (cur.Length > 0 && w + cw > maxEm)
                    {
                        sb.Append(cur);
                        sb.Append('\n');
                        cur.Clear();
                        w = 0;
                    }
                    cur.Append(ch);
                    w += cw;
                }
                sb.Append(cur);
            }
            return sb.ToString();
        }

        private static bool IsFullWidthChar(char c)
        {
            int v = c;
            return (v >= 0x1100 && v <= 0x115F)
                || (v >= 0x2E80 && v <= 0x303E)
                || (v >= 0x3041 && v <= 0x33FF)
                || (v >= 0x3400 && v <= 0x4DBF)
                || (v >= 0x4E00 && v <= 0x9FFF)
                || (v >= 0xA000 && v <= 0xA4CF)
                || (v >= 0xAC00 && v <= 0xD7A3)
                || (v >= 0xF900 && v <= 0xFAFF)
                || (v >= 0xFE30 && v <= 0xFE4F)
                || (v >= 0xFF00 && v <= 0xFF60)
                || (v >= 0xFFE0 && v <= 0xFFE6)
                || (v >= 0x20000 && v <= 0x2FA1F);
        }

        // ═══════════════════════ 矩阵 ═══════════════════════

        private readonly struct Matrix
        {
            public readonly double A, B, C, D, E, F;
            public Matrix(double a, double b, double c, double d, double e, double f) { A = a; B = b; C = c; D = d; E = e; F = f; }
        }

        private static Matrix Identity() => new Matrix(1, 0, 0, 1, 0, 0);

        private static Matrix BlockMatrix(double tx, double ty, double sx, double sy, double rot)
        {
            double c = Math.Cos(rot), s = Math.Sin(rot);
            return new Matrix(c * sx, s * sx, -s * sy, c * sy, tx, ty);
        }

        private static Matrix Compose(Matrix p, Matrix c) => new Matrix(
            p.A * c.A + p.C * c.B, p.B * c.A + p.D * c.B,
            p.A * c.C + p.C * c.D, p.B * c.C + p.D * c.D,
            p.A * c.E + p.C * c.F + p.E, p.B * c.E + p.D * c.F + p.F);

        private static (double x, double y) Tf(Matrix m, double x, double y)
            => (m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F);

        /// <summary>模型坐标（CAD 上正，不翻转；翻转在最终 MatrixTransform 完成）。</summary>
        private static (double x, double y) T(Matrix m, double x, double y) => Tf(m, x, y);

        private static double MatrixScale(Matrix m)
        {
            double sx = Math.Sqrt(m.A * m.A + m.B * m.B);
            double sy = Math.Sqrt(m.C * m.C + m.D * m.D);
            double s = (sx + sy) / 2;
            return s <= 1e-9 ? 1 : s;
        }
    }
}
