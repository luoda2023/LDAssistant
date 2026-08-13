using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace LDAssistant.Services
{
	///
	public class LocalVersionInfo
	{
		[JsonProperty("wpf")] public ComponentVersion Wpf { get; set; }
		[JsonProperty("standards")] public ComponentVersion Standards { get; set; }
	}

	public class ComponentVersion
	{
		[JsonProperty("version")] public string Version { get; set; }
		[JsonProperty("minStandardsVersion")] public string MinStandardsVersion { get; set; }
	}

	///
	public class RemoteManifest
	{
		[JsonProperty("wpf")] public RemoteComponent Wpf { get; set; }
		[JsonProperty("standards")] public RemoteComponent Standards { get; set; }
	}

	public class RemoteComponent
	{
		[JsonProperty("version")] public string Version { get; set; }
		[JsonProperty("url")] public string Url { get; set; }
		[JsonProperty("size")] public long Size { get; set; }
		[JsonProperty("notes")] public string Notes { get; set; }
	}

	public enum UpdateComponent { Wpf, Standards }

	public class UpdateCheckResult
	{
		public bool HasUpdate { get; set; }
		public UpdateComponent Component { get; set; }
		public string CurrentVersion { get; set; }
		public string RemoteVersion { get; set; }
		public string DownloadUrl { get; set; }
		public long DownloadSize { get; set; }
		public string Notes { get; set; }
	}

	///
	public class UpdateService
	{
		// GitHub raw 上的更新清单 URL（每次发版更新此文件）
		private const string ManifestUrl =
			"https://raw.githubusercontent.com/luoda2023/LDAssistant/main/update-manifest.json";

		private readonly HttpClient _http;
		private LocalVersionInfo _local;

		public UpdateService()
		{
			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
			_http.DefaultRequestHeaders.Add("User-Agent", "LDAssistant-Updater/3.1");
			_local = LoadLocalVersion();
		}

		///
		public LocalVersionInfo Local => _local;

		///
		private LocalVersionInfo LoadLocalVersion()
		{
			try
			{
				var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");
				if (File.Exists(path))
				{
					var json = File.ReadAllText(path);
					var info = JsonConvert.DeserializeObject<LocalVersionInfo>(json);
					if (info != null) return info;
				}
			}
			catch { }

			// 兜底：用程序集版本
			return new LocalVersionInfo
			{
				Wpf = new ComponentVersion { Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "3.1.0" },
				Standards = new ComponentVersion { Version = "20250806" }
			};
		}

		///
		public void SaveLocalVersion()
		{
			try
			{
				var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");
				var json = JsonConvert.SerializeObject(_local, Formatting.Indented);
				File.WriteAllText(path, json);
			}
			catch { }
		}

		///
		public void UpdateLocalVersion(UpdateComponent component, string version)
		{
			switch (component)
			{
				case UpdateComponent.Wpf:
					if (_local.Wpf == null) _local.Wpf = new ComponentVersion();
					_local.Wpf.Version = version;
					break;
				case UpdateComponent.Standards:
					if (_local.Standards == null) _local.Standards = new ComponentVersion();
					_local.Standards.Version = version;
					break;
			}
			SaveLocalVersion();
		}

		///
		public async Task<RemoteManifest> FetchRemoteManifestAsync()
		{
			try
			{
				var resp = await _http.GetAsync(ManifestUrl);
				if (resp.IsSuccessStatusCode)
				{
					var json = await resp.Content.ReadAsStringAsync();
					return JsonConvert.DeserializeObject<RemoteManifest>(json);
				}
			}
			catch { }
			return null;
		}

		///
		public async Task<UpdateCheckResult[]> CheckForUpdatesAsync()
		{
			var results = new System.Collections.Generic.List<UpdateCheckResult>();
			var remote = await FetchRemoteManifestAsync();
			if (remote == null) return results.ToArray();

			// WPF 主程序
			if (remote.Wpf != null && IsNewer(remote.Wpf.Version, _local?.Wpf?.Version))
			{
				results.Add(new UpdateCheckResult
				{
					HasUpdate = true,
					Component = UpdateComponent.Wpf,
					CurrentVersion = _local?.Wpf?.Version,
					RemoteVersion = remote.Wpf.Version,
					DownloadUrl = remote.Wpf.Url,
					DownloadSize = remote.Wpf.Size,
					Notes = remote.Wpf.Notes
				});
			}

			// 标准数据库
			if (remote.Standards != null && IsNewer(remote.Standards.Version, _local?.Standards?.Version))
			{
				results.Add(new UpdateCheckResult
				{
					HasUpdate = true,
					Component = UpdateComponent.Standards,
					CurrentVersion = _local?.Standards?.Version,
					RemoteVersion = remote.Standards.Version,
					DownloadUrl = remote.Standards.Url,
					DownloadSize = remote.Standards.Size,
					Notes = remote.Standards.Notes
				});
			}

			return results.ToArray();
		}

		///
		private bool IsNewer(string remote, string local)
		{
			if (string.IsNullOrEmpty(remote)) return false;
			if (string.IsNullOrEmpty(local)) return true;
			try
			{
				var r = Version.Parse(remote);
				var l = Version.Parse(local);
				return r > l;
			}
			catch
			{
				return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
			}
		}

		///
		public async Task<string> DownloadUpdateAsync(UpdateCheckResult update, IProgress<int> progress = null)
		{
			if (update?.DownloadUrl == null) return null;

			var tempDir = Path.Combine(Path.GetTempPath(), "LDAssistant_Update");
			Directory.CreateDirectory(tempDir);
			var ext = update.Component == UpdateComponent.Standards ? ".db" : ".zip";
			var tempFile = Path.Combine(tempDir, $"{update.Component}_v{update.RemoteVersion}{ext}");

			// 如果已下载过且大小匹配，跳过
			if (File.Exists(tempFile))
			{
				var fi = new FileInfo(tempFile);
				if (update.DownloadSize > 0 && fi.Length == update.DownloadSize)
					return tempFile;
			}

			try { File.Delete(tempFile); } catch { }

			using var resp = await _http.GetAsync(update.DownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
			resp.EnsureSuccessStatusCode();

			var totalBytes = resp.Content.Headers.ContentLength ?? update.DownloadSize;
			using var contentStream = await resp.Content.ReadAsStreamAsync();
			using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

			var buffer = new byte[81920];
			long bytesRead = 0;
			int read;
			while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
			{
				await fileStream.WriteAsync(buffer, 0, read);
				bytesRead += read;
				if (totalBytes > 0 && progress != null)
				{
					var pct = (int)(bytesRead * 100 / totalBytes);
					progress.Report(pct);
				}
			}

			return tempFile;
		}

		///
		public void ApplyWpfUpdate(string zipPath)
		{
			var appDir = AppDomain.CurrentDomain.BaseDirectory;
			ExtractZip(zipPath, appDir);
		}

		///
		public void ApplyStandardsUpdate(string dbPath)
		{
			var appDir = AppDomain.CurrentDomain.BaseDirectory;
			var targetDb = Path.Combine(appDir, "standards.db");
			// 备份旧版
			if (File.Exists(targetDb))
			{
				var backup = targetDb + ".bak";
				try { File.Copy(targetDb, backup, true); } catch { }
			}
			File.Copy(dbPath, targetDb, true);
		}

		///
		private void ExtractZip(string zipPath, string destDir)
		{
			System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destDir, true);
		}

		///
		public static string GenerateManifestTemplate(string wpfVer, string stdVer)
		{
			var manifest = new RemoteManifest
			{
				Wpf = new RemoteComponent
				{
					Version = wpfVer,
					Url = $"https://github.com/luoda2023/LDAssistant/releases/download/v{wpfVer}/LDAssistant-update.zip",
					Size = 0,
					Notes = "WPF 主程序更新"
				},
				Standards = new RemoteComponent
				{
					Version = stdVer,
					Url = $"https://github.com/luoda2023/LDAssistant/releases/download/v{stdVer}/standards.db",
					Size = 0,
					Notes = "标准数据库更新"
				}
			};
			return JsonConvert.SerializeObject(manifest, Formatting.Indented);
		}
	}
}
