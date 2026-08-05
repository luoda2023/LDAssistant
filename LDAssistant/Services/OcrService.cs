using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SkiaSharp;
using LDAssistant.Models;
// 别名消除歧义
using OcrResult = LDAssistant.Models.OcrResult;
using RapidOcrResult = RapidOcrNet.OcrResult;

namespace LDAssistant.Services
{
    /// <summary>
    /// 基于 RapidOcrNet (ONNX) 的 OCR 服务，内置中文模型，无需外部 exe。
    /// </summary>
    public class OcrService
    {
        private RapidOcr _ocr;

        /// <summary>
        /// 创建并初始化 OCR 引擎。模型文件位于 appDir/models/v5/ 目录。
        /// </summary>
        public static OcrService Create()
        {
            try
            {
                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                var modelDir = Path.Combine(appDir, "models", "v5");

                // 中文模型路径
                var detPath = Path.Combine(modelDir, "ch_PP-OCRv4_det_mobile.onnx");
                var clsPath = Path.Combine(modelDir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
                var recPath = Path.Combine(modelDir, "ch_PP-OCRv4_rec_mobile.onnx");
                var keysPath = Path.Combine(modelDir, "ppocrv4_chinese_dict.txt");

                // 如果中文模型不存在，尝试用包内置的 latin 模型
                if (!File.Exists(recPath) || !File.Exists(keysPath))
                {
                    detPath = Path.Combine(modelDir, "ch_PP-OCRv5_mobile_det.onnx");
                    recPath = Path.Combine(modelDir, "latin_PP-OCRv5_rec_mobile_infer.onnx");
                    keysPath = Path.Combine(modelDir, "ppocrv5_latin_dict.txt");
                }

                var ocr = new RapidOcr();
                ocr.InitModels(
                    detPath: detPath,
                    clsPath: clsPath,
                    recPath: recPath,
                    keysPath: keysPath);

                return new OcrService { _ocr = ocr };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OCR 初始化失败: {ex.Message}");
                return new OcrService(); // _ocr 为 null，Recognize 会返回错误
            }
        }

        /// <summary>
        /// 从图片文件识别文字。
        /// </summary>
        public OcrResult Recognize(string imagePath)
        {
            if (_ocr == null)
                return new OcrResult { FullText = "OCR_ERROR: OCR 引擎未初始化" };

            try
            {
                if (!File.Exists(imagePath))
                    return new OcrResult { FullText = "OCR_ERROR: 图片文件不存在" };

                // 用 SkiaSharp 加载图片
                using var bitmap = SKBitmap.Decode(imagePath);
                if (bitmap == null)
                    return new OcrResult { FullText = "OCR_ERROR: 无法加载图片" };

                var rapidResult = _ocr.Detect(bitmap, RapidOcrOptions.Default);
                if (rapidResult == null)
                    return new OcrResult { FullText = "OCR_ERROR: OCR 返回空结果" };

                var texts = new List<string>();
                var items = new List<OcrItem>();

 if (rapidResult.TextBlocks != null)
 {
 foreach (var block in rapidResult.TextBlocks)
 {
 if (!string.IsNullOrEmpty(block.Text))
 texts.Add(block.Text);

 if (block.BoxPoints != null && block.BoxPoints.Length >= 4)
 {
 double x1 = 1e9, y1 = 1e9, x2 = -1, y2 = -1;
 foreach (var pt in block.BoxPoints)
 {
 if (pt.X < x1) x1 = pt.X;
 if (pt.X > x2) x2 = pt.X;
 if (pt.Y < y1) y1 = pt.Y;
 if (pt.Y > y2) y2 = pt.Y;
 }
 items.Add(new OcrItem
 {
 Text = block.Text,
 X1 = x1, Y1 = y1, X2 = x2, Y2 = y2
 });
 }
 }
                }

                return new OcrResult
                {
                    FullText = string.Join("\n", texts),
                    Items = items
                };
            }
            catch (Exception ex)
            {
                return new OcrResult { FullText = $"OCR_ERROR: {ex.Message}" };
            }
        }

        /// <summary>
        /// 从 WPF BitmapSource 识别文字（先保存为临时 PNG）。
        /// </summary>
        public OcrResult Recognize(BitmapSource bitmapSource)
        {
            if (_ocr == null)
                return new OcrResult { FullText = "OCR_ERROR: OCR 引擎未初始化" };

            string tempImg = null;
            try
            {
                tempImg = Path.GetTempFileName() + ".png";
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                using var fs = File.OpenWrite(tempImg);
                encoder.Save(fs);
                return Recognize(tempImg);
            }
            finally
            {
                try { if (tempImg != null) File.Delete(tempImg); } catch { }
            }
        }
    }
}
