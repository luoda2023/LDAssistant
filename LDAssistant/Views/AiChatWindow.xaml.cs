using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LDAssistant.Services;

namespace LDAssistant.Views
{
    public partial class AiChatWindow : Window
    {
        private readonly AiService _ai;
        private readonly List<(string role, string content)> _history = new();
        private string _context = "";

        public AiChatWindow(AiService ai)
        {
            InitializeComponent();
            _ai = ai;
            ModelLabel.Text = $"模型: {ai.Model ?? "hermesAPI"}";
            AddMessage("AI", "你好！我是 AI 助手。你可以问我关于规范编号的问题，我会帮你分析。\n\n**支持的格式：**\n- **加粗**\n- `代码`\n- 列表\n- 标题");
        }

        public void SetContext(string context)
        {
            _context = context;
        }

        /// <summary>添加一条消息气泡（富文本）</summary>
        public void AddMessage(string role, string text)
        {
            var isUser = role == "我";

            var bubble = new Border
            {
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(4, 4, 4, 4),
                CornerRadius = new CornerRadius(12),
                MaxWidth = 440,
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = isUser
                    ? new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3))
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                BorderThickness = new Thickness(1, 1, 1, 1),
            };

            var rtb = new RichTextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                FontSize = 13,
                FontFamily = new FontFamily("微软雅黑"),
                Foreground = isUser ? Brushes.White : Brushes.Black,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
            };

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
                LineHeight = 22,
            };

            RenderMarkdown(doc, text, isUser);
            rtb.Document = doc;

            bubble.Child = rtb;
            MsgPanel.Children.Add(bubble);

            // 滚动到底部
            MsgPanel.UpdateLayout();
            MsgScroll.ScrollToBottom();
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e) => await SendMessage();

        private async void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await SendMessage();
            }
        }

        private async Task SendMessage()
        {
            var text = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            InputBox.Clear();
            AddMessage("我", text);

            // 添加上下文
            var fullMessage = text;
            if (!string.IsNullOrEmpty(_context))
                fullMessage = $"[上下文]\n{_context}\n\n[问题]\n{text}";

            AddMessage("AI", "正在思考...");

            try
            {
                var reply = await _ai.ChatAsync(fullMessage, _history.Count > 10 ? _history.GetRange(_history.Count - 10, 10) : _history);

                // 移除"正在思考"气泡
                MsgPanel.Children.RemoveAt(MsgPanel.Children.Count - 1);

                AddMessage("AI", reply);
                _history.Add(("user", text));
                _history.Add(("assistant", reply));
            }
            catch (Exception ex)
            {
                MsgPanel.Children.RemoveAt(MsgPanel.Children.Count - 1);
                AddMessage("AI", $"请求失败: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  简易 Markdown → FlowDocument 渲染器
        // ═══════════════════════════════════════════════════════════

        private void RenderMarkdown(FlowDocument doc, string text, bool isUser)
        {
            var textColor = isUser ? Brushes.White : Brushes.Black;
            var codeBg = isUser
                ? new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0))
                : new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
            var codeColor = isUser ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xC7, 0x25, 0x4E));
            var linkColor = isUser ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
            var secondaryColor = isUser
                ? new SolidColorBrush(Color.FromRgb(0xBB, 0xDE, 0xFB))
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            var lines = text.Replace("\r\n", "\n").Split('\n');
            bool inCodeBlock = false;
            var codeBlockLines = new List<string>();

            foreach (var rawLine in lines)
            {
                var line = rawLine;

                // 代码块开始/结束
                if (line.TrimStart().StartsWith("```"))
                {
                    if (inCodeBlock)
                    {
                        // 结束代码块
                        var p = new Paragraph
                        {
                            Background = codeBg,
                            Margin = new Thickness(0, 4, 0, 4),
                            Padding = new Thickness(8, 6, 8, 6),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                            BorderThickness = new Thickness(0, 0, 0, 0),
                        };
                        var run = new Run(string.Join("\n", codeBlockLines))
                        {
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 12,
                            Foreground = codeColor,
                        };
                        p.Inlines.Add(run);
                        doc.Blocks.Add(p);
                        codeBlockLines.Clear();
                        inCodeBlock = false;
                    }
                    else
                    {
                        inCodeBlock = true;
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    codeBlockLines.Add(line);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 2, 0, 2) });
                    continue;
                }

                // 标题 # ## ###
                var titleMatch = Regex.Match(line, @"^(#{1,4})\s+(.+)$");
                if (titleMatch.Success)
                {
                    int level = titleMatch.Groups[1].Value.Length;
                    var sizes = new double[] { 18, 16, 14, 13 };
                    var para = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
                    var run = new Run(titleMatch.Groups[2].Value)
                    {
                        FontSize = sizes[level - 1],
                        FontWeight = FontWeights.Bold,
                        Foreground = textColor,
                    };
                    para.Inlines.Add(run);
                    doc.Blocks.Add(para);
                    continue;
                }

                // 无序列表 - * +
                var listMatch = Regex.Match(line, @"^[\s]*[-*+]\s+(.+)$");
                if (listMatch.Success)
                {
                    var para = new Paragraph { Margin = new Thickness(16, 1, 0, 1) };
                    para.Inlines.Add(new Run("• ") { Foreground = textColor });
                    AddInlineSpans(para, listMatch.Groups[1].Value, textColor, codeBg, codeColor, linkColor, secondaryColor);
                    doc.Blocks.Add(para);
                    continue;
                }

                // 有序列表 1. 2.
                var orderedMatch = Regex.Match(line, @"^[\s]*(\d+)\.\s+(.+)$");
                if (orderedMatch.Success)
                {
                    var para = new Paragraph { Margin = new Thickness(16, 1, 0, 1) };
                    para.Inlines.Add(new Run($"{orderedMatch.Groups[1].Value}. ") { Foreground = textColor });
                    AddInlineSpans(para, orderedMatch.Groups[2].Value, textColor, codeBg, codeColor, linkColor, secondaryColor);
                    doc.Blocks.Add(para);
                    continue;
                }

                // 引用 >
                var quoteMatch = Regex.Match(line, @"^>\s*(.*)$");
                if (quoteMatch.Success)
                {
                    var para = new Paragraph
                    {
                        Margin = new Thickness(8, 2, 0, 2),
                        Padding = new Thickness(8, 2, 0, 2),
                        BorderBrush = linkColor,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Background = codeBg,
                    };
                    AddInlineSpans(para, quoteMatch.Groups[1].Value, textColor, codeBg, codeColor, linkColor, secondaryColor);
                    doc.Blocks.Add(para);
                    continue;
                }

                // 水平线
                if (line.Trim() == "---" || line.Trim() == "***")
                {
                    doc.Blocks.Add(new Paragraph
                    {
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Margin = new Thickness(0, 4, 0, 4),
                    });
                    continue;
                }

                // 普通段落
                var textPara = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlineSpans(textPara, line, textColor, codeBg, codeColor, linkColor, secondaryColor);
                doc.Blocks.Add(textPara);
            }

            // 未闭合代码块
            if (inCodeBlock && codeBlockLines.Count > 0)
            {
                var p = new Paragraph
                {
                    Background = codeBg,
                    Margin = new Thickness(0, 4, 0, 4),
                    Padding = new Thickness(8, 6, 8, 6),
                };
                p.Inlines.Add(new Run(string.Join("\n", codeBlockLines))
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = codeColor,
                });
                doc.Blocks.Add(p);
            }
        }

        /// <summary>处理行内 span：**加粗** *斜体* `代码` [链接](url)</summary>
        private void AddInlineSpans(Paragraph para, string text,
            Brush textColor, Brush codeBg, Brush codeColor, Brush linkColor, Brush secondaryColor)
        {
            // 正则匹配 **bold** *italic* `code` [text](url)
            var pattern = @"(\*\*(.+?)\*\*|\*(.+?)\*|`(.+?)`|\[([^\]]+)\]\(([^)]+)\))";
            var matches = Regex.Matches(text, pattern);
            int lastEnd = 0;

            foreach (Match m in matches)
            {
                // 前面的普通文本
                if (m.Index > lastEnd)
                    para.Inlines.Add(new Run(text.Substring(lastEnd, m.Index - lastEnd)) { Foreground = textColor });

                if (m.Groups[2].Success) // **bold**
                {
                    para.Inlines.Add(new Run(m.Groups[2].Value) { FontWeight = FontWeights.Bold, Foreground = textColor });
                }
                else if (m.Groups[3].Success) // *italic*
                {
                    para.Inlines.Add(new Run(m.Groups[3].Value) { FontStyle = FontStyles.Italic, Foreground = textColor });
                }
                else if (m.Groups[4].Success) // `code`
                {
                    var inline = new Run(m.Groups[4].Value)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        Foreground = codeColor,
                        Background = codeBg,
                    };
                    para.Inlines.Add(inline);
                }
                else if (m.Groups[5].Success) // [text](url)
                {
                    var link = new Hyperlink(new Run(m.Groups[5].Value) { Foreground = linkColor })
                    {
                        NavigateUri = new Uri(m.Groups[6].Value),
                    };
                    link.RequestNavigate += (s, e) => System.Diagnostics.Process.Start("explorer.exe", e.Uri.ToString());
                    para.Inlines.Add(link);
                }

                lastEnd = m.Index + m.Length;
            }

            // 剩余文本
            if (lastEnd < text.Length)
                para.Inlines.Add(new Run(text.Substring(lastEnd)) { Foreground = textColor });
        }
    }
}
