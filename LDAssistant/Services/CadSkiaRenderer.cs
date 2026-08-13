using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using SkiaSharp;

namespace LDAssistant.Services
{
    /// <summary>
    /// 基于 SkiaSharp 的 CAD 渲染引擎。
    /// 设计目标：接近 AutoCAD 官方显示效果，且可在后台线程运行（不阻塞 UI）。
    ///
    /// 相比旧的 WPF DrawingVisual 方案的改进：
    ///   1. 线程安全 —— 可在 Task.Run 中渲染，彻底解决大图卡死
    ///   2. 官方 ACI 256 色调色板（而非近似算法）
    ///   3. 比例线宽（按 lineweight 毫秒值换算），不再 ×32 糊成一团
    ///   4. 支持 Dimension（标注）/ Leader 块展开 —— 解决"显示不完整"
    ///   5. 支持线型（虚线 / 点划线），带性能保护
    ///   6. 百分位裁剪包围盒 —— 排除离群实体撑大画布导致图形变成小点
    /// </summary>
    internal static class CadSkiaRenderer
    {
        /// <summary>递归展开深度上限（防止循环引用块导致栈溢出）</summary>
        private const int MaxDepth = 12;

        /// <summary>渲染结果</summary>
        public sealed class Result
        {
            public BitmapSource Image;
            public int PixelWidth;
            public int PixelHeight;
            public int DrawnPrimitives;
            /// <summary>世界坐标包围盒（裁剪后）</summary>
            public double MinX, MinY, MaxX, MaxY;
            /// <summary>世界单位 → 像素 的缩放系数</summary>
            public double Scale;
        }

        // ══════════════════════════════════════════════════════════
        // 公开入口
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 渲染实体列表为 WPF 位图。可在任意线程调用；返回的 BitmapSource 已 Freeze。
        /// </summary>
        /// <param name="entities">要渲染的实体（模型空间或布局块）</param>
        /// <param name="targetLongSidePx">目标长边像素（越大越清晰，也越吃内存）</param>
        /// <param name="background">画布背景色</param>
        public static Result Render(IList<Entity> entities, int targetLongSidePx, SKColor background)
        {
            var result = new Result();
            if (entities == null || entities.Count == 0) return null;

            // ── 1. 收集几何点，计算包围盒 ──
            var pts = new List<(double x, double y)>(Math.Max(1024, entities.Count * 4));
            int walked = 0;
            WalkList(entities, pts, ref walked, Identity(), 0);
            if (pts.Count == 0) return null;

            // 百分位裁剪：丢弃两端各 0.2% 的离群点。
            // CAD 图纸常有极远处的孤立实体（脏数据 / 辅助点），
            // 若按全量包围盒计算，真实图形会被压缩成一个小点。
            var sortedX = pts.Select(p => p.x).OrderBy(v => v).ToArray();
            var sortedY = pts.Select(p => p.y).OrderBy(v => v).ToArray();
            int trim = pts.Count >= 500 ? pts.Count / 500 : 0;
            double mnX = sortedX[trim], mxX = sortedX[sortedX.Length - 1 - trim];
            double mnY = sortedY[trim], mxY = sortedY[sortedY.Length - 1 - trim];

            if (mxX - mnX <= 1e-9 || mxY - mnY <= 1e-9)
            {
                // 裁剪过度（例如全部实体共线），退回全量包围盒
                mnX = sortedX[0]; mxX = sortedX[sortedX.Length - 1];
                mnY = sortedY[0]; mxY = sortedY[sortedY.Length - 1];
            }
            if (mxX - mnX <= 1e-9) { mnX -= 1; mxX += 1; }
            if (mxY - mnY <= 1e-9) { mnY -= 1; mxY += 1; }

            // ── 2. 计算画布尺寸与世界→屏幕映射 ──
            double dwgW = mxX - mnX, dwgH = mxY - mnY;
            const double margin = 40;
            double scale = targetLongSidePx / Math.Max(dwgW, dwgH);
            int pxW = (int)(dwgW * scale + margin * 2);
            int pxH = (int)(dwgH * scale + margin * 2);

            // 显存安全上限：单边不超过 12000px（约 550MB 峰值以内）
            const int cap = 12000;
            if (pxW > cap || pxH > cap)
            {
                double k = cap / (double)Math.Max(pxW, pxH);
                pxW = Math.Max(1, (int)(pxW * k));
                pxH = Math.Max(1, (int)(pxH * k));
                scale *= k;
            }
            if (pxW < 1) pxW = 1;
            if (pxH < 1) pxH = 1;

            double ox = margin - mnX * scale;
            double oy = margin + mxY * scale;   // Y 轴翻转：屏幕 y = oy - 世界 y * scale
            Func<double, double> Sx = x => x * scale + ox;
            Func<double, double> Sy = y => oy - y * scale;

            // ── 3. Skia 绘制 ──
            // Bgra8888 + Premul 与 WPF 的 Pbgra32 内存布局完全一致，可零转换直接拷贝
            var info = new SKImageInfo(pxW, pxH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null) return null;
            var canvas = surface.Canvas;
            canvas.Clear(background);

            bool darkBg = Luminance(background) < 128;

            using var stroke = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            var ctx = new DrawContext
            {
                Sx = Sx,
                Sy = Sy,
                Scale = scale,
                Canvas = canvas,
                Stroke = stroke,
                Fill = fill,
                DarkBackground = darkBg
            };

            // 先画图形，后画文字 —— 保证标注文字不被填充遮挡
            DrawList(entities, ctx, Identity(), null, 0, textPass: false);
            DrawList(entities, ctx, Identity(), null, 0, textPass: true);

            canvas.Flush();

            // ── 4. Skia 位图 → WPF BitmapSource ──
            using var image = surface.Snapshot();
            using var skBmp = SKBitmap.FromImage(image);
            if (skBmp == null) return null;

            var bs = ToBitmapSource(skBmp);
            if (bs == null) return null;

            result.Image = bs;
            result.PixelWidth = pxW;
            result.PixelHeight = pxH;
            result.DrawnPrimitives = ctx.Drawn;
            result.MinX = mnX; result.MinY = mnY; result.MaxX = mxX; result.MaxY = mxY;
            result.Scale = scale;
            return result;
        }

        /// <summary>Skia 位图转 WPF BitmapSource（已 Freeze，可跨线程使用）</summary>
        private static BitmapSource ToBitmapSource(SKBitmap bmp)
        {
            IntPtr pixels = bmp.GetPixels(out IntPtr length);
            if (pixels == IntPtr.Zero) return null;
            int stride = bmp.RowBytes;
            var bs = BitmapSource.Create(
                bmp.Width, bmp.Height,
                96, 96,
                PixelFormats.Pbgra32,
                null,
                pixels,
                (int)length,
                stride);
            bs.Freeze();
            return bs;
        }

        // ══════════════════════════════════════════════════════════
        // 绘制上下文
        // ══════════════════════════════════════════════════════════
        private sealed class DrawContext
        {
            public Func<double, double> Sx;
            public Func<double, double> Sy;
            public double Scale;
            public SKCanvas Canvas;
            public SKPaint Stroke;
            public SKPaint Fill;
            public bool DarkBackground;
            public int Drawn;
        }

        // ══════════════════════════════════════════════════════════
        // 递归绘制
        // ══════════════════════════════════════════════════════════
        private static void DrawList(IList<Entity> entities, DrawContext ctx, Matrix m,
            SKColor? parentColor, int depth, bool textPass)
        {
            if (entities == null || depth > MaxDepth) return;

            foreach (var ent in entities)
            {
                if (ent == null) continue;
                try
                {
                    if (IsHidden(ent)) continue;

                    bool isText = ent is TextEntity || ent is MText;
                    // 块 / 标注需要两趟都递归（内部可能同时含图形和文字）
                    bool isContainer = ent is Insert || ent is Dimension;

                    if (!isContainer && (isText != textPass)) continue;

                    DrawEntity(ent, ctx, m, parentColor, depth, textPass);
                }
                catch
                {
                    // 单个实体失败不影响整图渲染
                }
            }
        }

        private static void DrawEntity(Entity ent, DrawContext ctx, Matrix m,
            SKColor? parentColor, int depth, bool textPass)
        {
            var Sx = ctx.Sx;
            var Sy = ctx.Sy;
            var canvas = ctx.Canvas;

            switch (ent)
            {
                // ── 块引用：展开 ──
                case Insert ins:
                {
                    var block = ins.Block;
                    if (block?.Entities == null) break;

                    double sx = ins.XScale == 0 ? 1 : ins.XScale;
                    double sy = ins.YScale == 0 ? 1 : ins.YScale;
                    var cm = Compose(m, BlockMatrix(ins.InsertPoint.X, ins.InsertPoint.Y, sx, sy, ins.Rotation));

                    SKColor? insColor = parentColor;
                    try { if (!ins.Color.IsByBlock) insColor = ResolveColor(ins, parentColor, ctx.DarkBackground); } catch { }

                    DrawList(block.Entities.ToList(), ctx, cm, insColor, depth + 1, textPass);

                    // 块属性（可见文字，如标高、编号）
                    try
                    {
                        if (ins.Attributes != null)
                            DrawList(ins.Attributes.Cast<Entity>().ToList(), ctx, cm, insColor, depth + 1, textPass);
                    }
                    catch { }
                    break;
                }

                // ── 标注：AutoCAD 会为每个标注生成一个图块，展开即可得到与官方一致的外观 ──
                case Dimension dim:
                {
                    BlockRecord blk = null;
                    try { blk = dim.Block; } catch { }
                    if (blk?.Entities != null && blk.Entities.Count > 0)
                    {
                        SKColor? dimColor = ResolveColor(dim, parentColor, ctx.DarkBackground);
                        DrawList(blk.Entities.ToList(), ctx, m, dimColor, depth + 1, textPass);
                    }
                    break;
                }

                case Line ln:
                {
                    var (x0, y0) = Tf(m, ln.StartPoint.X, ln.StartPoint.Y);
                    var (x1, y1) = Tf(m, ln.EndPoint.X, ln.EndPoint.Y);
                    SetStroke(ctx, ent, parentColor);
                    canvas.DrawLine((float)Sx(x0), (float)Sy(y0), (float)Sx(x1), (float)Sy(y1), ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }

                case LwPolyline pl:
                {
                    SetStroke(ctx, ent, parentColor);
                    using var path = new SKPath();
                    var verts = pl.Vertices;
                    bool started = false;
                    for (int i = 0; i < verts.Count; i++)
                    {
                        var (wx, wy) = Tf(m, verts[i].Location.X, verts[i].Location.Y);
                        float px = (float)Sx(wx), py = (float)Sy(wy);
                        if (!started) { path.MoveTo(px, py); started = true; continue; }

                        double bulge = verts[i - 1].Bulge;
                        if (Math.Abs(bulge) > 1e-4)
                        {
                            var (pvx, pvy) = Tf(m, verts[i - 1].Location.X, verts[i - 1].Location.Y);
                            AddBulge(path, (float)Sx(pvx), (float)Sy(pvy), px, py, bulge);
                        }
                        else path.LineTo(px, py);
                    }
                    if (pl.IsClosed && started && verts.Count > 1)
                    {
                        double lastBulge = verts[verts.Count - 1].Bulge;
                        var (fx, fy) = Tf(m, verts[0].Location.X, verts[0].Location.Y);
                        if (Math.Abs(lastBulge) > 1e-4)
                        {
                            var (lx, ly) = Tf(m, verts[verts.Count - 1].Location.X, verts[verts.Count - 1].Location.Y);
                            AddBulge(path, (float)Sx(lx), (float)Sy(ly), (float)Sx(fx), (float)Sy(fy), lastBulge);
                        }
                        else path.Close();
                    }
                    canvas.DrawPath(path, ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }

                case Polyline2D p2:
                {
                    SetStroke(ctx, ent, parentColor);
                    using var path = new SKPath();
                    bool st = false;
                    foreach (var v in p2.Vertices)
                    {
                        var (wx, wy) = Tf(m, v.Location.X, v.Location.Y);
                        if (!st) { path.MoveTo((float)Sx(wx), (float)Sy(wy)); st = true; }
                        else path.LineTo((float)Sx(wx), (float)Sy(wy));
                    }
                    if (p2.IsClosed && st) path.Close();
                    canvas.DrawPath(path, ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }

                case Polyline3D p3:
                {
                    SetStroke(ctx, ent, parentColor);
                    using var path = new SKPath();
                    bool st = false;
                    foreach (var v in p3.Vertices)
                    {
                        var (wx, wy) = Tf(m, v.Location.X, v.Location.Y);
                        if (!st) { path.MoveTo((float)Sx(wx), (float)Sy(wy)); st = true; }
                        else path.LineTo((float)Sx(wx), (float)Sy(wy));
                    }
                    if (p3.IsClosed && st) path.Close();
                    canvas.DrawPath(path, ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }

                // Arc 继承自 Circle，必须先匹配 Arc
                case Arc a:
                {
                    SetStroke(ctx, ent, parentColor);
                    DrawArcPoly(ctx, m, a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle);
                    break;
                }

                case Circle c:
                {
                    SetStroke(ctx, ent, parentColor);
                    // 圆在块变换下可能被拉成椭圆，统一按采样绘制保证正确
                    DrawArcPoly(ctx, m, c.Center.X, c.Center.Y, c.Radius, 0, Math.PI * 2);
                    break;
                }

                case Ellipse e:
                {
                    SetStroke(ctx, ent, parentColor);
                    using var path = new SKPath();
                    double rx = e.MajorAxis, ry = e.MajorAxis * e.RadiusRatio, rot = e.Rotation;
                    bool st = false;
                    const int seg = 96;
                    for (int i = 0; i <= seg; i++)
                    {
                        double t = i * 2 * Math.PI / seg;
                        double ex = e.Center.X + rx * Math.Cos(t) * Math.Cos(rot) - ry * Math.Sin(t) * Math.Sin(rot);
                        double ey = e.Center.Y + rx * Math.Cos(t) * Math.Sin(rot) + ry * Math.Sin(t) * Math.Cos(rot);
                        var (wx, wy) = Tf(m, ex, ey);
                        if (!st) { path.MoveTo((float)Sx(wx), (float)Sy(wy)); st = true; }
                        else path.LineTo((float)Sx(wx), (float)Sy(wy));
                    }
                    canvas.DrawPath(path, ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }

                case Spline sp:
                {
                    SetStroke(ctx, ent, parentColor);
                    var list = (sp.FitPoints != null && sp.FitPoints.Count >= 2) ? sp.FitPoints : sp.ControlPoints;
                    if (list == null || list.Count < 2) break;
                    using var path = new SKPath();
                    bool st = false;
                    foreach (var p in list)
                    {
                        var (wx, wy) = Tf(m, p.X, p.Y);
                        if (!st) { path.MoveTo((float)Sx(wx), (float)Sy(wy)); st = true; }
                        else path.LineTo((float)Sx(wx), (float)Sy(wy));
                    }
                    if (sp.IsClosed && st) path.Close();
                    canvas.DrawPath(path, ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }

                case Hatch h:
                {
                    DrawHatch(ctx, m, h, parentColor);
                    break;
                }

                case Solid sd:
                {
                    var color = ResolveColor(ent, parentColor, ctx.DarkBackground);
                    var pts = new[]
                    {
                        Tf(m, sd.FirstCorner.X,  sd.FirstCorner.Y),
                        Tf(m, sd.SecondCorner.X, sd.SecondCorner.Y),
                        Tf(m, sd.FourthCorner.X, sd.FourthCorner.Y),  // 注意 SOLID 的顶点顺序是 Z 字形
                        Tf(m, sd.ThirdCorner.X,  sd.ThirdCorner.Y),
                    };
                    FillPoly(ctx, color, pts);
                    break;
                }

                case Face3D f:
                {
                    var color = ResolveColor(ent, parentColor, ctx.DarkBackground);
                    var pts = new[]
                    {
                        Tf(m, f.FirstCorner.X,  f.FirstCorner.Y),
                        Tf(m, f.SecondCorner.X, f.SecondCorner.Y),
                        Tf(m, f.ThirdCorner.X,  f.ThirdCorner.Y),
                        Tf(m, f.FourthCorner.X, f.FourthCorner.Y),
                    };
                    FillPoly(ctx, color, pts);
                    break;
                }

                case Leader ld:
                {
                    SetStroke(ctx, ent, parentColor);
                    var vs = ld.Vertices;
                    if (vs == null || vs.Count < 2) break;
                    using var path = new SKPath();
                    bool st = false;
                    foreach (var v in vs)
                    {
                        var (wx, wy) = Tf(m, v.X, v.Y);
                        if (!st) { path.MoveTo((float)Sx(wx), (float)Sy(wy)); st = true; }
                        else path.LineTo((float)Sx(wx), (float)Sy(wy));
                    }
                    canvas.DrawPath(path, ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }

                case ACadSharp.Entities.Point ptE:
                {
                    SetStroke(ctx, ent, parentColor);
                    var (wx, wy) = Tf(m, ptE.Location.X, ptE.Location.Y);
                    canvas.DrawCircle((float)Sx(wx), (float)Sy(wy), 1.2f, ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }

                case MText mt:
                {
                    string txt = CleanMText(mt.Value);
                    if (string.IsNullOrWhiteSpace(txt)) break;
                    var (wx, wy) = Tf(m, mt.InsertPoint.X, mt.InsertPoint.Y);
                    double h = mt.Height > 0 ? mt.Height : 2.5;
                    float size = (float)(h * ctx.Scale * MatrixScale(m));
                    float rot = (float)(-mt.Rotation * 180 / Math.PI);
                    DrawText(ctx, ResolveColor(ent, parentColor, ctx.DarkBackground),
                        (float)Sx(wx), (float)Sy(wy), size, rot, txt.Replace("\n", " "), SKTextAlign.Left);
                    break;
                }

                case TextEntity tx:
                {
                    if (tx is AttributeDefinition) break;   // 属性定义是模板，不渲染

                    string txt = tx.Value;
                    if (string.IsNullOrWhiteSpace(txt)) break;
                    txt = CleanMText(txt);
                    if (string.IsNullOrWhiteSpace(txt)) break;

                    // 对齐方式：非左对齐时 AutoCAD 使用 AlignmentPoint
                    double bx = tx.InsertPoint.X, by = tx.InsertPoint.Y;
                    var align = SKTextAlign.Left;
                    try
                    {
                        if (tx.HorizontalAlignment != TextHorizontalAlignment.Left)
                        {
                            if (tx.AlignmentPoint.X != 0 || tx.AlignmentPoint.Y != 0)
                            {
                                bx = tx.AlignmentPoint.X;
                                by = tx.AlignmentPoint.Y;
                            }
                            align = tx.HorizontalAlignment == TextHorizontalAlignment.Right
                                ? SKTextAlign.Right
                                : SKTextAlign.Center;
                        }
                    }
                    catch { }

                    var (wx, wy) = Tf(m, bx, by);
                    double h = tx.Height > 0 ? tx.Height : 2.5;
                    float size = (float)(h * ctx.Scale * MatrixScale(m));
                    float rot = (float)(-tx.Rotation * 180 / Math.PI);
                    DrawText(ctx, ResolveColor(ent, parentColor, ctx.DarkBackground),
                        (float)Sx(wx), (float)Sy(wy), size, rot, txt, align);
                    break;
                }

                case Ray ray:
                {
                    SetStroke(ctx, ent, parentColor);
                    const double len = 1e6;
                    var (x0, y0) = Tf(m, ray.StartPoint.X, ray.StartPoint.Y);
                    var (x1, y1) = Tf(m, ray.StartPoint.X + ray.Direction.X * len, ray.StartPoint.Y + ray.Direction.Y * len);
                    canvas.DrawLine((float)Sx(x0), (float)Sy(y0), (float)Sx(x1), (float)Sy(y1), ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }

                case XLine xl:
                {
                    SetStroke(ctx, ent, parentColor);
                    const double len = 1e6;
                    var (x0, y0) = Tf(m, xl.FirstPoint.X + xl.Direction.X * len, xl.FirstPoint.Y + xl.Direction.Y * len);
                    var (x1, y1) = Tf(m, xl.FirstPoint.X - xl.Direction.X * len, xl.FirstPoint.Y - xl.Direction.Y * len);
                    canvas.DrawLine((float)Sx(x0), (float)Sy(y0), (float)Sx(x1), (float)Sy(y1), ctx.Stroke);
                    ctx.Drawn++;
                    break;
                }
            }
        }

        // ══════════════════════════════════════════════════════════
        // 图案填充
        // ══════════════════════════════════════════════════════════
        private static void DrawHatch(DrawContext ctx, Matrix m, Hatch h, SKColor? parentColor)
        {
            if (h.Paths == null) return;
            var color = ResolveColor(h, parentColor, ctx.DarkBackground);

            bool solid = false;
            try { solid = h.IsSolid || (h.Pattern != null && string.Equals(h.Pattern.Name, "SOLID", StringComparison.OrdinalIgnoreCase)); }
            catch { }

            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            bool any = false;

            foreach (var bp in h.Paths)
            {
                bool started = false;
                IEnumerable<Entity> edgeEntities = null;
                try
                {
                    if (bp.Entities != null && bp.Entities.Count > 0) edgeEntities = bp.Entities;
                    else if (bp.Edges != null) edgeEntities = bp.Edges.Select(e => { try { return e.ToEntity(); } catch { return null; } }).Where(e => e != null);
                }
                catch { }
                if (edgeEntities == null) continue;

                foreach (var e in edgeEntities)
                {
                    switch (e)
                    {
                        case Line l:
                        {
                            var (a, b) = Tf(m, l.StartPoint.X, l.StartPoint.Y);
                            var (c, d) = Tf(m, l.EndPoint.X, l.EndPoint.Y);
                            if (!started) { path.MoveTo((float)ctx.Sx(a), (float)ctx.Sy(b)); started = true; any = true; }
                            path.LineTo((float)ctx.Sx(c), (float)ctx.Sy(d));
                            break;
                        }
                        case LwPolyline pl:
                        {
                            foreach (var v in pl.Vertices)
                            {
                                var (x, y) = Tf(m, v.Location.X, v.Location.Y);
                                if (!started) { path.MoveTo((float)ctx.Sx(x), (float)ctx.Sy(y)); started = true; any = true; }
                                else path.LineTo((float)ctx.Sx(x), (float)ctx.Sy(y));
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
                                var (x, y) = Tf(m, arc.Center.X + arc.Radius * Math.Cos(t), arc.Center.Y + arc.Radius * Math.Sin(t));
                                if (!started) { path.MoveTo((float)ctx.Sx(x), (float)ctx.Sy(y)); started = true; any = true; }
                                else path.LineTo((float)ctx.Sx(x), (float)ctx.Sy(y));
                            }
                            break;
                        }
                        case Circle cc:
                        {
                            var (cx, cy) = Tf(m, cc.Center.X, cc.Center.Y);
                            path.AddCircle((float)ctx.Sx(cx), (float)ctx.Sy(cy), (float)(cc.Radius * ctx.Scale * MatrixScale(m)));
                            any = true;
                            break;
                        }
                    }
                }
                if (started) path.Close();
            }

            if (!any) return;

            if (solid)
            {
                ctx.Fill.Color = color;
                ctx.Canvas.DrawPath(path, ctx.Fill);
            }
            else
            {
                // 非实心图案：用半透明填充 + 边界线近似
                // （逐线绘制真实 pattern 在密集图纸上会产生数十万条线，严重拖慢渲染）
                ctx.Fill.Color = color.WithAlpha(60);
                ctx.Canvas.DrawPath(path, ctx.Fill);
                ctx.Stroke.Color = color;
                ctx.Stroke.StrokeWidth = 1f;
                ctx.Stroke.PathEffect = null;
                ctx.Canvas.DrawPath(path, ctx.Stroke);
            }
            ctx.Drawn++;
        }

        // ══════════════════════════════════════════════════════════
        // 画笔 / 颜色 / 线型
        // ══════════════════════════════════════════════════════════
        private static void SetStroke(DrawContext ctx, Entity ent, SKColor? parentColor)
        {
            ctx.Stroke.Color = ResolveColor(ent, parentColor, ctx.DarkBackground);

 // 线宽统一设为 0：所有线条都用同一条细线，不随缩放改变
 ctx.Stroke.StrokeWidth = 1.0f;

            ctx.Stroke.PathEffect = GetDashEffect(ent, ctx.Scale);
        }

        /// <summary>根据实体线型生成虚线效果；实线或过密时返回 null</summary>
        private static SKPathEffect GetDashEffect(Entity ent, double scale)
        {
            try
            {
                var lt = ent.LineType;
                if (lt == null && ent.Layer != null) lt = ent.Layer.LineType;
                if (lt == null) return null;

                string name = lt.Name ?? "";
                if (name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Continuous", StringComparison.OrdinalIgnoreCase))
                    return null;

                var segs = lt.Segments?.ToList();
                if (segs == null || segs.Count < 2) return null;

                var dashes = new List<float>();
                double total = 0;
                foreach (var s in segs)
                {
                    // 正 = 画线，负 = 空白，0 = 点（用极小值代替）
                    double len = Math.Abs(s.Length) * scale;
                    if (len < 0.01) len = 0.6;
                    dashes.Add((float)len);
                    total += len;
                }
                // dash 数组必须为偶数个
                if (dashes.Count % 2 != 0) dashes.Add(dashes[dashes.Count - 1]);

                // 性能与观感保护：整周期小于 3px 时视为实线
                if (total < 3.0) return null;
                // 周期过大（缩放后虚线看不出来）也退化为实线
                if (total > 4000) return null;

                return SKPathEffect.CreateDash(dashes.ToArray(), 0);
            }
            catch { return null; }
        }

        private static double LineWeightMm(Entity ent)
        {
            try
            {
                var lw = ent.LineWeight;
                if (lw == LineWeightType.ByLayer && ent.Layer != null) lw = ent.Layer.LineWeight;
                if (lw == LineWeightType.ByBlock || lw == LineWeightType.Default || lw == LineWeightType.ByLayer)
                    return 0.25;
                double mm = (int)lw / 100.0;
                return mm <= 0 ? 0.25 : mm;
            }
            catch { return 0.25; }
        }

        private static SKColor ResolveColor(Entity ent, SKColor? parentColor, bool darkBg)
        {
            try
            {
                var c = ent.Color;
                if (c.IsByBlock) return parentColor ?? DefaultColor(darkBg);
                if (c.IsByLayer && ent.Layer != null) c = ent.Layer.Color;
                if (c.IsByLayer) return parentColor ?? DefaultColor(darkBg);

                try
                {
                    if (c.IsTrueColor)
                    {
                        uint v = unchecked((uint)c.TrueColor);
                        return AdjustForBackground(
                            new SKColor((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)), darkBg);
                    }
                }
                catch { }

                int idx = 7;
                try { idx = c.Index; } catch { }
                if (idx == 0 || idx == 256) return parentColor ?? DefaultColor(darkBg);
                return AdjustForBackground(Aci(idx), darkBg);
            }
            catch { return DefaultColor(darkBg); }
        }

        private static SKColor DefaultColor(bool darkBg) => darkBg ? SKColors.White : SKColors.Black;

        /// <summary>
        /// AutoCAD 行为：颜色 7（白/黑）随背景自动反转；
        /// 另外对与背景过于接近的颜色做可见性提升。
        /// </summary>
        private static SKColor AdjustForBackground(SKColor c, bool darkBg)
        {
            double lum = Luminance(c);
            if (darkBg)
            {
                if (lum < 45)
                {
                    // 暗背景上的近黑色 —— AutoCAD 会显示为白色
                    if (lum < 12) return SKColors.White;
                    double k = 110.0 / Math.Max(lum, 1);
                    return new SKColor(
                        (byte)Math.Min(255, c.Red * k),
                        (byte)Math.Min(255, c.Green * k),
                        (byte)Math.Min(255, c.Blue * k));
                }
            }
            else
            {
                if (lum > 225) return SKColors.Black;
            }
            return c;
        }

        private static double Luminance(SKColor c) => 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;

        // ══════════════════════════════════════════════════════════
        // 文字
        // ══════════════════════════════════════════════════════════
        private static SKTypeface _typeface;
        private static readonly object _tfLock = new object();

        private static SKTypeface GetTypeface()
        {
            if (_typeface != null) return _typeface;
            lock (_tfLock)
            {
                if (_typeface != null) return _typeface;
                _typeface = SKTypeface.FromFamilyName("SimSun")
                         ?? SKTypeface.FromFamilyName("Microsoft YaHei")
                         ?? SKTypeface.FromFamilyName("SimHei")
                         ?? SKTypeface.Default;
                return _typeface;
            }
        }

        private static void DrawText(DrawContext ctx, SKColor color, float x, float y,
            float size, float rotDeg, string text, SKTextAlign align)
        {
            if (string.IsNullOrEmpty(text)) return;
            // 小于 1.5px 的文字渲染出来是一团噪点，不如不画（AutoCAD 缩小时同样不显示细节）
            if (size < 1.5f) return;
            if (size > 4000f) size = 4000f;

            using var font = new SKFont(GetTypeface(), size);
            ctx.Fill.Color = color;

            if (Math.Abs(rotDeg) > 0.1f)
            {
                ctx.Canvas.Save();
                ctx.Canvas.Translate(x, y);
                ctx.Canvas.RotateDegrees(rotDeg);
                ctx.Canvas.DrawText(text, 0, 0, align, font, ctx.Fill);
                ctx.Canvas.Restore();
            }
            else
            {
                ctx.Canvas.DrawText(text, x, y, align, font, ctx.Fill);
            }
            ctx.Drawn++;
        }

        /// <summary>清理 MText 控制码，还原为可读文本</summary>
        private static string CleanMText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var s = raw.Replace("\\P", "\n").Replace("\\p", "\n");
            // 去掉字体/颜色/高度/宽度等格式控制码
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[AaCcFfHhLlOoQqSsTtWwKkXx][^;\\]*;", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[Ll Oo Kk]", "");
            s = s.Replace("{", "").Replace("}", "");
            s = s.Replace("%%d", "°").Replace("%%D", "°")
                 .Replace("%%c", "Φ").Replace("%%C", "Φ")
                 .Replace("%%p", "±").Replace("%%P", "±")
                 .Replace("%%%", "%")
                 .Replace("%%u", "").Replace("%%U", "")
                 .Replace("%%o", "").Replace("%%O", "");
            return s.Trim();
        }

        // ══════════════════════════════════════════════════════════
        // 几何辅助
        // ══════════════════════════════════════════════════════════
        private static void DrawArcPoly(DrawContext ctx, Matrix m, double cx, double cy, double r, double a0, double a1)
        {
            double sweep = a1 - a0;
            if (sweep <= 0) sweep += 2 * Math.PI;

            // 分段数按屏幕弧长自适应：小圆少段（快），大圆多段（平滑）
            double screenR = r * ctx.Scale * MatrixScale(m);
            int seg = (int)Math.Clamp(screenR * sweep / 3.0, 8, 360);

            using var path = new SKPath();
            for (int i = 0; i <= seg; i++)
            {
                double t = a0 + sweep * i / seg;
                var (X, Y) = Tf(m, cx + r * Math.Cos(t), cy + r * Math.Sin(t));
                if (i == 0) path.MoveTo((float)ctx.Sx(X), (float)ctx.Sy(Y));
                else path.LineTo((float)ctx.Sx(X), (float)ctx.Sy(Y));
            }
            ctx.Canvas.DrawPath(path, ctx.Stroke);
            ctx.Drawn++;
        }

        private static void AddBulge(SKPath path, float x0, float y0, float x1, float y1, double bulge)
        {
            double chord = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            if (chord < 1e-6) { path.LineTo(x1, y1); return; }
            double sag = bulge * chord / 2;
            double mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
            double nx = -(y1 - y0), ny = (x1 - x0);
            double nl = Math.Sqrt(nx * nx + ny * ny);
            if (nl < 1e-6) { path.LineTo(x1, y1); return; }
            nx /= nl; ny /= nl;
            // 屏幕坐标 Y 向下，bulge 方向需取反
            path.QuadTo((float)(mx - nx * sag), (float)(my - ny * sag), x1, y1);
        }

        private static void FillPoly(DrawContext ctx, SKColor color, (double x, double y)[] ps)
        {
            using var path = new SKPath();
            bool st = false;
            foreach (var (X, Y) in ps)
            {
                if (!st) { path.MoveTo((float)ctx.Sx(X), (float)ctx.Sy(Y)); st = true; }
                else path.LineTo((float)ctx.Sx(X), (float)ctx.Sy(Y));
            }
            if (!st) return;
            path.Close();
            ctx.Fill.Color = color;
            ctx.Canvas.DrawPath(path, ctx.Fill);
            ctx.Drawn++;
        }

        private static bool IsHidden(Entity ent)
        {
            try
            {
                if (ent.IsInvisible) return true;
                // 注意：不检查图层"关闭"状态 —— 部分国产 CAD（如杰图）保存时会把所有图层标记为关闭，
                // 但 AutoCAD 打开时仍正常显示。只跳过"冻结"图层。
                if (ent.Layer != null && ent.Layer.Flags.HasFlag(LayerFlags.Frozen)) return true;
                return false;
            }
            catch { return false; }
        }

        // ── 2D 仿射矩阵 ──
        private readonly struct Matrix
        {
            public readonly double A, B, C, D, E, F;
            public Matrix(double a, double b, double c, double d, double e, double f)
            { A = a; B = b; C = c; D = d; E = e; F = f; }
        }

        private static Matrix Identity() => new Matrix(1, 0, 0, 1, 0, 0);

        private static Matrix BlockMatrix(double tx, double ty, double sx, double sy, double rot)
        {
            double c = Math.Cos(rot), s = Math.Sin(rot);
            return new Matrix(c * sx, s * sx, -s * sy, c * sy, tx, ty);
        }

        private static Matrix Compose(Matrix p, Matrix c) => new Matrix(
            p.A * c.A + p.C * c.B,
            p.B * c.A + p.D * c.B,
            p.A * c.C + p.C * c.D,
            p.B * c.C + p.D * c.D,
            p.A * c.E + p.C * c.F + p.E,
            p.B * c.E + p.D * c.F + p.F);

        private static (double x, double y) Tf(Matrix m, double x, double y)
            => (m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F);

        /// <summary>矩阵的平均缩放系数（用于换算半径 / 字高）</summary>
        private static double MatrixScale(Matrix m)
        {
            double sx = Math.Sqrt(m.A * m.A + m.B * m.B);
            double sy = Math.Sqrt(m.C * m.C + m.D * m.D);
            double s = (sx + sy) / 2;
            return s <= 1e-9 ? 1 : s;
        }

        // ══════════════════════════════════════════════════════════
        // 包围盒收集
        // ══════════════════════════════════════════════════════════
        private static void WalkList(IList<Entity> entities, List<(double x, double y)> pts,
            ref int cnt, Matrix m, int depth)
        {
            if (entities == null || depth > MaxDepth) return;
            foreach (var ent in entities)
            {
                if (ent == null) continue;
                cnt++;
                try { WalkEntity(ent, pts, ref cnt, m, depth); } catch { }
            }
        }

        private static void WalkEntity(Entity ent, List<(double x, double y)> pts, ref int cnt, Matrix m, int depth)
        {
            switch (ent)
            {
                case Line l:
                    Add(pts, m, l.StartPoint.X, l.StartPoint.Y);
                    Add(pts, m, l.EndPoint.X, l.EndPoint.Y);
                    break;
                case Arc a:
                    SampleArc(pts, m, a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle);
                    break;
                case Circle c:
                    Add(pts, m, c.Center.X + c.Radius, c.Center.Y);
                    Add(pts, m, c.Center.X - c.Radius, c.Center.Y);
                    Add(pts, m, c.Center.X, c.Center.Y + c.Radius);
                    Add(pts, m, c.Center.X, c.Center.Y - c.Radius);
                    break;
                case Ellipse e:
                {
                    double rx = e.MajorAxis, ry = e.MajorAxis * e.RadiusRatio, rot = e.Rotation;
                    for (int i = 0; i < 16; i++)
                    {
                        double t = i * 2 * Math.PI / 16;
                        Add(pts, m,
                            e.Center.X + rx * Math.Cos(t) * Math.Cos(rot) - ry * Math.Sin(t) * Math.Sin(rot),
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
                case Solid sd:
                    Add(pts, m, sd.FirstCorner.X, sd.FirstCorner.Y);
                    Add(pts, m, sd.SecondCorner.X, sd.SecondCorner.Y);
                    Add(pts, m, sd.ThirdCorner.X, sd.ThirdCorner.Y);
                    Add(pts, m, sd.FourthCorner.X, sd.FourthCorner.Y);
                    break;
                case Face3D f:
                    Add(pts, m, f.FirstCorner.X, f.FirstCorner.Y);
                    Add(pts, m, f.SecondCorner.X, f.SecondCorner.Y);
                    Add(pts, m, f.ThirdCorner.X, f.ThirdCorner.Y);
                    Add(pts, m, f.FourthCorner.X, f.FourthCorner.Y);
                    break;
                case Leader ld:
                    if (ld.Vertices != null) foreach (var v in ld.Vertices) Add(pts, m, v.X, v.Y);
                    break;
                case MText mt: Add(pts, m, mt.InsertPoint.X, mt.InsertPoint.Y); break;
                case TextEntity t: Add(pts, m, t.InsertPoint.X, t.InsertPoint.Y); break;
                case ACadSharp.Entities.Point p: Add(pts, m, p.Location.X, p.Location.Y); break;
                case Insert ins:
                {
                    var b = ins.Block;
                    if (b?.Entities == null) break;
                    double sx = ins.XScale == 0 ? 1 : ins.XScale;
                    double sy = ins.YScale == 0 ? 1 : ins.YScale;
                    WalkList(b.Entities.ToList(), pts, ref cnt,
                        Compose(m, BlockMatrix(ins.InsertPoint.X, ins.InsertPoint.Y, sx, sy, ins.Rotation)), depth + 1);
                    break;
                }
                case Dimension dim:
                {
                    BlockRecord blk = null;
                    try { blk = dim.Block; } catch { }
                    if (blk?.Entities != null)
                        WalkList(blk.Entities.ToList(), pts, ref cnt, m, depth + 1);
                    break;
                }
                case Hatch h:
                {
                    if (h.Paths == null) break;
                    foreach (var bp in h.Paths)
                    {
                        if (bp.Entities == null) continue;
                        foreach (var e in bp.Entities)
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

        // ══════════════════════════════════════════════════════════
        // AutoCAD 官方 ACI 256 色调色板
        // ══════════════════════════════════════════════════════════
        private static SKColor Aci(int idx)
        {
            if (idx < 0 || idx > 255) idx = 7;
            int v = AciTable[idx];
            return new SKColor((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
        }

        private static readonly int[] AciTable =
        {
            0x000000, 0xFF0000, 0xFFFF00, 0x00FF00, 0x00FFFF, 0x0000FF, 0xFF00FF, 0xFFFFFF,
            0x808080, 0xC0C0C0, 0xFF0000, 0xFF7F7F, 0xA50000, 0xA55252, 0x7F0000, 0x7F3F3F,
            0x4C0000, 0x4C2626, 0x260000, 0x261313, 0xFF3F00, 0xFF9F7F, 0xA52900, 0xA56752,
            0x7F1F00, 0x7F4F3F, 0x4C1300, 0x4C2F26, 0x260900, 0x261713, 0xFF7F00, 0xFFBF7F,
            0xA55200, 0xA57C52, 0x7F3F00, 0x7F5F3F, 0x4C2600, 0x4C3926, 0x261300, 0x261C13,
            0xFFBF00, 0xFFDF7F, 0xA57C00, 0xA59152, 0x7F5F00, 0x7F6F3F, 0x4C3900, 0x4C4226,
            0x261C00, 0x262113, 0xFFFF00, 0xFFFF7F, 0xA5A500, 0xA5A552, 0x7F7F00, 0x7F7F3F,
            0x4C4C00, 0x4C4C26, 0x262600, 0x262613, 0xBFFF00, 0xDFFF7F, 0x7CA500, 0x91A552,
            0x5F7F00, 0x6F7F3F, 0x394C00, 0x424C26, 0x1C2600, 0x212613, 0x7FFF00, 0xBFFF7F,
            0x52A500, 0x7CA552, 0x3F7F00, 0x5F7F3F, 0x264C00, 0x394C26, 0x132600, 0x1C2613,
            0x3FFF00, 0x9FFF7F, 0x29A500, 0x67A552, 0x1F7F00, 0x4F7F3F, 0x134C00, 0x2F4C26,
            0x092600, 0x172613, 0x00FF00, 0x7FFF7F, 0x00A500, 0x52A552, 0x007F00, 0x3F7F3F,
            0x004C00, 0x264C26, 0x002600, 0x132613, 0x00FF3F, 0x7FFF9F, 0x00A529, 0x52A567,
            0x007F1F, 0x3F7F4F, 0x004C13, 0x264C2F, 0x002609, 0x132617, 0x00FF7F, 0x7FFFBF,
            0x00A552, 0x52A57C, 0x007F3F, 0x3F7F5F, 0x004C26, 0x264C39, 0x002613, 0x13261C,
            0x00FFBF, 0x7FFFDF, 0x00A57C, 0x52A591, 0x007F5F, 0x3F7F6F, 0x004C39, 0x264C42,
            0x00261C, 0x132621, 0x00FFFF, 0x7FFFFF, 0x00A5A5, 0x52A5A5, 0x007F7F, 0x3F7F7F,
            0x004C4C, 0x264C4C, 0x002626, 0x132626, 0x00BFFF, 0x7FDFFF, 0x007CA5, 0x5291A5,
            0x005F7F, 0x3F6F7F, 0x00394C, 0x26424C, 0x001C26, 0x132126, 0x007FFF, 0x7FBFFF,
            0x0052A5, 0x527CA5, 0x003F7F, 0x3F5F7F, 0x00264C, 0x26394C, 0x001326, 0x131C26,
            0x003FFF, 0x7F9FFF, 0x0029A5, 0x5267A5, 0x001F7F, 0x3F4F7F, 0x00134C, 0x262F4C,
            0x000926, 0x131726, 0x0000FF, 0x7F7FFF, 0x0000A5, 0x5252A5, 0x00007F, 0x3F3F7F,
            0x00004C, 0x26264C, 0x000026, 0x131326, 0x3F00FF, 0x9F7FFF, 0x2900A5, 0x6752A5,
            0x1F007F, 0x4F3F7F, 0x13004C, 0x2F264C, 0x090026, 0x171326, 0x7F00FF, 0xBF7FFF,
            0x5200A5, 0x7C52A5, 0x3F007F, 0x5F3F7F, 0x26004C, 0x39264C, 0x130026, 0x1C1326,
            0xBF00FF, 0xDF7FFF, 0x7C00A5, 0x9152A5, 0x5F007F, 0x6F3F7F, 0x39004C, 0x42264C,
            0x1C0026, 0x211326, 0xFF00FF, 0xFF7FFF, 0xA500A5, 0xA552A5, 0x7F007F, 0x7F3F7F,
            0x4C004C, 0x4C264C, 0x260026, 0x261326, 0xFF00BF, 0xFF7FDF, 0xA5007C, 0xA55291,
            0x7F005F, 0x7F3F6F, 0x4C0039, 0x4C2642, 0x26001C, 0x261321, 0xFF007F, 0xFF7FBF,
            0xA50052, 0xA5527C, 0x7F003F, 0x7F3F5F, 0x4C0026, 0x4C2639, 0x260013, 0x26131C,
            0xFF003F, 0xFF7F9F, 0xA50029, 0xA55267, 0x7F001F, 0x7F3F4F, 0x4C0013, 0x4C262F,
            0x260009, 0x261317, 0x333333, 0x505050, 0x696969, 0x828282, 0xBEBEBE, 0xFFFFFF,
        };
    }
}
