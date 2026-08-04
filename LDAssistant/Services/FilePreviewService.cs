using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

// 别名消除歧义
using Run = DocumentFormat.OpenXml.Wordprocessing.Run;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;
using Tab = DocumentFormat.OpenXml.Wordprocessing.Tab;
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
        public int TotalPages { get; private set; }
        public string FileType { get; private set; } = "";
        private string _currentPath;

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
                    case "txt":
                        TotalPages = 1;
                        return true;
                    case "cad":
                        TotalPages = 1;
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

        private BitmapSource LoadImageFile(string path)
        {
            var bmp = new SD.Bitmap(path);
            return ConvertBitmap(bmp);
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
            return RenderDocxFormatted(_currentPath);
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

    /// <summary>Word 文档中的块元素</summary>
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
            var font = new SD.Font("微软雅黑", FontSize, fontStyle);
            var brush = new SD.SolidBrush(Color);
            var fmt = new SD.StringFormat { Trimming = SD.StringTrimming.WordWrap };
            var layout = new SD.RectangleF(40, y, maxW - 80, 5000);
            var sz = g.MeasureString(Text, font, (int)(maxW - 80), fmt);
            if (IsHeading || IsTitle)
            {
                g.DrawString(Text, font, brush, layout, fmt);
            }
            else
            {
                g.DrawString(Text, font, brush, layout, fmt);
            }
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
        public List<float> ColWidths { get; set; } = new();

        public override void Draw(SD.Graphics g, ref float y, float maxW, ref float maxH)
        {
            if (Rows.Count == 0) return;
            int cols = Rows[0].Count;
            float tableW = maxW - 80;
            float colW = tableW / cols;

            var headerFont = new SD.Font("微软雅黑", 10, SD.FontStyle.Bold);
            var cellFont = new SD.Font("微软雅黑", 10);
            var headerBg = new SD.SolidBrush(SD.Color.FromArgb(0x42, 0xA5, 0xF5));
            var altBg = new SD.SolidBrush(SD.Color.FromArgb(0xF5, 0xF5, 0xF5));
            var borderPen = new SD.Pen(SD.Color.FromArgb(0xCC, 0xCC, 0xCC), 1);
            var textBrush = new SD.SolidBrush(SD.Color.Black);

            // 计算行高
            var fmt = new SD.StringFormat { Trimming = SD.StringTrimming.WordWrap };
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
    private BitmapSource RenderDocxFormatted(string path)
    {
        var blocks = new List<DocBlock>();

        try
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var mainPart = doc.MainDocumentPart;
            if (mainPart == null) return RenderTextToImage("（无法读取 Word 文档）", Path.GetFileName(path));

            var body = mainPart.Document.Body;
            if (body == null) return RenderTextToImage("（Word 文档为空）", Path.GetFileName(path));

            // 获取图片关系
            var imageParts = new Dictionary<string, SD.Bitmap>();
            foreach (var rel in mainPart.GetRelationshipsByType("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"))
            {
                try
                {
                    var part = mainPart.GetPartById(rel.Id);
                    if (part != null)
                    {
                        using var stream = part.GetStream();
                        var img = new SD.Bitmap(stream);
                        imageParts[rel.Id] = img;
                    }
                }
                catch { }
            }

            // 遍历 body 子元素
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
            System.Diagnostics.Debug.WriteLine($"Word 渲染失败: {ex.Message}");
            return RenderTextToImage($"（Word 读取失败: {ex.Message}）", Path.GetFileName(path));
        }

        if (blocks.Count == 0)
            return RenderTextToImage("（文档内容为空）", Path.GetFileName(path));

        // 先测量高度
        float canvasW = 1000;
        float canvasH = 200;
        using (var measureBmp = new SD.Bitmap(1, 1))
        using (var measureG = SD.Graphics.FromImage(measureBmp))
        {
            float y = 60;
            float maxH = 60;
            foreach (var b in blocks)
            {
                b.Draw(measureG, ref y, canvasW, ref maxH);
            }
            canvasH = Math.Min(maxH + 40, 8000);
        }

        // 正式绘制
        var bmp = new SD.Bitmap((int)canvasW, (int)canvasH, SDI.PixelFormat.Format24bppRgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(SD.Color.White);

        // 标题
        var titleFont = new SD.Font("微软雅黑", 14, SD.FontStyle.Bold);
        var titleBrush = new SD.SolidBrush(SD.Color.FromArgb(0x1A, 0x23, 0x7A));
        g.DrawString(Path.GetFileName(path), titleFont, titleBrush, 40, 16);
        g.DrawLine(new SD.Pen(SD.Color.FromArgb(0x21, 0x96, 0xF3), 2), 40, 44, canvasW - 40, 44);

        float drawY = 60;
        float drawMaxH = 60;
        foreach (var b in blocks)
        {
            b.Draw(g, ref drawY, canvasW, ref drawMaxH);
        }

        return ConvertBitmap(bmp);
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

                foreach (var tab in run.Elements<Tab>())
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

        var titleFont = new SD.Font("微软雅黑", 16, SD.FontStyle.Bold);
        var textFont = new SD.Font("微软雅黑", 12);
        var titleBrush = new SD.SolidBrush(SD.Color.FromArgb(0, 51, 102));
        var textBrush = new SD.SolidBrush(SD.Color.Black);
        var linePen = new SD.Pen(SD.Color.FromArgb(0, 120, 200), 2);

        g.DrawString(title, titleFont, titleBrush, margin, 12);
        g.DrawLine(linePen, margin, 40, w - margin, 40);

        int cy = 56;
        foreach (var ln in lines)
        {
            if (cy + lineH > h - margin)
            {
                g.DrawString("...（内容截断）", textFont,
                    new SD.SolidBrush(SD.Color.Gray), margin, cy);
                break;
            }
            g.DrawString(ln, textFont, textBrush, margin, cy);
            cy += lineH;
        }

        return ConvertBitmap(bmp);
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
            TotalPages = 0;
        }
    }
}
