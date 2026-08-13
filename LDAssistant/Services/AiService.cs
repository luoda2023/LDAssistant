using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
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

        /// <summary>调用 AI 获取回复（SSE 流式输出，逐段回调增量；服务端不支持流式时自动回退为一次性返回）</summary>
        public async Task ChatStreamAsync(string userMessage, List<(string role, string content)> history = null,
            Action<string> onDelta = null, CancellationToken ct = default)
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
                new JProperty("stream", true),
                new JProperty("temperature", 0.3)
            );

            var contentJson = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = contentJson };

            if (!string.IsNullOrEmpty(ApiKey))
                request.Headers.Add("Authorization", $"Bearer {ApiKey}");

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // 跳过可能的空行，读第一个有效行判断是否为 SSE
            string firstLine = null;
            while (!reader.EndOfStream)
            {
                firstLine = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(firstLine)) break;
            }

            if (firstLine != null && firstLine.TrimStart().StartsWith("data:"))
            {
                // ── SSE 流式模式：逐段解析 delta ──
                string line = firstLine;
                while (true)
                {
                    if (line.TrimStart().StartsWith("data:"))
                    {
                        var data = line.Substring(line.IndexOf(':') + 1).Trim();
                        if (data == "[DONE]") break;
                        if (!string.IsNullOrEmpty(data))
                        {
                            try
                            {
                                var obj = JObject.Parse(data);
                                var delta = obj["choices"]?[0]?["delta"]?["content"]?.ToString()
                                    ?? obj["choices"]?[0]?["message"]?["content"]?.ToString()
                                    ?? obj["delta"]?["content"]?.ToString()
                                    ?? obj["reply"]?.ToString()
                                    ?? obj["content"]?.ToString();
                                if (!string.IsNullOrEmpty(delta))
                                    onDelta?.Invoke(delta);
                            }
                            catch { /* 忽略无法解析的分片 */ }
                        }
                    }
                    if (reader.EndOfStream) break;
                    line = await reader.ReadLineAsync(ct);
                }
            }
            else
            {
                // ── 非 SSE（服务端不支持流式）：一次性读取并解析 ──
                var buffer = new StringBuilder();
                if (firstLine != null) buffer.AppendLine(firstLine);
                while (!reader.EndOfStream)
                    buffer.AppendLine(await reader.ReadLineAsync(ct));
                try
                {
                    var result = JObject.Parse(buffer.ToString());
                    var reply = result["reply"]?.ToString()
                        ?? result["content"]?.ToString()
                        ?? result["choices"]?[0]?["message"]?["content"]?.ToString()
                        ?? result.ToString();
                    onDelta?.Invoke(reply);
                }
                catch
                {
                    onDelta?.Invoke(buffer.ToString());
                }
            }
        }

        /// <summary>总结长文本为紧凑要点（供上下文压缩使用）；失败返回 null</summary>
        public async Task<string> SummarizeAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            // 输入上限 20000 字符：足够保留关键内容，又控制总结请求本身的 token 开销
            var input = text.Length > 20000 ? text[..20000] + "\n…（过长已截断）" : text;
            var prompt = "你是文档分析助手。以下是从文档中 OCR 识别出的文本（可能含噪声、乱行）及规范检查结果。"
                + "请提炼关键信息：保留所有规范编号、数值、表格关键数据、专业结论；去掉重复行、页眉页脚和无意义噪声；"
                + "用简洁要点输出，500 字以内，不要添加原文没有的内容。\n\n" + input;
            var reply = await ChatAsync(prompt);
            // ChatAsync 失败时返回以“AI 请求失败”开头的字符串，识别后视为总结失败
            if (string.IsNullOrWhiteSpace(reply) || reply.StartsWith("AI 请求失败"))
                return null;
            return reply.Trim();
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
