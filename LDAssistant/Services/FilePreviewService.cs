using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
// 别名消除歧义
using Path = System.IO.Path;
using WpfColor = System.Windows.Media.Color;
using WpfPath = System.Windows.Shapes.Path;

// 别名消除歧义
using Run = DocumentFormat.OpenXml.Wordprocessing.Run;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;
using Break = DocumentFormat.OpenXml.Wordprocessing.Break;
using Table = DocumentFormat.OpenXml.Wordprocessing.Table;
using TableRowW = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using TableCellW = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using ParagraphW = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using Drawing = DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline;

namespace LDAssistant.Services
{
    /// <summary>文件预览服务 - 渲染 PDF/图片/Word 为可显示的图片</summary>
    public class FilePreviewService
    {
 private PdfiumViewer.PdfDocument _pdfDoc;
 private readonly object _pdfLock = new();
 // DOCX 解析缓存
 private List<DocBlock> _docxBlocksCache;
 private Dictionary<string, SD.Bitmap> _docxImagesCache;
 // 页面位图缓存（最多5页）
 private readonly Dictionary<int, BitmapSource> _pageCache = new();
 private readonly int _pageCacheMax = 5;
 public int TotalPages { get; private set; }
 public string FileType { get; private set; } = "";
 private string _currentPath;
 private ACadSharp.CadDocument _cadDoc;
 private List<string> _cadSpaceNames = new(); // DWG 空间名称列表（模型 + 布局）
 private double _cadLtScale = 1.0; // 全局线型比例 LTSCALE
 private double _cadRenderScale = 1.0; // 渲染缩放：CAD单位→像素（≤1，大图时缩小画布避免WPF极限）
 /// <summary>DWG 各空间/页面名称（用于缩略图标签）</summary>
 public IReadOnlyList<string> PageNames => _cadSpaceNames;

        public string CurrentPath
        {
            get => _currentPath;
            set => _currentPath = value;
        }

        public static string DetectFileType(string path)
        {
            var ext = Path.GetExtension(path).ToLower();
            return ext switch
            {
                ".pdf" => "pdf",
                ".docx" => "docx",
                ".txt" => "txt",
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tiff" or ".tif" or ".webp" => "image",
                ".dwg" or ".dxf" => "cad",
                _ => "unknown"
            };
        }

        /// <summary>打开文件</summary>
        public bool Open(string path)
        {
            Close();
            FileType = DetectFileType(path);
            _currentPath = path;

            try
            {
                switch (FileType)
                {
                    case "pdf":
                        _pdfDoc = PdfiumViewer.PdfDocument.Load(path);
                        TotalPages = _pdfDoc.PageCount;
                        return true;
case "image":
TotalPages = 1;
return true;
 case "docx":
 // DOCX 缩略图改用 WebView2 截图，不再用 GDI+ 分页计数
 TotalPages = 1;
 return true;
case "txt":
TotalPages = 1;
return true;
 case "cad":
 LoadCadDocument(path);
 return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"打开文件失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>渲染指定页为 BitmapSource</summary>
/// <summary>
	/// 判断当前文件是否使用矢量渲染（非位图）。
	/// </summary>
	public bool IsVectorRender => FileType == "cad" || FileType == "docx" || FileType == "txt";

	/// <summary>
	/// 矢量渲染：返回 WPF UIElement，缩放不失真。
	/// CAD 返回 Canvas（矢量线条），DOCX/TXT 返回 StackPanel（原生文字）。
	/// </summary>
	public UIElement RenderVector(int pageIndex)
	{
		try
		{
			switch (FileType)
			{
				case "cad":
					return RenderCadVector(pageIndex);
				case "docx":
				case "txt":
					return RenderDocxVector(pageIndex);
				default:
					return null;
			}
		}
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"矢量渲染失败: {ex}");
 return null;
 }
	}

	/// <summary>
	/// 提取指定页（模型空间 / 布局）的实体列表。
	/// 纯读取操作，可在后台线程调用。
	/// </summary>
	public List<ACadSharp.Entities.Entity> GetCadEntities(int pageIndex)
	{
		var doc = _cadDoc;
		if (doc == null)
		{
			LoadCadDocument(_currentPath);
			doc = _cadDoc;
			if (doc == null) return null;
		}

		string spaceName = "模型";
		if (pageIndex >= 0 && pageIndex < _cadSpaceNames.Count)
			spaceName = _cadSpaceNames[pageIndex];
		bool isModelSpace = (pageIndex <= 0);

		List<ACadSharp.Entities.Entity> entities;
		if (isModelSpace)
		{
			entities = doc.ModelSpace?.Entities?.ToList() ?? new List<ACadSharp.Entities.Entity>();
		}
		else
		{
			var layout = doc.Layouts?.FirstOrDefault(l => l.Name == spaceName);
			entities = layout?.AssociatedBlock?.Entities?.ToList() ?? new List<ACadSharp.Entities.Entity>();
		}

		if (entities.Count == 0) return null;
		entities = ExplodeMTexts(entities);
		return SortEntitiesByDrawOrder(doc, entities);
	}
	/// <summary>
	/// 炸开 MText：多行文本拆成单行 TextEntity，行位按基线逐行下移，
	/// 与 WPF 渲染器 MText 分支的布局规则一致（第一行基线在插入点下方一个字高，行距 1.5 倍字高），
	/// 避免多行文本在设置字宽后位置漂移。
	/// </summary>
	private static List<ACadSharp.Entities.Entity> ExplodeMTexts(List<ACadSharp.Entities.Entity> entities)
	{
		if (entities == null || entities.Count == 0) return entities;
		var result = new List<ACadSharp.Entities.Entity>(entities.Count);
		foreach (var ent in entities)
		{
			if (ent is ACadSharp.Entities.MText mt)
				result.AddRange(ExplodeMText(mt));
			else
				result.Add(ent);
		}
		return result;
	}

	private static IEnumerable<ACadSharp.Entities.Entity> ExplodeMText(ACadSharp.Entities.MText mt)
	{
		double h = mt.Height > 0 ? mt.Height : 2.5;
		string text = ParseMTextContent(mt.Value ?? mt.PlainText ?? "");
		if (string.IsNullOrWhiteSpace(text)) yield break;

		// 按参考框宽折行（CJK 1em、ASCII 0.55em，与渲染器一致）
		double boxW = mt.RectangleWidth > 0 ? mt.RectangleWidth : mt.HorizontalWidth;
		text = WrapMTextCad(text, boxW, h);

		var lines = text.Split('\n');
		// 旋转的多行文本：整段保持为一个 TextEntity（渲染器对旋转多行同样不拆行）
		if (Math.Abs(mt.Rotation) > 0.001 || lines.Length <= 1)
		{
			yield return MakeTextEntity(mt, text, new CSMath.XYZ(mt.InsertPoint.X, mt.InsertPoint.Y - h, 0), mt.Rotation);
			yield break;
		}

		double lineSpacing = h * 1.5;
		for (int i = 0; i < lines.Length; i++)
		{
			if (string.IsNullOrWhiteSpace(lines[i])) continue;
			yield return MakeTextEntity(mt, lines[i],
				new CSMath.XYZ(mt.InsertPoint.X, mt.InsertPoint.Y - h - i * lineSpacing, 0), 0);
		}
	}

	private static ACadSharp.Entities.TextEntity MakeTextEntity(ACadSharp.Entities.MText mt, string value, CSMath.XYZ pos, double rotation)
	{
		var te = new ACadSharp.Entities.TextEntity
		{
			Value = value,
			Height = mt.Height > 0 ? mt.Height : 2.5,
			InsertPoint = pos,
			Rotation = rotation,
		};
		try { te.Style = mt.Style; } catch { }
		try { te.Layer = mt.Layer; } catch { }
		try { te.Color = mt.Color; } catch { }
		return te;
	}

	/// <summary>MText 按参考框宽自动折行：CJK 按 1em、ASCII 按 0.55em 估宽。</summary>
	private static string WrapMTextCad(string text, double boxWidth, double fontHeight)
	{
		if (string.IsNullOrEmpty(text) || boxWidth <= 0 || fontHeight <= 0) return text;
		double maxEm = boxWidth / fontHeight;
		if (maxEm < 2) return text;
		var sb = new StringBuilder();
		bool first = true;
		foreach (var rawLine in text.Split('\n'))
		{
			if (!first) sb.Append('\n');
			first = false;
			if (rawLine.Length == 0) continue;
			var cur = new StringBuilder();
			double w = 0;
			foreach (char ch in rawLine)
			{
				double cw = IsFullWidthCharCad(ch) ? 1.0 : 0.55;
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

	private static bool IsFullWidthCharCad(char c)
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


	/// <summary>
	/// 【CAD 渲染引擎 v2】基于 SkiaSharp 的高保真渲染。
	///
	/// 相比旧的 WPF DrawingVisual 方案：
	///   • 线程安全 —— 必须在后台线程调用，彻底解决大图纸打开时主界面卡死
	///   • 官方 ACI 256 色调色板、比例线宽、虚线线型
	///   • 支持标注（Dimension）块展开，解决"图纸显示不完整"
	///
	/// 返回的 BitmapSource 已 Freeze，可直接跨线程赋给 UI 控件。
	/// </summary>
	/// <param name="pageIndex">页索引（0 = 模型空间）</param>
	/// <param name="targetLongSidePx">目标长边像素，越大越清晰</param>
	/// <param name="darkBackground">true = AutoCAD 暗色背景，false = 白色（打印/OCR）</param>
	public BitmapSource RenderCadSkia(int pageIndex, int targetLongSidePx, bool darkBackground)
	{
		try
		{
			var entities = GetCadEntities(pageIndex);
			if (entities == null || entities.Count == 0) return null;

			var bg = darkBackground
				? new SkiaSharp.SKColor(0x2A, 0x2A, 0x2E)
				: new SkiaSharp.SKColor(0xFF, 0xFF, 0xFF);

			var result = CadSkiaRenderer.Render(entities, targetLongSidePx, bg);
			return result?.Image;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[RenderCadSkia] 失败: {ex}");
			return null;
		}
	}

	/// <summary>
	/// DWG/DXF 矢量渲染——直接用 WPF Shape 绘制到 Canvas，缩放不失真。
	/// </summary>
 private Canvas RenderCadVector(int pageIndex)
 {
 var doc = _cadDoc;
 if (doc == null)
 {
 LoadCadDocument(_currentPath);
 doc = _cadDoc;
 if (doc == null) { System.Diagnostics.Debug.WriteLine("[RenderCadVector] doc is null after LoadCadDocument"); return null; }
 }

 string spaceName = "模型";
 if (pageIndex >= 0 && pageIndex < _cadSpaceNames.Count)
 spaceName = _cadSpaceNames[pageIndex];
 bool isModelSpace = (pageIndex <= 0);

 List<ACadSharp.Entities.Entity> entities;
 if (isModelSpace)
 entities = doc.ModelSpace?.Entities?.ToList() ?? new List<ACadSharp.Entities.Entity>();
 else
 {
 var layout = doc.Layouts?.FirstOrDefault(l => l.Name == spaceName);
 var block = layout?.AssociatedBlock;
 entities = block?.Entities?.ToList() ?? new List<ACadSharp.Entities.Entity>();
 }

 if (entities.Count == 0) return null;

 // 尝试按绘制顺序排序
 entities = SortEntitiesByDrawOrder(doc, entities);

 // 计算包围盒——始终从实体计算，不信任header extents（杰图CAD等可能包含离群实体坐标）
 // 使用两阶段算法：先收集所有坐标点，再用中位数+IQR过滤离群值
 var allPoints = new System.Collections.Generic.List<(double x, double y)>();
 foreach (var ent in entities)
 AccumulatePoints(ent, allPoints);
 if (allPoints.Count == 0) return null;

 // 计算中位数和四分位距(IQR)来过滤离群值
 double medX = Median(allPoints.Select(p => p.x).ToList());
 double medY = Median(allPoints.Select(p => p.y).ToList());
 var sortedX = allPoints.Select(p => p.x).OrderBy(v => v).ToList();
 var sortedY = allPoints.Select(p => p.y).OrderBy(v => v).ToList();
 double q1X = Percentile(sortedX, 0.25), q3X = Percentile(sortedX, 0.75);
 double q1Y = Percentile(sortedY, 0.25), q3Y = Percentile(sortedY, 0.75);
 double iqrX = q3X - q1X, iqrY = q3Y - q1Y;
 // 离群值过滤范围：中位数 ± max(6*IQR, 合理的最小范围)
 // 如果IQR很小（实体密集），用一个基于中位数的比例作为范围
 double rangeX = Math.Max(iqrX * 6, Math.Abs(medX) * 0.5 + 1000);
 double rangeY = Math.Max(iqrY * 6, Math.Abs(medY) * 0.5 + 1000);
 double minX = medX - rangeX, maxX = medX + rangeX;
 double minY = medY - rangeY, maxY = medY + rangeY;

 // 在过滤后的范围内重新计算精确bbox
 minX = double.MaxValue; minY = double.MaxValue; maxX = double.MinValue; maxY = double.MinValue;
 foreach (var (px, py) in allPoints)
 {
 if (px < medX - rangeX || px > medX + rangeX) continue;
 if (py < medY - rangeY || py > medY + rangeY) continue;
 if (px < minX) minX = px;
 if (py < minY) minY = py;
 if (px > maxX) maxX = px;
 if (py > maxY) maxY = py;
 }

 if (minX >= maxX || minY >= maxY)
 {
 // 过滤太严格，降级为用所有点
 minX = allPoints.Min(p => p.x); minY = allPoints.Min(p => p.y);
 maxX = allPoints.Max(p => p.x); maxY = allPoints.Max(p => p.y);
 if (minX >= maxX || minY >= maxY) return null;
 }

 double dwgW = maxX - minX;
 double dwgH = maxY - minY;
 double margin = 40;
 double rawCanvasW = dwgW + margin * 2;
 double rawCanvasH = dwgH + margin * 2;

 // ═══ 渲染缩放：将超大画布限制在 WPF 安全范围内（≤16384px） ═══
 // 大坐标CAD图（如测量/测绘图）原始尺寸可达数万单位，直接渲染会：
 //   1) 超过WPF的32767像素限制导致空白
 //   2) 笔宽在CAD单位空间下极细（fit后<0.01px）→线条不可见
 // 解决：用DrawingGroup.ScaleTransform缩小整个矢量图，同时放大笔宽补偿
 const double MAX_CANVAS_DIM = 16000.0;
 _cadRenderScale = 1.0;
 if (rawCanvasW > MAX_CANVAS_DIM || rawCanvasH > MAX_CANVAS_DIM)
     _cadRenderScale = MAX_CANVAS_DIM / Math.Max(rawCanvasW, rawCanvasH);
 double canvasW = rawCanvasW * _cadRenderScale;
 double canvasH = rawCanvasH * _cadRenderScale;

 // 外层Canvas：背景透明——背景色由CadScrollViewer提供（固定不动，无限延伸）
 // 这样平移/缩放时图形在固定背景上移动，不会露出Canvas边界
 var canvas = new Canvas
 {
 Width = canvasW,
 Height = canvasH,
 Background = Brushes.Transparent,
 };

 double offsetX = (margin - minX) * _cadRenderScale;
 double offsetY = (margin + maxY) * _cadRenderScale;

 var visual = new System.Windows.Media.DrawingVisual();
 using (var dc = visual.RenderOpen())
 {
 // 不画白色页面背景——暗色画布直接渲染实体，和 CAD viewer 一致
 // 先画非文字实体
 foreach (var ent in entities)
 {
 if (IsLayerOff(ent)) continue;
 if (ent is ACadSharp.Entities.TextEntity or ACadSharp.Entities.MText) continue;
 DrawEntityToVisual(dc, ent, offsetX, offsetY, _cadDoc, 0);
 }
 // 再画文字
 foreach (var ent in entities)
 {
 if (IsLayerOff(ent)) continue;
 if (ent is ACadSharp.Entities.TextEntity or ACadSharp.Entities.MText)
 DrawEntityToVisual(dc, ent, offsetX, offsetY, _cadDoc, 0);
 }
 }

 // 用 DrawingImage 承载矢量图形——WPF标准矢量渲染，缩放不模糊
 System.Windows.Media.Drawing drawing;
 if (_cadRenderScale < 0.999)
 {
 // 大图模式：用 DrawingGroup 缩放包裹，笔宽已在 GetEntityPen 中按 1/scale 放大
 var group = new System.Windows.Media.DrawingGroup();
 group.Transform = new System.Windows.Media.ScaleTransform(_cadRenderScale, _cadRenderScale);
 group.Children.Add(visual.Drawing);
 drawing = group;
 }
 else
 {
 drawing = visual.Drawing;
 }
 var drawingImg = new System.Windows.Media.DrawingImage(drawing);
 drawingImg.Freeze();
 var img = new Image
 {
 Source = drawingImg,
 Width = canvasW,
 Height = canvasH,
 Stretch = Stretch.None,
 };
 Canvas.SetLeft(img, 0);
 Canvas.SetTop(img, 0);
 canvas.Children.Add(img);

 return canvas;
	}

	/// 检查图层是否冻结或实体不可见
	/// 注意：不跳过"关闭"图层的实体——很多CAD软件（如杰图CAD）保存时所有图层都是关闭状态，
	/// 但AutoCAD打开时仍然渲染。只有冻结和显式不可见才跳过。
	private static bool IsLayerOff(ACadSharp.Entities.Entity ent)
	{
 try
 {
 if (ent.Layer == null) return false;
 // 冻结图层跳过
 if (ent.Layer.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen)) return true;
 // 注意：不跳过关闭的图层——杰图CAD等保存时所有图层关闭
 // 但AutoCAD打开时仍然显示，所以这里不检查 IsOn
 return ent.IsInvisible; // 实体本身不可见
 }
 catch { return false; }
	}

        public string DebugCadInfo()
        {
            if (_cadDoc == null) return "DOC_NULL";
            try
            {
                var ms = _cadDoc.ModelSpace;
                int n = ms?.Entities?.Count ?? -1;
                return $"MODELSPACE_ENTITIES={n}";
            }
            catch (Exception ex) { return $"INFO_ERR {ex.Message}"; }
        }

	/// 按绘制顺序排序实体（尝试读取SortEntitiesTable）
	private List<ACadSharp.Entities.Entity> SortEntitiesByDrawOrder(ACadSharp.CadDocument doc, List<ACadSharp.Entities.Entity> entities)
	{
 try
 {
 // 尝试从RootDictionary获取SortEntitiesTable
 var rootDict = doc.RootDictionary;
 if (rootDict == null) return entities;

 // 查找所有SortEntitiesTable
 foreach (var entry in rootDict)
 {
 if (entry is ACadSharp.Objects.SortEntitiesTable sortTable)
 {
 // 按SorterHandle排序
 var sorted = entities
 .OrderBy(e => sortTable.GetSorterHandle(e))
 .ToList();
 if (sorted.Count == entities.Count)
 return sorted;
 }
 }
 }
 catch { }
 return entities;
	}

	/// ═══ 高性能批量绘制：直接画到DrawingContext，不创建UIElement ═══
	private void DrawEntityToVisual(System.Windows.Media.DrawingContext dc, ACadSharp.Entities.Entity ent,
double offsetX, double offsetY, ACadSharp.CadDocument doc, int depth, Brush parentBlockColor = null,
ACadSharp.LineWeightType parentBlockLineWeight = ACadSharp.LineWeightType.Default)
	{
 if (depth > 10) return;
 var color = GetEntityWpfColor(ent, parentBlockColor);
 var pen = GetEntityPen(ent, parentBlockColor, parentBlockLineWeight);

		switch (ent)
		{
		case ACadSharp.Entities.Line line:
		{
			dc.DrawLine(pen,
				new Point(line.StartPoint.X + offsetX, offsetY - line.StartPoint.Y),
				new Point(line.EndPoint.X + offsetX, offsetY - line.EndPoint.Y));
			break;
		}
		case ACadSharp.Entities.Arc arc:
		{
			double cx = arc.Center.X + offsetX;
			double cy = offsetY - arc.Center.Y;
			double r = Math.Max(0.1, arc.Radius);
			double startAngle = arc.StartAngle;
			double endAngle = arc.EndAngle;
			// 完整圆
			if (Math.Abs(endAngle - startAngle) >= Math.PI * 2 - 0.001)
			{
				dc.DrawEllipse(null, pen, new Point(cx, cy), r, r);
				break;
			}
			// 弧线：用StreamGeometry
			var p1 = new Point(cx + r * Math.Cos(startAngle), cy - r * Math.Sin(startAngle));
			var p2 = new Point(cx + r * Math.Cos(endAngle), cy - r * Math.Sin(endAngle));
			double sweep = endAngle - startAngle;
			bool isLargeArc = Math.Abs(sweep) > Math.PI;
			var sweepDir = sweep > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
 var sg = new StreamGeometry();
 using (var sctx = sg.Open())
 {
 sctx.BeginFigure(p1, false, false);
 sctx.ArcTo(p2, new Size(r, r), 0, isLargeArc, sweepDir, true, false);
 }
 sg.Freeze();
 dc.DrawGeometry(null, pen, sg);
			break;
		}
		case ACadSharp.Entities.Circle circle:
		{
			double cx = circle.Center.X + offsetX;
			double cy = offsetY - circle.Center.Y;
			double r = Math.Max(0.1, circle.Radius);
			dc.DrawEllipse(null, pen, new Point(cx, cy), r, r);
			break;
		}
		case ACadSharp.Entities.Ellipse ellipse:
		{
			double cx = ellipse.Center.X + offsetX;
			double cy = offsetY - ellipse.Center.Y;
			double rx = Math.Max(0.1, ellipse.MajorAxis);
			double ry = Math.Max(0.1, ellipse.MajorAxis * ellipse.RadiusRatio);
			double rotation = ellipse.Rotation * 180.0 / Math.PI;
			if (Math.Abs(rotation) > 0.1)
			{
				dc.PushTransform(new System.Windows.Media.RotateTransform(rotation, cx, cy));
				dc.DrawEllipse(null, pen, new Point(cx, cy), rx, ry);
				dc.Pop();
			}
			else
				dc.DrawEllipse(null, pen, new Point(cx, cy), rx, ry);
			break;
		}
 case ACadSharp.Entities.LwPolyline poly:
 {
 var verts = poly.Vertices;
 if (verts == null || verts.Count < 2) break;
 var pts = new List<Point>(verts.Count);
 foreach (var v in verts)
 pts.Add(new Point(v.Location.X + offsetX, offsetY - v.Location.Y));

 // 检查是否有bulge弧段
 bool hasBulge = false;
 foreach (var v in verts)
 if (Math.Abs(v.Bulge) > 0.001) { hasBulge = true; break; }

 if (hasBulge)
 {
 // 用PathGeometry绘制含弧段的折线
 var fig = new PathFigure { StartPoint = pts[0] };
 for (int i = 1; i < pts.Count; i++)
 {
 double bulge = verts[i].Bulge;
 if (Math.Abs(bulge) > 0.001)
 {
 double dx = pts[i].X - pts[i-1].X;
 double dy = pts[i].Y - pts[i-1].Y;
 double chordLen = Math.Sqrt(dx*dx + dy*dy);
 double s = bulge * chordLen / 2;
 double r = Math.Abs((chordLen * chordLen / 4 + s * s) / (2 * s));
 if (r < 0.1) r = 0.1;
 bool isLargeArc = Math.Abs(bulge) > 1;
 var sweepDir = bulge > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
 fig.Segments.Add(new ArcSegment(pts[i], new Size(r, r), 0, isLargeArc, sweepDir, true));
 }
 else
 fig.Segments.Add(new LineSegment(pts[i], true));
 }
 if (poly.IsClosed) fig.IsClosed = true;
 var pg = new PathGeometry();
 pg.Figures.Add(fig);
 dc.DrawGeometry(null, pen, pg);
 }
 else if (poly.IsClosed && pts.Count >= 3)
 dc.DrawGeometry(null, pen, CreatePolygonGeometry(pts));
 else
 {
 for (int i = 1; i < pts.Count; i++)
 dc.DrawLine(pen, pts[i - 1], pts[i]);
 }
 break;
 }
 case ACadSharp.Entities.Polyline2D poly2d:
 {
 var verts = poly2d.Vertices;
 if (verts == null || verts.Count < 2) break;
 // 检查bulge
 bool hasBulge = false;
 if (verts is IList<ACadSharp.Entities.Vertex2D> v2dList)
 {
 foreach (var v in v2dList)
 if (Math.Abs(v.Bulge) > 0.001) { hasBulge = true; break; }
 }
 if (hasBulge)
 {
 var pts = new List<Point>(verts.Count);
 foreach (var v in verts)
 pts.Add(new Point(v.Location.X + offsetX, offsetY - v.Location.Y));
 var fig = new PathFigure { StartPoint = pts[0] };
 for (int i = 1; i < pts.Count; i++)
 {
 double bulge = 0;
 try { bulge = ((ACadSharp.Entities.Vertex2D)verts[i]).Bulge; } catch { }
 if (Math.Abs(bulge) > 0.001)
 {
 double dx = pts[i].X - pts[i-1].X;
 double dy = pts[i].Y - pts[i-1].Y;
 double chordLen = Math.Sqrt(dx*dx + dy*dy);
 double s = bulge * chordLen / 2;
 double r = Math.Abs((chordLen * chordLen / 4 + s * s) / (2 * s));
 if (r < 0.1) r = 0.1;
 bool isLargeArc = Math.Abs(bulge) > 1;
 var sweepDir = bulge > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
 fig.Segments.Add(new ArcSegment(pts[i], new Size(r, r), 0, isLargeArc, sweepDir, true));
 }
 else
 fig.Segments.Add(new LineSegment(pts[i], true));
 }
 var pg = new PathGeometry();
 pg.Figures.Add(fig);
 dc.DrawGeometry(null, pen, pg);
 }
 else
 {
 Point? prev = null;
 foreach (var v in verts)
 {
 var pt = new Point(v.Location.X + offsetX, offsetY - v.Location.Y);
 if (prev != null) dc.DrawLine(pen, prev.Value, pt);
 prev = pt;
 }
 }
 break;
 }
		case ACadSharp.Entities.Polyline3D poly3d:
		{
			var verts = poly3d.Vertices;
			if (verts == null || verts.Count < 2) break;
			Point? prev = null;
			foreach (var v in verts)
			{
				var pt = new Point(v.Location.X + offsetX, offsetY - v.Location.Y);
				if (prev != null) dc.DrawLine(pen, prev.Value, pt);
				prev = pt;
			}
			break;
		}
 case ACadSharp.Entities.Spline spline:
 {
 // 优先用控制点+节点构造B样条
 var ctrlPts = spline.ControlPoints;
 var fitPts = spline.FitPoints;

 if (ctrlPts != null && ctrlPts.Count >= 4)
 {
 // 用Bezier曲线段连接控制点（3次贝塞尔）
 var pts = new List<Point>(ctrlPts.Count);
 foreach (var p in ctrlPts)
 pts.Add(new Point(p.X + offsetX, offsetY - p.Y));
 var geom = CreateBezierGeometry(pts, spline.IsClosed);
 if (geom != null) dc.DrawGeometry(null, pen, geom);
 }
 else if (fitPts != null && fitPts.Count >= 2)
 {
 // 用Catmull-Rom插值生成平滑曲线
 var pts = new List<Point>(fitPts.Count);
 foreach (var p in fitPts)
 pts.Add(new Point(p.X + offsetX, offsetY - p.Y));
 var geom = CreateCatmullRomGeometry(pts, spline.IsClosed);
 if (geom != null) dc.DrawGeometry(null, pen, geom);
 else
 {
 // 回退：折线
 for (int i = 1; i < pts.Count; i++)
 dc.DrawLine(pen, pts[i - 1], pts[i]);
 }
 }
 break;
 }
 case ACadSharp.Entities.TableEntity table:
 {
 // CAD表格：画网格线+单元格文字（必须放在Insert之前，因为TableEntity继承自Insert）
 try
 {
 double tx = table.InsertPoint.X + offsetX;
 double ty = offsetY - table.InsertPoint.Y;
 // 画表格线
 if (table.Rows != null && table.Columns != null && table.Rows.Count > 0 && table.Columns.Count > 0)
 {
 // 水平线（行边界）
 for (int r = 0; r <= table.Rows.Count; r++)
 {
 double y = ty;
 for (int i = 0; i < r && i < table.Rows.Count; i++)
 y -= table.Rows[i].Height;
 double xStart = tx;
 double xEnd = tx;
 for (int c = 0; c < table.Columns.Count; c++)
 xEnd += table.Columns[c].Width;
 dc.DrawLine(pen, new System.Windows.Point(xStart, y), new System.Windows.Point(xEnd, y));
 }
 // 垂直线（列边界）
 for (int c = 0; c <= table.Columns.Count; c++)
 {
 double x = tx;
 for (int i = 0; i < c && i < table.Columns.Count; i++)
 x += table.Columns[i].Width;
 double yTop = ty;
 double yBottom = ty;
 for (int i = 0; i < table.Rows.Count; i++)
 yBottom -= table.Rows[i].Height;
 dc.DrawLine(pen, new System.Windows.Point(x, yTop), new System.Windows.Point(x, yBottom));
 }
 // 单元格文字
 for (int r = 0; r < table.Rows.Count; r++)
 {
 for (int c = 0; c < table.Columns.Count; c++)
 {
 var cells = table.Rows[r].Cells;
 if (c >= cells.Count) continue;
 var cell = cells[c];
 var rawVal = cell?.Content?.CadValue?.Value as string;
 if (string.IsNullOrEmpty(rawVal))
 rawVal = cell?.Content?.CadValue?.ToString();
 if (string.IsNullOrEmpty(rawVal)) continue;
 // 清理MText格式代码，提取纯文本
 var cellText = ParseMTextContent(rawVal);
 if (string.IsNullOrEmpty(cellText)) continue;
 double cellX = tx;
 for (int i = 0; i < c; i++) cellX += table.Columns[i].Width;
 double cellY = ty;
 for (int i = 0; i <= r; i++) cellY -= table.Rows[i].Height;
 double cellW = table.Columns[c].Width;
 double cellH = table.Rows[r].Height;
 double textH = 3.0;
 if (textH <= 0) textH = 3.0;
 DrawCadTextTtf(dc, cellText, textH, color,
 cellX + cellW * 0.1, cellY + cellH * 0.5, 0, 1.0, "Arial", "宋体");
 }
 }
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.Insert insert:
 {
			var block = insert.Block;
			if (block?.Entities == null) break;
			double insX = insert.InsertPoint.X + offsetX;
			double insY = offsetY - insert.InsertPoint.Y;
			double scaleX = insert.XScale == 0 ? 1 : insert.XScale;
			double scaleY = insert.YScale == 0 ? 1 : insert.YScale;
			double rotation = insert.Rotation * 180.0 / Math.PI;
 // 用push变换——始终push固定4个变换，pop也固定4个，避免数量不匹配导致 InvalidOperationException
 dc.PushTransform(new System.Windows.Media.TranslateTransform(insX, insY));
 dc.PushTransform(new System.Windows.Media.RotateTransform(rotation));
 dc.PushTransform(new System.Windows.Media.ScaleTransform(scaleX, scaleY));
 dc.PushTransform(new System.Windows.Media.TranslateTransform(-offsetX, -offsetY));
 // 获取Insert的颜色和线宽，传给子实体用于ByBlock
 Brush insertColor = color;
 var insertLW = insert.LineWeight;
 foreach (var subEnt in block.Entities)
 {
 if (IsLayerOff(subEnt)) continue;
 DrawEntityToVisual(dc, subEnt, offsetX, offsetY, doc, depth + 1, insertColor, insertLW);
 }
 dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
			break;
		}
 case ACadSharp.Entities.Hatch hatch:
 {
 // 填充渲染：实心填充或图案填充
 var fillBrush = GetEntityWpfColor(hatch);
 var hatchPaths = new List<PathGeometry>();

 // 收集所有边界路径
 if (hatch.Paths != null)
 {
 foreach (var path in hatch.Paths)
 {
 // 从path.Entities构建边界
 if (path?.Entities != null)
 {
 // 先画边界线
 foreach (var subEnt in path.Entities)
 {
 if (IsLayerOff(subEnt)) continue;
 DrawEntityToVisual(dc, subEnt, offsetX, offsetY, doc, depth + 1);
 }

 // 收集边界几何用于填充
 var pg = BuildHatchBoundaryGeometry(path, offsetX, offsetY);
 if (pg != null) hatchPaths.Add(pg);
 }
 // 从path.Edges构建（如果Entities为空但有Edges）
 else if (path?.Edges != null)
 {
 var pg = BuildHatchBoundaryFromEdges(path, offsetX, offsetY);
 if (pg != null) hatchPaths.Add(pg);
 }
 }
 }

 // 绘制填充
 try
 {
 if (hatchPaths.Count > 0)
 {
 // 合并所有边界为一个GeometryGroup
 var geomGroup = new GeometryGroup();
 foreach (var pg in hatchPaths)
 geomGroup.Children.Add(pg);
 geomGroup.FillRule = FillRule.EvenOdd; // 支持岛屿（外层填充，内层镂空）

 // 实心填充
 if (hatch.IsSolid || (hatch.Pattern != null && hatch.Pattern.Name == "SOLID"))
 {
 dc.DrawGeometry(fillBrush, null, geomGroup);
 }
 // 图案填充：用线段阵列
 else if (hatch.Pattern != null && hatch.Pattern.Lines != null && hatch.Pattern.Lines.Count > 0)
 {
 // 用Geometry裁剪绘制图案线
 var patternScale = hatch.PatternScale > 0 ? hatch.PatternScale : 1.0;
 var patternAngle = hatch.PatternAngle;

 foreach (var line in hatch.Pattern.Lines)
 {
 DrawHatchPatternLine(dc, geomGroup, line, patternScale, patternAngle,
 fillBrush, offsetX, offsetY);
 }
 }
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.DimensionLinear dim:
 {
 try
 {
 double x1 = dim.FirstPoint.X + offsetX;
 double y1 = offsetY - dim.FirstPoint.Y;
 double x2 = dim.SecondPoint.X + offsetX;
 double y2 = offsetY - dim.SecondPoint.Y;
 // TextMiddlePoint: 尺寸线位置
 double dimX = 0, dimY = 0;
 try { dimX = dim.TextMiddlePoint.X + offsetX; dimY = offsetY - dim.TextMiddlePoint.Y; } catch {}
 if (dimX == 0 && dimY == 0) { dimX = (x1+x2)/2; dimY = (y1+y2)/2; }

 // 引出线（FirstPoint→DimensionLine, SecondPoint→DimensionLine）
 dc.DrawLine(pen, new Point(x1, y1), new Point(x1, dimY));
 dc.DrawLine(pen, new Point(x2, y2), new Point(x2, dimY));
 // 尺寸线
 dc.DrawLine(pen, new Point(x1, dimY), new Point(x2, dimY));
 // 箭头
 double arrowSize = Math.Max(2, dim.Style?.TextHeight ?? 3.0);
 double lineAngle = Math.Atan2(dimY - dimY, x2 - x1);
 DrawDimensionArrow(dc, pen, color, new Point(x1, dimY), lineAngle, arrowSize);
 DrawDimensionArrow(dc, pen, color, new Point(x2, dimY), lineAngle + Math.PI, arrowSize);
 // 文字
 var dimText = GetDimensionText(dim);
 if (!string.IsNullOrEmpty(dimText))
 {
 double dimH = Math.Max(0.1, dim.Style?.TextHeight ?? 3.0);
 ACadSharp.Tables.TextStyle dimTs = null;
 try { dimTs = dim.GetType().GetProperty("TextStyle")?.GetValue(dim) as ACadSharp.Tables.TextStyle; } catch { }
 var (duf, dbf) = GetFontNamesFromStyle(dimTs);
 double midX = (x1+x2)/2;
 double textW = EstimateTextWidth(dimText, dimH);
 double dimTx = midX - textW / 2;
 double dimTy = dimY - dimH * 0.3;
 DrawCadTextTtf(dc, ConvertCadControlCodes(dimText), dimH, color, dimTx, dimTy, 0, 1.0, duf, dbf);
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.DimensionAligned dim:
 {
 try
 {
 double x1 = dim.FirstPoint.X + offsetX;
 double y1 = offsetY - dim.FirstPoint.Y;
 double x2 = dim.SecondPoint.X + offsetX;
 double y2 = offsetY - dim.SecondPoint.Y;
 // 对齐标注：尺寸线与两点连线平行
 dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
 double arrowSize = Math.Max(2, dim.Style?.TextHeight ?? 3.0);
 double angle = Math.Atan2(y2 - y1, x2 - x1);
 DrawDimensionArrow(dc, pen, color, new Point(x1, y1), angle + Math.PI, arrowSize);
 DrawDimensionArrow(dc, pen, color, new Point(x2, y2), angle, arrowSize);
 var dimText = GetDimensionText(dim);
 if (!string.IsNullOrEmpty(dimText))
 {
 double dimH = Math.Max(0.1, dim.Style?.TextHeight ?? 3.0);
 ACadSharp.Tables.TextStyle dimTs = null;
 try { dimTs = dim.GetType().GetProperty("TextStyle")?.GetValue(dim) as ACadSharp.Tables.TextStyle; } catch { }
 var (duf, dbf) = GetFontNamesFromStyle(dimTs);
 double midX = (x1+x2)/2, midY = (y1+y2)/2;
 double textW = EstimateTextWidth(dimText, dimH);
 double rotDeg = angle * 180 / Math.PI;
 DrawCadTextTtf(dc, ConvertCadControlCodes(dimText), dimH, color, midX - textW/2, midY, rotDeg, 1.0, duf, dbf);
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.DimensionRadius dimR:
 {
 try
 {
 // DimensionRadius: Center is via DefinitionPoint, AngleVertex is the point on circle
 double cx = dimR.DefinitionPoint.X + offsetX;
 double cy = offsetY - dimR.DefinitionPoint.Y;
 double dx = 0, dy = 0;
 try { dx = dimR.AngleVertex.X + offsetX; dy = offsetY - dimR.AngleVertex.Y; } catch {}
 dc.DrawLine(pen, new Point(cx, cy), new Point(dx, dy));
 double arrowSize = Math.Max(2, dimR.Style?.TextHeight ?? 3.0);
 double angle = Math.Atan2(dy - cy, dx - cx);
 DrawDimensionArrow(dc, pen, color, new Point(dx, dy), angle, arrowSize);
 var dimText = GetDimensionText(dimR);
 if (!string.IsNullOrEmpty(dimText))
 {
 double dimH = Math.Max(0.1, dimR.Style?.TextHeight ?? 3.0);
 var (duf, dbf) = GetFontNamesFromStyle(null);
 double textW = EstimateTextWidth(dimText, dimH);
 DrawCadTextTtf(dc, ConvertCadControlCodes(dimText), dimH, color, dx - textW/2, dy - dimH, 0, 1.0, duf, dbf);
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.DimensionDiameter dimD:
 {
 try
 {
 double cx = dimD.Center.X + offsetX;
 double cy = offsetY - dimD.Center.Y;
 double dx = 0, dy = 0;
 try { dx = dimD.AngleVertex.X + offsetX; dy = offsetY - dimD.AngleVertex.Y; } catch {}
 dc.DrawLine(pen, new Point(cx, cy), new Point(dx, dy));
 double arrowSize = Math.Max(2, dimD.Style?.TextHeight ?? 3.0);
 double angle = Math.Atan2(dy - cy, dx - cx);
 DrawDimensionArrow(dc, pen, color, new Point(dx, dy), angle, arrowSize);
 DrawDimensionArrow(dc, pen, color, new Point(cx, cy), angle + Math.PI, arrowSize);
 var dimText = GetDimensionText(dimD);
 if (!string.IsNullOrEmpty(dimText))
 {
 double dimH = Math.Max(0.1, dimD.Style?.TextHeight ?? 3.0);
 var (duf, dbf) = GetFontNamesFromStyle(null);
 double textW = EstimateTextWidth(dimText, dimH);
 DrawCadTextTtf(dc, ConvertCadControlCodes(dimText), dimH, color, dx - textW/2, dy - dimH, 0, 1.0, duf, dbf);
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.DimensionAngular2Line dimA:
 {
 try
 {
 double x1 = dimA.FirstPoint.X + offsetX;
 double y1 = offsetY - dimA.FirstPoint.Y;
 double x2 = dimA.SecondPoint.X + offsetX;
 double y2 = offsetY - dimA.SecondPoint.Y;
 double cx = dimA.Center.X + offsetX;
 double cy = offsetY - dimA.Center.Y;
 dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
 var dimText = GetDimensionText(dimA);
 if (!string.IsNullOrEmpty(dimText))
 {
 double dimH = Math.Max(0.1, dimA.Style?.TextHeight ?? 3.0);
 var (duf, dbf) = GetFontNamesFromStyle(null);
 double midX = (x1+x2)/2, midY = (y1+y2)/2;
 double textW = EstimateTextWidth(dimText, dimH);
 DrawCadTextTtf(dc, ConvertCadControlCodes(dimText), dimH, color, midX - textW/2, midY, 0, 1.0, duf, dbf);
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.DimensionAngular3Pt dimA3:
 {
 try
 {
 double x1 = dimA3.FirstPoint.X + offsetX;
 double y1 = offsetY - dimA3.FirstPoint.Y;
 double x2 = dimA3.SecondPoint.X + offsetX;
 double y2 = offsetY - dimA3.SecondPoint.Y;
 double cx = dimA3.AngleVertex.X + offsetX;
 double cy = offsetY - dimA3.AngleVertex.Y;
 dc.DrawLine(pen, new Point(cx, cy), new Point(x1, y1));
 dc.DrawLine(pen, new Point(cx, cy), new Point(x2, y2));
 var dimText = GetDimensionText(dimA3);
 if (!string.IsNullOrEmpty(dimText))
 {
 double dimH = Math.Max(0.1, dimA3.Style?.TextHeight ?? 3.0);
 var (duf, dbf) = GetFontNamesFromStyle(null);
 double midX = (x1+x2)/2, midY = (y1+y2)/2;
 double textW = EstimateTextWidth(dimText, dimH);
 DrawCadTextTtf(dc, ConvertCadControlCodes(dimText), dimH, color, midX - textW/2, midY, 0, 1.0, duf, dbf);
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.Leader leader:
 {
 try
 {
 var verts = leader.Vertices;
 if (verts != null && verts.Count >= 2)
 {
 var pts = new List<Point>(verts.Count);
 foreach (var v in verts)
 pts.Add(new Point(v.X + offsetX, offsetY - v.Y));
 // 画引线
 for (int i = 1; i < pts.Count; i++)
 dc.DrawLine(pen, pts[i-1], pts[i]);
 // 箭头在起点
 double arrowSize = Math.Max(2, leader.TextHeight > 0 ? leader.TextHeight : 3.0);
 double angle = Math.Atan2(pts[1].Y - pts[0].Y, pts[1].X - pts[0].X);
 DrawDimensionArrow(dc, pen, color, pts[0], angle, arrowSize);
 // Leader没有直接Text属性，文字来自AssociatedAnnotation
 try
 {
 var ann = leader.AssociatedAnnotation;
 if (ann is ACadSharp.Entities.TextEntity te && !string.IsNullOrEmpty(te.Value))
 {
 double dimH = Math.Max(0.1, te.Height);
 var (duf, dbf) = GetFontNamesFromStyle(te.Style);
 var lastPt = pts[pts.Count - 1];
 DrawCadTextTtf(dc, ConvertCadControlCodes(te.Value), dimH, color, lastPt.X, lastPt.Y, 0, 1.0, duf, dbf);
 }
 else if (ann is ACadSharp.Entities.MText mt && !string.IsNullOrEmpty(mt.PlainText))
 {
 double dimH = Math.Max(0.1, mt.Height);
 var (duf, dbf) = GetFontNamesFromStyle(mt.Style);
 var lastPt = pts[pts.Count - 1];
 DrawCadTextTtf(dc, ParseMTextContent(mt.Value ?? mt.PlainText), dimH, color, lastPt.X, lastPt.Y, 0, 1.0, duf, dbf);
 }
 }
 catch { }
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.MultiLeader mleader:
 {
 try
 {
 // MultiLeader: 画引线段
 // MultiLeader API较复杂，用反射安全获取
 var vertsProp = mleader.GetType().GetProperty("Vertices");
 if (vertsProp != null)
 {
 var vertsObj = vertsProp.GetValue(mleader) as System.Collections.IList;
 if (vertsObj != null && vertsObj.Count >= 2)
 {
 var pts = new List<Point>();
 foreach (var v in vertsObj)
 {
 var vx = (double)v.GetType().GetProperty("X")?.GetValue(v);
 var vy = (double)v.GetType().GetProperty("Y")?.GetValue(v);
 pts.Add(new Point(vx + offsetX, offsetY - vy));
 }
 for (int i = 1; i < pts.Count; i++)
 dc.DrawLine(pen, pts[i-1], pts[i]);
 if (pts.Count >= 2)
 {
 double arrowSize = 3.0;
 double angle = Math.Atan2(pts[1].Y - pts[0].Y, pts[1].X - pts[0].X);
 DrawDimensionArrow(dc, pen, color, pts[0], angle, arrowSize);
 }
 }
 }
 // MultiLeader文字
 try
 {
 var textProp = mleader.GetType().GetProperty("Text")?.GetValue(mleader) as string;
 if (!string.IsNullOrEmpty(textProp))
 {
 double h = 3.0;
 var hProp = mleader.GetType().GetProperty("TextHeight");
 if (hProp != null) { var hv = hProp.GetValue(mleader); if (hv != null) h = Convert.ToDouble(hv); }
 var (uf, bf) = GetFontNamesFromStyle(null);
 var lastVert = (vertsProp?.GetValue(mleader) as System.Collections.IList);
 if (lastVert != null && lastVert.Count > 0)
 {
 var last = lastVert[lastVert.Count - 1];
 double tx = (double)last.GetType().GetProperty("X")?.GetValue(last) + offsetX;
 double ty = offsetY - (double)last.GetType().GetProperty("Y")?.GetValue(last);
 DrawCadTextTtf(dc, ConvertCadControlCodes(textProp), h, color, tx, ty, 0, 1.0, uf, bf);
 }
 }
 }
 catch { }
 }
 catch { }
 break;
 }
		case ACadSharp.Entities.Point point:
		{
			double px = point.Location.X + offsetX;
			double py = offsetY - point.Location.Y;
			dc.DrawEllipse(color, null, new Point(px, py), 1.5, 1.5);
			break;
		}
 case ACadSharp.Entities.TextEntity text:
 {
 // AttributeDefinition 在模型空间/布局空间中不显示（只在块定义内部有效）
 // AttributeEntity（块属性引用）正常显示
 if (text is ACadSharp.Entities.AttributeDefinition) break;

 // AttributeEntity 继承自 TextEntity，会匹配此处
 // 块属性文字优先显示Tag
 string displayText = text.Value ?? "";
 try
 {
 if (text is ACadSharp.Entities.AttributeEntity attr)
 displayText = attr.Tag ?? attr.Value ?? "";
 }
 catch { }

 double tx = text.InsertPoint.X + offsetX;
 double ty = offsetY - text.InsertPoint.Y; // 屏幕Y = 基线位置
 double h = Math.Max(0.1, text.Height);
 var rotation = (text.Rotation * 180.0 / Math.PI) % 360.0;
 double widthFactor = text.WidthFactor > 0 ? text.WidthFactor : 1.0;
 var textStr = ConvertCadControlCodes(displayText);
 if (!string.IsNullOrEmpty(textStr))
 {
 var (uf, bf) = GetFontNamesFromStyle(text.Style);

 // ═══ 处理文字对齐方式（Justification）═══
 // HorizontalAlignment: Left(默认), Center, Right, Aligned, Middle, Fit
 // VerticalAlignment: Baseline(默认), Bottom, Middle, Top
 // 当对齐方式不是Left/Baseline时，AutoCAD使用AlignmentPoint作为第二对齐点
 try
 {
 var ha = text.HorizontalAlignment;
 var va = text.VerticalAlignment;
 bool hasAlign = (ha != ACadSharp.Entities.TextHorizontalAlignment.Left && ha != 0) ||
 (va != ACadSharp.Entities.TextVerticalAlignmentType.Baseline && va != 0);
 if (hasAlign)
 {
 // 使用AlignmentPoint作为对齐参考点
 var ap = text.AlignmentPoint;
 double ax = ap.X + offsetX;
 double ay = offsetY - ap.Y;
 // 估算文字宽度
 double textW = EstimateTextWidth(textStr, h);

 // 水平对齐调整
 double adjX = 0;
 if (ha == ACadSharp.Entities.TextHorizontalAlignment.Center ||
 ha == ACadSharp.Entities.TextHorizontalAlignment.Middle)
 adjX = -textW / 2;
 else if (ha == ACadSharp.Entities.TextHorizontalAlignment.Right)
 adjX = -textW;
 // Aligned/Fit: 文字两端对齐到InsertPoint和AlignmentPoint，用AlignmentPoint做右端

 // 垂直对齐调整
 double adjY = 0;
 if (va == ACadSharp.Entities.TextVerticalAlignmentType.Middle)
 adjY = -h / 2;
 else if (va == ACadSharp.Entities.TextVerticalAlignmentType.Top)
 adjY = -h;
 else if (va == ACadSharp.Entities.TextVerticalAlignmentType.Bottom)
 adjY = 0; // Bottom ≈ Baseline

 // 如果水平对齐是Aligned或Fit，使用两端点之间的中点
 if (ha == ACadSharp.Entities.TextHorizontalAlignment.Aligned ||
 ha == ACadSharp.Entities.TextHorizontalAlignment.Fit)
 {
 tx = (tx + ax) / 2 - textW / 2;
 ty = (ty + ay) / 2;
 // Aligned: 文字旋转角度跟随两点连线
 if (ha == ACadSharp.Entities.TextHorizontalAlignment.Aligned)
 rotation = Math.Atan2(ay - ty, ax - tx) * 180.0 / Math.PI;
 }
 else
 {
 tx = ax + adjX;
 ty = ay + adjY;
 }
 }
 }
 catch { }

 // AutoCAD: InsertPoint是基线左点（Left/Baseline对齐时）
 // FormattedText.DrawText从左上角画，所以y要减h
 // WidthFactor通过ScaleTransform(wf, 1)实现，旋转以插入点为原点
 DrawCadTextTtf(dc, textStr, h, color, tx, ty, rotation, widthFactor, uf, bf);
 }
 break;
 }
 case ACadSharp.Entities.MText mtext:
 {
 double tx = mtext.InsertPoint.X + offsetX;
 double ty = offsetY - mtext.InsertPoint.Y; // 基线位置
 double h = Math.Max(0.1, mtext.Height);
 var rotation = (mtext.Rotation * 180.0 / Math.PI) % 360.0;
 double widthFactor = 1.0; // MText默认不缩放
 var mtextText = ParseMTextContent(mtext.Value ?? mtext.PlainText ?? "");
 if (!string.IsNullOrEmpty(mtextText))
 {
 var (uf, bf) = GetFontNamesFromStyle(mtext.Style);
 // 获取 MText 的参考宽度（CAD中文字框宽度），用于自动换行
 double refWidth = 0;
 try
 {
 refWidth = mtext.RectangleWidth;
 if (refWidth <= 0) refWidth = mtext.HorizontalWidth;
 }
 catch { }
 // 如果仍然为0，不自动换行（单行）
 bool doWrap = refWidth > h * 0.5;

 // 先按 \P（段落换行）拆分
 var paragraphs = mtextText.Split('\n');
 double lineY = ty; // 基线位置
 foreach (var para in paragraphs)
 {
 var lineText = para.TrimEnd();
 if (string.IsNullOrEmpty(lineText)) { lineY -= h * _cadLineFactor; continue; }

 if (doWrap)
 {
 var wrappedLines = WrapCadTextByWidth(lineText, refWidth, h);
 foreach (var wl in wrappedLines)
 {
 DrawCadTextTtf(dc, wl, h, color, tx, lineY, rotation, widthFactor, uf, bf);
 lineY -= h * _cadLineFactor;
 }
 }
 else
 {
 DrawCadTextTtf(dc, lineText, h, color, tx, lineY, rotation, widthFactor, uf, bf);
 lineY -= h * _cadLineFactor;
 }
 }
 }
 break;
 }
 // ═══ 新增实体类型 ═══
 case ACadSharp.Entities.Ray ray:
 {
 // 射线：从StartPoint沿Direction画一条很长的线
 var sp = new System.Windows.Point(ray.StartPoint.X + offsetX, offsetY - ray.StartPoint.Y);
 var dir = ray.Direction;
 // 截断到一个很大的范围（100000单位）
 double len = 100000;
 var ep = new System.Windows.Point(sp.X + dir.X * len, sp.Y - dir.Y * len);
 dc.DrawLine(pen, sp, ep);
 break;
 }
 case ACadSharp.Entities.XLine xline:
 {
 // 构造线：双向无限线
 var fp = new System.Windows.Point(xline.FirstPoint.X + offsetX, offsetY - xline.FirstPoint.Y);
 var dir = xline.Direction;
 double len = 100000;
 var ep1 = new System.Windows.Point(fp.X + dir.X * len, fp.Y - dir.Y * len);
 var ep2 = new System.Windows.Point(fp.X - dir.X * len, fp.Y + dir.Y * len);
 dc.DrawLine(pen, ep1, ep2);
 break;
 }
 case ACadSharp.Entities.Solid solid:
 {
 // 实心填充四边形（或三角形）
 var p1 = new System.Windows.Point(solid.FirstCorner.X + offsetX, offsetY - solid.FirstCorner.Y);
 var p2 = new System.Windows.Point(solid.SecondCorner.X + offsetX, offsetY - solid.SecondCorner.Y);
 var p3 = new System.Windows.Point(solid.ThirdCorner.X + offsetX, offsetY - solid.ThirdCorner.Y);
 var p4 = new System.Windows.Point(solid.FourthCorner.X + offsetX, offsetY - solid.FourthCorner.Y);
 // 检查是否有第四点（如果FourthCorner为零则画三角形）
 if (solid.FourthCorner.X == 0 && solid.FourthCorner.Y == 0)
 {
 var geo = new StreamGeometry();
 using (var ctx = geo.Open())
 {
 ctx.BeginFigure(p1, true, true);
 ctx.LineTo(p2, true, false);
 ctx.LineTo(p3, true, false);
 }
 geo.Freeze();
 dc.DrawGeometry(color, null, geo);
 }
 else
 {
 var geo = new StreamGeometry();
 using (var ctx = geo.Open())
 {
 ctx.BeginFigure(p1, true, true);
 ctx.LineTo(p2, true, false);
 ctx.LineTo(p3, true, false);
 ctx.LineTo(p4, true, false);
 }
 geo.Freeze();
 dc.DrawGeometry(color, null, geo);
 }
 break;
 }
 case ACadSharp.Entities.Face3D face:
 {
 // 3D面：3或4个顶点填充
 var p1 = new System.Windows.Point(face.FirstCorner.X + offsetX, offsetY - face.FirstCorner.Y);
 var p2 = new System.Windows.Point(face.SecondCorner.X + offsetX, offsetY - face.SecondCorner.Y);
 var p3 = new System.Windows.Point(face.ThirdCorner.X + offsetX, offsetY - face.ThirdCorner.Y);
 var geo = new StreamGeometry();
 using (var ctx = geo.Open())
 {
 ctx.BeginFigure(p1, true, true);
 ctx.LineTo(p2, true, false);
 ctx.LineTo(p3, true, false);
 try
 {
 var p4v = face.FourthCorner;
 if (p4v.X != 0 || p4v.Y != 0)
 ctx.LineTo(new System.Windows.Point(p4v.X + offsetX, offsetY - p4v.Y), true, false);
 }
 catch { }
 }
 geo.Freeze();
 dc.DrawGeometry(color, null, geo);
 break;
 }
 case ACadSharp.Entities.MLine mline:
 {
 // 多线（MLine）：读取Vertices画连续线
 try
 {
 var verts = mline.Vertices;
 if (verts != null && verts.Count > 0)
 {
 var pts = new List<System.Windows.Point>();
 foreach (var v in verts)
 pts.Add(new System.Windows.Point(v.Position.X + offsetX, offsetY - v.Position.Y));
 if (pts.Count >= 2)
 {
 var geo = new StreamGeometry();
 using (var ctx = geo.Open())
 {
 ctx.BeginFigure(pts[0], false, false);
 for (int i = 1; i < pts.Count; i++)
 ctx.LineTo(pts[i], true, false);
 }
 geo.Freeze();
 dc.DrawGeometry(null, pen, geo);
 }
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.Wipeout wipeout:
 {
 // Wipeout：用背景色填充遮罩区域
 try
 {
 var clip = wipeout.ClipBoundaryVertices;
 if (clip != null && clip.Count > 0)
 {
 var pts = new List<System.Windows.Point>();
 foreach (var p in clip)
 pts.Add(new System.Windows.Point(p.X + offsetX, offsetY - p.Y));
 if (pts.Count >= 3)
 {
 var geo = new StreamGeometry();
 using (var ctx = geo.Open())
 {
 ctx.BeginFigure(pts[0], true, true);
 for (int i = 1; i < pts.Count; i++)
 ctx.LineTo(pts[i], true, false);
 }
 geo.Freeze();
 dc.DrawGeometry(new SolidColorBrush(CadBgColor), null, geo);
 }
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.DimensionOrdinate dimOrd:
 {
 // 坐标标注：引出线+文字
 try
 {
 var feature = dimOrd.FeatureLocation;
 var defPoint = dimOrd.DefinitionPoint;
 var leaderPt = dimOrd.LeaderEndpoint;
 var op1 = new System.Windows.Point(feature.X + offsetX, offsetY - feature.Y);
 var op2 = new System.Windows.Point(defPoint.X + offsetX, offsetY - defPoint.Y);
 var op3 = new System.Windows.Point(leaderPt.X + offsetX, offsetY - leaderPt.Y);
 dc.DrawLine(pen, op1, op2);
 dc.DrawLine(pen, op2, op3);
 // 标注文字
 var dimText = GetDimensionText(dimOrd);
 if (!string.IsNullOrEmpty(dimText))
 {
 double textH = 2.5;
 try { textH = dimOrd.Style?.TextHeight ?? 2.5; } catch { }
 if (textH <= 0) textH = 2.5;
 DrawCadTextTtf(dc, dimText, textH, color, op3.X, op3.Y, 0, 1.0, "Arial", "宋体");
 }
 }
 catch { }
 break;
 }
 }
 }

 // ═══ 标注文字自动计算（当dim.Text为空时） ═══
 private static string GetDimensionText(ACadSharp.Entities.Dimension dim)
 {
 // 如果有显式文字，直接返回
 if (!string.IsNullOrEmpty(dim.Text)) return dim.Text;
 try
 {
 // 尝试读取标注样式的小数位数
 int dec = 2;
 try { dec = dim.Style?.DecimalPlaces ?? 2; } catch { }
 string fmt = $"F{Math.Max(0, Math.Min(8, dec))}";

 switch (dim)
 {
 case ACadSharp.Entities.DimensionLinear dl:
 {
 // 线性标注：测量两点在尺寸线方向上的投影距离
 double dx = dl.SecondPoint.X - dl.FirstPoint.X;
 double dy = dl.SecondPoint.Y - dl.FirstPoint.Y;
 double dist = Math.Sqrt(dx * dx + dy * dy);
 return dist.ToString(fmt);
 }
 case ACadSharp.Entities.DimensionAligned da:
 {
 double dx = da.SecondPoint.X - da.FirstPoint.X;
 double dy = da.SecondPoint.Y - da.FirstPoint.Y;
 double dist = Math.Sqrt(dx * dx + dy * dy);
 return dist.ToString(fmt);
 }
 case ACadSharp.Entities.DimensionRadius dr:
 {
 double r = Math.Max(0, dr.LeaderLength);
 // 如果LeaderLength不靠谱，尝试用DefinitionPoint到AngleVertex的距离
 try
 {
 double ddx = dr.AngleVertex.X - dr.DefinitionPoint.X;
 double ddy = dr.AngleVertex.Y - dr.DefinitionPoint.Y;
 double calc = Math.Sqrt(ddx * ddx + ddy * ddy);
 if (calc > 0.1) r = calc;
 } catch { }
 if (r <= 0.1) r = 1;
 return "R" + r.ToString(fmt);
 }
 case ACadSharp.Entities.DimensionDiameter dd:
 {
 double r = 0;
 try
 {
 double ddx = dd.AngleVertex.X - dd.Center.X;
 double ddy = dd.AngleVertex.Y - dd.Center.Y;
 r = Math.Sqrt(ddx * ddx + ddy * ddy);
 } catch { }
 if (r <= 0.1) r = 1;
 return "Ø" + (r * 2).ToString(fmt);
 }
 case ACadSharp.Entities.DimensionAngular2Line da2:
 {
 // 两线夹角：线1=FirstPoint→SecondPoint, 线2=Center→AngleVertex
 double a1 = Math.Atan2(da2.SecondPoint.Y - da2.FirstPoint.Y, da2.SecondPoint.X - da2.FirstPoint.X);
 double a2 = Math.Atan2(da2.AngleVertex.Y - da2.Center.Y, da2.AngleVertex.X - da2.Center.X);
 double angle = Math.Abs((a2 - a1) * 180.0 / Math.PI);
 if (angle > 180) angle = 360 - angle;
 return angle.ToString(fmt) + "°";
 }
 case ACadSharp.Entities.DimensionAngular3Pt da3:
 {
 double a1 = Math.Atan2(da3.FirstPoint.Y - da3.AngleVertex.Y, da3.FirstPoint.X - da3.AngleVertex.X);
 double a2 = Math.Atan2(da3.SecondPoint.Y - da3.AngleVertex.Y, da3.SecondPoint.X - da3.AngleVertex.X);
 double angle = Math.Abs((a2 - a1) * 180.0 / Math.PI);
 if (angle > 180) angle = 360 - angle;
 return angle.ToString(fmt) + "°";
 }
 case ACadSharp.Entities.DimensionOrdinate dor:
 {
 // 坐标标注：显示FeatureLocation的X或Y坐标
 // 根据引线方向判断是X还是Y坐标
 double ddx = dor.LeaderEndpoint.X - dor.FeatureLocation.X;
 double ddy = dor.LeaderEndpoint.Y - dor.FeatureLocation.Y;
 if (Math.Abs(ddx) > Math.Abs(ddy))
 return "X=" + dor.FeatureLocation.X.ToString(fmt);
 else
 return "Y=" + dor.FeatureLocation.Y.ToString(fmt);
 }
 }
 }
 catch { }
 return "";
 }

 // ═══ Hatch填充辅助方法 ═══

 /// 从Hatch.BoundaryPath.Entities构建闭合路径几何
 private PathGeometry BuildHatchBoundaryGeometry(ACadSharp.Entities.Hatch.BoundaryPath path, double offsetX, double offsetY)
 {
 try
 {
 var figure = new PathFigure();
 bool started = false;
 Point firstPt = new Point();

 foreach (var ent in path.Entities)
 {
 switch (ent)
 {
 case ACadSharp.Entities.Line line:
 {
 var p1 = new Point(line.StartPoint.X + offsetX, offsetY - line.StartPoint.Y);
 var p2 = new Point(line.EndPoint.X + offsetX, offsetY - line.EndPoint.Y);
 if (!started) { figure.StartPoint = p1; started = true; firstPt = p1; }
 else if (!ArePointsClose(figure.StartPoint, p1, 0.1))
 // 尝试连接
 figure.Segments.Add(new LineSegment(p1, true));
 figure.Segments.Add(new LineSegment(p2, true));
 break;
 }
 case ACadSharp.Entities.Arc arc:
 {
 double cx = arc.Center.X + offsetX;
 double cy = offsetY - arc.Center.Y;
 double r = Math.Max(0.1, arc.Radius);
 double startAngle = arc.StartAngle;
 double endAngle = arc.EndAngle;
 var p1 = new Point(cx + r * Math.Cos(startAngle), cy - r * Math.Sin(startAngle));
 var p2 = new Point(cx + r * Math.Cos(endAngle), cy - r * Math.Sin(endAngle));
 if (!started) { figure.StartPoint = p1; started = true; firstPt = p1; }
 double sweep = endAngle - startAngle;
 bool isLargeArc = Math.Abs(sweep) > Math.PI;
 var sweepDir = sweep > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
 figure.Segments.Add(new ArcSegment(p2, new Size(r, r), 0, isLargeArc, sweepDir, true));
 break;
 }
 case ACadSharp.Entities.Circle circle:
 {
 double cx = circle.Center.X + offsetX;
 double cy = offsetY - circle.Center.Y;
 double r = Math.Max(0.1, circle.Radius);
 var p1 = new Point(cx + r, cy);
 if (!started) { figure.StartPoint = p1; started = true; firstPt = p1; }
 figure.Segments.Add(new ArcSegment(new Point(cx + r, cy + 0.01), new Size(r, r), 0, true, SweepDirection.Counterclockwise, true));
 break;
 }
 case ACadSharp.Entities.Ellipse ellipse:
 {
 double cx = ellipse.Center.X + offsetX;
 double cy = offsetY - ellipse.Center.Y;
 double rx = Math.Max(0.1, ellipse.MajorAxis);
 double ry = Math.Max(0.1, ellipse.MajorAxis * ellipse.RadiusRatio);
 var p1 = new Point(cx + rx, cy);
 if (!started) { figure.StartPoint = p1; started = true; firstPt = p1; }
 figure.Segments.Add(new ArcSegment(new Point(cx + rx, cy + 0.01), new Size(rx, ry), ellipse.Rotation * 180 / Math.PI, true, SweepDirection.Counterclockwise, true));
 break;
 }
 case ACadSharp.Entities.LwPolyline poly:
 {
 var verts = poly.Vertices;
 if (verts == null || verts.Count == 0) break;
 var p0 = new Point(verts[0].Location.X + offsetX, offsetY - verts[0].Location.Y);
 if (!started) { figure.StartPoint = p0; started = true; firstPt = p0; }
 else figure.Segments.Add(new LineSegment(p0, true));
 for (int i = 1; i < verts.Count; i++)
 {
 var v = verts[i];
 var pt = new Point(v.Location.X + offsetX, offsetY - v.Location.Y);
 // bulge弧段
 if (Math.Abs(v.Bulge) > 0.001)
 {
 double bulge = v.Bulge;
 var prevPt = new Point(verts[i-1].Location.X + offsetX, offsetY - verts[i-1].Location.Y);
 double dx = pt.X - prevPt.X, dy = pt.Y - prevPt.Y;
 double chordLen = Math.Sqrt(dx*dx + dy*dy);
 double sagitta = bulge * chordLen / 2;
 double r = (chordLen * chordLen / 4 + sagitta * sagitta) / (2 * Math.Abs(sagitta));
 bool isLargeArc = Math.Abs(bulge) > 1;
 var sweepDir = bulge > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
 figure.Segments.Add(new ArcSegment(pt, new Size(r, r), 0, isLargeArc, sweepDir, true));
 }
 else
 {
 figure.Segments.Add(new LineSegment(pt, true));
 }
 }
 if (poly.IsClosed) figure.IsClosed = true;
 break;
 }
 case ACadSharp.Entities.Polyline2D poly2d:
 {
 var verts = poly2d.Vertices;
 if (verts == null || verts.Count == 0) break;
 var p0 = new Point(verts[0].Location.X + offsetX, offsetY - verts[0].Location.Y);
 if (!started) { figure.StartPoint = p0; started = true; firstPt = p0; }
 else figure.Segments.Add(new LineSegment(p0, true));
 for (int i = 1; i < verts.Count; i++)
 figure.Segments.Add(new LineSegment(
 new Point(verts[i].Location.X + offsetX, offsetY - verts[i].Location.Y), true));
 if (poly2d.IsClosed) figure.IsClosed = true;
 break;
 }
 case ACadSharp.Entities.Spline spline:
 {
 var fitPts = spline.FitPoints;
 if (fitPts == null || fitPts.Count == 0) break;
 var p0 = new Point(fitPts[0].X + offsetX, offsetY - fitPts[0].Y);
 if (!started) { figure.StartPoint = p0; started = true; firstPt = p0; }
 else figure.Segments.Add(new LineSegment(p0, true));
 for (int i = 1; i < fitPts.Count; i++)
 figure.Segments.Add(new LineSegment(
 new Point(fitPts[i].X + offsetX, offsetY - fitPts[i].Y), true));
 figure.IsClosed = spline.IsClosed;
 break;
 }
 }
 }

 if (!started) return null;
 figure.IsClosed = true;
 var pg = new PathGeometry();
 pg.Figures.Add(figure);
 pg.FillRule = FillRule.EvenOdd;
 return pg;
 }
 catch { return null; }
 }

 /// 从Hatch.BoundaryPath.Edges构建闭合路径几何
 private PathGeometry BuildHatchBoundaryFromEdges(ACadSharp.Entities.Hatch.BoundaryPath path, double offsetX, double offsetY)
 {
 try
 {
 var figure = new PathFigure();
 bool started = false;

 if (path.Edges == null) return null;
 foreach (var edge in path.Edges)
 {
 // 用类型匹配而非EdgeType枚举（EdgeType只有Polyline，其他通过子类类型判断）
 var edgeEnt = edge.ToEntity();
 switch (edgeEnt)
 {
 case ACadSharp.Entities.Line le:
 {
 var p1 = new Point(le.StartPoint.X + offsetX, offsetY - le.StartPoint.Y);
 var p2 = new Point(le.EndPoint.X + offsetX, offsetY - le.EndPoint.Y);
 if (!started) { figure.StartPoint = p1; started = true; }
 figure.Segments.Add(new LineSegment(p2, true));
 break;
 }
 case ACadSharp.Entities.Arc ae:
 {
 double cx = ae.Center.X + offsetX;
 double cy = offsetY - ae.Center.Y;
 double r = Math.Max(0.1, ae.Radius);
 var p1 = new Point(cx + r * Math.Cos(ae.StartAngle), cy - r * Math.Sin(ae.StartAngle));
 var p2 = new Point(cx + r * Math.Cos(ae.EndAngle), cy - r * Math.Sin(ae.EndAngle));
 if (!started) { figure.StartPoint = p1; started = true; }
 double sweep = ae.EndAngle - ae.StartAngle;
 bool isLargeArc = Math.Abs(sweep) > Math.PI;
 var sweepDir = sweep > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
 figure.Segments.Add(new ArcSegment(p2, new Size(r, r), 0, isLargeArc, sweepDir, true));
 break;
 }
 case ACadSharp.Entities.Ellipse ee:
 {
 double cx = ee.Center.X + offsetX;
 double cy = offsetY - ee.Center.Y;
 double rx = Math.Max(0.1, ee.MajorAxis);
 double ry = Math.Max(0.1, ee.MajorAxis * ee.RadiusRatio);
 var p1 = new Point(cx + rx * Math.Cos(ee.StartParameter), cy - ry * Math.Sin(ee.StartParameter));
 var p2 = new Point(cx + rx * Math.Cos(ee.EndParameter), cy - ry * Math.Sin(ee.EndParameter));
 if (!started) { figure.StartPoint = p1; started = true; }
 double sweep = ee.EndParameter - ee.StartParameter;
 bool isLargeArc = Math.Abs(sweep) > Math.PI;
 var sweepDir = sweep > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;
 figure.Segments.Add(new ArcSegment(p2, new Size(rx, ry), ee.Rotation * 180 / Math.PI, isLargeArc, sweepDir, true));
 break;
 }
 case ACadSharp.Entities.Spline se:
 {
 var fitPts = se.FitPoints;
 if (fitPts == null || fitPts.Count == 0) break;
 var p0 = new Point(fitPts[0].X + offsetX, offsetY - fitPts[0].Y);
 if (!started) { figure.StartPoint = p0; started = true; }
 for (int i = 1; i < fitPts.Count; i++)
 figure.Segments.Add(new LineSegment(
 new Point(fitPts[i].X + offsetX, offsetY - fitPts[i].Y), true));
 break;
 }
 }
 }

 if (!started) return null;
 figure.IsClosed = true;
 var pg = new PathGeometry();
 pg.Figures.Add(figure);
 pg.FillRule = FillRule.EvenOdd;
 return pg;
 }
 catch { return null; }
 }

 /// 绘制Hatch图案填充的一条线（用Geometry裁剪）
 private void DrawHatchPatternLine(System.Windows.Media.DrawingContext dc, Geometry clipGeom,
 ACadSharp.Entities.HatchPattern.Line patLine, double scale, double patternAngle,
 Brush brush, double offsetX, double offsetY)
 {
 try
 {
 double angle = patLine.Angle + patternAngle;
 double angleRad = angle * Math.PI / 180.0;
 double cosA = Math.Cos(angleRad), sinA = Math.Sin(angleRad);

 // 基点
 double bx = patLine.BasePoint.X + offsetX;
 double by = offsetY - patLine.BasePoint.Y;

 // 线间距（垂直于线方向的偏移）
 double spacing = patLine.LineOffset * scale;
 if (spacing <= 0) spacing = 5;

 // 获取裁剪区域的包围盒
 var bounds = clipGeom.Bounds;
 if (bounds.Width <= 0 || bounds.Height <= 0) return;

 // 扩展包围盒以确保覆盖
 double margin = Math.Max(bounds.Width, bounds.Height) * 2;
 double minX = bounds.X - margin, maxX = bounds.Right + margin;
 double minY = bounds.Y - margin, maxY = bounds.Bottom + margin;

 // 沿垂直于线方向生成多条平行线
 double perpDist = Math.Sqrt((maxX-minX)*(maxX-minX) + (maxY-minY)*(maxY-minY));
 int lineCount = (int)(perpDist / spacing) + 4;

 // dash pattern
 var dashLengths = patLine.DashLengths;
 double[] dashes = null;
 if (dashLengths != null && dashLengths.Count > 0)
 {
 dashes = new double[dashLengths.Count];
 for (int i = 0; i < dashLengths.Count; i++)
 dashes[i] = dashLengths[i] * scale;
 }

 var pen = new System.Windows.Media.Pen(brush, 0.5);
 if (dashes != null)
 {
 var dc2 = new DoubleCollection();
 foreach (var d in dashes)
 dc2.Add(Math.Abs(d));
 pen.DashStyle = new DashStyle(dc2, 0);
 }

 // 生成平行线
 double perpX = -sinA, perpY = cosA;
 double lineX = cosA, lineY = sinA;

 for (int i = -lineCount/2; i <= lineCount/2; i++)
 {
 double offsetDist = i * spacing;
 // 线上一点
 double cx = bx + perpX * offsetDist;
 double cy = by + perpY * offsetDist;
 // 线的两端点（足够长以覆盖包围盒）
 double len = perpDist;
 var p1 = new Point(cx - lineX * len, cy - lineY * len);
 var p2 = new Point(cx + lineX * len, cy + lineY * len);

 // 用裁剪绘制
 dc.PushClip(clipGeom);
 dc.DrawLine(pen, p1, p2);
 dc.Pop();
 }
 }
 catch { }
 }

 private static bool ArePointsClose(Point a, Point b, double tol)
 {
 return Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol;
 }

 /// 绘制标注箭头（三角形）
 private static void DrawDimensionArrow(System.Windows.Media.DrawingContext dc,
 System.Windows.Media.Pen pen, Brush fillBrush, Point tip, double angle, double size)
 {
 try
 {
 double cosA = Math.Cos(angle), sinA = Math.Sin(angle);
 // 箭头三角形：tip → tip-size → tip-size（偏移）
 var p1 = tip;
 var p2 = new Point(tip.X - size * cosA + size * 0.3 * sinA,
   tip.Y - size * sinA - size * 0.3 * cosA);
 var p3 = new Point(tip.X - size * cosA - size * 0.3 * sinA,
   tip.Y - size * sinA + size * 0.3 * cosA);
 var geom = new StreamGeometry();
 using (var ctx = geom.Open())
 {
 ctx.BeginFigure(p1, true, true);
 ctx.LineTo(p2, true, false);
 ctx.LineTo(p3, true, false);
 }
 geom.Freeze();
 dc.DrawGeometry(fillBrush, pen, geom);
 }
 catch { }
 }


 private double EstimateTextWidth(string text, double fontSize)
 {
 if (string.IsNullOrEmpty(text)) return 0;
 // 粗略估算：每个ASCII字符宽度≈fontSize*0.6*0.5，中文字符≈fontSize*0.6
 double w = 0;
 foreach (var c in text)
 w += c > 127 ? fontSize * 0.6 : fontSize * 0.35;
 return w;
 }

 /// TrueType FormattedText 文字渲染
 /// x,y = 文字基线左点（CAD InsertPoint对应的屏幕坐标）
 /// rotation = 角度（度）
 /// widthFactor = X方向缩放因子（CAD WidthFactor）
 private void DrawCadTextTtf(System.Windows.Media.DrawingContext dc, string text, double fontSize, Brush color,
 double x, double y, double rotation, double widthFactor, string uf, string bf)
 {
 try
 {
 if (string.IsNullOrEmpty(text)) return;

 // 构造字体列表：SHX大字体名 + SHX主字体名 + 常见中文字体回退
 var fontFamilyStr = $"{bf}, {uf}, 仿宋, 宋体, SimSun, Microsoft YaHei";
 var typeface = new System.Windows.Media.Typeface(
 new System.Windows.Media.FontFamily(fontFamilyStr),
 System.Windows.FontStyles.Normal,
 System.Windows.FontWeights.Normal,
 System.Windows.FontStretches.Normal);

 double fontPt = fontSize / 1.2 * 32.0; // CAD高度→WPF pt(显示放大×8)
 // 渲染缩放补偿：字体大小需放大，否则缩小后文字不可读
 if (_cadRenderScale < 0.999) fontPt /= _cadRenderScale;

 var ft = new System.Windows.Media.FormattedText(
 text,
 System.Globalization.CultureInfo.CurrentCulture,
 System.Windows.FlowDirection.LeftToRight,
 typeface,
 fontPt,
 color,
 1.0);

 // AutoCAD InsertPoint = 基线左点（Left/Baseline对齐）
 // FormattedText DrawText 从左上角画 → 需要偏移 (0, -fontSize)
 // 变换链：先平移到插入点(x,y)，再旋转，再缩放WidthFactor，最后画文字(0,-h)
 bool needTransform = Math.Abs(rotation) > 0.1 || Math.Abs(widthFactor - 1.0) > 0.01;
 if (needTransform)
 {
 dc.PushTransform(new System.Windows.Media.TranslateTransform(x, y));
 dc.PushTransform(new System.Windows.Media.RotateTransform(rotation));
 if (Math.Abs(widthFactor - 1.0) > 0.01)
 dc.PushTransform(new System.Windows.Media.ScaleTransform(widthFactor, 1));
 dc.DrawText(ft, new System.Windows.Point(0, -fontSize));
 // Pop数量要和Push一致
 int popCount = 2 + (Math.Abs(widthFactor - 1.0) > 0.01 ? 1 : 0);
 for (int i = 0; i < popCount; i++) dc.Pop();
 }
 else
 {
 // 无变换：直接画，y偏移-fontSize
 dc.DrawText(ft, new System.Windows.Point(x, y - fontSize));
 }
 }
 catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DrawCadTextTtf失败: {ex.Message} text='{text?.Substring(0, Math.Min(20, text?.Length ?? 0))}'"); }
 }
	private static System.Windows.Media.Geometry CreatePolygonGeometry(List<Point> pts)
	{
 var sg = new StreamGeometry();
 using (var ctx = sg.Open())
 {
 ctx.BeginFigure(pts[0], true, true);
 for (int i = 1; i < pts.Count; i++)
 ctx.LineTo(pts[i], true, false);
 }
 return sg;
	}

 // ═══ 曲线插值辅助方法 ═══

 /// 用控制点构造3次贝塞尔曲线
 private static PathGeometry CreateBezierGeometry(List<Point> ctrlPts, bool isClosed)
 {
 try
 {
 var pg = new PathGeometry();
 var fig = new PathFigure { StartPoint = ctrlPts[0] };

 // 每三个控制点形成一段贝塞尔曲线
 for (int i = 0; i + 3 <= ctrlPts.Count; i += 3)
 {
 fig.Segments.Add(new BezierSegment(ctrlPts[i], ctrlPts[i+1], ctrlPts[i+2], true));
 // 如果有第4个点，用它作为下一段起点
 if (i + 3 < ctrlPts.Count)
 fig.Segments.Add(new LineSegment(ctrlPts[i+2], true));
 }
 // 处理剩余点
 int rem = ctrlPts.Count % 3;
 if (rem > 0)
 {
 int startIdx = ctrlPts.Count - rem;
 for (int i = startIdx + 1; i < ctrlPts.Count; i++)
 fig.Segments.Add(new LineSegment(ctrlPts[i], true));
 }

 if (isClosed) fig.IsClosed = true;
 pg.Figures.Add(fig);
 return pg;
 }
 catch { return null; }
 }

 /// Catmull-Rom插值生成平滑曲线（通过所有拟合点）
 private static PathGeometry CreateCatmullRomGeometry(List<Point> pts, bool isClosed)
 {
 try
 {
 if (pts.Count < 2) return null;
 var pg = new PathGeometry();
 var fig = new PathFigure { StartPoint = pts[0] };

 int n = pts.Count;
 int segCount = isClosed ? n : n - 1;
 for (int i = 0; i < segCount; i++)
 {
 // Catmull-Rom: 需要4个点 p0,p1,p2,p3
 Point p0 = (i == 0) ? (isClosed ? pts[n-1] : pts[0]) : pts[i-1];
 Point p1 = pts[i];
 Point p2 = pts[(i+1) % n];
 Point p3 = (i+2 < n) ? pts[i+2] : (isClosed ? pts[(i+2) % n] : pts[n-1]);

 // 转换为3次贝塞尔控制点
 double t = 0.5; // 张力
 var cp1 = new Point(p1.X + (p2.X - p0.X) / 6 * t, p1.Y + (p2.Y - p0.Y) / 6 * t);
 var cp2 = new Point(p2.X - (p3.X - p1.X) / 6 * t, p2.Y - (p3.Y - p1.Y) / 6 * t);

 fig.Segments.Add(new BezierSegment(cp1, cp2, p2, true));
 }

 if (isClosed) fig.IsClosed = true;
 pg.Figures.Add(fig);
 return pg;
 }
 catch { return null; }
 }


 private static string _cadFontName = "仿宋, 仿宋_GB2312, FangSong, SimFang, 宋体, SimSun";
 private static string _cadBigFontName = "仿宋, 仿宋_GB2312, FangSong, SimFang, 宋体, SimSun";
 private static string _cadFontFilePath = "";
 private static string _cadBigFontFilePath = "";
 private static double _cadWidthFactor = 1.0;
 private static double _cadLineFactor = 1.2;
 private static double _cadCharSpacing = 1.0;
 private static double _cadObliqueAngle = 0; // 倾斜角度
 private static bool _cadUpsideDown = false; // 颠倒
 private static bool _cadBackwards = false; // 反向
 private static bool _cadIsDarkBg = true; // CAD背景：true=黑底白字, false=白底黑字

 /// CAD背景色
 public static WpfColor CadBgColor => _cadIsDarkBg
 ? WpfColor.FromRgb(0x2A, 0x2A, 0x2E)
 : WpfColor.FromRgb(0xFA, 0xFA, 0xFA);

 /// CAD默认文字色（黑底用白字，白底用黑字）
 private static WpfColor CadDefaultTextColor => _cadIsDarkBg
 ? WpfColor.FromRgb(0xFF, 0xFF, 0xFF)
 : WpfColor.FromRgb(0x32, 0x32, 0x32);

// 主字体GlyphTypeface（西文）和大字体GlyphTypeface（中文）分离
 private static GlyphTypeface _cachedGlyphTypeface; // 主字体（西文）
 private static GlyphTypeface _cachedBigGlyphTypeface; // 大字体（中文）

 /// 更新CAD字体设置参数
 public void UpdateCadFontSettings(string fontName, string bigFontName,
 string fontFilePath, string bigFontFilePath,
 string shxFontName, string bigShxFontName, bool useBigFont,
 double widthFactor, double lineFactor, double charSpacing,
 double obliqueAngle, bool upsideDown, bool backwards, bool isDarkBg = true)
		{
 var oldFont = _cadFontName;
 var oldBigFont = _cadBigFontName;
 var oldDarkBg = _cadIsDarkBg;

 _cadFontName = fontName;
 _cadBigFontName = bigFontName;
 _cadFontFilePath = fontFilePath;
 _cadBigFontFilePath = bigFontFilePath;
 _cadWidthFactor = widthFactor;
 _cadLineFactor = lineFactor;
 _cadCharSpacing = charSpacing;
 _cadObliqueAngle = obliqueAngle;
	 _cadUpsideDown = upsideDown;
	 _cadBackwards = backwards;
	 _cadIsDarkBg = isDarkBg;

 // 字体变了，重建缓存（shxFontName/bigShxFontName/useBigFont 参数保留以维持API兼容，
 // 但已不再使用 SHX 矢量渲染，故仅按主/大字体名变化重建 GlyphTypeface 缓存）
 if (oldFont != _cadFontName)
 {
 _cachedGlyphTypeface = null;
 }
 if (oldBigFont != _cadBigFontName)
 {
 _cachedBigGlyphTypeface = null;
 }
 // 背景色变化时清理画笔缓存（默认文字颜色变化）
 if (oldDarkBg != _cadIsDarkBg)
 _brushCache.Clear();
		// 同步字宽到 WPF 渲染器
		CadWpfRenderer.CadWidthFactor = _cadWidthFactor;
 }

	/// 获取主字体GlyphTypeface（西文字符）
	private static GlyphTypeface GetCachedGlyphTypeface()
	{
 if (_cachedGlyphTypeface != null) return _cachedGlyphTypeface;
 // 如果有字体文件路径，用文件路径加载
 if (!string.IsNullOrEmpty(_cadFontFilePath) && File.Exists(_cadFontFilePath))
 {
 try
 {
 var gc = new System.Windows.Media.GlyphTypeface(new Uri(_cadFontFilePath));
 _cachedGlyphTypeface = gc;
 return gc;
 }
 catch { }
 }
 // 否则用系统字体名——依次尝试主字体名、常见西文字体
 var asciiCandidates = new[] { _cadFontName, "Arial", "Times New Roman", "Calibri" };
 foreach (var name in asciiCandidates)
 {
 if (string.IsNullOrEmpty(name)) continue;
 try
 {
 var tf = new Typeface(new System.Windows.Media.FontFamily(name),
 FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
 if (tf.TryGetGlyphTypeface(out _cachedGlyphTypeface) && _cachedGlyphTypeface != null)
 return _cachedGlyphTypeface;
 }
 catch { }
 }
 return _cachedGlyphTypeface;
	}

	/// 获取大字体GlyphTypeface（中文字符）
	private static GlyphTypeface GetCachedBigGlyphTypeface()
	{
 if (_cachedBigGlyphTypeface != null) return _cachedBigGlyphTypeface;
 // 如果有字体文件路径，用文件路径加载（SHX字体文件路径）
 if (!string.IsNullOrEmpty(_cadBigFontFilePath) && File.Exists(_cadBigFontFilePath))
 {
 try
 {
 var gc = new System.Windows.Media.GlyphTypeface(new Uri(_cadBigFontFilePath));
 _cachedBigGlyphTypeface = gc;
 return gc;
 }
 catch { }
 }
 // 否则用系统字体名——依次尝试大字体名、主字体名、常见中文字体
 var cjkCandidates = new[] { _cadBigFontName, _cadFontName, "宋体", "SimSun", "Microsoft YaHei" };
 foreach (var name in cjkCandidates)
 {
 if (string.IsNullOrEmpty(name)) continue;
 try
 {
 var tf = new Typeface(new System.Windows.Media.FontFamily(name),
 FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
 if (tf.TryGetGlyphTypeface(out _cachedBigGlyphTypeface) && _cachedBigGlyphTypeface != null)
 return _cachedBigGlyphTypeface;
 }
 catch { }
 }
 return _cachedBigGlyphTypeface;
	}

 /// 从CAD TextStyle读取字体名（unifont, bigfont）
 private static (string unifont, string bigfont) GetFontNamesFromStyle(ACadSharp.Tables.TextStyle style)
 {
 if (style == null) return ("仿宋, 宋体, SimSun", "仿宋, 宋体, SimSun");
 try
 {
 string uf = "仿宋, 宋体, SimSun";
 string bf = "仿宋, 宋体, SimSun";

 // ACadSharp 3.x: TextStyle 有 Filename 和 BigFontFilename 属性
 var styleType = style.GetType();

 // 主字体文件名（如 fsdb_e.shx）
 var fontProp = styleType.GetProperty("Filename") ?? styleType.GetProperty("FontFile") ?? styleType.GetProperty("PrimaryFont");
 if (fontProp != null)
 {
 var fam = fontProp.GetValue(style)?.ToString();
 if (!string.IsNullOrEmpty(fam))
 {
 uf = Path.GetFileNameWithoutExtension(fam);
 // SHX字体名WPF不认识，加中文回退
 uf = $"{uf}, 仿宋, 宋体, SimSun, Microsoft YaHei";
 }
 }

 // 大字体文件名（如 fsdb.shx）
 var bigProp = styleType.GetProperty("BigFontFilename") ?? styleType.GetProperty("BigFont");
 if (bigProp != null)
 {
 var big = bigProp.GetValue(style)?.ToString();
 if (!string.IsNullOrEmpty(big))
 {
 bf = Path.GetFileNameWithoutExtension(big);
 bf = $"{bf}, 仿宋, 宋体, SimSun, Microsoft YaHei";
 }
 }

 return (uf, bf);
 }
 catch { return ("仿宋, 宋体, SimSun", "仿宋, 宋体, SimSun"); }
 }

 /// <summary>
 /// 根据CAD字体名(unifont/bigfont)解析出可用的 System.Drawing 字体族名
 /// </summary>
 private static readonly Dictionary<WpfColor, SolidColorBrush> _brushCache = new();
 private static SolidColorBrush GetOrCreateBrush(WpfColor color)
 {
 if (_brushCache.TryGetValue(color, out var cached)) return cached;
 var brush = new SolidColorBrush(color);
 brush.Freeze();
 _brushCache[color] = brush;
 return brush;
 }

 private static SolidColorBrush GetEntityWpfColor(ACadSharp.Entities.Entity ent, Brush parentBlockColor = null)
 {
 if (ent == null) return GetOrCreateBrush(CadDefaultTextColor);
 var c = ent.Color;
 SolidColorBrush baseBrush;
 try {
 // ByBlock: 使用父块的颜色
 if (c.IsByBlock)
 {
 if (parentBlockColor is SolidColorBrush pcb)
 baseBrush = GetOrCreateBrush(pcb.Color);
 else
 baseBrush = GetOrCreateBrush(CadDefaultTextColor);
 }
 else
 {
 // ByLayer: 用图层颜色
 if (c.IsByLayer && ent.Layer != null)
 c = ent.Layer.Color;
 if (c.IsByLayer)
 baseBrush = GetOrCreateBrush(CadDefaultTextColor);
 else
 {
 var rgb = c.GetRgb();
 if (rgb != null && rgb.Length >= 3)
 {
 // 保留CAD原始颜色——不强制改色
 // 暗色背景下纯黑色不可见，自动反色为白色
 if (_cadIsDarkBg && rgb[0] == 0 && rgb[1] == 0 && rgb[2] == 0)
 baseBrush = GetOrCreateBrush(CadDefaultTextColor);
 // 亮色背景下纯白色不可见，自动反色为黑色
 else if (!_cadIsDarkBg && rgb[0] == 255 && rgb[1] == 255 && rgb[2] == 255)
 baseBrush = GetOrCreateBrush(WpfColor.FromRgb(0x32, 0x32, 0x32));
 else
 baseBrush = GetOrCreateBrush(WpfColor.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]));
 }
 else
 baseBrush = GetOrCreateBrush(CadDefaultTextColor);
 }
 }
 } catch { baseBrush = GetOrCreateBrush(CadDefaultTextColor); }

 // 透明度处理
 try
 {
 var trans = ent.Transparency;
 if (!trans.IsByLayer && !trans.IsByBlock)
 {
 // Transparency.Value: 0=不透明, 90=完全透明
 double val = trans.Value;
 if (val > 0)
 {
 double opacity = 1.0 - val / 90.0;
 if (opacity < 0) opacity = 0;
 if (opacity > 1) opacity = 1;
 if (opacity < 1.0)
 {
 // 不能修改缓存的brush，创建新的
 var newBrush = new SolidColorBrush(baseBrush.Color) { Opacity = opacity };
 newBrush.Freeze();
 return newBrush;
 }
 }
 }
 }
 catch { }

 return baseBrush;
 }

 /// 读取CAD实体线宽，映射到WPF像素宽度
 private static double GetEntityPenWidth(ACadSharp.Entities.Entity ent, ACadSharp.LineWeightType parentBlockLineWeight = ACadSharp.LineWeightType.Default)
 {
 try
 {
 // ACadSharp: LineWeightType 枚举（ByLayer, ByBlock, Default, 或具体值如 W050=0.50mm）
 var lw = ent.LineWeight;
 // ByLayer → 取图层线宽
 if (lw == ACadSharp.LineWeightType.ByLayer && ent.Layer != null)
 lw = ent.Layer.LineWeight;
 // ByBlock → 取父块线宽
 if (lw == ACadSharp.LineWeightType.ByBlock)
 lw = parentBlockLineWeight;
 // Default 或 ByBlock无父块 → 用默认值
 if (lw == ACadSharp.LineWeightType.Default || lw == ACadSharp.LineWeightType.ByBlock || lw == ACadSharp.LineWeightType.ByLayer)
 return 0.5;
 // 枚举值代表线宽（单位：0.01mm），如 LineWeightType.W050 = 0.50mm
 // WPF像素 = mm * 96 / 25.4 ≈ 3.78 px/mm
 double mm = (double)(int)lw / 100.0;
 double px = mm * 96.0 / 25.4;
 // 限制范围：0.2px ~ 4px
 return Math.Max(0.2, Math.Min(4.0, px));
 }
 catch { return 0.5; }
 }

 // ═══ 线型（Linetype）支持 ═══

 /// 线型DashStyle缓存：Key = 线型名+缩放
 private static readonly Dictionary<string, DashStyle> _dashStyleCache = new();

 /// 获取实体的Pen（含线型虚线/点划线支持）
 private System.Windows.Media.Pen GetEntityPen(ACadSharp.Entities.Entity ent, Brush parentBlockColor = null, ACadSharp.LineWeightType parentBlockLineWeight = ACadSharp.LineWeightType.Default)
 {
 var color = GetEntityWpfColor(ent, parentBlockColor);
 // 线宽统一设为 0：所有线条都用同一条细线，不随缩放改变
 // 不再按 LineWeight 分级、不做 DISPLAY_SCALE 放大，保持视觉粗细恒定
 double penWidth = 1.0;
 // 渲染缩放补偿：画布被 ScaleTransform 缩小了 _cadRenderScale 倍，
 // 笔宽需同比放大，否则线条在屏幕上变得极细不可见
 if (_cadRenderScale < 0.999 && penWidth > 0)
 penWidth /= _cadRenderScale;
 var pen = new System.Windows.Media.Pen(color, penWidth);

 // 尝试获取线型
 try
 {
 var lt = GetActiveLineType(ent);
 if (lt != null && lt.Name != null && lt.Name != "ByLayer" && lt.Name != "ByBlock")
 {
 var scale = GetLineTypeScale(ent);
 var dashKey = $"{lt.Name}_{scale:F2}";
 if (!_dashStyleCache.TryGetValue(dashKey, out var dashStyle))
 {
 dashStyle = BuildDashStyle(lt, scale);
 if (dashStyle != null)
 {
 dashStyle.Freeze();
 _dashStyleCache[dashKey] = dashStyle;
 }
 }
 if (dashStyle != null)
 {
 // 渲染缩放补偿：虚线段长度也需放大，否则缩放后虚线变密/消失
 if (_cadRenderScale < 0.999 && dashStyle.Dashes != null && dashStyle.Dashes.Count > 0)
 {
    var scaledDashes = new DoubleCollection(dashStyle.Dashes.Count);
    double invScale = 1.0 / _cadRenderScale;
    foreach (var d in dashStyle.Dashes) scaledDashes.Add(d * invScale);
    var scaledDash = new DashStyle(scaledDashes, dashStyle.Offset * invScale);
    pen.DashStyle = scaledDash;
 }
 else
 {
 pen.DashStyle = dashStyle;
 }
 }
 }
 }
 catch { }

 return pen;
 }

 /// 获取实体激活的线型（ByLayer→取图层线型）
 private static ACadSharp.Tables.LineType GetActiveLineType(ACadSharp.Entities.Entity ent)
 {
 try
 {
 var lt = ent.LineType;
 if (lt == null || lt.Name == "ByLayer")
 {
 if (ent.Layer != null)
 return ent.Layer.LineType;
 }
 if (lt != null && lt.Name != "ByBlock")
 return lt;
 }
 catch { }
 return null;
 }

 /// 获取线型缩放（实体缩放 × 全局LTSCALE）
 private double GetLineTypeScale(ACadSharp.Entities.Entity ent)
 {
 try
 {
 double scale = ent.LineTypeScale;
 if (scale <= 0) scale = 1.0;
 // 乘以全局LTSCALE
 return scale * _cadLtScale;
 }
 catch { return _cadLtScale; }
 }

 /// 从ACadSharp LineType.Segments构建WPF DashStyle
 private static DashStyle BuildDashStyle(ACadSharp.Tables.LineType lt, double scale)
 {
 try
 {
 var segments = lt.Segments;
 if (segments == null || !segments.Any()) return null;

 var dashArray = new List<double>();
 bool hasDash = false;
 foreach (var seg in segments)
 {
 double len = seg.Length * scale;
 if (len > 0.01)
 {
 dashArray.Add(len);
 hasDash = true;
 }
 else if (len < -0.01)
 {
 dashArray.Add(Math.Abs(len));
 hasDash = true;
 }
 else
 {
 // 点：零长度
 dashArray.Add(0);
 hasDash = true;
 }
 }

 if (!hasDash || dashArray.Count == 0) return null;

 // WPF DashStyle需要成对的dash+gap
 // 如果只有奇数个元素，补一个默认gap
 if (dashArray.Count % 2 == 1)
 dashArray.Add(dashArray[0] * 0.5);

 var style = new DashStyle();
 // 用DashArray属性
 var doubledArray = dashArray.ToArray();
 // 设置DashStyle.Dashes（DoubleCollection）
 var dc = new DoubleCollection();
 foreach (var d in doubledArray)
 dc.Add(Math.Max(0.1, d));
 style.Dashes = dc;
 style.Offset = 0;
 return style;
 }
 catch { return null; }
 }

 /// 按宽度自动换行CAD文字（用FormattedText测量）
 private List<string> WrapCadTextByWidth(string text, double maxWidth, double fontSize)
 {
 var result = new List<string>();
 if (string.IsNullOrEmpty(text)) return result;

 // 用GlyphTypeface的AdvanceWidths计算字符宽度，避免逐字符构造FormattedText（性能提升10x+）
 var gt = GetCachedGlyphTypeface();
 var bigGt = GetCachedBigGlyphTypeface();
 double fontPt = fontSize / 1.2;
 double emSize = fontPt * 96.0 / 72.0; // pt→px

 var current = new StringBuilder();
 double currentWidth = 0;

 foreach (var ch in text)
 {
 double charWidth = GetCharWidthFast(ch, gt, bigGt, emSize);
 if (currentWidth + charWidth > maxWidth && current.Length > 0)
 {
 result.Add(current.ToString());
 current.Clear();
 current.Append(ch);
 currentWidth = charWidth;
 }
 else
 {
 current.Append(ch);
 currentWidth += charWidth;
 }
 }
 if (current.Length > 0) result.Add(current.ToString());
 return result;
 }

 /// 用GlyphTypeface.AdvanceWidths快速计算字符宽度（不构造FormattedText）
 private static double GetCharWidthFast(char ch, GlyphTypeface gt, GlyphTypeface bigGt, double emSize)
 {
 try
 {
 var typeface = (ch < 128) ? (gt ?? bigGt) : (bigGt ?? gt);
 if (typeface == null) return emSize * 0.5; // 回退
 ushort glyphIndex = typeface.CharacterToGlyphMap.TryGetValue(ch, out var gi) ? gi : (ushort)0;
 double advance = 0.5; // 默认半角宽
 if (typeface.AdvanceWidths.Count > glyphIndex)
 advance = typeface.AdvanceWidths[glyphIndex];
 return advance * emSize;
 }
 catch { return emSize * 0.5; }
 }

 // 预编译正则，避免每次调用重新编译
 private static readonly System.Text.RegularExpressions.Regex _mtextFmtWithSemi =
 new(@"\\[AaFfHhCcSsQqWwTtOoLlKkDd][^;]*;", System.Text.RegularExpressions.RegexOptions.Compiled);
 private static readonly System.Text.RegularExpressions.Regex _mtextFmtNoSemi =
 new(@"\\[AaFfHhCcSsQqWwTtOoLlKkDd][^;]*", System.Text.RegularExpressions.RegexOptions.Compiled);
 private static readonly System.Text.RegularExpressions.Regex _mtextStack =
 new(@"\\S[^;]*;", System.Text.RegularExpressions.RegexOptions.Compiled);
 private static readonly System.Text.RegularExpressions.Regex _mtextBraces =
 new(@"[{}]", System.Text.RegularExpressions.RegexOptions.Compiled);

 /// 转换CAD控制码：%%d→°, %%c→Φ, %%p→±, %%u→下划线标记
 private static string ConvertCadControlCodes(string text)
 {
 if (string.IsNullOrEmpty(text)) return "";
 text = DecodeUniEscapes(text);  // \U+XXXX 转义解码（钢筋符号/上下标/Unicode）
 return text
 .Replace("%%d", "°").Replace("%%D", "°")
 .Replace("%%c", "Φ").Replace("%%C", "Φ")
 .Replace("%%p", "±").Replace("%%P", "±")
 .Replace("%%u", "").Replace("%%U", "")
 // 钢筋等级符号：%%130-133 → AutoCAD 字体码位 0x82-0x85（Tssdeng 等专业字体含对应字形）
 .Replace("%%130", "\u0082").Replace("%%131", "\u0083")
 .Replace("%%132", "\u0084").Replace("%%133", "\u0085")
 // %%140/141 = 上下标起止标记，去除后数字正常显示（如 kN/m² → kN/m2）
 .Replace("%%140", "").Replace("%%141", "")
 .Replace("%%142", "").Replace("%%143", "");
 }

 /// 解析MText内容，清理格式化代码
 /// 解码 AutoCAD \U+XXXX Unicode 转义：钢筋符号 0x82-85 映射到字体码位（Tssdeng 等
 /// 专业 SHX 字形），上下标控制码 0x8C-8F 去除，其余解码为对应 Unicode 字符。
 private static string DecodeUniEscapes(string s)
 {
 if (string.IsNullOrEmpty(s)) return s;
 return System.Text.RegularExpressions.Regex.Replace(s, @"\\U\+([0-9A-Fa-f]{4})", m =>
 {
 int cp;
 try { cp = Convert.ToInt32(m.Groups[1].Value, 16); }
 catch { return m.Value; }
 switch (cp)
 {
 case 0x82: return "\u0082";  // 一级钢筋
 case 0x83: return "\u0083";  // 二级钢筋
 case 0x84: return "\u0084";  // 三级钢筋
 case 0x85: return "\u0085";  // 四级钢筋
 case 0x8C: case 0x8D: case 0x8E: case 0x8F: return "";  // 上下标起止
 default: return ((char)cp).ToString();
 }
 });
 }

 private static string ParseMTextContent(string raw)
 {
 if (string.IsNullOrEmpty(raw)) return "";

 var text = raw;
 // \U+XXXX 转义先解码（\U 之后会被格式码正则删除，残留 "+0084" 字面文本）
 text = DecodeUniEscapes(text);
 // 移除MText格式化代码
 // \P = 段落换行, \p = 段落换行
 text = text.Replace("\\P", "\n").Replace("\\p", "\n");
 // \n 在原始Value中也是换行
 text = text.Replace("\\n", "\n");
 // \A = 对齐, \f = 字体, \H = 高度, \C = 颜色, \S = 堆叠, \Q = 倾斜, \T = 跟踪, \W = 宽度
 text = _mtextFmtWithSemi.Replace(text, "");
 text = _mtextFmtNoSemi.Replace(text, "");
 // 堆叠文字 \S...;
 text = _mtextStack.Replace(text, "");
 // { } 分组
 text = _mtextBraces.Replace(text, "");
 // %%d=度, %%c=直径, %%p=正负, %%u=下划线开始/结束
 text = text.Replace("%%d", "°").Replace("%%c", "Φ").Replace("%%p", "±");
 text = text.Replace("%%D", "°").Replace("%%C", "Φ").Replace("%%P", "±");
 text = text.Replace("%%u", "").Replace("%%U", "");
 // 钢筋等级符号 %%130-133 → 字体码位 0x82-0x85；%%140-143 上下标标记去除
 text = text.Replace("%%130", "\u0082").Replace("%%131", "\u0083")
     .Replace("%%132", "\u0084").Replace("%%133", "\u0085")
     .Replace("%%140", "").Replace("%%141", "")
     .Replace("%%142", "").Replace("%%143", "");

 return text.Trim();
 }

	/// <summary>
	/// DOCX/TXT 矢量渲染——用 WPF TextBlock 原生文字，缩放不失真。
	/// </summary>
	private StackPanel RenderDocxVector(int pageIndex)
	{
 if (FileType == "txt")
 {
 var txtPanel = new StackPanel { Margin = new Thickness(40, 30, 40, 30), MaxWidth = 900 };
 txtPanel.Children.Add(new TextBlock
 {
 Text = Path.GetFileName(_currentPath),
 FontSize = 16, FontWeight = FontWeights.Bold,
 Foreground = new SolidColorBrush(WpfColor.FromRgb(0x1A, 0x23, 0x7A)),
 Margin = new Thickness(0, 0, 0, 16),
 });

 string text;
 try { text = File.ReadAllText(_currentPath, System.Text.Encoding.UTF8); }
 catch { text = "（无法读取文件）"; }

 txtPanel.Children.Add(new TextBlock
 {
 Text = text,
 FontSize = 13,
 FontFamily = new System.Windows.Media.FontFamily("微软雅黑"),
 TextWrapping = TextWrapping.Wrap,
 LineHeight = 22,
 });
 return txtPanel;
 }

 // DOCX
 var (blocks, imageParts) = GetOrParseDocxBlocks(_currentPath);
 if (blocks.Count == 0) return null;

		const float canvasW = 1000;
		const float pageH = 1400;

		// 测量每块高度
		var blockHeights = new List<float>();
		using (var measureBmp = new SD.Bitmap(1, 1))
		using (var measureG = SD.Graphics.FromImage(measureBmp))
		{
			float y = 60; float maxH = 60;
			foreach (var b in blocks)
			{
				float beforeY = y;
				b.Draw(measureG, ref y, canvasW, ref maxH);
				blockHeights.Add(y - beforeY);
			}
		}

		// 按页分割
		var pageBlocks = new List<List<DocBlock>>();
		var currentPageBlocks = new List<DocBlock>();
		float pageY = 60;
		for (int i = 0; i < blocks.Count; i++)
		{
			float blockH = blockHeights[i];
			if (pageY + blockH > pageH && currentPageBlocks.Count > 0)
			{
				pageBlocks.Add(currentPageBlocks);
				currentPageBlocks = new List<DocBlock>();
				pageY = 20;
			}
			currentPageBlocks.Add(blocks[i]);
			pageY += blockH;
		}
		if (currentPageBlocks.Count > 0) pageBlocks.Add(currentPageBlocks);

		if (pageBlocks.Count == 0) return null;
		if (pageIndex < 0 || pageIndex >= pageBlocks.Count) pageIndex = 0;

		// 构建 WPF 原生文字面板
		var panel = new StackPanel
		{
			Margin = new Thickness(0),
			Background = Brushes.White,
			MaxWidth = canvasW,
			MinWidth = canvasW,
		};

		if (pageIndex == 0)
		{
			panel.Children.Add(new TextBlock
			{
				Text = Path.GetFileName(_currentPath),
				FontSize = 14, FontWeight = FontWeights.Bold,
				Foreground = new SolidColorBrush(WpfColor.FromRgb(0x1A, 0x23, 0x7A)),
				Margin = new Thickness(40, 16, 40, 8),
			});
 panel.Children.Add(new System.Windows.Controls.Border
 {
 Height = 2,
 Background = new SolidColorBrush(WpfColor.FromRgb(0x21, 0x96, 0xF3)),
 Margin = new Thickness(40, 0, 40, 12),
 });
		}

		if (pageBlocks.Count > 1)
		{
			panel.Children.Add(new TextBlock
			{
				Text = $"第 {pageIndex + 1} / {pageBlocks.Count} 页",
				FontSize = 10,
				Foreground = new SolidColorBrush(WpfColor.FromRgb(0x99, 0x99, 0x99)),
				Margin = new Thickness(40, 0, 0, 8),
				HorizontalAlignment = HorizontalAlignment.Right,
			});
		}

 // 将 DocBlock 转为 WPF 元素
 foreach (var b in pageBlocks[pageIndex])
 {
 if (b is DocTextBlock tb)
 {
 panel.Children.Add(new TextBlock
 {
 Text = tb.Text,
 FontSize = tb.FontSize,
 FontWeight = tb.Bold ? FontWeights.Bold : FontWeights.Normal,
 FontStyle = tb.Italic ? FontStyles.Italic : FontStyles.Normal,
 Foreground = new SolidColorBrush(WpfColor.FromRgb(
 tb.Color.R, tb.Color.G, tb.Color.B)),
 TextWrapping = TextWrapping.Wrap,
 Margin = new Thickness(40, 2, 40, 2),
 LineHeight = tb.FontSize * 1.6,
 });
 }
 else if (b is DocImageBlock ib && ib.Image != null)
 {
 panel.Children.Add(new System.Windows.Controls.Image
 {
 Source = ConvertBitmap(ib.Image),
 Stretch = System.Windows.Media.Stretch.Uniform,
 MaxHeight = 400,
 HorizontalAlignment = HorizontalAlignment.Center,
 Margin = new Thickness(40, 8, 40, 8),
 });
 }
 else if (b is DocTableBlock tblock)
 {
 // 表格 → WPF Grid
 var grid = new System.Windows.Controls.Grid
 {
 Margin = new Thickness(40, 4, 40, 8),
 };
 int cols = tblock.Rows.Count > 0 ? tblock.Rows[0].Count : 0;
 for (int c = 0; c < cols; c++)
 grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
 for (int r = 0; r < tblock.Rows.Count; r++)
 {
 grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
 for (int c = 0; c < tblock.Rows[r].Count && c < cols; c++)
 {
 var cellText = tblock.Rows[r][c] ?? "";
 var cellBorder = new System.Windows.Controls.Border
 {
 BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0xCC, 0xCC, 0xCC)),
 BorderThickness = new Thickness(0.5),
 Padding = new Thickness(4, 2, 4, 2),
 };
 if (r == 0)
 cellBorder.Background = new SolidColorBrush(WpfColor.FromRgb(0x42, 0xA5, 0xF5));
 else if (r % 2 == 0)
 cellBorder.Background = new SolidColorBrush(WpfColor.FromRgb(0xF5, 0xF5, 0xF5));

 cellBorder.Child = new TextBlock
 {
 Text = cellText,
 FontSize = r == 0 ? 11 : 10,
 FontWeight = r == 0 ? FontWeights.Bold : FontWeights.Normal,
 Foreground = r == 0 ? Brushes.White : Brushes.Black,
 TextWrapping = TextWrapping.Wrap,
 };
 System.Windows.Controls.Grid.SetRow(cellBorder, r);
 System.Windows.Controls.Grid.SetColumn(cellBorder, c);
 grid.Children.Add(cellBorder);
 }
 }
 panel.Children.Add(grid);
 }
 else if (b is DocSpacerBlock sb)
 {
 panel.Children.Add(new System.Windows.Controls.Border { Height = sb.Space });
 }
 }

 // 不在此处dispose图片缓存——图片由_docxImagesCache持有，会在Close()时统一清理
 return panel;
	}

	/// 
public BitmapSource RenderPage(int pageIndex, int width = 0, int dpi = 150)
{
 try
 {
 // PDF 页面缓存（仅缓存缩略图渲染，不缓存高分辨率OCR渲染）
 if (FileType == "pdf" && width > 0 && dpi == 150)
 {
 var cacheKey = pageIndex * 1000 + width;
 if (_pageCache.TryGetValue(cacheKey, out var cached))
 return cached;
 var result = RenderPdfPage(pageIndex, width, dpi);
 if (result != null)
 {
 if (_pageCache.Count >= _pageCacheMax)
 {
 var oldestKey = _pageCache.Keys.First();
 _pageCache.Remove(oldestKey);
 }
 _pageCache[cacheKey] = result;
 }
 return result;
 }

 switch (FileType)
 {
 case "pdf":
 return RenderPdfPage(pageIndex, width, dpi);
	case "image":
	return LoadImageFile(_currentPath);
 case "docx":
 case "txt":
 return RenderTextFile(pageIndex, dpi);
	case "cad":
		return RenderCadPageToBitmap(pageIndex, dpi, width);
	default:
	return null;
 }
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"渲染页面失败: {ex.Message}");
 return null;
 }
 }

private BitmapSource RenderPdfPage(int pageIndex, int width, int dpi)
{
lock (_pdfLock)
{
if (_pdfDoc == null || pageIndex < 0 || pageIndex >= _pdfDoc.PageCount)
return null;

var size = _pdfDoc.PageSizes[pageIndex];
double scale = dpi / 72.0;
if (width > 0)
scale = width / size.Width;

int w = (int)(size.Width * scale);
int h = (int)(size.Height * scale);

var img = _pdfDoc.Render(pageIndex, w, h, dpi, dpi, true);
return ConvertBitmap(img);
}
}

private BitmapSource LoadImageFile(string path)
{
 var bmp = new SD.Bitmap(path);
 return ConvertBitmap(bmp, disposeInput: true);
}

/// <summary>DWG/DXF CAD 文件 — 提取文本信息显示</summary>
/// <summary>
/// 读取 DWG/DXF 文件，解析模型空间和布局空间列表。
/// </summary>
private void LoadCadDocument(string path)
{
        _cadDoc = null;
        _cadSpaceNames.Clear();
        _cadLtScale = 1.0;

        // ACadSharp 读取DWG/DXF时依赖当前线程 Culture 解析数值（坐标/比例等），
        // 中文(zh-CN)区域的小数点/分隔符会导致解析异常、_cadDoc 为 null → 预览空白。
        // 临时切换 InvariantCulture 读取后还原（独立诊断工程已验证此修复）。
        var prevCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
 if (ext == ".dxf")
 {
 using var dxfReader = new ACadSharp.IO.DxfReader(path);
 dxfReader.OnNotification += (s, e) => { System.Diagnostics.Debug.WriteLine($"ACadSharp: {e.Message}"); };
 _cadDoc = dxfReader.Read();
 }
 else
 {
 using var dwgReader = new ACadSharp.IO.DwgReader(path);
 dwgReader.OnNotification += (s, e) => { System.Diagnostics.Debug.WriteLine($"ACadSharp: {e.Message}"); };
 _cadDoc = dwgReader.Read();
 }

 // 第一个空间始终是模型空间
 _cadSpaceNames.Add("模型");

 // 添加所有布局空间（按 TabOrder 排序）
 if (_cadDoc.Layouts != null)
 {
 var layouts = _cadDoc.Layouts
 .Where(l => l.Name != "Model" && l.Name != "模型") // 跳过模型空间（已手动添加）
 .OrderBy(l => l.TabOrder)
 .Select(l => l.Name)
 .ToList();
 _cadSpaceNames.AddRange(layouts);
 }

 TotalPages = Math.Max(1, _cadSpaceNames.Count);

 // 读取全局线型比例 LTSCALE
 try
 {
 var lt = _cadDoc.Header?.LineTypeScale;
 if (lt.HasValue && lt.Value > 0)
 _cadLtScale = lt.Value;
 }
 catch { }
 }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CAD 加载失败: {ex}");
                TotalPages = 1;
                _cadSpaceNames.Add("模型");
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prevCulture;
            }
        }

/// <summary>
/// CAD 页面渲染为位图（用于 OCR / 缩略图）
/// 核心思路：复用 RenderCadVector 生成 WPF Canvas（坐标原点0,0，画布跟随内容），
/// 再用 RenderTargetBitmap 截图。这样 OCR 看到的图和屏幕预览完全一致。
/// </summary>
public BitmapSource RenderCadPageToBitmap(int pageIndex, int dpi, int targetWidth = 0)
{
 try
 {
 // ═══ 优先走 SkiaSharp 渲染引擎（白底，便于 OCR 识别与打印）═══
 int skiaLongSide = targetWidth > 0
     ? Math.Max(1200, targetWidth * 3)
     : Math.Max(2400, dpi * 10);
 var skiaBmp = RenderCadSkia(pageIndex, skiaLongSide, darkBackground: false);
 if (skiaBmp != null) return skiaBmp;

 // 降级：旧的 WPF 矢量路径
 var canvas = RenderCadVector(pageIndex);
 if (canvas == null) return null;

 // 2. 直接用 Canvas 的 Width/Height
 double w = canvas.Width;
 double h = canvas.Height;
 if (w <= 0 || h <= 0) return null;

 // 3. 计算缩放比和最终像素尺寸
 double scale;
 if (targetWidth > 0)
 {
 // 缩略图模式：按目标宽度缩放
 scale = targetWidth / w;
 }
 else
 {
 // OCR/高分辨率模式：按 DPI 缩放
 scale = dpi / 96.0;
 }
 int pixelW = Math.Max(1, (int)(w * scale));
 int pixelH = Math.Max(1, (int)(h * scale));

 // 4. 强制测量排列 Canvas 到原始尺寸
 canvas.Measure(new Size(w, h));
 canvas.Arrange(new Rect(0, 0, w, h));

 // 5. 用 VisualBrush 缩放渲染到目标尺寸（解决缩略图截断问题）
 var brush = new System.Windows.Media.VisualBrush(canvas)
 {
 Stretch = System.Windows.Media.Stretch.Uniform,
 AlignmentX = System.Windows.Media.AlignmentX.Center,
 AlignmentY = System.Windows.Media.AlignmentY.Center,
 };
 var targetVisual = new System.Windows.Media.DrawingVisual();
 using (var dc = targetVisual.RenderOpen())
 {
 // 暗色背景
 dc.DrawRectangle(System.Windows.Media.Brushes.White, null,
 new Rect(0, 0, pixelW, pixelH));
 // 用 VisualBrush 画 canvas 内容
 dc.DrawRectangle(brush, null, new Rect(0, 0, pixelW, pixelH));
 }

 var rtb = new RenderTargetBitmap(pixelW, pixelH, 96, 96, PixelFormats.Pbgra32);
 rtb.Render(targetVisual);

 var bmp = BitmapFrame.Create(rtb);
 bmp.Freeze();
 return bmp;
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"CAD 页面截图失败: {ex.Message}");
 return RenderTextToImage($"📄 CAD 截图失败: {ex.Message}", Path.GetFileName(_currentPath));
 }
}

/// <summary>
/// CAD OCR 专用：将整页渲染为高分辨率位图（长边 12000px）并按条切分，
/// 返回各条 PNG 文件路径。整图直接送 OCR 会被 RapidOCR 的 maxSideLen
/// 压缩到 1024px，小字全部丢失（表现为"识别不出文字"）；切条后每条
/// 长边 ≤1000px，文本高度足以被检测识别。
/// </summary>
/// <param name="pageIndex">页索引（0 = 模型空间）</param>
/// <param name="outDir">临时 PNG 输出目录</param>
public List<string> RenderCadOcrStrips(int pageIndex, string outDir,
    string shxFont = "Tssdeng", string bigShx = "hztxt", bool useBig = true)
{
    // 整个渲染流程必须在同一 STA 线程执行：模型缓存中的 WPF 对象（Brush/FormattedText）
    // 在首次构建线程创建，跨线程复用会在 Freeze 时抛"调用线程无法访问此对象"。
    return RunInSta(() => RenderCadOcrStripsCore(pageIndex, outDir, shxFont, bigShx, useBig));
}

private List<string> RenderCadOcrStripsCore(int pageIndex, string outDir,
    string shxFont, string bigShx, bool useBig)
{
    try
    {
        var entities = GetCadEntities(pageIndex);
        if (entities == null || entities.Count == 0) return null;

        // OCR 需要文字达到可识别尺寸（约 30px 字高）。整页 fit 渲染会把超大图纸的
        // 文字压成几像素高，OCR 识别不出任何文字（表现为"始终识别不了"）。
        // 正常图纸用渲染器的智能初始视图（自动放大到典型文字可读并定位文字密集区）；
        // 超大稀疏图纸（典型字高极小、放大 100 倍窗口仍装不下文字）则按文字簇
        // 聚类分块扫描，每块独立放大到可读字高。
        const int viewW = 2000, viewH = 1400;
        const int stripPx = 1000, overlap = 100;
        string cacheKey = $"{_currentPath}|{pageIndex}|{shxFont}|{bigShx}|{useBig}";

        // 探针渲染：拿模型信息（FitScale/典型字高/文字坐标/内容范围）
        var probe = CadWpfRenderer.RenderViewport(entities, viewW, viewH, 1.0, 0, 0, cacheKey, true);
        if (probe?.Image?.Drawing == null) return null;

        double fitScale = probe.FitScale;
        double typicalH = probe.TypicalTextHeight;
        double initZoom = probe.InitZoom;
        double cMinX = probe.ContentMinX, cMinY = probe.ContentMinY;
        double cMaxX = probe.ContentMaxX, cMaxY = probe.ContentMaxY;
        var textXs = probe.TextXs;
        var textYs = probe.TextYs;
        var textHeights = probe.TextHeights;

        // 目标 zoom：典型字高 → 30px 可读（上限 3000，防极端小字图分块失控）
        double zoomOcr = 1.0;
        if (typicalH > 0 && fitScale > 0)
            zoomOcr = Math.Min(30.0 / (typicalH * fitScale), 3000.0);
        if (zoomOcr < 1.0) zoomOcr = 1.0;

        var files = new List<string>();
        int sliceSeq = 0;  // 全局条带序号（多簇渲染时避免文件名互相覆盖）

        // 渲染一个窗口（黑底）并切条，跳过纯空白条
        List<string> RenderAndSlice(double zoom, double panX, double panY, bool allowInitFit)
        {
            // textOnly=true：只渲染文字不画图形，避免密集线条干扰 OCR 识别
            var res = CadWpfRenderer.RenderViewport(entities, viewW, viewH, zoom, panX, panY, cacheKey, allowInitFit, true);
            if (res?.Image?.Drawing == null) return new List<string>();
            var rtb = new RenderTargetBitmap(viewW, viewH, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, viewW, viewH));
                dc.DrawDrawing(res.Image.Drawing);
            }
            rtb.Render(dv);
            rtb.Freeze();
            int w = rtb.PixelWidth, h = rtb.PixelHeight;
            if (w < 64 || h < 64) return new List<string>();
            var outFiles = new List<string>();
            bool vertical = w >= h;
            int longSide = vertical ? w : h;
            for (int x0 = 0; x0 < longSide; x0 += stripPx - overlap)
            {
                int x1 = Math.Min(longSide, x0 + stripPx);
                CroppedBitmap crop = vertical
                    ? new CroppedBitmap(rtb, new Int32Rect(x0, 0, x1 - x0, h))
                    : new CroppedBitmap(rtb, new Int32Rect(0, x0, w, x1 - x0));
                if (CountBrightPixels(crop) == 0) continue;
                var fn = Path.Combine(outDir, $"cad_ocr_strip_{pageIndex}_{sliceSeq++}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(crop));
                using (var fs = File.Create(fn)) enc.Save(fs);
                outFiles.Add(fn);
            }
            return outFiles;
        }

        if (initZoom >= zoomOcr * 0.6)
        {
            // 初始视图字高已够（正常图纸）：直接用智能视图（zoom=1, pan=0 → initFit 定位）
            files.AddRange(RenderAndSlice(1.0, 0, 0, true));
        }
        else if (textXs != null && textXs.Count > 0)
        {
            // 初始视图字高不足（超大稀疏图纸）：文字簇聚类扫描。
            // 网格范围用文字实际分布（+边距），避免图形内容范围与文字分离时
            // 范围外文字被 clamp 到角格形成假簇。
            double winW = viewW / (fitScale * zoomOcr);
            double winH = viewH / (fitScale * zoomOcr);
            if (winW <= 0 || winH <= 0) winW = winH = 1.0;
            double txMin = textXs[0], txMax = textXs[textXs.Count - 1];
            double tyMin = textYs[0], tyMax = textYs[textYs.Count - 1];
            txMin -= (txMax - txMin) * 0.05 + winW;
            txMax += (txMax - txMin) * 0.05 + winW;
            tyMin -= (tyMax - tyMin) * 0.05 + winH;
            tyMax += (tyMax - tyMin) * 0.05 + winH;
            int cols = Math.Max(1, (int)Math.Ceiling((txMax - txMin) / winW));
            int rows = Math.Max(1, (int)Math.Ceiling((tyMax - tyMin) / winH));
            if ((long)cols * rows > 4_000_000) { cols = 2000; rows = 2000; }
            // 桶宽按 clamp 后的实际网格尺寸计算：直接除以 winW 会因桶数被截断
            // 导致范围外文字全部 clamp 到角格（角格假簇）
            double bw = (txMax - txMin) / cols;
            double bh = (tyMax - tyMin) / rows;
            // 桶内累加文字数与坐标和，簇中心用文字实际质心（网格只是分桶，
            // 格中心可能因边界偏移落在空白处）
            var cells = new Dictionary<long, (int n, double sx, double sy)>();
            for (int i = 0; i < textXs.Count; i++)
            {
                int cc = (int)Math.Max(0, Math.Min(cols - 1, (textXs[i] - txMin) / bw));
                int rr = (int)Math.Max(0, Math.Min(rows - 1, (textYs[i] - tyMin) / bh));
                long id = (long)rr * cols + cc;
                if (cells.TryGetValue(id, out var v))
                    cells[id] = (v.n + 1, v.sx + textXs[i], v.sy + textYs[i]);
                else
                    cells[id] = (1, textXs[i], textYs[i]);
            }
            double renderZoom = Math.Max(1.0, zoomOcr);
            var used = new List<(double x, double y)>();
            int taken = 0;
            foreach (var kv in cells.OrderByDescending(kv => kv.Value.n).Take(128))
            {
                if (taken >= 6) break;
                double cx = kv.Value.sx / kv.Value.n;
                double cy = kv.Value.sy / kv.Value.n;
                bool ov = false;
                foreach (var u in used)
                    if (Math.Abs(u.x - cx) < winW * 1.2 && Math.Abs(u.y - cy) < winH * 1.2) { ov = true; break; }
                if (ov) continue;
                used.Add((cx, cy));
                taken++;
                // 簇级目标 zoom：用簇内文字实际字高（中位数）算 30px 目标。
                // 全局典型字高可能与簇内字高差异大（超大稀疏图纸混排大小字），
                // 用簇内字高才能让该簇文字真正可读。
                double hh = 0;
                if (textHeights != null && textHeights.Count == textXs.Count)
                {
                    var hs = new List<double>();
                    for (int i = 0; i < textXs.Count; i++)
                        if (Math.Abs(textXs[i] - cx) < winW * 1.5 && Math.Abs(textYs[i] - cy) < winH * 1.5)
                            hs.Add(textHeights[i]);
                    if (hs.Count > 0) { hs.Sort(); hh = hs[hs.Count / 2]; }
                }
                double clusterZoom = hh > 0 ? Math.Min(30.0 / (hh * fitScale), 3000.0) : renderZoom;
                if (clusterZoom < 1.0) clusterZoom = 1.0;
                double panX = fitScale * clusterZoom * (cMinX - cx) + viewW / 2.0;
                double panY = fitScale * clusterZoom * (cy - cMaxY) + viewH / 2.0;
                files.AddRange(RenderAndSlice(clusterZoom, panX, panY, false));
            }
            if (files.Count == 0) files.AddRange(RenderAndSlice(1.0, 0, 0, true));
        }
        else
        {
            files.AddRange(RenderAndSlice(1.0, 0, 0, true));
        }
        return files.Count > 0 ? files : null;
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[RenderCadOcrStrips] {ex.Message}");
        return null;
    }
}

/// <summary>在 STA 线程执行 WPF 渲染并返回结果（CAD OCR 分块渲染用）</summary>
private static T RunInSta<T>(Func<T> fn)
{
 T result = default;
 Exception error = null;
 var t = new System.Threading.Thread(() =>
 {
 try { result = fn(); }
    catch (Exception ex) { error = ex; }
 });
 t.SetApartmentState(System.Threading.ApartmentState.STA);
 t.Start();
 t.Join();
 if (error != null)
 {
  System.Diagnostics.Debug.WriteLine($"[RunInSta] {error.Message}");
 }
 return result;
}

/// <summary>统计位图前景（亮色）像素数（阈值 200），用于跳过空白条（黑底 OCR 图）</summary>
private static int CountBrightPixels(System.Windows.Media.Imaging.BitmapSource bmp)
{
 try
 {
 int w = bmp.PixelWidth, h = bmp.PixelHeight;
 var stride = w * 4;
 var buf = new byte[stride * h];
 bmp.CopyPixels(buf, stride, 0);
 int bright = 0;
 int step = Math.Max(1, (w * h) / 200000); // 采样上限 ~20 万像素
 for (int i = 0; i < buf.Length; i += 4 * step)
 {
 byte b = buf[i], g = buf[i + 1], r = buf[i + 2];
 if ((r + g + b) / 3 > 80) bright++;  // 黑底图：>80 即清晰前景（含灰色文字），纯白 255 亦满足
 }
 return bright;
 }
 catch { return -1; }
}

private static void AccumulateBoundingBox(ACadSharp.Entities.Entity ent,
	ref double minX, ref double minY, ref double maxX, ref double maxY)
{
	switch (ent)
	{
 case ACadSharp.Entities.Line line:
 ExpandBox(line.StartPoint.X, line.StartPoint.Y, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(line.EndPoint.X, line.EndPoint.Y, ref minX, ref minY, ref maxX, ref maxY);
 break;
	case ACadSharp.Entities.Arc arc: // 必须在Circle之前匹配
	{
	 double r = Math.Max(0.1, arc.Radius);
	 double cx = arc.Center.X, cy = arc.Center.Y;
	 double sa = arc.StartAngle, ea = arc.EndAngle;
	 ExpandBox(cx + r * Math.Cos(sa), cy + r * Math.Sin(sa), ref minX, ref minY, ref maxX, ref maxY);
	 ExpandBox(cx + r * Math.Cos(ea), cy + r * Math.Sin(ea), ref minX, ref minY, ref maxX, ref maxY);
	 double sweep = ea - sa;
	 if (sweep < 0) sweep += Math.PI * 2;
	 for (int k = 0; k < 4; k++)
	 {
	 double angle = k * Math.PI / 2;
	 double normalized = angle - sa;
	 while (normalized < 0) normalized += Math.PI * 2;
	 while (normalized >= Math.PI * 2) normalized -= Math.PI * 2;
	 if (normalized < sweep)
	 ExpandBox(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle), ref minX, ref minY, ref maxX, ref maxY);
	 }
	 break;
	}
	case ACadSharp.Entities.Circle circle:
	ExpandBox(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius, ref minX, ref minY, ref maxX, ref maxY);
	ExpandBox(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius, ref minX, ref minY, ref maxX, ref maxY);
	break;
 case ACadSharp.Entities.Ellipse ellipse:
 try
 {
 var cx = ellipse.Center.X; var cy = ellipse.Center.Y;
 // MajorAxis是半长轴长度（非向量），RadiusRatio是短/长比
 var rMajor = Math.Max(ellipse.MajorAxis, 0.1);
 var rMinor = rMajor * Math.Max(ellipse.RadiusRatio, 0.01);
 // 用长轴作为近似半径（考虑旋转后短轴方向可能更长）
 var r = Math.Max(rMajor, rMinor);
 ExpandBox(cx - r, cy - r, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(cx + r, cy + r, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.LwPolyline poly:
 foreach (var v in poly.Vertices)
 ExpandBox(v.Location.X, v.Location.Y, ref minX, ref minY, ref maxX, ref maxY);
 break;
 case ACadSharp.Entities.Polyline2D poly2d:
 try
 {
 foreach (var v in poly2d.Vertices)
 ExpandBox(v.Location.X, v.Location.Y, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.Polyline3D poly3d:
 try
 {
 foreach (var v in poly3d.Vertices)
 ExpandBox(v.Location.X, v.Location.Y, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.Spline spline:
 try
 {
 foreach (var p in spline.ControlPoints)
 ExpandBox(p.X, p.Y, ref minX, ref minY, ref maxX, ref maxY);
 foreach (var p in spline.FitPoints)
 ExpandBox(p.X, p.Y, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.Hatch hatch:
 try
 {
 foreach (var path in hatch.Paths)
 {
 foreach (var pe in path.Entities)
 AccumulateBoundingBox(pe, ref minX, ref minY, ref maxX, ref maxY);
 }
 }
 catch { }
 break;
	 case ACadSharp.Entities.TextEntity text:
	 ExpandBox(text.InsertPoint.X, text.InsertPoint.Y, ref minX, ref minY, ref maxX, ref maxY);
	 break;
	 case ACadSharp.Entities.MText mtext:
	 ExpandBox(mtext.InsertPoint.X, mtext.InsertPoint.Y, ref minX, ref minY, ref maxX, ref maxY);
	 break;
 case ACadSharp.Entities.Insert insert:
 ExpandBox(insert.InsertPoint.X, insert.InsertPoint.Y, ref minX, ref minY, ref maxX, ref maxY);
 // 递归到块内容（考虑旋转）
	 try
	 {
	 if (insert.Block?.Entities != null)
	 {
	 double bx1 = double.MaxValue, by1 = double.MaxValue, bx2 = double.MinValue, by2 = double.MinValue;
	 foreach (var be in insert.Block.Entities)
	 AccumulateBoundingBox(be, ref bx1, ref by1, ref bx2, ref by2);
	 if (bx1 < bx2 && by1 < by2)
	 {
	 var sx = insert.XScale == 0 ? 1 : insert.XScale;
	 var sy = insert.YScale == 0 ? 1 : insert.YScale;
	 var rot = insert.Rotation;
	 var cosR = Math.Cos(rot);
	 var sinR = Math.Sin(rot);
	 // 四角点经过scale+rotation后取范围
	 double[] cornerX = { bx1 * sx, bx2 * sx, bx1 * sx, bx2 * sx };
	 double[] cornerY = { by1 * sy, by1 * sy, by2 * sy, by2 * sy };
	 for (int ci = 0; ci < 4; ci++)
	 {
	 double rx = cornerX[ci] * cosR - cornerY[ci] * sinR;
	 double ry = cornerX[ci] * sinR + cornerY[ci] * cosR;
	 ExpandBox(insert.InsertPoint.X + rx, insert.InsertPoint.Y + ry, ref minX, ref minY, ref maxX, ref maxY);
	 }
	 }
	 }
	 }
	 catch { }
 break;
	 case ACadSharp.Entities.Point point:
	 ExpandBox(point.Location.X, point.Location.Y, ref minX, ref minY, ref maxX, ref maxY);
	 break;
 case ACadSharp.Entities.Leader leader:
 try
 {
 foreach (var v in leader.Vertices)
 ExpandBox(v.X, v.Y, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.MLine mline:
 try
 {
 foreach (var v in mline.Vertices)
 ExpandBox(v.Position.X, v.Position.Y, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.Solid solid:
 try
 {
 ExpandBox(solid.FirstCorner.X, solid.FirstCorner.Y, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(solid.SecondCorner.X, solid.SecondCorner.Y, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(solid.ThirdCorner.X, solid.ThirdCorner.Y, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(solid.FourthCorner.X, solid.FourthCorner.Y, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.Face3D face:
 try
 {
 ExpandBox(face.FirstCorner.X, face.FirstCorner.Y, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(face.SecondCorner.X, face.SecondCorner.Y, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(face.ThirdCorner.X, face.ThirdCorner.Y, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(face.FourthCorner.X, face.FourthCorner.Y, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.Dimension dim:
 try
 {
 ExpandBox(dim.DefinitionPoint.X, dim.DefinitionPoint.Y, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(dim.InsertionPoint.X, dim.InsertionPoint.Y, ref minX, ref minY, ref maxX, ref maxY);
 ExpandBox(dim.TextMiddlePoint.X, dim.TextMiddlePoint.Y, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.Wipeout wipeout:
 try
 {
 foreach (var p in wipeout.ClipBoundaryVertices)
 ExpandBox(p.X, p.Y, ref minX, ref minY, ref maxX, ref maxY);
 }
 catch { }
 break;
 case ACadSharp.Entities.Ray ray:
 try { ExpandBox(ray.StartPoint.X, ray.StartPoint.Y, ref minX, ref minY, ref maxX, ref maxY); } catch { }
 break;
 case ACadSharp.Entities.XLine xline:
 try { ExpandBox(xline.FirstPoint.X, xline.FirstPoint.Y, ref minX, ref minY, ref maxX, ref maxY); } catch { }
 break;
 }
 }

/// <summary>
/// 收集实体的所有坐标点（用于bbox计算和离群值过滤）
/// </summary>
private static void AccumulatePoints(ACadSharp.Entities.Entity ent, System.Collections.Generic.List<(double x, double y)> points)
{
 switch (ent)
 {
 case ACadSharp.Entities.Line line:
 points.Add((line.StartPoint.X, line.StartPoint.Y));
 points.Add((line.EndPoint.X, line.EndPoint.Y));
 break;
 case ACadSharp.Entities.Arc arc: // 必须在Circle之前匹配（Arc继承自Circle）
 {
 double r = Math.Max(0.1, arc.Radius);
 double cx = arc.Center.X, cy = arc.Center.Y;
 double sa = arc.StartAngle, ea = arc.EndAngle;
 // 弧的端点
 double sx = cx + r * Math.Cos(sa), sy = cy + r * Math.Sin(sa);
 double ex = cx + r * Math.Cos(ea), ey = cy + r * Math.Sin(ea);
 points.Add((sx, sy));
 points.Add((ex, ey));
 // 检查弧是否跨越0/90/180/270度方向（这些方向是极值点）
 double sweep = ea - sa;
 if (sweep < 0) sweep += Math.PI * 2;
 for (int k = 0; k < 4; k++)
 {
 double angle = k * Math.PI / 2; // 0, 90, 180, 270度
 // 归一化到 [sa, sa+sweep) 范围内检查
 double normalized = angle - sa;
 while (normalized < 0) normalized += Math.PI * 2;
 while (normalized >= Math.PI * 2) normalized -= Math.PI * 2;
 if (normalized < sweep)
 {
 points.Add((cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)));
 }
 }
 break;
 }
 case ACadSharp.Entities.Circle circle:
 {
 double r = circle.Radius;
 points.Add((circle.Center.X - r, circle.Center.Y - r));
 points.Add((circle.Center.X + r, circle.Center.Y + r));
 break;
 }
 case ACadSharp.Entities.Ellipse ellipse:
 {
 double r = Math.Max(ellipse.MajorAxis, 1);
 points.Add((ellipse.Center.X - r, ellipse.Center.Y - r));
 points.Add((ellipse.Center.X + r, ellipse.Center.Y + r));
 break;
 }
 case ACadSharp.Entities.LwPolyline poly:
 foreach (var v in poly.Vertices)
 points.Add((v.Location.X, v.Location.Y));
 break;
 case ACadSharp.Entities.Polyline2D poly2d:
 foreach (var v in poly2d.Vertices)
 points.Add((v.Location.X, v.Location.Y));
 break;
 case ACadSharp.Entities.Polyline3D poly3d:
 foreach (var v in poly3d.Vertices)
 points.Add((v.Location.X, v.Location.Y));
 break;
 case ACadSharp.Entities.Spline spline:
 {
 foreach (var p in spline.ControlPoints) points.Add((p.X, p.Y));
 foreach (var p in spline.FitPoints) points.Add((p.X, p.Y));
 break;
 }
 case ACadSharp.Entities.TextEntity text:
 points.Add((text.InsertPoint.X, text.InsertPoint.Y));
 break;
 case ACadSharp.Entities.MText mtext:
 points.Add((mtext.InsertPoint.X, mtext.InsertPoint.Y));
 break;
 case ACadSharp.Entities.TableEntity table:
 {
 // 表格bbox = 插入点 + 列宽总和 × 行高总和
 double tw = 0, th = 0;
 if (table.Columns != null) foreach (var col in table.Columns) tw += col.Width;
 if (table.Rows != null) foreach (var row in table.Rows) th += row.Height;
 points.Add((table.InsertPoint.X, table.InsertPoint.Y));
 points.Add((table.InsertPoint.X + tw, table.InsertPoint.Y - th));
 break;
 }
 case ACadSharp.Entities.Insert insert:
 {
 points.Add((insert.InsertPoint.X, insert.InsertPoint.Y));
 // 递归到块内容（只取插入点+块内偏移）
 if (insert.Block?.Entities != null)
 {
 double bx1 = double.MaxValue, by1 = double.MaxValue, bx2 = double.MinValue, by2 = double.MinValue;
 foreach (var be in insert.Block.Entities)
 AccumulateBoundingBox(be, ref bx1, ref by1, ref bx2, ref by2);
 if (bx1 < bx2 && by1 < by2)
 {
 var sx = insert.XScale == 0 ? 1 : insert.XScale;
 var sy = insert.YScale == 0 ? 1 : insert.YScale;
 var rot = insert.Rotation;
 var cosR = Math.Cos(rot);
 var sinR = Math.Sin(rot);
 // 块内容bbox四角点，经过scale+rotation变换后，计算实际bbox
 double[] cornerX = { bx1 * sx, bx2 * sx, bx1 * sx, bx2 * sx };
 double[] cornerY = { by1 * sy, by1 * sy, by2 * sy, by2 * sy };
 double rMinX = double.MaxValue, rMinY = double.MaxValue, rMaxX = double.MinValue, rMaxY = double.MinValue;
 for (int ci = 0; ci < 4; ci++)
 {
 double rx = cornerX[ci] * cosR - cornerY[ci] * sinR;
 double ry = cornerX[ci] * sinR + cornerY[ci] * cosR;
 if (rx < rMinX) rMinX = rx;
 if (ry < rMinY) rMinY = ry;
 if (rx > rMaxX) rMaxX = rx;
 if (ry > rMaxY) rMaxY = ry;
 }
 points.Add((insert.InsertPoint.X + rMinX, insert.InsertPoint.Y + rMinY));
 points.Add((insert.InsertPoint.X + rMaxX, insert.InsertPoint.Y + rMaxY));
 }
 }
 break;
 }
 case ACadSharp.Entities.Point point:
 points.Add((point.Location.X, point.Location.Y));
 break;
 case ACadSharp.Entities.Leader leader:
 foreach (var v in leader.Vertices) points.Add((v.X, v.Y));
 break;
 case ACadSharp.Entities.MLine mline:
 foreach (var v in mline.Vertices) points.Add((v.Position.X, v.Position.Y));
 break;
 case ACadSharp.Entities.Solid solid:
 {
 points.Add((solid.FirstCorner.X, solid.FirstCorner.Y));
 points.Add((solid.SecondCorner.X, solid.SecondCorner.Y));
 points.Add((solid.ThirdCorner.X, solid.ThirdCorner.Y));
 points.Add((solid.FourthCorner.X, solid.FourthCorner.Y));
 break;
 }
 case ACadSharp.Entities.Dimension dim:
 {
 points.Add((dim.DefinitionPoint.X, dim.DefinitionPoint.Y));
 points.Add((dim.InsertionPoint.X, dim.InsertionPoint.Y));
 points.Add((dim.TextMiddlePoint.X, dim.TextMiddlePoint.Y));
 break;
 }
 case ACadSharp.Entities.Wipeout wipeout:
 {
 try { foreach (var p in wipeout.ClipBoundaryVertices) points.Add((p.X, p.Y)); } catch { }
 break;
 }
 case ACadSharp.Entities.Face3D face:
 {
 try
 {
 points.Add((face.FirstCorner.X, face.FirstCorner.Y));
 points.Add((face.SecondCorner.X, face.SecondCorner.Y));
 points.Add((face.ThirdCorner.X, face.ThirdCorner.Y));
 points.Add((face.FourthCorner.X, face.FourthCorner.Y));
 } catch { }
 break;
 }
 case ACadSharp.Entities.Ray ray:
 {
 points.Add((ray.StartPoint.X, ray.StartPoint.Y));
 break;
 }
 case ACadSharp.Entities.XLine xline:
 {
 points.Add((xline.FirstPoint.X, xline.FirstPoint.Y));
 break;
 }
 }
}

private static double Median(System.Collections.Generic.List<double> sorted)
{
 if (sorted.Count == 0) return 0;
 var s = sorted.OrderBy(v => v).ToList();
 return Percentile(s, 0.5);
}

private static double Percentile(System.Collections.Generic.List<double> sortedData, double percentile)
{
 if (sortedData.Count == 0) return 0;
 if (sortedData.Count == 1) return sortedData[0];
 // 确保已排序
 var sorted = sortedData.OrderBy(v => v).ToList();
 double index = percentile * (sorted.Count - 1);
 int lower = (int)Math.Floor(index);
 int upper = (int)Math.Ceiling(index);
 if (lower == upper) return sorted[lower];
 double fraction = index - lower;
 return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
}

private static void ExpandBox(double x, double y, ref double minX, ref double minY, ref double maxX, ref double maxY)
{
	if (x < minX) minX = x;
	if (y < minY) minY = y;
	if (x > maxX) maxX = x;
	if (y > maxY) maxY = y;
}

private BitmapSource RenderTextFile(int pageIndex, int dpi = 150)
{
if (FileType == "txt")
{
var text = File.ReadAllText(_currentPath, System.Text.Encoding.UTF8);
if (string.IsNullOrWhiteSpace(text))
text = "（文件内容为空）";
return RenderTextToImage(text, Path.GetFileName(_currentPath));
}
else if (FileType == "docx")
{
return RenderDocxPage(_currentPath, pageIndex, dpi);
}
return null;
}

    /// <summary>提取 docx 纯文本（用于 OCR 替代）</summary>
    public static string ExtractDocxText(string path)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return "";
            return body.InnerText;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Word 读取失败: {ex.Message}");
            return "";
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Word 格式化渲染 — 保留字体大小、粗体、表格、图片
    // ═══════════════════════════════════════════════════════════

    private abstract class DocBlock
    {
        public abstract void Draw(SD.Graphics g, ref float y, float maxW, ref float maxH);
    }

    private class DocTextBlock : DocBlock
    {
        public string Text { get; set; } = "";
        public float FontSize { get; set; } = 12;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool IsHeading { get; set; }
        public bool IsTitle { get; set; }
        public SD.Color Color { get; set; } = SD.Color.Black;

        public override void Draw(SD.Graphics g, ref float y, float maxW, ref float maxH)
        {
            if (string.IsNullOrEmpty(Text)) { y += FontSize * 1.5f; return; }
            var fontStyle = SD.FontStyle.Regular;
            if (Bold) fontStyle |= SD.FontStyle.Bold;
            if (Italic) fontStyle |= SD.FontStyle.Italic;
            using var font = new SD.Font("微软雅黑", FontSize, fontStyle);
            using var brush = new SD.SolidBrush(Color);
            using var fmt = new SD.StringFormat { Trimming = SD.StringTrimming.EllipsisCharacter, FormatFlags = SD.StringFormatFlags.NoClip };
            var layout = new SD.RectangleF(40, y, maxW - 80, 5000);
            var sz = g.MeasureString(Text, font, (int)(maxW - 80), fmt);
            g.DrawString(Text, font, brush, layout, fmt);
            y += sz.Height + 4;
            if (y > maxH) maxH = y;
        }
    }

    private class DocImageBlock : DocBlock
    {
        public SD.Bitmap Image { get; set; }

        public override void Draw(SD.Graphics g, ref float y, float maxW, ref float maxH)
        {
            if (Image == null) return;
            float maxImgW = maxW - 80;
            float scale = Math.Min(1, maxImgW / Image.Width);
            int w = (int)(Image.Width * scale);
            int h = (int)(Image.Height * scale);
            g.DrawImage(Image, 40, y, w, h);
            y += h + 6;
            if (y > maxH) maxH = y;
        }
    }

    private class DocTableBlock : DocBlock
    {
        public List<List<string>> Rows { get; set; } = new();

        public override void Draw(SD.Graphics g, ref float y, float maxW, ref float maxH)
        {
            if (Rows.Count == 0) return;
            int cols = Rows[0].Count;
            float tableW = maxW - 80;
            float colW = tableW / cols;

            using var headerFont = new SD.Font("微软雅黑", 10, SD.FontStyle.Bold);
            using var cellFont = new SD.Font("微软雅黑", 10);
            using var headerBg = new SD.SolidBrush(SD.Color.FromArgb(0x42, 0xA5, 0xF5));
            using var altBg = new SD.SolidBrush(SD.Color.FromArgb(0xF5, 0xF5, 0xF5));
            using var borderPen = new SD.Pen(SD.Color.FromArgb(0xCC, 0xCC, 0xCC), 1);
            using var textBrush = new SD.SolidBrush(SD.Color.Black);
            using var fmt = new SD.StringFormat { Trimming = SD.StringTrimming.EllipsisCharacter, FormatFlags = SD.StringFormatFlags.NoClip };
            var rowHeights = new float[Rows.Count];

            for (int r = 0; r < Rows.Count; r++)
            {
                float maxRh = 24;
                for (int c = 0; c < Rows[r].Count && c < cols; c++)
                {
                    var cellText = Rows[r][c] ?? "";
                    var font = r == 0 ? headerFont : cellFont;
                    var sz = g.MeasureString(cellText, font, (int)(colW - 8), fmt);
                    if (sz.Height + 6 > maxRh) maxRh = sz.Height + 6;
                }
                rowHeights[r] = maxRh;
            }

            // 绘制表格
            float startX = 40;
            for (int r = 0; r < Rows.Count; r++)
            {
                float rh = rowHeights[r];
                float x = startX;

                // 行背景
                var bgRect = new SD.RectangleF(startX, y, tableW, rh);
                if (r == 0)
                    g.FillRectangle(headerBg, bgRect);
                else if (r % 2 == 0)
                    g.FillRectangle(altBg, bgRect);

                for (int c = 0; c < cols; c++)
                {
                    var cellText = r < Rows[r].Count ? Rows[r][c] : "";
                    var font = r == 0 ? headerFont : cellFont;
                    var cellRect = new SD.RectangleF(x + 4, y + 3, colW - 8, rh - 6);
                    g.DrawString(cellText ?? "", font, textBrush, cellRect, fmt);
                    g.DrawRectangle(borderPen, x, y, colW, rh);
                    x += colW;
                }
                y += rh;
            }
            y += 6;
            if (y > maxH) maxH = y;
        }
    }

    private class DocSpacerBlock : DocBlock
    {
        public float Space { get; set; } = 8;
        public override void Draw(SD.Graphics g, ref float y, float maxW, ref float maxH)
        {
            y += Space;
            if (y > maxH) maxH = y;
        }
    }

    /// <summary>渲染 Word 文档，保留格式</summary>
private int CountDocxPages(string path)
    {
        try
        {
 var (blocks, _) = GetOrParseDocxBlocks(path);
 if (blocks.Count == 0) return 1;

            const float pageH = 1400;
            using var measureBmp = new SD.Bitmap(1, 1);
            using var measureG = SD.Graphics.FromImage(measureBmp);
            float y = 60;
            float maxH = 60;
            foreach (var b in blocks)
                b.Draw(measureG, ref y, 1000, ref maxH);

            int pages = (int)Math.Ceiling(maxH / pageH);
            return Math.Max(1, pages);
        }
        catch { return 1; }
    }

 private (List<DocBlock> blocks, Dictionary<string, SD.Bitmap> images) GetOrParseDocxBlocks(string path)
 {
 if (_docxBlocksCache != null && _docxImagesCache != null)
 return (_docxBlocksCache, _docxImagesCache);
 var result = ParseDocxBlocks(path);
 _docxBlocksCache = result.blocks;
 _docxImagesCache = result.images;
 return result;
 }

 private (List<DocBlock> blocks, Dictionary<string, SD.Bitmap> images) ParseDocxBlocks(string path)
 {
        var blocks = new List<DocBlock>();
        var imageParts = new Dictionary<string, SD.Bitmap>();

        try
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var mainPart = doc.MainDocumentPart;
            if (mainPart == null) return (blocks, imageParts);

            var body = mainPart.Document.Body;
            if (body == null) return (blocks, imageParts);

            foreach (var ipart in mainPart.ImageParts)
            {
                try
                {
                    using var stream = ipart.GetStream();
                    var img = new SD.Bitmap(stream);
                    var rid = mainPart.GetIdOfPart(ipart);
                    imageParts[rid] = img;
                }
                catch { }
            }

            foreach (var child in body.ChildElements)
            {
                if (child is ParagraphW para)
                {
                    var block = ParseParagraph(para, mainPart, imageParts);
                    if (block != null) blocks.Add(block);
                }
                else if (child is Table table)
                {
                    var block = ParseTable(table);
                    if (block != null) blocks.Add(block);
                    blocks.Add(new DocSpacerBlock { Space = 6 });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Word 解析失败: {ex.Message}");
        }

        return (blocks, imageParts);
    }

private BitmapSource RenderDocxPage(string path, int pageIndex, int dpi = 150)
{
 var (blocks, imageParts) = GetOrParseDocxBlocks(path);
 if (blocks.Count == 0)
 return RenderTextToImage("（文档内容为空）", Path.GetFileName(path));

 // 根据 dpi 放大画布，提高 OCR 识别率
 double dpiScale = dpi / 96.0;
 if (dpiScale < 1) dpiScale = 1;
 // 页面参数：A4 比例，上下左右各留白 60px（模拟 Word 页边距）
 float margin = 60;
 float pageH = 1400;
 float canvasW = 1000;
 float contentH = pageH - margin * 2; // 实际可用内容高度

 // 测量每个块高度
 var blockHeights = new List<float>();
 using (var measureBmp = new SD.Bitmap(1, 1))
 using (var measureG = SD.Graphics.FromImage(measureBmp))
 {
 float y = margin;
 float maxH = margin;
 foreach (var b in blocks)
 {
 float beforeY = y;
 b.Draw(measureG, ref y, canvasW, ref maxH);
 blockHeights.Add(y - beforeY);
 }
 }

 // 按页分割（每页上下留白 margin）
 var pageBlocks = new List<List<DocBlock>>();
 var currentPageBlocks = new List<DocBlock>();
 float pageY = margin;
 int currentBlocks = 0;

 for (int i = 0; i < blocks.Count; i++)
 {
 float blockH = blockHeights[i];
 if (pageY + blockH > margin + contentH && currentBlocks > 0)
 {
 pageBlocks.Add(currentPageBlocks);
 currentPageBlocks = new List<DocBlock>();
 pageY = margin;
 currentBlocks = 0;
 }
 currentPageBlocks.Add(blocks[i]);
 pageY += blockH;
 currentBlocks++;
 }
 if (currentPageBlocks.Count > 0)
 pageBlocks.Add(currentPageBlocks);

 if (pageBlocks.Count == 0)
 return RenderTextToImage("（文档内容为空）", Path.GetFileName(path));

 if (pageIndex < 0 || pageIndex >= pageBlocks.Count)
 pageIndex = 0;

 var pageBlockList = pageBlocks[pageIndex];
 float canvasH = pageH;

 // 画布按 dpiScale 放大，绘制时用 ScaleTransform 等比放大所有内容
 var bmp = new SD.Bitmap((int)(canvasW * dpiScale), (int)(canvasH * dpiScale), SDI.PixelFormat.Format24bppRgb);
 using var g = SD.Graphics.FromImage(bmp);
 g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
 g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
 g.Clear(SD.Color.White);
 // 整体缩放，让文字和布局等比放大
 g.ScaleTransform((float)dpiScale, (float)dpiScale);

 float drawY = margin; // 每页起始 Y = margin（上下留白一致）
 if (pageIndex == 0)
 {
 using var titleFont = new SD.Font("微软雅黑", 14, SD.FontStyle.Bold);
 using var titleBrush = new SD.SolidBrush(SD.Color.FromArgb(0x1A, 0x23, 0x7A));
 using var titlePen = new SD.Pen(SD.Color.FromArgb(0x21, 0x96, 0xF3), 2);
 g.DrawString(Path.GetFileName(path), titleFont, titleBrush, margin, 16);
 g.DrawLine(titlePen, margin, 44, canvasW - margin, 44);
 drawY = margin + 20;
 }

        if (pageBlocks.Count > 1)
        {
            using var pageFont = new SD.Font("微软雅黑", 9);
            using var pageBrush = new SD.SolidBrush(SD.Color.FromArgb(0x99, 0x99, 0x99));
            g.DrawString($"第 {pageIndex + 1} / {pageBlocks.Count} 页", pageFont, pageBrush, canvasW - 120, 16);
        }

        float drawMaxH = drawY;
        foreach (var b in pageBlockList)
            b.Draw(g, ref drawY, canvasW, ref drawMaxH);

 var result = ConvertBitmap(bmp, disposeInput: true);
 // 不在此处dispose图片缓存——图片由_docxImagesCache持有，会在Close()时统一清理
 return result;
    }

    /// <summary>解析 Word 段落 → 文本块或图片块</summary>
    private DocBlock ParseParagraph(ParagraphW para, MainDocumentPart mainPart, Dictionary<string, SD.Bitmap> images)
    {
        var pPr = para.ParagraphProperties;
        var styleId = pPr?.ParagraphStyleId?.Val?.Value ?? "";
        bool isHeading = styleId.StartsWith("Heading") || styleId.StartsWith("标题");
        int headingLevel = 0;
        if (isHeading && int.TryParse(new string(styleId.Where(char.IsDigit).ToArray()), out int hl))
            headingLevel = hl;

        var sb = new System.Text.StringBuilder();
        float fontSize = 12;
        bool bold = false;
        bool italic = false;
        SD.Color textColor = SD.Color.Black;
        DocImageBlock imageBlock = null;

        foreach (var child in para.ChildElements)
        {
            if (child is Run run)
            {
                // 运行属性
                var rPr = run.RunProperties;
                float runFontSize = fontSize;
                bool runBold = bold;
                bool runItalic = italic;

                if (rPr?.FontSize?.Val != null)
                {
                    if (int.TryParse(rPr.FontSize.Val.Value, out int halfPts))
                        runFontSize = halfPts / 2f;
                }
                if (rPr?.Bold != null) runBold = true;
                if (rPr?.Italic != null) runItalic = true;
                if (rPr?.Color?.Val != null)
                {
                    var hex = rPr.Color.Val.Value;
                    if (hex.Length == 6)
                    {
                        try
                        {
                            var r = Convert.ToByte(hex.Substring(0, 2), 16);
                            var gr = Convert.ToByte(hex.Substring(2, 2), 16);
                            var b = Convert.ToByte(hex.Substring(4, 2), 16);
                            textColor = SD.Color.FromArgb(r, gr, b);
                        }
                        catch { }
                    }
                }

                // 取最大字号
                if (runFontSize > fontSize) fontSize = runFontSize;
                if (runBold) bold = true;
                if (runItalic) italic = true;

                // 检查 run 中的 Drawing（图片）
                foreach (var drawChild in run.ChildElements)
                {
                    if (drawChild is DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline inline)
                    {
                        var blip = inline.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault();
                        if (blip?.Embed?.Value != null && images.TryGetValue(blip.Embed.Value, out var img))
                        {
                            imageBlock = new DocImageBlock { Image = img };
                        }
                    }
                }

                // 文本
                foreach (var t in run.Elements<Text>())
                    sb.Append(t.Text);

                foreach (var br in run.Elements<Break>())
                    sb.Append("\n");

                foreach (var tab in run.Elements<DocumentFormat.OpenXml.Wordprocessing.TabChar>())
                    sb.Append("\t");
            }
        }

        // 如果段落有图片，返回图片块
        if (imageBlock != null)
            return imageBlock;

        var text = sb.ToString().Trim();
        if (string.IsNullOrEmpty(text))
            return new DocSpacerBlock { Space = 6 };

        // 标题字号
        if (headingLevel > 0)
        {
            fontSize = headingLevel switch
            {
                1 => 22f,
                2 => 18f,
                3 => 16f,
                4 => 14f,
                _ => 13f,
            };
            bold = true;
            textColor = SD.Color.FromArgb(0x1A, 0x23, 0x7A);
        }

        return new DocTextBlock
        {
            Text = text,
            FontSize = fontSize,
            Bold = bold,
            Italic = italic,
            IsHeading = headingLevel > 0,
            Color = textColor,
        };
    }

    /// <summary>解析 Word 表格 → 表格块</summary>
    private DocTableBlock ParseTable(Table table)
    {
        var result = new DocTableBlock();

        foreach (var row in table.Elements<TableRowW>())
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<TableCellW>())
            {
                var cellText = cell.InnerText?.Trim() ?? "";
                cells.Add(cellText);
            }
            result.Rows.Add(cells);
        }

        return result.Rows.Count > 0 ? result : null;
    }

    /// 
    public static BitmapSource RenderTextToImage(string text, string title)
    {
        var lines = new List<string>();
        int maxChars = 50;

        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd();
            while (l.Length > maxChars)
            {
                lines.Add(l.Substring(0, maxChars));
                l = l.Substring(maxChars);
            }
            if (l.Length > 0) lines.Add(l);
        }

        int lineH = 28;
        int margin = 40;
        int w = Math.Max(600, margin * 2 + maxChars * 22);
        int h = margin * 2 + lines.Count * lineH;
        if (h > 1600) { h = 1600; lineH = Math.Max(16, (h - margin * 2) / lines.Count); }

        var bmp = new SD.Bitmap(w, h, SDI.PixelFormat.Format24bppRgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.White);

        using var titleFont = new SD.Font("微软雅黑", 16, SD.FontStyle.Bold);
        using var textFont = new SD.Font("微软雅黑", 12);
        using var titleBrush = new SD.SolidBrush(SD.Color.FromArgb(0, 51, 102));
        using var textBrush = new SD.SolidBrush(SD.Color.Black);
        using var grayBrush = new SD.SolidBrush(SD.Color.Gray);
        using var linePen = new SD.Pen(SD.Color.FromArgb(0, 120, 200), 2);

        g.DrawString(title, titleFont, titleBrush, margin, 12);
        g.DrawLine(linePen, margin, 40, w - margin, 40);

        int cy = 56;
        foreach (var ln in lines)
        {
            if (cy + lineH > h - margin)
            {
                g.DrawString("...（内容截断）", textFont, grayBrush, margin, cy);
                break;
            }
            g.DrawString(ln, textFont, textBrush, margin, cy);
            cy += lineH;
        }

 var result = ConvertBitmap(bmp, disposeInput: true);
 return result;
 }


        /// <summary>System.Drawing.Bitmap → WPF BitmapSource</summary>
 public static BitmapSource ConvertBitmap(SD.Bitmap bmp, bool disposeInput = true)
 {
 var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
 var data = bmp.LockBits(rect, SDI.ImageLockMode.ReadOnly, bmp.PixelFormat);
 
 BitmapSource bmpSrc;
 try
 {
 var pixelFormat = bmp.PixelFormat switch
 {
 SDI.PixelFormat.Format24bppRgb => PixelFormats.Bgr24,
 SDI.PixelFormat.Format32bppArgb => PixelFormats.Bgra32,
 SDI.PixelFormat.Format32bppRgb => PixelFormats.Bgr32,
 _ => PixelFormats.Bgr24
 };
 
 bmpSrc = BitmapSource.Create(
 bmp.Width, bmp.Height, 96, 96,
 pixelFormat, null,
 data.Scan0, data.Stride * bmp.Height, data.Stride);
 }
 finally
 {
 bmp.UnlockBits(data);
 }
 bmpSrc.Freeze();
 if (disposeInput) bmp.Dispose();
 return bmpSrc;
 }
 
 /// 
 public static BitmapSource ConvertBitmap(SD.Image img, bool disposeInput = true)
 {
 if (img is SD.Bitmap bmp)
 return ConvertBitmap(bmp, disposeInput);
 
 // 非 Bitmap 类型，先转为 Bitmap
 using var ms = new MemoryStream();
 img.Save(ms, SDI.ImageFormat.Png);
 ms.Position = 0;
 var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
 var src = decoder.Frames[0];
 src.Freeze();
 if (disposeInput) img.Dispose();
 return src;
 }

 public void Close()
 {
 lock (_pdfLock)
 {
 try { _pdfDoc?.Dispose(); } catch { }
 _pdfDoc = null;
 }
 _cadDoc = null;
 _cadSpaceNames.Clear();
 // 清理DOCX缓存
 if (_docxImagesCache != null)
 {
 foreach (var kv in _docxImagesCache)
 try { kv.Value?.Dispose(); } catch { }
 _docxImagesCache = null;
 }
 _docxBlocksCache = null;
 _pageCache.Clear();
 TotalPages = 0;
 }
 }

/// <summary>
/// 直接承载 DrawingVisual 的 UIElement，保持矢量渲染（缩放不模糊）。
/// 通过 Visual 层直接绘制，不走 RenderTargetBitmap 栅格化。
/// </summary>
public class CadVisualHost : FrameworkElement
{
 private readonly DrawingVisual _visual;

 public CadVisualHost(DrawingVisual visual)
 {
 _visual = visual ?? throw new ArgumentNullException(nameof(visual));
 AddVisualChild(_visual);
 }

 protected override int VisualChildrenCount => 1;

 protected override Visual GetVisualChild(int index)
 {
 if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
 return _visual;
 }

 protected override Size MeasureOverride(Size availableSize)
 {
 return new Size(Width > 0 ? Width : 0, Height > 0 ? Height : 0);
 }

 protected override Size ArrangeOverride(Size finalSize)
 {
 _visual.Offset = new System.Windows.Vector(0, 0);
 return finalSize;
 }
}
}
