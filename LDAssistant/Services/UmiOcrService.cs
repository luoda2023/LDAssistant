using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace LDAssistant.Services
{
	/// UmiOCR HTTP API 服务
	/// UmiOCR 是本地 PaddleOCR 封装，支持中文+表格识别
	/// HTTP API: http://127.0.0.1:1224/api/ocr
	public class UmiOcrService
	{
		private const string ApiUrl = "http://127.0.0.1:1224/api/ocr";
		private const int DefaultPort = 1224;
		private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

		/// UmiOCR 安装路径配置
		private static string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "umiocr_path.txt");

		public static string GetSavedPath()
		{
			try { return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath).Trim() : ""; }
			catch { return ""; }
		}

		public static void SavePath(string path)
		{
			try { File.WriteAllText(ConfigPath, path?.Trim() ?? ""); } catch { }
		}

		/// 自动查找 UmiOCR.exe
		public static string AutoDetectUmiOcr()
		{
			// 常见安装路径
			var candidates = new[]
			{
				@"D:\Program Files\图片文字识别\Umi-OCR.exe",
				@"C:\Program Files\图片文字识别\Umi-OCR.exe",
				@"C:\Program Files (x86)\图片文字识别\Umi-OCR.exe",
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "图片文字识别", "Umi-OCR.exe"),
			};

			foreach (var p in candidates)
			{
				if (File.Exists(p)) return p;
			}

			// 搜索桌面和D盘
			try
			{
				var drives = new[] { "D:", "C:", "E:" };
				foreach (var drive in drives)
				{
					var baseDir = $"{drive}\\Program Files\\图片文字识别";
					if (Directory.Exists(baseDir))
					{
						var exe = Path.Combine(baseDir, "Umi-OCR.exe");
						if (File.Exists(exe)) return exe;
					}
					var baseDir2 = $"{drive}\\图片文字识别";
					if (Directory.Exists(baseDir2))
					{
						var exe = Path.Combine(baseDir2, "Umi-OCR.exe");
						if (File.Exists(exe)) return exe;
					}
				}
			}
			catch { }

			return null;
		}

		/// 检查 UmiOCR HTTP 服务是否在运行
		public static async Task<bool> IsRunningAsync()
		{
			try
			{
				using var resp = await _http.GetAsync("http://127.0.0.1:1224/umiocr");
				return resp.IsSuccessStatusCode;
			}
			catch { return false; }
		}

		/// 启动 UmiOCR（如果没在运行）
		public static async Task<bool> EnsureRunningAsync(Action<string> log = null)
		{
			if (await IsRunningAsync())
			{
				log?.Invoke("UmiOCR HTTP服务已在运行");
				return true;
			}

			var exePath = GetSavedPath();
			if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
			{
				exePath = AutoDetectUmiOcr();
				if (exePath != null) SavePath(exePath);
			}

			if (exePath == null || !File.Exists(exePath))
			{
				log?.Invoke("未找到 UmiOCR.exe，请手动选择路径");
				return false;
			}

			log?.Invoke($"启动 UmiOCR: {exePath}");
			try
			{
				var psi = new ProcessStartInfo
				{
					FileName = exePath,
					WorkingDirectory = Path.GetDirectoryName(exePath),
					UseShellExecute = false,
					CreateNoWindow = true
				};
				Process.Start(psi);

				// 等待HTTP服务就绪（最多等30秒）
				for (int i = 0; i < 30; i++)
				{
					await Task.Delay(1000);
					if (await IsRunningAsync())
					{
						log?.Invoke($"UmiOCR HTTP服务已就绪（等待{i + 1}秒）");
						return true;
					}
				}
				log?.Invoke("UmiOCR启动超时，HTTP服务未就绪");
				return false;
			}
			catch (Exception ex)
			{
				log?.Invoke($"启动UmiOCR失败: {ex.Message}");
				return false;
			}
		}

		/// 调用 UmiOCR HTTP API 识别图片
		public async Task<UmiOcrResult> RecognizeAsync(string imagePath, string format = "text")
		{
			var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log");
			void Log(string msg)
			{
				try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [UmiOCR] {msg}\n"); } catch { }
			}

			try
			{
				Log($"Recognize: {imagePath}");

				if (!File.Exists(imagePath))
					return new UmiOcrResult { Error = "图片文件不存在" };

				// 确保服务运行
				if (!await EnsureRunningAsync(Log))
					return new UmiOcrResult { Error = "UmiOCR服务未运行" };

				// 读取图片转base64
				var imgBytes = File.ReadAllBytes(imagePath);
				var base64 = Convert.ToBase64String(imgBytes);
				Log($"Image base64 length: {base64.Length}");

				// 构建请求
				var requestData = new Dictionary<string, object>
				{
					["base64"] = base64,
					["options"] = new Dictionary<string, object>
					{
						["ocr.language"] = "简体中文",
						["ocr.maxSideLen"] = 2048,
						["ocr.angle"] = true,
						["tbpu.parser"] = "multi_para",
						["data.format"] = format
					}
				};

				var json = JsonSerializer.Serialize(requestData);
				var content = new StringContent(json, Encoding.UTF8, "application/json");

				Log("Sending request to UmiOCR...");
				var resp = await _http.PostAsync(ApiUrl, content);
				var respJson = await resp.Content.ReadAsStringAsync();
				Log($"Response: status={resp.StatusCode}, length={respJson.Length}");

				return ParseResult(respJson, format, Log);
			}
			catch (Exception ex)
			{
				Log($"Exception: {ex}");
				return new UmiOcrResult { Error = ex.Message };
			}
		}

		private UmiOcrResult ParseResult(string json, string format, Action<string> Log)
		{
			try
			{
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
				if (code != 100)
				{
					var data = root.TryGetProperty("data", out var d) ? d.GetString() : "";
					Log($"OCR失败 code={code}, data={data}");
					return new UmiOcrResult { Error = $"UmiOCR错误(code={code}): {data}" };
				}

				var result = new UmiOcrResult { Success = true };
				var dataElem = root.GetProperty("data");

				if (format == "text")
				{
					result.FullText = dataElem.GetString() ?? "";
					Log($"Result(text): len={result.FullText.Length}");
				}
				else
				{
					// dict格式：含位置信息
					var sb = new StringBuilder();
					if (dataElem.ValueKind == JsonValueKind.Array)
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
					Log($"Result(dict): len={result.FullText.Length}");
				}

				return result;
			}
			catch (Exception ex)
			{
				Log($"ParseResult exception: {ex.Message}");
				return new UmiOcrResult { Error = $"解析结果失败: {ex.Message}" };
			}
		}
	}

	public class UmiOcrResult
	{
		public bool Success { get; set; }
		public string FullText { get; set; } = "";
		public string Error { get; set; } = "";
	}
}
