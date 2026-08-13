using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LDAssistant.Services
{
	/// <summary>
	/// 在线OCR服务——支持多引擎（SiliconFlow多模态 / OCR.space）
	/// SiliconFlow: OpenAI兼容格式，支持 PaddleOCR-VL（表格+公式）、DeepSeek-OCR 等
	/// OCR.space: 免费OCR，支持表格返回
	/// 配置文件: ocr_online_config.json
	/// </summary>
public class OnlineOcrService
 {
 private static readonly HttpClient _http;

 static OnlineOcrService()
 {
 // 创建HttpClientHandler，支持系统代理
 var handler = new HttpClientHandler
 {
 AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
 };
 try
 {
 // 读取系统默认代理设置
 handler.Proxy = System.Net.WebRequest.GetSystemWebProxy();
 handler.UseProxy = true;
 }
 catch { }
 _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
 // 设置默认请求头
 _http.DefaultRequestHeaders.Add("User-Agent", "LDAssistant/1.0");
 }

		/// 配置文件路径
		private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_online_config.json");

		// SiliconFlow 默认模型列表
		public static readonly string[] SiliconFlowModels = new[]
		{
			"PaddlePaddle/PaddleOCR-VL",
			"deepseek-ai/DeepSeek-OCR",
			"Qwen/Qwen2.5-VL-72B-Instruct",
		};

		// 默认提示词
		public const string DefaultPrompt = "请识别图片中的所有文字和表格，以 Markdown 格式输出。表格用 Markdown 表格语法，公式用 LaTeX。只输出识别结果，不要解释。";

		/// 当前配置
		public OnlineOcrConfig Config { get; private set; }

		public bool IsConfigured => Config != null && !string.IsNullOrEmpty(Config.Engine) && Config.Engine != "disabled" && !string.IsNullOrEmpty(Config.ApiKey);

		public OnlineOcrService()
		{
			LoadConfig();
		}

		// ═══════════════ 配置存取 ═══════════════

		public void LoadConfig()
		{
			try
			{
				if (File.Exists(ConfigPath))
				{
					var json = File.ReadAllText(ConfigPath);
					Config = JsonConvert.DeserializeObject<OnlineOcrConfig>(json) ?? new OnlineOcrConfig();
				}
				else
				{
					Config = new OnlineOcrConfig();
				}
			}
			catch { Config = new OnlineOcrConfig(); }
		}

 public void SaveConfig()
 {
 try
 {
 var json = JsonConvert.SerializeObject(Config, Formatting.Indented);
 File.WriteAllText(ConfigPath, json);
 }
 catch { }
 }

 /// <summary>
 /// 更新配置并保存
 /// </summary>
 public void UpdateConfig(OnlineOcrConfig newConfig)
 {
 Config = newConfig;
 SaveConfig();
 }

		// ═══════════════ OCR 识别 ═══════════════

		/// <summary>
		/// 在线OCR识别（根据配置引擎自动分流）
		/// </summary>
		/// <param name="imagePath">图片文件路径</param>
		/// <param name="isTable">是否表格识别（OCR.space用）</param>
		public async Task<OnlineOcrResult> RecognizeAsync(string imagePath, bool isTable = true)
		{
			var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log");
			void Log(string msg)
			{
				try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [OnlineOCR] {msg}\n"); } catch { }
			}

			try
			{
				Log($"Recognize: {imagePath}, engine={Config.Engine}, model={Config.Model}");

				if (!File.Exists(imagePath))
					return new OnlineOcrResult { Error = "图片文件不存在" };

				return Config.Engine switch
				{
					"siliconflow" => await RecognizeViaSiliconFlowAsync(imagePath, Log),
					"ocrspace" => await RecognizeViaOcrSpaceAsync(imagePath, isTable, Log),
 _ => new OnlineOcrResult { Error = "在线OCR引擎不支持" },
				};
			}
			catch (Exception ex)
			{
				Log($"Exception: {ex}");
				return new OnlineOcrResult { Error = ex.Message };
			}
		}

		// ═══════════════ SiliconFlow 引擎（OpenAI 兼容） ═══════════════

		private async Task<OnlineOcrResult> RecognizeViaSiliconFlowAsync(string imagePath, Action<string> Log)
		{
			try
			{
				var imgBytes = File.ReadAllBytes(imagePath);
				var base64 = Convert.ToBase64String(imgBytes);
				var dataUri = $"data:image/png;base64,{base64}";

				Log($"SiliconFlow: base64 len={base64.Length}, model={Config.Model}");

				// OpenAI 兼容的 vision 请求
				var contentArr = new JArray();
				contentArr.Add(new JObject(
					new JProperty("type", "text"),
					new JProperty("text", Config.Prompt ?? DefaultPrompt)));
				contentArr.Add(new JObject(
					new JProperty("type", "image_url"),
					new JProperty("image_url", new JObject(
						new JProperty("url", dataUri)))));

				var messages = new JArray();
				messages.Add(new JObject(
					new JProperty("role", "user"),
					new JProperty("content", contentArr)));

				var body = new JObject(
					new JProperty("model", Config.Model ?? "PaddlePaddle/PaddleOCR-VL"),
					new JProperty("messages", messages),
					new JProperty("stream", false),
					new JProperty("temperature", 0.1),
					new JProperty("max_tokens", 4096));

 var contentJson = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
 var request = new HttpRequestMessage(HttpMethod.Post, Config.ApiUrl) { Content = contentJson };
 request.Headers.Add("Authorization", $"Bearer {Config.ApiKey}");

 Log($"POST {Config.ApiUrl}, model={Config.Model}, key={Config.ApiKey?.Substring(0, Math.Min(8, Config.ApiKey?.Length ?? 0))}...");
 // 使用ResponseHeadersRead先读header，再读body，避免大响应体导致超时
 var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
 var responseText = await response.Content.ReadAsStringAsync();
 Log($"Response status: {response.StatusCode}, length: {responseText.Length}");

 if (!response.IsSuccessStatusCode)
 {
 Log($"Error response: {responseText.Substring(0, Math.Min(500, responseText.Length))}");
 return new OnlineOcrResult { Error = $"HTTP {(int)response.StatusCode}: {responseText.Substring(0, Math.Min(500, responseText.Length))}" };
 }

				var result = JObject.Parse(responseText);
				var reply = result["choices"]?[0]?["message"]?["content"]?.ToString()
					?? result["content"]?.ToString()
					?? result["reply"]?.ToString()
					?? result.ToString();

				Log($"Result: len={reply.Length}");
				return new OnlineOcrResult { Success = true, FullText = reply };
			}
			catch (Exception ex)
			{
				Log($"SiliconFlow exception: {ex}");
				return new OnlineOcrResult { Error = ex.Message };
			}
		}

		// ═══════════════ OCR.space 引擎 ═══════════════

		private async Task<OnlineOcrResult> RecognizeViaOcrSpaceAsync(string imagePath, bool isTable, Action<string> Log)
		{
			try
			{
				var imgBytes = File.ReadAllBytes(imagePath);
				Log($"OCR.space: file={imagePath}, isTable={isTable}");

				using var form = new MultipartFormDataContent();
				var fileContent = new ByteArrayContent(imgBytes);
				fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
				form.Add(fileContent, "file", "image.png");
				form.Add(new StringContent(Config.ApiKey ?? ""), "apikey");
				form.Add(new StringContent(isTable ? "true" : "false"), "isTable");
				form.Add(new StringContent("3"), "OCREngine");
				form.Add(new StringContent("chs"), "language");
				form.Add(new StringContent("true"), "isOverlayRequired");

				Log("Sending request to OCR.space...");
				var response = await _http.PostAsync(Config.ApiUrl, form);
				var json = await response.Content.ReadAsStringAsync();
				Log($"Response status: {response.StatusCode}, length: {json.Length}");

				if (!response.IsSuccessStatusCode)
					return new OnlineOcrResult { Error = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}" };

				return ParseOcrSpaceResult(json, Log);
			}
			catch (Exception ex)
			{
				Log($"OCR.space exception: {ex}");
				return new OnlineOcrResult { Error = ex.Message };
			}
		}

		private OnlineOcrResult ParseOcrSpaceResult(string json, Action<string> Log)
		{
			try
			{
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				if (root.TryGetProperty("OCRExitCode", out var exitCode) && exitCode.GetInt32() != 1)
				{
					var errMsg = root.TryGetProperty("ErrorMessage", out var em) ? em.GetString() : $"OCR Exit Code: {exitCode}";
					Log($"OCR Exit Code: {exitCode}, Error: {errMsg}");
					return new OnlineOcrResult { Error = errMsg };
				}

				var result = new OnlineOcrResult { Success = true };
				var sb = new StringBuilder();

				if (root.TryGetProperty("ParsedResults", out var parsedResults))
				{
					foreach (var pr in parsedResults.EnumerateArray())
					{
						if (pr.TryGetProperty("ParsedText", out var text))
						{
							var t = text.GetString();
							if (!string.IsNullOrEmpty(t)) sb.AppendLine(t);
						}
					}
				}

				result.FullText = sb.ToString().Trim();
				Log($"Result: textLen={result.FullText.Length}");
				return result;
			}
			catch (Exception ex)
			{
				Log($"ParseOcrSpaceResult exception: {ex.Message}");
				return new OnlineOcrResult { Error = $"解析结果失败: {ex.Message}" };
			}
		}

		// ═══════════════ 测试连接 ═══════════════

		/// <summary>
		/// 测试在线OCR连接是否正常（发送一个简单的文字识别请求）
		/// </summary>
		public async Task<(bool ok, string msg)> TestConnectionAsync()
		{
			try
			{
				// 生成一张小的测试图片（白底黑字"OCR"）
				var tempImg = Path.GetTempFileName() + ".png";
				using (var bmp = new System.Drawing.Bitmap(120, 50))
				using (var g = System.Drawing.Graphics.FromImage(bmp))
				{
					g.Clear(System.Drawing.Color.White);
					g.DrawString("OCR", new System.Drawing.Font("Arial", 20),
						System.Drawing.Brushes.Black, 10, 10);
					bmp.Save(tempImg, System.Drawing.Imaging.ImageFormat.Png);
				}

				var result = await RecognizeAsync(tempImg, false);
				try { File.Delete(tempImg); } catch { }

				if (result.Success)
					return (true, $"连接成功！识别结果: {result.FullText.Substring(0, Math.Min(50, result.FullText.Length))}");
 return (false, result.Error ?? "识别失败");
 }
 catch (TaskCanceledException)
 {
 return (false, "请求超时（60秒）。请检查：\n1. API地址是否正确\n2. 网络是否能访问该API\n3. 是否需要配置代理");
 }
 catch (Exception ex)
 {
 return (false, ex.Message);
 }
		}
	}

	/// <summary>在线OCR配置</summary>
	public class OnlineOcrConfig
	{
 /// 引擎: "siliconflow" | "ocrspace"
 public string Engine { get; set; } = "siliconflow";

		/// API 端点 URL
		public string ApiUrl { get; set; } = "https://api.siliconflow.cn/v1/chat/completions";

		/// API Key
		public string ApiKey { get; set; } = "";

		/// 模型名（SiliconFlow 用）
		public string Model { get; set; } = "PaddlePaddle/PaddleOCR-VL";

		/// OCR 提示词（SiliconFlow 用）
		public string Prompt { get; set; } = OnlineOcrService.DefaultPrompt;
	}

	/// <summary>在线OCR识别结果</summary>
	public class OnlineOcrResult
	{
		public bool Success { get; set; }
		public string FullText { get; set; } = "";
		public string Error { get; set; } = "";
	}
}
