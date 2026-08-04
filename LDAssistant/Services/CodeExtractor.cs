using System.Collections.Generic;
using System.Text.RegularExpressions;
using LDAssistant.Models;

namespace LDAssistant.Services
{
    /// <summary>从文本中提取规范编号</summary>
    public static class CodeExtractor
    {
        // 规范编号正则: 字母+数字+可选/字母+数字+年份
        public static readonly Regex CodePattern = new(
            @"[A-Z]{1,5}[0-9]*(?:/[A-Z]{1,10})?\s*\d+(?:\.\d+)?-\d{4}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 编号+名称正则
        public static readonly Regex NamePattern = new(
            @"(?:[A-Z]{1,5}(?:/[A-Z]{1,2})?)\s*\d+(?:\.\d+)?-\d{4}\s+([\u4e00-\u9fff]{2,60})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>从文本中提取所有规范编号及名称</summary>
        public static List<CheckResult> Extract(string text)
        {
            var results = new List<CheckResult>();
            if (string.IsNullOrWhiteSpace(text)) return results;

            var seen = new HashSet<string>();
            var codeMatches = CodePattern.Matches(text);
            int idx = 1;

            // 构建编号→名称映射
            var nameMap = new Dictionary<string, string>();
            foreach (Match m in NamePattern.Matches(text))
            {
                var codeStr = CodePattern.Match(m.Value).Value.Trim();
                var nameStr = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(codeStr) && !nameMap.ContainsKey(codeStr))
                    nameMap[codeStr] = nameStr;
            }

            foreach (Match m in codeMatches)
            {
                var code = m.Value.Trim();
                if (seen.Contains(code)) continue;
                seen.Add(code);

                string name = "";
                nameMap.TryGetValue(code, out name);

                results.Add(new CheckResult
                {
                    No = idx++,
                    Code = code,
                    Name = name ?? "",
                    Status = "待检查"
                });
            }

            return results;
        }
    }
}
