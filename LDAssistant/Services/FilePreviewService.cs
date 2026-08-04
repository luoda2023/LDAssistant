using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

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
            string text = "";
            if (FileType == "txt")
            {
                text = File.ReadAllText(_currentPath, System.Text.Encoding.UTF8);
            }
            else if (FileType == "docx")
            {
                text = ExtractDocxText(_currentPath);
            }

            if (string.IsNullOrWhiteSpace(text))
                text = "（文件内容为空）";

            return RenderTextToImage(text, Path.GetFileName(_currentPath));
        }

        /// <summary>提取 docx 文本</summary>
        public static string ExtractDocxText(string path)
        {
            try
            {
                using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
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

        /// <summary>将文本渲染为图片</summary>
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
