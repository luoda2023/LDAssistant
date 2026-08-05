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
 private string _initError;

        /// <summary>
        /// 创建并初始化 OCR 引擎。模型文件位于 appDir/models/v5/ 目录。
        /// </summary>
 public static OcrService Create()
 {
 try
 {
 var appDir = AppDomain.CurrentDomain.BaseDirectory;
 var modelDir = Path.Combine(appDir, "models", "v5");

 // 写日志
 var logPath = Path.Combine(appDir, "ocr_init.log");
 void Log(string msg)
 {
 try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
 }

 Log($"appDir={appDir}");
 Log($"modelDir={modelDir}");
 Log($"modelDir exists={Directory.Exists(modelDir)}");

 // 中文模型路径
 var detPath = Path.Combine(modelDir, "ch_PP-OCRv4_det_mobile.onnx");
 var clsPath = Path.Combine(modelDir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
 var recPath = Path.Combine(modelDir, "ch_PP-OCRv4_rec_mobile.onnx");
 var keysPath = Path.Combine(modelDir, "ppocrv4_chinese_dict.txt");

 Log($"det exists={File.Exists(detPath)}: {detPath}");
 Log($"cls exists={File.Exists(clsPath)}: {clsPath}");
 Log($"rec exists={File.Exists(recPath)}: {recPath}");
 Log($"keys exists={File.Exists(keysPath)}: {keysPath}");

 // 如果中文模型不存在，尝试用包内置的 latin 模型
 if (!File.Exists(recPath) || !File.Exists(keysPath))
 {
 detPath = Path.Combine(modelDir, "ch_PP-OCRv5_mobile_det.onnx");
 recPath = Path.Combine(modelDir, "latin_PP-OCRv5_rec_mobile_infer.onnx");
 keysPath = Path.Combine(modelDir, "ppocrv5_latin_dict.txt");
 Log($"Falling back to latin models");
 }

 Log("Creating RapidOcr instance...");
 var ocr = new RapidOcr();
 Log("RapidOcr created. Calling InitModels...");
 ocr.InitModels(
 detPath: detPath,
 clsPath: clsPath,
 recPath: recPath,
 keysPath: keysPath);
 Log("InitModels OK!");

 return new OcrService { _ocr = ocr };
 }
 catch (Exception ex)
 {
 var appDir = AppDomain.CurrentDomain.BaseDirectory;
 try { File.AppendAllText(Path.Combine(appDir, "ocr_init.log"),
 $"[{DateTime.Now:HH:mm:ss}] EXCEPTION: {ex}\n\n"); } catch { }
 return new OcrService { _initError = ex.ToString() }; // 保留错误信息
 }
 }

        /// <summary>
        /// 从图片文件识别文字。
        /// </summary>
 public OcrResult Recognize(string imagePath)
 {
 if (_ocr == null)
 return new OcrResult { FullText = $"OCR_ERROR: OCR 引擎未初始化\n{_initError}" };

 try
 {
 var appDir = AppDomain.CurrentDomain.BaseDirectory;
 void Log(string msg)
 {
 try { File.AppendAllText(Path.Combine(appDir, "ocr_init.log"), $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
 }

 Log($"Recognize: imagePath={imagePath}, exists={File.Exists(imagePath)}");

 if (!File.Exists(imagePath))
 return new OcrResult { FullText = "OCR_ERROR: 图片文件不存在" };

 // 用 SkiaSharp 加载图片
 Log("Loading image with SKBitmap.Decode...");
 using var bitmap = SKBitmap.Decode(imagePath);
 if (bitmap == null)
 {
 Log("SKBitmap.Decode returned null!");
 return new OcrResult { FullText = "OCR_ERROR: 无法加载图片" };
 }
 Log($"Image loaded: {bitmap.Width}x{bitmap.Height}, colorType={bitmap.ColorType}");

 Log("Calling _ocr.Detect...");
 var rapidResult = _ocr.Detect(bitmap, RapidOcrOptions.Default);
 Log($"Detect returned: {rapidResult != null}");
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
 try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log"),
 $"[{DateTime.Now:HH:mm:ss}] Recognize EXCEPTION: {ex}\n\n"); } catch { }
 return new OcrResult { FullText = $"OCR_ERROR: {ex.Message}\n{ex.StackTrace}" };
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
