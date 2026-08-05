using System;
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
 public int TotalPages { get; private set; }
 public string FileType { get; private set; } = "";
 private string _currentPath;
 private ACadSharp.CadDocument _cadDoc;
 private List<string> _cadSpaceNames = new(); // DWG 空间名称列表（模型 + 布局）
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
TotalPages = CountDocxPages(path);
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
			System.Diagnostics.Debug.WriteLine($"矢量渲染失败: {ex.Message}");
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
 if (doc == null) return null;
 }
 // 初始化SHX字体缓存
 InitShxFonts();

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

		// 计算包围盒
		double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
		if (isModelSpace)
		{
			var hMin = doc.Header.ModelSpaceExtMin;
			var hMax = doc.Header.ModelSpaceExtMax;
			if (!double.IsNaN(hMin.X) && !double.IsNaN(hMax.X))
			{ minX = hMin.X; minY = hMin.Y; maxX = hMax.X; maxY = hMax.Y; }
		}
		if (minX >= maxX || minY >= maxY)
			foreach (var ent in entities)
				AccumulateBoundingBox(ent, ref minX, ref minY, ref maxX, ref maxY);

		if (minX >= maxX || minY >= maxY) return null;

		// CAD 坐标→WPF 坐标：Y 轴翻转
		double dwgW = maxX - minX;
		double dwgH = maxY - minY;
		double margin = 50;
		double canvasW = dwgW + margin * 2;
		double canvasH = dwgH + margin * 2;

		var canvas = new Canvas
		{
			Width = canvasW,
			Height = canvasH,
			Background = new SolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x1E)),
		};

		// 标题
		var title = new TextBlock
		{
			Text = $"{Path.GetFileName(_currentPath)}  [{spaceName}]",
			FontSize = 14,
			FontWeight = FontWeights.Bold,
			Foreground = new SolidColorBrush(WpfColor.FromRgb(0xBB, 0xBB, 0xBB)),
		};
		Canvas.SetLeft(title, margin);
		Canvas.SetTop(title, 8);
		canvas.Children.Add(title);

		// 标题分隔线
		var sep = new Line
		{
			X1 = margin, Y1 = 36, X2 = canvasW - margin, Y2 = 36,
			Stroke = new SolidColorBrush(WpfColor.FromRgb(0x44, 0x44, 0x44)),
			StrokeThickness = 1,
		};
		canvas.Children.Add(sep);

 // 绘制实体
 double offsetX = margin - minX;
 double offsetY = margin + maxY;

 // 先绘制非文字实体（线/圆/弧/多段线/块引用），再绘制文字，避免文字被遮挡
 foreach (var ent in entities)
 {
 if (IsLayerOff(ent)) continue; // 跳过关闭/冻结的图层
 if (ent is ACadSharp.Entities.TextEntity || ent is ACadSharp.Entities.MText) continue;
 AddCadEntityToCanvas(canvas, ent, offsetX, offsetY, _cadDoc);
 }
 // 最后绘制文字
 foreach (var ent in entities)
 {
 if (IsLayerOff(ent)) continue;
 if (ent is ACadSharp.Entities.TextEntity || ent is ACadSharp.Entities.MText)
 AddCadEntityToCanvas(canvas, ent, offsetX, offsetY, _cadDoc);
 }

		// 边界框
		var border = new Rectangle
		{
			Width = dwgW + 4, Height = dwgH + 4,
			Stroke = new SolidColorBrush(WpfColor.FromRgb(0x55, 0x55, 0x55)),
			StrokeThickness = 1,
		};
		Canvas.SetLeft(border, margin - 2);
		Canvas.SetTop(border, margin - 2);
		canvas.Children.Add(border);

		// 底部信息
		var layerCount = doc.Layers?.Count() ?? 0;
		var info = new TextBlock
		{
			Text = $"[{spaceName}] 实体: {entities.Count} | 图层: {layerCount} | 范围: {dwgW:F1}×{dwgH:F1}",
			FontSize = 10,
			Foreground = new SolidColorBrush(WpfColor.FromRgb(0x88, 0x88, 0x88)),
		};
		Canvas.SetLeft(info, margin);
		Canvas.SetTop(info, canvasH - 25);
		canvas.Children.Add(info);

		return canvas;
	}

	/// 检查图层是否关闭或冻结
	private static bool IsLayerOff(ACadSharp.Entities.Entity ent)
	{
		try
		{
			if (ent.Layer == null) return false;
			if (!ent.Layer.IsOn) return true; // 图层关闭
			if (ent.Layer.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen)) return true; // 图层冻结
			return ent.IsInvisible; // 实体本身不可见
		}
		catch { return false; }
	}

	private void AddCadEntityToCanvas(Canvas canvas, ACadSharp.Entities.Entity ent,
 double offsetX, double offsetY, ACadSharp.CadDocument doc, int depth = 0)
	{
 if (depth > 10) return; // 防止无限递归
 var color = GetEntityWpfColor(ent);
 double penWidth = 0.5;

 switch (ent)
 {
 case ACadSharp.Entities.Line line:
 {
 var shape = new Line
 {
 X1 = line.StartPoint.X + offsetX,
 Y1 = offsetY - line.StartPoint.Y,
 X2 = line.EndPoint.X + offsetX,
 Y2 = offsetY - line.EndPoint.Y,
 Stroke = color,
 StrokeThickness = penWidth,
 };
 canvas.Children.Add(shape);
 break;
 }
 case ACadSharp.Entities.Arc arc:
 {
 double cx = arc.Center.X + offsetX;
 double cy = offsetY - arc.Center.Y;
 double r = arc.Radius;
 double startAngle = arc.StartAngle;
 double endAngle = arc.EndAngle;

 // 处理完整圆
 if (Math.Abs(endAngle - startAngle) >= Math.PI * 2 - 0.001)
 {
 var shape = new Ellipse
 {
 Width = r * 2, Height = r * 2,
 Stroke = color, StrokeThickness = penWidth,
 };
 Canvas.SetLeft(shape, cx - r);
 Canvas.SetTop(shape, cy - r);
 canvas.Children.Add(shape);
 break;
 }

 var p1 = new Point(cx + r * Math.Cos(startAngle), cy - r * Math.Sin(startAngle));
 var p2 = new Point(cx + r * Math.Cos(endAngle), cy - r * Math.Sin(endAngle));
 double sweep = endAngle - startAngle;
 bool isLargeArc = Math.Abs(sweep) > Math.PI;
 var sweepDir = sweep > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;

	 var path = new WpfPath
	 {
	 Stroke = color,
	 StrokeThickness = penWidth,
	 Data = new PathGeometry
	 {
	 Figures =
	 {
	 new PathFigure
	 {
	 StartPoint = p1,
	 Segments = { new ArcSegment(p2, new Size(r, r), 0, isLargeArc, sweepDir, true) }
	 }
	 }
	 }
	 };
	 canvas.Children.Add(path);
 break;
 }
 case ACadSharp.Entities.Circle circle:
 {
 double cx = circle.Center.X + offsetX;
 double cy = offsetY - circle.Center.Y;
 double r = circle.Radius;
 var shape = new Ellipse
 {
 Width = r * 2, Height = r * 2,
 Stroke = color, StrokeThickness = penWidth,
 };
 Canvas.SetLeft(shape, cx - r);
 Canvas.SetTop(shape, cy - r);
 canvas.Children.Add(shape);
 break;
 }
 case ACadSharp.Entities.Ellipse ellipse:
 {
 double cx = ellipse.Center.X + offsetX;
 double cy = offsetY - ellipse.Center.Y;
 double rx = ellipse.MajorAxis;
 double ry = ellipse.MajorAxis * ellipse.RadiusRatio;
 double rotation = ellipse.Rotation * 180.0 / Math.PI;
 var shape = new Ellipse
 {
 Width = rx * 2, Height = ry * 2,
 Stroke = color, StrokeThickness = penWidth,
 };
 if (Math.Abs(rotation) > 0.1)
 {
 shape.RenderTransform = new System.Windows.Media.RotateTransform(rotation);
 shape.RenderTransformOrigin = new Point(0.5, 0.5);
 }
 Canvas.SetLeft(shape, cx - rx);
 Canvas.SetTop(shape, cy - ry);
 canvas.Children.Add(shape);
 break;
 }
 case ACadSharp.Entities.LwPolyline poly:
 {
 var verts = poly.Vertices;
 if (verts.Count < 2) break;
 var pts = new PointCollection(verts.Count);
 foreach (var v in verts)
	 pts.Add(new Point(v.Location.X + offsetX, offsetY - v.Location.Y));
	 if (poly.IsClosed)
	 {
	 canvas.Children.Add(new Polygon
	 {
	 Points = pts,
	 Stroke = color, StrokeThickness = penWidth,
	 });
	 }
	 else
	 {
	 canvas.Children.Add(new Polyline
	 {
	 Points = pts,
	 Stroke = color, StrokeThickness = penWidth,
	 });
	 }
 break;
 }
 case ACadSharp.Entities.Polyline2D poly2d:
 {
 // 2D多段线
 var verts = poly2d.Vertices;
 if (verts == null || verts.Count < 2) break;
 var pts = new PointCollection(verts.Count);
 foreach (var v in verts)
	 pts.Add(new Point(v.Location.X + offsetX, offsetY - v.Location.Y));
	 canvas.Children.Add(new Polyline
	 {
	 Points = pts,
	 Stroke = color, StrokeThickness = penWidth,
	 });
 break;
 }
 case ACadSharp.Entities.Polyline3D poly3d:
 {
 var verts = poly3d.Vertices;
 if (verts == null || verts.Count < 2) break;
 var pts = new PointCollection(verts.Count);
 foreach (var v in verts)
	 pts.Add(new Point(v.Location.X + offsetX, offsetY - v.Location.Y));
	 canvas.Children.Add(new Polyline
	 {
	 Points = pts,
	 Stroke = color, StrokeThickness = penWidth,
	 });
 break;
 }
 case ACadSharp.Entities.Spline spline:
 {
 var fitPts = spline.FitPoints;
 if (fitPts == null || fitPts.Count < 2) break;
 var pts = new PointCollection(fitPts.Count);
 foreach (var p in fitPts)
	 pts.Add(new Point(p.X + offsetX, offsetY - p.Y));
	 canvas.Children.Add(new Polyline
	 {
	 Points = pts,
	 Stroke = color, StrokeThickness = penWidth,
	 });
 break;
 }
 case ACadSharp.Entities.Insert insert:
 {
 // ★ 块引用递归展开——这是最关键的修复！
 var block = insert.Block;
 if (block?.Entities == null) break;

 // 计算变换参数
 double insX = insert.InsertPoint.X + offsetX;
 double insY = offsetY - insert.InsertPoint.Y;
 double scaleX = insert.XScale == 0 ? 1 : insert.XScale;
 double scaleY = insert.YScale == 0 ? 1 : insert.YScale;
 double rotation = insert.Rotation * 180.0 / Math.PI;

 // 为块引用内容创建容器
 var blockCanvas = new Canvas();
 foreach (var subEnt in block.Entities)
 {
 if (IsLayerOff(subEnt)) continue;
 AddCadEntityToCanvas(blockCanvas, subEnt, offsetX, offsetY, doc, depth + 1);
 }

 if (blockCanvas.Children.Count == 0) break;

 // 应用变换：缩放+旋转+平移
 var tg = new System.Windows.Media.TransformGroup();
 tg.Children.Add(new System.Windows.Media.ScaleTransform(scaleX, scaleY));
 if (Math.Abs(rotation) > 0.1)
 tg.Children.Add(new System.Windows.Media.RotateTransform(rotation));
 tg.Children.Add(new System.Windows.Media.TranslateTransform(
 insX - offsetX * scaleX, insY - offsetY * scaleY));
 blockCanvas.RenderTransform = tg;
 blockCanvas.RenderTransformOrigin = new Point(0, 0);

 canvas.Children.Add(blockCanvas);
 break;
 }
 case ACadSharp.Entities.Hatch hatch:
 {
 // 填充——用边界路径中的实体绘制
 if (hatch.Paths == null) break;
 foreach (var path in hatch.Paths)
 {
	if (path?.Entities == null) continue;
	foreach (var subEnt in path.Entities)
	{
	if (IsLayerOff(subEnt)) continue;
	AddCadEntityToCanvas(canvas, subEnt, offsetX, offsetY, doc, depth + 1);
	}
 }
 break;
 }
 case ACadSharp.Entities.DimensionLinear dim:
 {
 // 线性标注——画标注线
 try
 {
 double x1 = dim.FirstPoint.X + offsetX;
 double y1 = offsetY - dim.FirstPoint.Y;
 double x2 = dim.SecondPoint.X + offsetX;
 double y2 = offsetY - dim.SecondPoint.Y;
 var dimLine = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = color, StrokeThickness = penWidth };
 canvas.Children.Add(dimLine);
 // 标注文字
 if (!string.IsNullOrEmpty(dim.Text))
 {
 var textPath = CreateTextGeometry(dim.Text, 12, color, (x1+x2)/2, (y1+y2)/2 - 8, 0);
 if (textPath != null) canvas.Children.Add(textPath);
 }
 }
 catch { }
 break;
 }
 case ACadSharp.Entities.Point point:
 {
 double px = point.Location.X + offsetX;
 double py = offsetY - point.Location.Y;
 var dot = new Ellipse { Width = 3, Height = 3, Fill = color };
 Canvas.SetLeft(dot, px - 1.5);
 Canvas.SetTop(dot, py - 1.5);
 canvas.Children.Add(dot);
 break;
 }
 case ACadSharp.Entities.TextEntity text:
 {
 double tx = text.InsertPoint.X + offsetX;
 double ty = offsetY - text.InsertPoint.Y;
 double h = Math.Max(8, text.Height);
 var rotation = text.Rotation * 180.0 / Math.PI;
 var textStr = text.Value ?? "";
 if (string.IsNullOrEmpty(textStr)) break;
 // 文字转矢量路径
 var textPath = CreateTextGeometry(textStr, h, color, tx, ty - h, rotation);
 if (textPath != null) canvas.Children.Add(textPath);
 break;
 }
 case ACadSharp.Entities.MText mtext:
 {
 double tx = mtext.InsertPoint.X + offsetX;
 double ty = offsetY - mtext.InsertPoint.Y;
 double h = Math.Max(8, mtext.Height);
 var rotation = mtext.Rotation * 180.0 / Math.PI;
 var mtextText = ParseMTextContent(mtext.PlainText ?? "");
 if (string.IsNullOrEmpty(mtextText)) break;
 // 多行文字：按行分割，每行转矢量
 var lines = mtextText.Split('\n');
 for (int i = 0; i < lines.Length; i++)
 {
 var lineText = lines[i].TrimEnd();
 if (string.IsNullOrEmpty(lineText)) continue;
 var textPath = CreateTextGeometry(lineText, h, color, tx, ty - h - i * h * 1.2, rotation);
 if (textPath != null) canvas.Children.Add(textPath);
 }
 break;
 }
 }
	}

	/// SHX字体解析缓存
	private static ShxFontCache _shxCache;

	/// 初始化SHX字体缓存（在Open时调用）
	private void InitShxFonts()
	{
		try
		{
			if (_shxCache != null) return;
			_shxCache = new ShxFontCache();
			var fontsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");
			if (!Directory.Exists(fontsDir)) return;
			_shxCache.LoadFonts(fontsDir);
		}
		catch { }
	}

	/// 将文字转为矢量路径
	/// 优先使用SHX矢量字体，回退到TrueType（仿宋）
	private System.Windows.Shapes.Path CreateTextGeometry(string text, double fontSize, Brush color,
 double x, double y, double rotation)
	{
 try
 {
 if (string.IsNullOrEmpty(text)) return null;

 var geometry = new StreamGeometry();
 using (var ctx = geometry.Open())
 {
 double xOffset = 0;
 foreach (var ch in text)
 {
 // 尝试用SHX字体解析
 var strokes = GetShxCharStrokes(ch, fontSize);
 if (strokes != null && strokes.Count > 0)
 {
 // 用SHX矢量笔画
 foreach (var stroke in strokes)
 {
 if (stroke.Count < 2) continue;
 ctx.BeginFigure(new Point(xOffset + stroke[0].X, stroke[0].Y), false, false);
 for (int i = 1; i < stroke.Count; i++)
 ctx.LineTo(new Point(xOffset + stroke[i].X, stroke[i].Y), true, false);
 }
 }
 else
 {
 // 回退到TrueType字形（仿宋）
 var ttfStrokes = GetTtfCharStrokes(ch, fontSize);
 if (ttfStrokes != null)
 {
 foreach (var stroke in ttfStrokes)
 {
 if (stroke.Count < 2) continue;
 ctx.BeginFigure(new Point(xOffset + stroke[0].X, stroke[0].Y), true, false);
 for (int i = 1; i < stroke.Count; i++)
 ctx.LineTo(new Point(xOffset + stroke[i].X, stroke[i].Y), true, false);
 }
 }
 }
 xOffset += fontSize; // 简单等宽间距
 }
 }

 var path = new System.Windows.Shapes.Path
 {
 Data = geometry,
 Stroke = color,
 StrokeThickness = Math.Max(0.5, fontSize / 20),
 };
 // 应用变换：先平移到目标位置，再旋转
 var tg = new System.Windows.Media.TransformGroup();
 tg.Children.Add(new System.Windows.Media.TranslateTransform(x, y));
 if (Math.Abs(rotation) > 0.1)
 tg.Children.Add(new System.Windows.Media.RotateTransform(rotation, 0, fontSize / 2));
 path.RenderTransform = tg;
 return path;
 }
 catch { return null; }
	}

	/// 从SHX字体获取字符的笔画
	private List<List<Point>> GetShxCharStrokes(char ch, double fontSize)
	{
		if (_shxCache == null) return null;
		try
		{
			// ASCII字符用unifont (Tssdeng.shx)
			if (ch < 128)
				return _shxCache.GetCharStrokes("Tssdeng", ch, fontSize);

			// 中文字符用bigfont (HZTXT.SHX)
			// 需要将Unicode字符转为GB2312编码
			var gbBytes = Encoding.GetEncoding("GB2312").GetBytes(ch.ToString());
			if (gbBytes.Length == 2)
			{
				int gbCode = (gbBytes[0] << 8) | gbBytes[1];
				return _shxCache.GetCharStrokes("HZTXT", (char)gbCode, fontSize);
			}
			return null;
		}
		catch { return null; }
	}

	/// 从TrueType字体获取字符的笔画（回退方案）
	private List<List<Point>> GetTtfCharStrokes(char ch, double fontSize)
	{
		try
		{
			var typeface = new Typeface(new System.Windows.Media.FontFamily("仿宋, 仿宋_GB2312, FangSong, SimFang, 宋体, SimSun"),
				FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
			if (!typeface.TryGetGlyphTypeface(out var glyphTypeface)) return null;

			ushort glyphIndex = glyphTypeface.CharacterToGlyphMap.ContainsKey(ch) ? glyphTypeface.CharacterToGlyphMap[ch] : (ushort)0;
			if (glyphIndex == 0) return null;

			// 获取字形几何
			var glyphGeometry = glyphTypeface.GetGlyphOutline(glyphIndex, fontSize, 1.0);
			// 转换为笔画列表
			var strokes = new List<List<Point>>();
			var pathGeometry = PathGeometry.CreateFromGeometry(glyphGeometry);
			foreach (var figure in pathGeometry.Figures)
			{
				var stroke = new List<Point>();
				stroke.Add(figure.StartPoint);
				foreach (var seg in figure.Segments)
				{
					if (seg is LineSegment ls)
						stroke.Add(ls.Point);
					else if (seg is PolyLineSegment pls)
						foreach (var p in pls.Points) stroke.Add(p);
				}
				if (stroke.Count >= 2)
					strokes.Add(stroke);
			}
			return strokes;
		}
		catch { return null; }
	}

 private static SolidColorBrush GetEntityWpfColor(ACadSharp.Entities.Entity ent)
 {
 var c = ent.Color;
 if (c.IsByLayer && ent.Layer != null)
 c = ent.Layer.Color;
 if (c.IsByLayer || c.IsByBlock)
 return Brushes.White;
 var rgb = c.GetRgb();
 if (rgb.Length >= 3)
 {
 if (rgb[0] == 0 && rgb[1] == 0 && rgb[2] == 0)
 return Brushes.White;
 return new SolidColorBrush(WpfColor.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]));
 }
 return Brushes.White;
 }

 /// 根据CAD文字样式名获取对应的WPF字体
 private static System.Windows.Media.FontFamily TryGetCadFontFamily(string styleName)
 {
 if (string.IsNullOrEmpty(styleName))
 return new System.Windows.Media.FontFamily("宋体, Microsoft YaHei, Arial");

 // CAD常见样式名→WPF字体映射
 var lowerName = styleName.ToLower();
 var fontMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
 {
 { "standard", "txt.shx, 宋体" },
 { "annotative", "宋体, Microsoft YaHei" },
 { "宋体", "宋体, SimSun" },
 { "仿宋", "仿宋, FangSong" },
 { "黑体", "黑体, SimHei" },
 { "楷体", "楷体, KaiTi" },
 { "工程字", "宋体, SimSun" },
 { "hztxt", "宋体, SimSun" },
 { "txt", "txt.shx, 宋体" },
 { " simplex", "Arial" },
 { "complex", "Arial" },
 };

 foreach (var kv in fontMap)
 {
 if (lowerName.Contains(kv.Key))
 return new System.Windows.Media.FontFamily(kv.Value);
 }

 // 默认用宋体（最接近CAD中文字体）
 return new System.Windows.Media.FontFamily("宋体, Microsoft YaHei, Arial");
 }

 /// 解析MText内容，清理格式化代码
 private static string ParseMTextContent(string raw)
 {
 if (string.IsNullOrEmpty(raw)) return "";

 var text = raw;
 // 移除MText格式化代码
 // \P = 段落换行, \n = 换行
 text = text.Replace("\\P", "\n").Replace("\\p", "\n");
 // \A = 对齐, \f = 字体, \H = 高度, \C = 颜色, \S = 堆叠, \Q = 倾斜
 text = System.Text.RegularExpressions.Regex.Replace(text, @"\\[AaFfHhCcSsQqWwTtOoLlKkDd][^;]*;", "");
 text = System.Text.RegularExpressions.Regex.Replace(text, @"\\[AaFfHhCcSsQqWwTtOoLlKkDd][^;]*", "");
 // { } 分组
 text = System.Text.RegularExpressions.Regex.Replace(text, @"[{}]", "");
 // %%d=度, %%c=直径, %%p=正负
 text = text.Replace("%%d", "°").Replace("%%c", "Φ").Replace("%%p", "±");
 text = text.Replace("%%D", "°").Replace("%%C", "Φ").Replace("%%P", "±");

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
		var (blocks, imageParts) = ParseDocxBlocks(_currentPath);
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

		// 清理图片缓存
		foreach (var kv in imageParts)
			try { kv.Value?.Dispose(); } catch { }

		return panel;
	}

	/// 
        public BitmapSource RenderPage(int pageIndex, int width = 0, int dpi = 150)
        {
            try
            {
                switch (FileType)
                {
                    case "pdf":
                        return RenderPdfPage(pageIndex, width, dpi);
case "image":
return LoadImageFile(_currentPath);
case "docx":
case "txt":
return RenderTextFile(pageIndex);
case "cad":
return RenderCadFile(pageIndex);
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
return ConvertBitmap(bmp);
}

/// <summary>DWG/DXF CAD 文件 — 提取文本信息显示</summary>
/// <summary>
/// 读取 DWG/DXF 文件，解析模型空间和布局空间列表。
/// </summary>
private void LoadCadDocument(string path)
{
 _cadDoc = null;
 _cadSpaceNames.Clear();

 try
 {
 using var reader = new ACadSharp.IO.DwgReader(path);
 reader.OnNotification += (s, e) => { System.Diagnostics.Debug.WriteLine($"ACadSharp: {e.Message}"); };
 _cadDoc = reader.Read();

 // 第一个空间始终是模型空间
 _cadSpaceNames.Add("模型");

 // 添加所有布局空间（按 TabOrder 排序）
 if (_cadDoc.Layouts != null)
 {
 var layouts = _cadDoc.Layouts
 .Where(l => !l.IsPaperSpace || l.Name != "Model")
 .OrderBy(l => l.TabOrder)
 .Select(l => l.Name)
 .ToList();
 _cadSpaceNames.AddRange(layouts);
 }

 TotalPages = Math.Max(1, _cadSpaceNames.Count);
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"CAD 加载失败: {ex.Message}");
 TotalPages = 1;
 _cadSpaceNames.Add("模型");
 }
}

private BitmapSource RenderCadFile(int pageIndex)
{
 try
 {
 var doc = _cadDoc;
 if (doc == null)
 {
 // 兜底：重新加载
 LoadCadDocument(_currentPath);
 doc = _cadDoc;
 if (doc == null)
 return RenderTextToImage($"📄 CAD 文件: {Path.GetFileName(_currentPath)}\n\n（文件加载失败）", Path.GetFileName(_currentPath));
 }

 // 根据 pageIndex 获取空间名称
 string spaceName = "模型";
 if (pageIndex >= 0 && pageIndex < _cadSpaceNames.Count)
 spaceName = _cadSpaceNames[pageIndex];
 bool isModelSpace = (pageIndex <= 0);

 // 获取对应空间的实体
 List<ACadSharp.Entities.Entity> entities;
 if (isModelSpace)
 {
 entities = doc.ModelSpace?.Entities?.ToList() ?? new List<ACadSharp.Entities.Entity>();
 }
 else
 {
 // 布局空间：通过 Layout.AssociatedBlock 获取 BlockRecord
 var layout = doc.Layouts?.FirstOrDefault(l => l.Name == spaceName);
 var block = layout?.AssociatedBlock;
 entities = block?.Entities?.ToList() ?? new List<ACadSharp.Entities.Entity>();
 }

 if (entities.Count == 0)
 return RenderTextToImage($"📄 CAD 文件: {Path.GetFileName(_currentPath)}\n\n[{spaceName}] 空间中未找到几何实体", Path.GetFileName(_currentPath));

 // 计算包围盒
 double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

 if (isModelSpace)
 {
 var headerMin = doc.Header.ModelSpaceExtMin;
 var headerMax = doc.Header.ModelSpaceExtMax;
 if (!double.IsNaN(headerMin.X) && !double.IsNaN(headerMax.X))
 {
 minX = headerMin.X; minY = headerMin.Y; maxX = headerMax.X; maxY = headerMax.Y;
 }
 }

 if (minX >= maxX || minY >= maxY)
 {
 // Header 无效或布局空间，遍历实体计算
 foreach (var ent in entities)
 AccumulateBoundingBox(ent, ref minX, ref minY, ref maxX, ref maxY);
 }

 if (minX >= maxX || minY >= maxY)
 return RenderCadInfoText(doc, entities.Count, spaceName);

		double dwgW = maxX - minX;
		double dwgH = maxY - minY;
		int canvasW = 1000;
		int canvasH = 700;
		int margin = 40;
		double availW = canvasW - margin * 2;
		double availH = canvasH - margin * 2;
		double scale = Math.Min(availW / dwgW, availH / dwgH);
		if (scale <= 0 || double.IsInfinity(scale) || double.IsNaN(scale))
			scale = 1.0;

		double offsetX = margin - minX * scale + (availW - dwgW * scale) / 2;
		double offsetY = margin + maxY * scale + (availH - dwgH * scale) / 2;

var bmp = new SD.Bitmap(canvasW, canvasH, SDI.PixelFormat.Format24bppRgb);
using var g = SD.Graphics.FromImage(bmp);
g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
g.Clear(SD.Color.FromArgb(0x1E, 0x1E, 0x1E)); // CAD 黑色背景

using var titleFont = new SD.Font("微软雅黑", 14, SD.FontStyle.Bold);
using var titleBrush = new SD.SolidBrush(SD.Color.FromArgb(0xBB, 0xBB, 0xBB)); // 浅灰标题
using var titlePen = new SD.Pen(SD.Color.FromArgb(0x44, 0x44, 0x44), 2);
g.DrawString($"{Path.GetFileName(_currentPath)}  [{spaceName}]", titleFont, titleBrush, margin, 12);
g.DrawLine(titlePen, margin, 40, canvasW - margin, 40);

using var borderPen = new SD.Pen(SD.Color.FromArgb(0x55, 0x55, 0x55), 1);
 g.DrawRectangle(borderPen, margin - 2, margin - 2, (int)(dwgW * scale) + 4, (int)(dwgH * scale) + 4);
 
 foreach (var ent in entities)
 DrawCadEntity(g, ent, scale, offsetX, offsetY);
 
using var infoFont = new SD.Font("微软雅黑", 9);
using var infoBrush = new SD.SolidBrush(SD.Color.FromArgb(0x88, 0x88, 0x88));
var layerCount = doc.Layers?.Count() ?? 0;
 g.DrawString($"[{spaceName}] 实体: {entities.Count} | 图层: {layerCount} | 范围: {dwgW:F1}x{dwgH:F1}",
 infoFont, infoBrush, margin, canvasH - 25);

		var result = ConvertBitmap(bmp);
		bmp.Dispose();
		return result;
	}
	catch (Exception ex)
	{
		return RenderTextToImage(
			$"📄 CAD 文件: {Path.GetFileName(_currentPath)}\n\n解析失败: {ex.Message}",
			Path.GetFileName(_currentPath));
	}
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
case ACadSharp.Entities.Circle circle: // Arc 继承自 Circle，会匹配此处
ExpandBox(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius, ref minX, ref minY, ref maxX, ref maxY);
ExpandBox(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius, ref minX, ref minY, ref maxX, ref maxY);
break;
		case ACadSharp.Entities.LwPolyline poly:
			foreach (var v in poly.Vertices)
				ExpandBox(v.Location.X, v.Location.Y, ref minX, ref minY, ref maxX, ref maxY);
			break;
		case ACadSharp.Entities.TextEntity text:
			ExpandBox(text.InsertPoint.X, text.InsertPoint.Y, ref minX, ref minY, ref maxX, ref maxY);
			break;
		case ACadSharp.Entities.MText mtext:
			ExpandBox(mtext.InsertPoint.X, mtext.InsertPoint.Y, ref minX, ref minY, ref maxX, ref maxY);
			break;
	}
}

private static void ExpandBox(double x, double y, ref double minX, ref double minY, ref double maxX, ref double maxY)
{
	if (x < minX) minX = x;
	if (y < minY) minY = y;
	if (x > maxX) maxX = x;
	if (y > maxY) maxY = y;
}

private static SD.Color GetEntityColor(ACadSharp.Entities.Entity ent)
{
	var c = ent.Color;
	if (c.IsByLayer && ent.Layer != null)
		c = ent.Layer.Color;
	if (c.IsByLayer || c.IsByBlock)
		return SD.Color.White; // 黑色背景上用白色
	var rgb = c.GetRgb();
	if (rgb.Length >= 3)
	{
		// 纯黑(0,0,0)在黑色背景上不可见，转为白色
		if (rgb[0] == 0 && rgb[1] == 0 && rgb[2] == 0)
			return SD.Color.White;
		return SD.Color.FromArgb(rgb[0], rgb[1], rgb[2]);
	}
	return SD.Color.White;
}

private static void DrawCadEntity(SD.Graphics g, ACadSharp.Entities.Entity ent,
	double scale, double offsetX, double offsetY)
{
	var color = GetEntityColor(ent);
	using var pen = new SD.Pen(color, 0.5f);
	using var brush = new SD.SolidBrush(color);

	switch (ent)
	{
		case ACadSharp.Entities.Line line:
		{
			float x1 = (float)(line.StartPoint.X * scale + offsetX);
			float y1 = (float)(offsetY - line.StartPoint.Y * scale);
			float x2 = (float)(line.EndPoint.X * scale + offsetX);
			float y2 = (float)(offsetY - line.EndPoint.Y * scale);
			g.DrawLine(pen, x1, y1, x2, y2);
			break;
		}
		case ACadSharp.Entities.Arc arc:
		{
			float cx = (float)(arc.Center.X * scale + offsetX);
			float cy = (float)(offsetY - arc.Center.Y * scale);
			float r = (float)(arc.Radius * scale);
			float startDeg = (float)(-arc.StartAngle * 180.0 / Math.PI);
			float sweepDeg = (float)(-(arc.EndAngle - arc.StartAngle) * 180.0 / Math.PI);
			if (sweepDeg < 0) sweepDeg += 360;
			g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, startDeg, sweepDeg);
			break;
		}
		case ACadSharp.Entities.Circle circle:
		{
			float cx = (float)(circle.Center.X * scale + offsetX);
			float cy = (float)(offsetY - circle.Center.Y * scale);
			float r = (float)(circle.Radius * scale);
			g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
			break;
		}
		case ACadSharp.Entities.LwPolyline poly:
		{
			var verts = poly.Vertices;
			if (verts.Count < 2) break;
			var points = new SD.PointF[verts.Count];
			for (int i = 0; i < verts.Count; i++)
			{
				points[i] = new SD.PointF(
					(float)(verts[i].Location.X * scale + offsetX),
					(float)(offsetY - verts[i].Location.Y * scale));
			}
			if (poly.IsClosed)
				g.DrawPolygon(pen, points);
			else
				g.DrawLines(pen, points);
			break;
		}
		case ACadSharp.Entities.TextEntity text:
		{
			float tx = (float)(text.InsertPoint.X * scale + offsetX);
			float ty = (float)(offsetY - text.InsertPoint.Y * scale);
			float h = Math.Max(8f, (float)(text.Height * scale));
			using var font = new SD.Font("微软雅黑", h);
			g.DrawString(text.Value ?? "", font, brush, tx, ty - h);
			break;
		}
		case ACadSharp.Entities.MText mtext:
		{
			float tx = (float)(mtext.InsertPoint.X * scale + offsetX);
			float ty = (float)(offsetY - mtext.InsertPoint.Y * scale);
			float h = Math.Max(8f, (float)(mtext.Height * scale));
			using var font = new SD.Font("微软雅黑", h);
			g.DrawString(mtext.PlainText ?? "", font, brush, tx, ty - h);
			break;
		}
	}
}

private BitmapSource RenderCadInfoText(ACadSharp.CadDocument doc, int entityCount, string spaceName = "模型")
{
	var sb = new System.Text.StringBuilder();
	sb.Append($"📄 CAD 文件: {Path.GetFileName(_currentPath)}\n\n");
	sb.Append($"当前空间: {spaceName}\n");
	sb.Append($"实体数量: {entityCount}\n");
	sb.Append($"文件大小: {new FileInfo(_currentPath).Length / 1024} KB\n\n");

	var layerNames = doc.Layers?.Select(l => l.Name).Take(20).ToList() ?? new List<string>();
	if (layerNames.Count > 0)
	{
 sb.Append($"图层 ({layerNames.Count}):\n");
 foreach (var l in layerNames)
 sb.Append($"  • {l}\n");
	}
	return RenderTextToImage(sb.ToString(), Path.GetFileName(_currentPath));
}

private BitmapSource RenderTextFile(int pageIndex)
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
return RenderDocxPage(_currentPath, pageIndex);
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
            var (blocks, _) = ParseDocxBlocks(path);
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

    private BitmapSource RenderDocxPage(string path, int pageIndex)
    {
        var (blocks, imageParts) = ParseDocxBlocks(path);
        if (blocks.Count == 0)
            return RenderTextToImage("（文档内容为空）", Path.GetFileName(path));

        const float pageH = 1400;
        const float canvasW = 1000;

        // 测量每个块高度
        var blockHeights = new List<float>();
        using (var measureBmp = new SD.Bitmap(1, 1))
        using (var measureG = SD.Graphics.FromImage(measureBmp))
        {
            float y = 60;
            float maxH = 60;
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
        int currentBlocks = 0;

        for (int i = 0; i < blocks.Count; i++)
        {
            float blockH = blockHeights[i];
            if (pageY + blockH > pageH && currentBlocks > 0)
            {
                pageBlocks.Add(currentPageBlocks);
                currentPageBlocks = new List<DocBlock>();
                pageY = 20;
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

        var bmp = new SD.Bitmap((int)canvasW, (int)canvasH, SDI.PixelFormat.Format24bppRgb);
        using var g = SD.Graphics.FromImage(bmp);
g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(SD.Color.White);

        float drawY = 20;
        if (pageIndex == 0)
        {
            using var titleFont = new SD.Font("微软雅黑", 14, SD.FontStyle.Bold);
            using var titleBrush = new SD.SolidBrush(SD.Color.FromArgb(0x1A, 0x23, 0x7A));
            using var titlePen = new SD.Pen(SD.Color.FromArgb(0x21, 0x96, 0xF3), 2);
            g.DrawString(Path.GetFileName(path), titleFont, titleBrush, 40, 16);
            g.DrawLine(titlePen, 40, 44, canvasW - 40, 44);
            drawY = 60;
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

        var result = ConvertBitmap(bmp);
        bmp.Dispose();
        foreach (var kv in imageParts)
            try { kv.Value?.Dispose(); } catch { }
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

        var result = ConvertBitmap(bmp);
        bmp.Dispose();
        return result;
    }

        /// <summary>System.Drawing.Bitmap → WPF BitmapSource</summary>
        public static BitmapSource ConvertBitmap(SD.Bitmap bmp)
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
            return bmpSrc;
        }

        /// <summary>PdfiumViewer 的 Image (System.Drawing.Image) → BitmapSource</summary>
        public static BitmapSource ConvertBitmap(SD.Image img)
        {
            if (img is SD.Bitmap bmp)
                return ConvertBitmap(bmp);

            // 非 Bitmap 类型，先转为 Bitmap
            using var ms = new MemoryStream();
            img.Save(ms, SDI.ImageFormat.Png);
            ms.Position = 0;
            var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var src = decoder.Frames[0];
            src.Freeze();
            return src;
        }

 public void Close()
 {
 try { _pdfDoc?.Dispose(); } catch { }
 _pdfDoc = null;
 _cadDoc = null;
 _cadSpaceNames.Clear();
 TotalPages = 0;
 }
    }
}
