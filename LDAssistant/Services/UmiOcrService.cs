using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
 // 1. 程序目录下打包的 RapidOCR（优先用 exe 实际路径，兼容单文件发布）
 var appDir = AppDomain.CurrentDomain.BaseDirectory;
 // 单文件发布时 BaseDirectory 可能是临时解压目录，用 ProcessPath 更可靠
 try
 {
 var procPath = Environment.ProcessPath;
 if (!string.IsNullOrEmpty(procPath))
 appDir = Path.GetDirectoryName(procPath) ?? appDir;
 }
 catch { }
 var bundled = Path.Combine(appDir, "RapidOCR", "RapidOCR-json.exe");
 if (File.Exists(bundled)) return bundled;

 // 1b. 也可能在 BaseDirectory 下
 var bundled2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RapidOCR", "RapidOCR-json.exe");
 if (File.Exists(bundled2)) return bundled2;

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
 var logDir = AppDomain.CurrentDomain.BaseDirectory;
 try
 {
 var procPath = Environment.ProcessPath;
 if (!string.IsNullOrEmpty(procPath))
 logDir = Path.GetDirectoryName(procPath) ?? logDir;
 }
 catch { }
 // 两个路径都写，确保能看到日志
 var logPath = Path.Combine(logDir, "ocr_init.log");
 var logPath2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log");
 void Log(string msg)
 {
 try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [RapidOCR] {msg}\n"); } catch { }
 try { File.AppendAllText(logPath2, $"[{DateTime.Now:HH:mm:ss}] [RapidOCR] {msg}\n"); } catch { }
 }

try
{
 if (!File.Exists(imagePath))
 return new UmiOcrResult { Error = "图片文件不存在" };

 // 自动检测黑底白字：如果未手动指定反色，采样图片平均亮度
 if (!invertColors && IsDarkBackground(imagePath))
 {
 invertColors = true;
 Log("自动检测到黑底白字图片，启用反色");
 }

 Log($"Recognize: {imagePath}, invert={invertColors}");

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
 StandardOutputEncoding = System.Text.Encoding.UTF8,
 StandardErrorEncoding = System.Text.Encoding.UTF8,
 };
 // 用 ArgumentList 避免路径空格问题（.NET Core+ 推荐方式）
 psi.ArgumentList.Add("--models");
 psi.ArgumentList.Add("models");
 psi.ArgumentList.Add("--det");
 psi.ArgumentList.Add("ch_PP-OCRv4_det_infer.onnx");
 psi.ArgumentList.Add("--cls");
 psi.ArgumentList.Add("ch_ppocr_mobile_v2.0_cls_infer.onnx");
 psi.ArgumentList.Add("--rec");
 psi.ArgumentList.Add("rec_ch_PP-OCRv4_infer.onnx");
 psi.ArgumentList.Add("--keys");
 psi.ArgumentList.Add("dict_chinese.txt");
 psi.ArgumentList.Add("--image_path");
 psi.ArgumentList.Add(imgToOcr);
 psi.ArgumentList.Add("--ensureAscii");
 psi.ArgumentList.Add("0");
 Log($"Image: {imgToOcr}, WorkingDir: {psi.WorkingDirectory}");

 using var proc = Process.Start(psi);
 var stdout = await proc.StandardOutput.ReadToEndAsync();
 var stderr = await proc.StandardError.ReadToEndAsync();
 await proc.WaitForExitAsync();
 Log($"Exit code: {proc.ExitCode}, stdout len: {stdout.Length}, stderr: {stderr.Substring(0, Math.Min(200, stderr.Length))}");
 Log($"Raw stdout: {stdout}");

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

 // 提取所有文本块（含 box 坐标）
 var items = new List<(int x1, int y1, int x2, int y2, string text)>();
 if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
 {
 foreach (var block in dataElem.EnumerateArray())
 {
 if (block.TryGetProperty("text", out var textProp))
 {
 var text = textProp.GetString() ?? "";
 if (block.TryGetProperty("box", out var boxElem) && boxElem.ValueKind == JsonValueKind.Array)
 {
 var boxArr = boxElem.EnumerateArray().ToArray();
 if (boxArr.Length >= 4)
 {
 int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
 foreach (var pt in boxArr)
 {
 if (pt.ValueKind == JsonValueKind.Array)
 {
 var xy = pt.EnumerateArray().ToArray();
 if (xy.Length >= 2)
 {
 int x = xy[0].GetInt32(), y = xy[1].GetInt32();
 if (x < minX) minX = x;
 if (x > maxX) maxX = x;
 if (y < minY) minY = y;
 if (y > maxY) maxY = y;
 }
 }
 }
 items.Add((minX, minY, maxX, maxY, text));
 continue;
 }
 }
 items.Add((0, 0, 0, 0, text));
 }
 }
 }

 // 智能后处理：双栏区分 + 段落合并
 result.FullText = PostProcessOcrText(items);
 Log($"Result: len={result.FullText.Length}, blocks={items.Count}");
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
/// 智能后处理：双栏区分 + 段落合并
private string PostProcessOcrText(List<(int x1, int y1, int x2, int y2, string text)> items)
{
 if (items.Count == 0) return "";
 if (items.Count == 1) return items[0].text;

 // 1. 检测是否是双栏布局
 var allMinX = items.Min(i => i.x1);
 var allMaxX = items.Max(i => i.x2);
 double pageWidth = allMaxX - allMinX;
 if (pageWidth <= 0) return string.Join("\n", items.Select(i => i.text));

 double midX = allMinX + pageWidth * 0.5;
 var leftItems = items.Where(i => (i.x1 + i.x2) / 2.0 < midX).ToList();
 var rightItems = items.Where(i => (i.x1 + i.x2) / 2.0 >= midX).ToList();
 bool isTwoColumn = leftItems.Count >= 3 && rightItems.Count >= 3;

 if (isTwoColumn)
 {
 var leftText = MergeParagraphs(leftItems.OrderBy(i => i.y1).ToList(), pageWidth);
 var rightText = MergeParagraphs(rightItems.OrderBy(i => i.y1).ToList(), pageWidth);
 return leftText + (leftText.EndsWith("\n") ? "\n" : "\n\n") + rightText;
 }
 else
 {
 return MergeParagraphs(items.OrderBy(i => i.y1).ToList(), pageWidth);
 }
}

/// 段落合并：相邻行如果末尾不是结束符，合并为一段
private string MergeParagraphs(List<(int x1, int y1, int x2, int y2, string text)> sortedItems, double pageWidth)
{
 if (sortedItems.Count == 0) return "";

 var sb = new StringBuilder();
 string currentPara = "";
 double prevBottomY = -1;
 double prevX1 = -1;
 double avgLineHeight = 0;

 var heights = sortedItems.Select(i => (double)(i.y2 - i.y1)).Where(h => h > 0).ToList();
 avgLineHeight = heights.Count > 0 ? heights.Average() : 40;

 // 段落结束符
 var endChars = new HashSet<char> { '。', '；', '：', '？', '！', '}', '）', '…', '.', '?', '!', ';', ':' };

 // 判断是否以编号/序号开头
 Func<string, bool> StartsWithNumbering = (string s) =>
 {
 if (string.IsNullOrEmpty(s)) return false;
 s = s.TrimStart();
 if (s.Length < 2) return false;
 // 数字 + 右括号/点/顿号/逗号：1) 1. 1、 1， 2. 10)
 int i = 0;
 int digitCount = 0;
 while (i < s.Length && char.IsDigit(s[i]) && digitCount < 3) { i++; digitCount++; }
 if (digitCount > 0 && i < s.Length)
 {
 char c = s[i];
 if (c == ')' || c == '.' || c == '\u3001' || c == '\uff0c' || c == '\uff09')
 return true;
 }
 // 全角括号开头
 if (s[0] == '\uff08' || s[0] == '(' || s[0] == '\u3010')
 {
 int j = 1;
 while (j < s.Length && char.IsDigit(s[j])) j++;
 if (j > 1 && j < s.Length && (s[j] == '\uff09' || s[j] == ')' || s[j] == '\u3011'))
 return true;
 }
 // 字母编号：A. B. a) b)
 if (s.Length >= 2 && char.IsLetter(s[0]) && (s[1] == '.' || s[1] == ')' || s[1] == '\uff09'))
 return true;
 // 中文序号
 var cnNums = new HashSet<string> { "\u4e00\u3001", "\u4e8c\u3001", "\u4e09\u3001", "\u56db\u3001", "\u4e94\u3001",
 "\u516d\u3001", "\u4e03\u3001", "\u516b\u3001", "\u4e5d\u3001", "\u5341\u3001",
 "1\u3001", "2\u3001", "3\u3001", "4\u3001", "5\u3001",
 "6\u3001", "7\u3001", "8\u3001", "9\u3001", "10\u3001" };
 foreach (var cn in cnNums)
 if (s.StartsWith(cn)) return true;
 return false;
 };

 foreach (var item in sortedItems)
 {
 var line = item.text?.Trim() ?? "";
 if (string.IsNullOrEmpty(line)) continue;

 // 判断是否是新段落的开始
 bool isSameParagraph = false;
 if (prevBottomY >= 0)
 {
 double gap = item.y1 - prevBottomY;
 bool yClose = gap >= -avgLineHeight * 0.5 && gap <= avgLineHeight * 1.5;
 bool xClose = prevX1 >= 0 && Math.Abs(item.x1 - prevX1) < pageWidth * 0.15;
 isSameParagraph = yClose && xClose;
 }

 // 强制新段落的条件
 bool forceNewPara = false;
 // 1. 当前行以编号开头
 if (StartsWithNumbering(line))
 forceNewPara = true;
 // 2. 上行以结束符结尾
 if (currentPara.Length > 0 && endChars.Contains(currentPara[currentPara.Length - 1]))
 forceNewPara = true;

 if (isSameParagraph && !forceNewPara && currentPara.Length > 0)
 {
 currentPara += line;
 }
 else
 {
 if (currentPara.Length > 0)
 sb.AppendLine(currentPara);
 currentPara = line;
 }

 prevBottomY = item.y2;
 prevX1 = item.x1;
 }

 if (currentPara.Length > 0)
 sb.AppendLine(currentPara);

 return sb.ToString().TrimEnd();
}

/// 自动检测图片是否为黑底白字（采样像素平均亮度 < 阈值则判定为黑底）
private static bool IsDarkBackground(string imagePath)
{
 try
 {
 using var bmp = new Bitmap(imagePath);
 int sampleStep = Math.Max(1, (bmp.Width * bmp.Height) / 1000); // 采样约1000像素
 long totalBrightness = 0;
 int sampleCount = 0;
 for (int y = 0; y < bmp.Height; y += (int)Math.Sqrt(sampleStep))
 {
 for (int x = 0; x < bmp.Width; x += (int)Math.Sqrt(sampleStep))
 {
 var px = bmp.GetPixel(x, y);
 // 亮度 = 0.299R + 0.587G + 0.114B
 totalBrightness += (int)(0.299 * px.R + 0.587 * px.G + 0.114 * px.B);
 sampleCount++;
 }
 }
 if (sampleCount == 0) return false;
 double avgBrightness = (double)totalBrightness / sampleCount;
 return avgBrightness < 128; // 平均亮度<128 = 黑底
 }
 catch { return false; }
}

private byte[] InvertImageColors(string imagePath)
{
 using var srcBmp = new Bitmap(imagePath);
 using var bmp = new Bitmap(srcBmp.Width, srcBmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
 using (var g = Graphics.FromImage(bmp))
 {
 g.Clear(Color.White);
 g.DrawImage(srcBmp, 0, 0, srcBmp.Width, srcBmp.Height);
 }

 var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
 var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, bmp.PixelFormat);
 try
 {
 var stride = data.Stride;
 var bytes = new byte[stride * data.Height];
 System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

 for (int i = 0; i < bytes.Length; i++)
 bytes[i] = (byte)(255 - bytes[i]);

 System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
 }
 finally
 {
 bmp.UnlockBits(data);
 }

 using var ms = new MemoryStream();
 bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
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
