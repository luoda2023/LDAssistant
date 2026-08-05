using System;
using System.Text;

namespace LDAssistant.Services
{
 /// <summary>
 /// 文本规范化工具：全角转半角，统一中英文标点
 /// </summary>
 public static class TextNormalizer
 {
 /// <summary>
 /// 将全角字符转为半角。
 /// 包括：全角字母数字、全角标点符号（空格、！＂＃＄％＆＇（）＊＋，－．／：；＜＝＞？＠［＼］＾＿｀｛｜｝～）
 /// </summary>
 public static string ToHalfWidth(string input)
 {
 if (string.IsNullOrEmpty(input)) return input;

 var sb = new StringBuilder(input.Length);
 foreach (var c in input)
 {
 if (c == '\u3000') // 全角空格
 sb.Append(' ');
 else if (c >= '\uFF01' && c <= '\uFF5E') // 全角ASCII区域 !~ ~
 sb.Append((char)(c - 0xFEE0));
 else if (c >= '\uFF10' && c <= '\uFF19') // 全角数字 ０-９ (已在上面覆盖)
 sb.Append((char)(c - 0xFEE0));
 else if (c >= '\uFF21' && c <= '\uFF3A') // 全角大写字母 Ａ-Ｚ (已在上面覆盖)
 sb.Append((char)(c - 0xFEE0));
 else if (c >= '\uFF41' && c <= '\uFF5A') // 全角小写字母 ａ-ｚ (已在上面覆盖)
 sb.Append((char)(c - 0xFEE0));
 else
 sb.Append(c);
 }
 return sb.ToString();
 }

 /// <summary>
 /// 规范化文本：全角转半角 + 去除多余空白 + 统一换行
 /// </summary>
 public static string Normalize(string input)
 {
 if (string.IsNullOrEmpty(input)) return input;
 var text = ToHalfWidth(input);
 // 统一换行
 text = text.Replace("\r\n", "\n").Replace("\r", "\n");
 // 去除行尾空白
 var lines = text.Split('\n');
 for (int i = 0; i < lines.Length; i++)
 lines[i] = lines[i].TrimEnd();
 return string.Join("\n", lines);
 }
 }
}
