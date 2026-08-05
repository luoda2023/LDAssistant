using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SkiaSharp;
using OcrResult = LDAssistant.Models.OcrResult;
using RapidOcrResult = RapidOcrNet.OcrResult;
using LDAssistant.Models;

namespace LDAssistant.Services
{
	/// 基于 RapidOcrNet (ONNX) 的 OCR 服务，内置中文模型，无需外部 exe。
	public class OcrService
	{
 private RapidOcr _ocr;
 private string _initError;

 private static string _logPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log");
 private static void Log(string msg)
 {
 try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
 }

/// 创建并初始化 OCR 引擎。模型文件位于 appDir/models/v5/ 目录。
public static OcrService Create()
{
var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log");
void Log(string msg)
{
try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
}

try
{
var appDir = AppDomain.CurrentDomain.BaseDirectory;
var modelDir = Path.Combine(appDir, "models", "v5");

Log($"appDir={appDir}");
Log($"modelDir={modelDir}, exists={Directory.Exists(modelDir)}");

// 检查 native DLL 是否存在
var onnxDll = Path.Combine(appDir, "onnxruntime.dll");
var onnxShared = Path.Combine(appDir, "onnxruntime_providers_shared.dll");
var skiaDll = Path.Combine(appDir, "libSkiaSharp.dll");
Log($"onnxruntime.dll exists={File.Exists(onnxDll)}");
Log($"onnxruntime_providers_shared.dll exists={File.Exists(onnxShared)}");
Log($"libSkiaSharp.dll exists={File.Exists(skiaDll)}");

				// 中文模型路径
				var detPath = Path.Combine(modelDir, "ch_PP-OCRv4_det_mobile.onnx");
				var clsPath = Path.Combine(modelDir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
				var recPath = Path.Combine(modelDir, "ch_PP-OCRv4_rec_mobile.onnx");
				var keysPath = Path.Combine(modelDir, "ppocrv4_chinese_dict.txt");

				Log($"det={File.Exists(detPath)}, cls={File.Exists(clsPath)}, rec={File.Exists(recPath)}, keys={File.Exists(keysPath)}");

				// 如果中文模型不存在，尝试用包内置的 latin 模型
				if (!File.Exists(recPath) || !File.Exists(keysPath))
				{
					detPath = Path.Combine(modelDir, "ch_PP-OCRv5_mobile_det.onnx");
					recPath = Path.Combine(modelDir, "latin_PP-OCRv5_rec_mobile_infer.onnx");
					keysPath = Path.Combine(modelDir, "ppocrv5_latin_dict.txt");
					Log("Falling back to latin models");
				}

				Log("Creating RapidOcr...");
				var ocr = new RapidOcr();
				Log("Calling InitModels...");
				ocr.InitModels(detPath: detPath, clsPath: clsPath, recPath: recPath, keysPath: keysPath);
				Log("InitModels OK!");

				return new OcrService { _ocr = ocr };
			}
			catch (Exception ex)
			{
				Log($"EXCEPTION: {ex}");
				return new OcrService { _initError = ex.ToString() };
			}
		}

		/// 识别图片中的文字
		public OcrResult Recognize(string imagePath)
		{
			if (_ocr == null)
				return new OcrResult { FullText = $"OCR_ERROR: OCR 引擎未初始化\n{_initError}" };

			var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log");
			void Log(string msg)
			{
				try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
			}

			try
			{
		Log($"Recognize: {imagePath}, exists={File.Exists(imagePath)}");
			if (!File.Exists(imagePath))
				return new OcrResult { FullText = "OCR_ERROR: 图片文件不存在" };

			var fi = new FileInfo(imagePath);
			Log($"File size: {fi.Length} bytes");

 // 用 SkiaSharp 加载图片，如果失败则用 System.Drawing
 SKBitmap bitmap = null;
 try
 {
 bitmap = SKBitmap.Decode(imagePath);
 Log($"SKBitmap.Decode: {(bitmap != null ? $"{bitmap.Width}x{bitmap.Height} type={bitmap.ColorType} alpha={bitmap.AlphaType}" : "null")}");
 }
 catch (Exception ex) { Log($"SKBitmap.Decode exception: {ex.Message}"); }

				if (bitmap == null)
				{
					// 备选：用 System.Drawing.Bitmap 加载，再转为 SKBitmap
					Log("Trying System.Drawing.Bitmap fallback...");
					using var sdBmp = new System.Drawing.Bitmap(imagePath);
					Log($"System.Drawing.Bitmap: {sdBmp.Width}x{sdBmp.Height}");
					bitmap = ConvertToSkBitmap(sdBmp);
					Log($"Converted to SKBitmap: {(bitmap != null ? $"{bitmap.Width}x{bitmap.Height}" : "null")}");
				}

 if (bitmap == null)
 return new OcrResult { FullText = "OCR_ERROR: 无法加载图片" };

	 // 确保颜色类型为 Bgra8888（RapidOcr 需要）
	 if (bitmap.ColorType != SKColorType.Bgra8888)
	 {
	 Log($"Converting {bitmap.ColorType} -> Bgra8888");
	 var converted = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
	 bitmap.CopyTo(converted);
	 bitmap.Dispose();
	 bitmap = converted;
	 Log($"Converted: {bitmap.Width}x{bitmap.Height} type={bitmap.ColorType}");
	 }

	 // 检测是否为黑底白字（暗背景），如果是则反色
	 if (IsDarkBackground(bitmap))
	 {
	 Log("Detected dark background, inverting image...");
	 InvertBitmap(bitmap);
	 Log("Image inverted.");
	 }

	 using (bitmap)
	 {
	 Log("Calling _ocr.Detect...");
	 var rapidResult = _ocr.Detect(bitmap, RapidOcrOptions.Default);
	 Log($"Detect returned: {rapidResult != null}");
 return BuildResult(rapidResult);
 }
			}
			catch (Exception ex)
			{
				Log($"Recognize EXCEPTION: {ex}");
				return new OcrResult { FullText = $"OCR_ERROR: {ex.Message}" };
			}
		}

		/// 将 System.Drawing.Bitmap 转为 SKBitmap
		private SKBitmap ConvertToSkBitmap(System.Drawing.Bitmap bmp)
		{
			var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
			var skBmp = new SKBitmap(info);
			var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
				System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
			try
			{
				IntPtr skPtr = skBmp.GetPixels();
				// 逐行复制像素数据
				byte[] rowBuffer = new byte[bmp.Width * 4];
				for (int y = 0; y < bmp.Height; y++)
				{
					System.Runtime.InteropServices.Marshal.Copy(
						data.Scan0 + y * data.Stride,
						rowBuffer, 0, bmp.Width * 4);
					System.Runtime.InteropServices.Marshal.Copy(
						rowBuffer, 0,
						skPtr + y * skBmp.RowBytes, bmp.Width * 4);
				}
			}
			finally
			{
				bmp.UnlockBits(data);
			}
			return skBmp;
		}

	private OcrResult BuildResult(RapidOcrResult rapidResult)
	{
		var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log");
		void Log(string msg)
		{
			try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
		}

		if (rapidResult == null)
			return new OcrResult { FullText = "OCR_ERROR: OCR 返回空结果" };

		try
		{
			int tbCount = rapidResult.TextBlocks != null ? rapidResult.TextBlocks.Count() : 0;
			Log($"TextBlocks count: {tbCount}");

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

			Log($"Result: texts={texts.Count}, items={items.Count}, fullTextLen={string.Join("\n", texts).Length}");

			return new OcrResult
			{
				FullText = string.Join("\n", texts),
				Items = items
			};
		}
		catch (Exception ex)
		{
			Log($"BuildResult EXCEPTION: {ex}");
			return new OcrResult { FullText = $"OCR_ERROR: BuildResult异常: {ex.Message}" };
		}
	}

		/// 识别 BitmapSource
public OcrResult Recognize(BitmapSource bitmapSource)
{
 if (_ocr == null)
 return new OcrResult { FullText = "OCR_ERROR: OCR 引擎未初始化" };
 if (bitmapSource == null)
 return new OcrResult { FullText = "OCR_ERROR: bitmapSource 为空" };

 string tempImg = null;
 try
 {
 // 用 System.Drawing.Bitmap 保存，避免 PngBitmapEncoder 跨线程问题
 tempImg = Path.GetTempFileName() + ".png";
 int width = bitmapSource.PixelWidth;
 int height = bitmapSource.PixelHeight;
 int stride = width * ((bitmapSource.Format.BitsPerPixel + 7) / 8);
 byte[] pixels = new byte[height * stride];
 bitmapSource.CopyPixels(pixels, stride, 0);

 using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
 var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
 System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
 try
 {
 System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
 }
 finally
 {
 bmp.UnlockBits(data);
 }
 bmp.Save(tempImg, System.Drawing.Imaging.ImageFormat.Png);
 return Recognize(tempImg);
 }
 finally
 {
 try { if (tempImg != null) File.Delete(tempImg); } catch { }
 }
 }

	/// 检测图片是否为暗背景（黑底白字等）
	private bool IsDarkBackground(SKBitmap bitmap)
	{
		try
		{
		// 采样图片中心区域和四角，计算平均亮度
		int w = bitmap.Width;
		int h = bitmap.Height;
		long totalBrightness = 0;
		int sampleCount = 0;
		int stepX = Math.Max(1, w / 20);
		int stepY = Math.Max(1, h / 20);

		for (int y = 0; y < h; y += stepY)
		{
		for (int x = 0; x < w; x += stepX)
		{
		var pixel = bitmap.GetPixel(x, y);
		// 亮度 = (R*0.299 + G*0.587 + B*0.114)
		int brightness = (int)(pixel.Red * 0.299 + pixel.Green * 0.587 + pixel.Blue * 0.114);
		totalBrightness += brightness;
		sampleCount++;
		}
		}

		if (sampleCount == 0) return false;
		double avg = (double)totalBrightness / sampleCount;
		Log($"Average brightness: {avg:F1} (threshold=128)");
		// 亮度 < 128 表示暗背景
		return avg < 128;
		}
		catch (Exception ex) { Log($"IsDarkBackground exception: {ex.Message}"); return false; }
	}

	/// 反色处理（黑底白字 → 白底黑字）
	private void InvertBitmap(SKBitmap bitmap)
	{
		IntPtr ptr = bitmap.GetPixels();
		int len = bitmap.RowBytes * bitmap.Height;
		byte[] buffer = new byte[len];
		System.Runtime.InteropServices.Marshal.Copy(ptr, buffer, 0, len);

		// BGRA 格式，每4字节一个像素，反转 RGB（不动 Alpha）
		for (int i = 0; i < len; i += 4)
		{
		buffer[i] = (byte)(255 - buffer[i]);	 // B
		buffer[i + 1] = (byte)(255 - buffer[i + 1]); // G
		buffer[i + 2] = (byte)(255 - buffer[i + 2]); // R
		// buffer[i + 3] 是 Alpha，不动
		}

		System.Runtime.InteropServices.Marshal.Copy(buffer, 0, ptr, len);
		bitmap.NotifyPixelsChanged();
	}
 }
}
