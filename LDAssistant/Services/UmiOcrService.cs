using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Drawing;

namespace LDAssistant.Services
{
	/// <summary>
	/// RapidOCR-json 命令行 OCR 服务
	/// 直接调用 RapidOCR-json.exe，无需 Python/Qt 环境，仅 32MB
	/// 支持：简体中文识别、角度校正、黑底白字反色
	/// </summary>
	public class UmiOcrService
	{
		private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "umiocr_path.txt");

		/// <summary>
		/// 自动查找 RapidOCR-json.exe
		/// 优先：程序目录下打包的 RapidOCR/RapidOCR-json.exe
		/// 其次：用户保存的路径
		/// 最后：Umi-OCR 安装目录下的插件
		/// </summary>
		public static string AutoDetectRapidOcr()
		{
			// 1. 程序目录下打包的 RapidOCR
			var appDir = AppDomain.CurrentDomain.BaseDirectory;
			var bundled = Path.Combine(appDir, "RapidOCR", "RapidOCR-json.exe");
			if (File.Exists(bundled)) return bundled;

			// 2. 用户保存的路径
			var saved = GetSavedPath();
			if (!string.IsNullOrEmpty(saved) && File.Exists(saved)) return saved;

			// 3. Umi-OCR 安装目录下的插件
			var candidates = new[]
			{
				@"D:\Program Files\图片文字识别\UmiOCR-data\plugins\win7_x64_RapidOCR-json\RapidOCR-json.exe",
				@"C:\Program Files\图片文字识别\UmiOCR-data\plugins\win7_x64_RapidOCR-json\RapidOCR-json.exe",
				@"C:\Program Files (x86)\图片文字识别\UmiOCR-data\plugins\win7_x64_RapidOCR-json\RapidOCR-json.exe",
			};
			foreach (var p in candidates)
			{
				if (File.Exists(p)) return p;
			}

			return null;
		}

		public static string GetSavedPath()
		{
			try { return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath).Trim() : null; }
			catch { return null; }
		}

		public static void SavePath(string path)
		{
			try { File.WriteAllText(ConfigPath, path?.Trim() ?? ""); } catch { }
		}

		/// <summary>
		/// 调用 RapidOCR-json 识别图片
		/// </summary>
		/// <param name="imagePath">图片文件路径</param>
		/// <param name="format">返回格式：text=纯文本, json=含位置信息</param>
		/// <param name="invertColors">黑底白字反色</param>
		public async Task<UmiOcrResult> RecognizeAsync(string imagePath, string format = "text", bool invertColors = false)
		{
			var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log");
			void Log(string msg)
			{
				try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [RapidOCR] {msg}\n"); } catch { }
			}

			try
			{
				Log($"Recognize: {imagePath}, invert={invertColors}");

				if (!File.Exists(imagePath))
					return new UmiOcrResult { Error = "图片文件不存在" };

				// 查找 exe
				var exePath = AutoDetectRapidOcr();
				if (string.IsNullOrEmpty(exePath))
				{
					Log("未找到 RapidOCR-json.exe");
					return new UmiOcrResult { Error = "未找到 RapidOCR-json.exe" };
				}
				Log($"Using: {exePath}");

				// 反色处理
				string imgToOcr = imagePath;
				if (invertColors)
				{
					Log("反色处理（黑底白字）");
					imgToOcr = Path.GetTempFileName() + ".png";
					var invertedBytes = InvertImageColors(imagePath);
					await File.WriteAllBytesAsync(imgToOcr, invertedBytes);
				}

				// 调用 RapidOCR-json.exe
				var modelDir = Path.Combine(Path.GetDirectoryName(exePath), "models");
				var psi = new ProcessStartInfo
				{
					FileName = exePath,
					WorkingDirectory = Path.GetDirectoryName(exePath),
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					Arguments = $"--models models" +
						$" --det ch_PP-OCRv4_det_infer.onnx" +
						$" --cls ch_ppocr_mobile_v2.0_cls_infer.onnx" +
						$" --rec rec_ch_PP-OCRv4_infer.onnx" +
						$" --keys dict_chinese.txt" +
						$" --image_path \"{imgToOcr}\"" +
						$" --ensureAscii 0"
				};
				Log($"Args: {psi.Arguments}");

				using var proc = Process.Start(psi);
				var stdout = await proc.StandardOutput.ReadToEndAsync();
				var stderr = await proc.StandardError.ReadToEndAsync();
				await proc.WaitForExitAsync();
				Log($"Exit code: {proc.ExitCode}, stdout length: {stdout.Length}");

				// 清理反色临时文件
				if (invertColors && imgToOcr != imagePath)
				{
					try { File.Delete(imgToOcr); } catch { }
				}

				if (proc.ExitCode != 0)
				{
					Log($"Error: {stderr}");
					return new UmiOcrResult { Error = $"RapidOCR退出码 {proc.ExitCode}: {stderr}" };
				}

				return ParseResult(stdout, format, Log);
			}
			catch (Exception ex)
			{
				Log($"Exception: {ex}");
				return new UmiOcrResult { Error = ex.Message };
			}
		}

		/// <summary>
		/// 解析 RapidOCR-json 输出
		/// 输出格式：第一行版本信息，第二行 OCR init completed，第三行 JSON
		/// JSON: {"code":100,"data":[{"box":[...],"score":0.99,"text":"..."}]}
		/// </summary>
		private UmiOcrResult ParseResult(string output, string format, Action<string> Log)
		{
			try
			{
				// 找到 JSON 行（以 { 开头）
				string jsonLine = null;
				var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
				foreach (var line in lines)
				{
					var trimmed = line.Trim();
					if (trimmed.StartsWith("{"))
					{
						jsonLine = trimmed;
						break;
					}
				}

				if (string.IsNullOrEmpty(jsonLine))
				{
					Log("未找到JSON输出");
					return new UmiOcrResult { Error = "未找到OCR结果" };
				}

				using var doc = JsonDocument.Parse(jsonLine);
				var root = doc.RootElement;

				var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
				if (code != 100)
				{
					var data = root.TryGetProperty("data", out var d) ? d.GetString() : "";
					Log($"OCR失败 code={code}, data={data}");
					return new UmiOcrResult { Error = $"RapidOCR错误(code={code}): {data}" };
				}

				var result = new UmiOcrResult { Success = true };
				var sb = new StringBuilder();

				// data 是数组，每项含 text
				if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
				{
					foreach (var block in dataElem.EnumerateArray())
					{
						if (block.TryGetProperty("text", out var text))
						{
							sb.AppendLine(text.GetString());
						}
					}
				}

				result.FullText = sb.ToString().Trim();
				Log($"Result: len={result.FullText.Length}");
				return result;
			}
			catch (Exception ex)
			{
				Log($"ParseResult exception: {ex.Message}");
				return new UmiOcrResult { Error = $"解析结果失败: {ex.Message}" };
			}
		}

		/// <summary>
		/// 对图片做反色处理（黑底白字→白底黑字）
		/// </summary>
		private byte[] InvertImageColors(string imagePath)
		{
			using var srcBmp = new Bitmap(imagePath);
			var bmp = new Bitmap(srcBmp.Width, srcBmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
			using (var g = Graphics.FromImage(bmp))
			{
				g.Clear(Color.White);
				g.DrawImage(srcBmp, 0, 0, srcBmp.Width, srcBmp.Height);
			}

			var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
			var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, bmp.PixelFormat);
			var stride = data.Stride;
			var bytes = new byte[stride * data.Height];
			System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

			for (int i = 0; i < bytes.Length; i++)
				bytes[i] = (byte)(255 - bytes[i]);

			System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
			bmp.UnlockBits(data);

			using var ms = new MemoryStream();
			bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
			bmp.Dispose();
			return ms.ToArray();
		}
	}

	public class UmiOcrResult
	{
		public bool Success { get; set; }
		public string FullText { get; set; } = "";
		public string Error { get; set; } = "";
	}
}
