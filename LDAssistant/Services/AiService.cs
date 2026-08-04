using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LDAssistant.Services
{
    /// <summary>AI 对话服务 - OpenAI 兼容 API</summary>
    public class AiService
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ldassistant_config.json");

        private static readonly string FreeConfig = new JObject(
            new JProperty("api_url", "http://47.114.75.115:40000/v1/chat/completions"),
            new JProperty("api_key", "sk-proxy-local-51f5bd4b9797f2620bc55460946802711cf7312b38c24794"),
            new JProperty("model", "hermesAPI"),
            new JProperty("use_free_model", true)
        ).ToString();

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

        public string ApiUrl { get; set; }
        public string ApiKey { get; set; }
        public string Model { get; set; }
        public bool UseFreeModel { get; set; }

        public AiService()
        {
            LoadConfig();
        }

        public void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = JObject.Parse(File.ReadAllText(ConfigPath));
                    ApiUrl = json["api_url"]?.ToString();
                    ApiKey = json["api_key"]?.ToString();
                    Model = json["model"]?.ToString();
                    UseFreeModel = json["use_free_model"]?.ToObject<bool>() ?? true;
                }
            }
            catch { }
            LoadDefaults();
        }

        private void LoadDefaults()
        {
            if (string.IsNullOrEmpty(ApiUrl))
            {
                var free = JObject.Parse(FreeConfig);
                ApiUrl = free["api_url"].ToString();
                ApiKey = free["api_key"].ToString();
                Model = free["model"].ToString();
                UseFreeModel = true;
            }
        }

        public void SaveConfig()
        {
            var json = new JObject(
                new JProperty("api_url", ApiUrl),
                new JProperty("api_key", ApiKey),
                new JProperty("model", Model),
                new JProperty("use_free_model", UseFreeModel)
            );
            File.WriteAllText(ConfigPath, json.ToString());
        }

        /// <summary>调用 AI 获取回复</summary>
        public async Task<string> ChatAsync(string userMessage, List<(string role, string content)> history = null)
        {
            var messages = new JArray();
            if (history != null)
            {
                foreach (var (role, content) in history)
                    messages.Add(new JObject(
                        new JProperty("role", role),
                        new JProperty("content", content)));
            }
            messages.Add(new JObject(
                new JProperty("role", "user"),
                new JProperty("content", userMessage)));

            var body = new JObject(
                new JProperty("model", Model ?? "hermesAPI"),
                new JProperty("messages", messages),
                new JProperty("stream", false),
                new JProperty("temperature", 0.3)
            );

            var content_json = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = content_json };

            if (!string.IsNullOrEmpty(ApiKey))
                request.Headers.Add("Authorization", $"Bearer {ApiKey}");

            try
            {
                var response = await _http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var responseText = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(responseText);

                // 尝试多种回复格式
                var reply = result["reply"]?.ToString()
                    ?? result["content"]?.ToString()
                    ?? result["choices"]?[0]?["message"]?["content"]?.ToString()
                    ?? result.ToString();
                return reply;
            }
            catch (Exception ex)
            {
                return $"AI 请求失败: {ex.Message}";
            }
        }

        /// <summary>测试连接</summary>
        public async Task<(bool ok, string msg)> TestConnection()
        {
            try
            {
                var reply = await ChatAsync("你好");
                return (true, reply);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
