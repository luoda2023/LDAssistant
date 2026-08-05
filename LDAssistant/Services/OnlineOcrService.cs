using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace LDAssistant.Services
{
	/// 在线OCR服务（OCR.space API）——支持文字+表格识别
	/// 免费注册获取API Key: https://ocr.space/ocrapi
	/// 免费额度: 每月25,000次，Engine 3支持表格返回Markdown
	public class OnlineOcrService
	{
		private const string ApiUrl = "https://api.ocr.space/parse/image";
		private string _apiKey;
		private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

		/// API Key保存在配置文件中
		private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_api_key.txt");

		public static string GetSavedApiKey()
		{
			try { return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath).Trim() : ""; }
			catch { return ""; }
		}

		public static void SaveApiKey(string key)
		{
			try { File.WriteAllText(ConfigPath, key?.Trim() ?? ""); } catch { }
		}

		public OnlineOcrService(string apiKey = null)
		{
			_apiKey = apiKey ?? GetSavedApiKey();
			if (string.IsNullOrEmpty(_apiKey))
				_apiKey = "K123456789012"; // 测试用key，用户需替换为自己的
		}

		public bool IsConfigured => !string.IsNullOrEmpty(_apiKey) && _apiKey != "K123456789012";

		/// 在线OCR识别（支持表格、中文）
		public async Task<OcrOnlineResult> RecognizeAsync(string imagePath, bool isTable = true)
		{
			var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log");
			void Log(string msg)
			{
				try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [OnlineOCR] {msg}\n"); } catch { }
			}

			try
			{
				Log($"Recognize: {imagePath}, isTable={isTable}");

				if (!File.Exists(imagePath))
					return new OcrOnlineResult { Error = "图片文件不存在" };

				var fileSize = new FileInfo(imagePath).Length;
				Log($"File size: {fileSize} bytes");

				// 读取图片为base64
				var imgBytes = File.ReadAllBytes(imagePath);
				var base64 = Convert.ToBase64String(imgBytes);

				using var form = new MultipartFormDataContent();
				var fileContent = new ByteArrayContent(imgBytes);
				fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
				form.Add(fileContent, "file", "image.png");
				form.Add(new StringContent(_apiKey), "apikey");
				form.Add(new StringContent(isTable ? "true" : "false"), "isTable");
				form.Add(new StringContent("3"), "OCREngine"); // Engine 3: 支持表格+中文
				form.Add(new StringContent("chs"), "language"); // 简体中文
				form.Add(new StringContent("true"), "isOverlayRequired"); // 返回坐标

				Log("Sending request to OCR.space...");
				var response = await _http.PostAsync(ApiUrl, form);
				var json = await response.Content.ReadAsStringAsync();
				Log($"Response status: {response.StatusCode}, length: {json.Length}");

				if (!response.IsSuccessStatusCode)
					return new OcrOnlineResult { Error = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}" };

				return ParseResult(json, Log);
			}
			catch (Exception ex)
			{
				Log($"Exception: {ex}");
				return new OcrOnlineResult { Error = ex.Message };
			}
		}

		private OcrOnlineResult ParseResult(string json, Action<string> Log)
		{
			try
			{
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				// 检查退出码
				if (root.TryGetProperty("OCRExitCode", out var exitCode) && exitCode.GetInt32() != 1)
				{
					var errMsg = root.TryGetProperty("ErrorMessage", out var em) ? em.GetString() : $"OCR Exit Code: {exitCode}";
					Log($"OCR Exit Code: {exitCode}, Error: {errMsg}");
					return new OcrOnlineResult { Error = errMsg };
				}

				var result = new OcrOnlineResult { Success = true };
				var sb = new StringBuilder();

				if (root.TryGetProperty("ParsedResults", out var parsedResults))
				{
					foreach (var pr in parsedResults.EnumerateArray())
					{
						if (pr.TryGetProperty("ParsedText", out var text))
						{
							var t = text.GetString();
							if (!string.IsNullOrEmpty(t))
							{
								sb.AppendLine(t);
							}
						}

						// 提取文本块坐标信息
						if (pr.TryGetProperty("TextOverlay", out var overlay) &&
							overlay.TryGetProperty("Lines", out var lines))
						{
							foreach (var line in lines.EnumerateArray())
							{
								var lineText = line.TryGetProperty("LineText", out var lt) ? lt.GetString() : "";
								var words = new List<OcrWord>();
								if (line.TryGetProperty("Words", out var wArr))
								{
									foreach (var w in wArr.EnumerateArray())
									{
										words.Add(new OcrWord
										{
											Text = w.TryGetProperty("WordText", out var wt) ? wt.GetString() : "",
											Left = w.TryGetProperty("Left", out var l) ? l.GetDouble() : 0,
											Top = w.TryGetProperty("Top", out var tp) ? tp.GetDouble() : 0,
											Width = w.TryGetProperty("Width", out var wd) ? wd.GetDouble() : 0,
											Height = w.TryGetProperty("Height", out var ht) ? ht.GetDouble() : 0,
										});
									}
								}
								result.Items.Add(new OcrLine
								{
									Text = lineText,
									Words = words
								});
							}
						}
					}
				}

				result.FullText = sb.ToString().Trim();
				Log($"Result: lines={result.Items.Count}, textLen={result.FullText.Length}");
				return result;
			}
			catch (Exception ex)
			{
				Log($"ParseResult exception: {ex.Message}");
				return new OcrOnlineResult { Error = $"解析结果失败: {ex.Message}" };
			}
		}
	}

	public class OcrOnlineResult
	{
		public bool Success { get; set; }
		public string FullText { get; set; } = "";
		public string Error { get; set; } = "";
		public List<OcrLine> Items { get; set; } = new List<OcrLine>();
	}

	public class OcrLine
	{
		public string Text { get; set; } = "";
		public List<OcrWord> Words { get; set; } = new List<OcrWord>();
	}

	public class OcrWord
	{
		public string Text { get; set; } = "";
		public double Left { get; set; }
		public double Top { get; set; }
		public double Width { get; set; }
		public double Height { get; set; }
	}
}
