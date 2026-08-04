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
            AddMessage("AI", "你好！我是 AI 助手。\n\n点击工具栏的 **OCR** 识别文字，**检查** 规范编号，结果都会显示在这里。");
        }

        public void SetContext(string context) => _context = context;

        // ═════════════ 添加消息气泡 ═════════════

        public void AddMessage(string role, string text)
        {
            var isUser = role == "我";
            var isSystem = role == "系统";

            // 颜色方案
            var textColor = isUser ? Brushes.White : Brushes.Black;
            var codeBg = isUser
                ? new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0))
                : isSystem
                    ? new SolidColorBrush(Color.FromRgb(0xC8, 0xE6, 0xC9))
                    : new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
            var codeColor = isUser ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xC7, 0x25, 0x4E));
            var linkColor = isUser ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));

            var bubble = new Border
            {
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(4, 4, 4, 4),
                CornerRadius = new CornerRadius(12),
                MaxWidth = 460,
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Background = isUser
                    ? new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3))
                    : isSystem
                        ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9))
                        : new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = isSystem
                    ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
                    : new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                BorderThickness = new Thickness(1),
            };

            var rtb = new RichTextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                FontSize = 13,
                FontFamily = new FontFamily("微软雅黑"),
                Foreground = textColor,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
            };

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
                LineHeight = 22,
            };

            RenderMarkdown(doc, text, textColor, codeBg, codeColor, linkColor);
            rtb.Document = doc;

            bubble.Child = rtb;
            MsgPanel.Children.Add(bubble);

            MsgPanel.UpdateLayout();
            MsgScroll.ScrollToBottom();
        }

        // ═════════════ 发送消息 ═════════════

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

            var fullMessage = text;
            if (!string.IsNullOrEmpty(_context))
                fullMessage = $"[上下文]\n{_context}\n\n[问题]\n{text}";

            AddMessage("AI", "正在思考...");

            try
            {
                var reply = await _ai.ChatAsync(fullMessage,
                    _history.Count > 10 ? _history.GetRange(_history.Count - 10, 10) : _history);

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
        //  Markdown → FlowDocument 渲染器
        // ═══════════════════════════════════════════════════════════

        private void RenderMarkdown(FlowDocument doc, string text,
            Brush textColor, Brush codeBg, Brush codeColor, Brush linkColor)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            bool inCodeBlock = false;
            var codeBlockLines = new List<string>();
            var tableLines = new List<string>();
            bool inTable = false;

            foreach (var rawLine in lines)
            {
                var line = rawLine;

                // 代码块 ``` 开始/结束
                if (line.TrimStart().StartsWith("```"))
                {
                    // 先刷新表格
                    if (inTable) { FlushTable(doc, tableLines, textColor, codeBg, linkColor); tableLines.Clear(); inTable = false; }

                    if (inCodeBlock)
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

                // Markdown 表格行 | ... | ... |
                if (line.TrimStart().StartsWith("|") && line.TrimEnd().EndsWith("|"))
                {
                    // 跳过分隔行 |---|---|
                    if (Regex.IsMatch(line, @"^\|[\s\-:|]+\|$"))
                    { inTable = true; continue; }

                    inTable = true;
                    tableLines.Add(line);
                    continue;
                }
                else if (inTable)
                {
                    // 表格结束，渲染
                    FlushTable(doc, tableLines, textColor, codeBg, linkColor);
                    tableLines.Clear();
                    inTable = false;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 2, 0, 2) });
                    continue;
                }

                // 标题 # ## ### ####
                var titleMatch = Regex.Match(line, @"^(#{1,4})\s+(.+)$");
                if (titleMatch.Success)
                {
                    int level = titleMatch.Groups[1].Value.Length;
                    var sizes = new double[] { 18, 16, 14, 13 };
                    var para = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
                    para.Inlines.Add(new Run(titleMatch.Groups[2].Value)
                    {
                        FontSize = sizes[level - 1],
                        FontWeight = FontWeights.Bold,
                        Foreground = textColor,
                    });
                    doc.Blocks.Add(para);
                    continue;
                }

                // 无序列表 - * +
                var listMatch = Regex.Match(line, @"^[\s]*[-*+]\s+(.+)$");
                if (listMatch.Success)
                {
                    var para = new Paragraph { Margin = new Thickness(16, 1, 0, 1) };
                    para.Inlines.Add(new Run("• ") { Foreground = textColor });
                    AddInlineSpans(para, listMatch.Groups[1].Value, textColor, codeBg, codeColor, linkColor);
                    doc.Blocks.Add(para);
                    continue;
                }

                // 有序列表 1. 2.
                var orderedMatch = Regex.Match(line, @"^[\s]*(\d+)\.\s+(.+)$");
                if (orderedMatch.Success)
                {
                    var para = new Paragraph { Margin = new Thickness(16, 1, 0, 1) };
                    para.Inlines.Add(new Run($"{orderedMatch.Groups[1].Value}. ") { Foreground = textColor });
                    AddInlineSpans(para, orderedMatch.Groups[2].Value, textColor, codeBg, codeColor, linkColor);
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
                    AddInlineSpans(para, quoteMatch.Groups[1].Value, textColor, codeBg, codeColor, linkColor);
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
                AddInlineSpans(textPara, line, textColor, codeBg, codeColor, linkColor);
                doc.Blocks.Add(textPara);
            }

            // 结束时刷新表格
            if (inTable && tableLines.Count > 0)
                FlushTable(doc, tableLines, textColor, codeBg, linkColor);

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

        // ═══════════════════════════════════════════════════════════
        //  Markdown 表格 → WPF Table 渲染
        // ═══════════════════════════════════════════════════════════

        private void FlushTable(FlowDocument doc, List<string> tableLines,
            Brush textColor, Brush codeBg, Brush linkColor)
        {
            if (tableLines.Count == 0) return;

            // 解析单元格
            var rows = new List<List<string>>();
            foreach (var line in tableLines)
            {
                var trimmed = line.Trim().Trim('|');
                var cells = trimmed.Split('|');
                var row = new List<string>();
                foreach (var c in cells)
                    row.Add(c.Trim());
                rows.Add(row);
            }

            if (rows.Count == 0) return;
            int colCount = rows[0].Count;

            var table = new System.Windows.Documents.Table
            {
                Margin = new Thickness(0, 4, 0, 4),
                CellSpacing = 0,
            };

            // 列
            for (int i = 0; i < colCount; i++)
                table.Columns.Add(new TableColumn());

            // 表格边框
            var borderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            var headerBg = new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5));
            var altBg = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));

            var rowGroup = new TableRowGroup();
            table.RowGroups.Add(rowGroup);

            for (int r = 0; r < rows.Count; r++)
            {
                var tr = new TableRow();

                // 行背景
                if (r == 0)
                    tr.Background = headerBg;
                else if (r % 2 == 0)
                    tr.Background = altBg;

                for (int c = 0; c < colCount; c++)
                {
                    var cellText = c < rows[r].Count ? rows[r][c] : "";
                    var para = new Paragraph { Margin = new Thickness(2, 1, 2, 1) };

                    // 表头加粗白色，普通行黑色
                    var cellTextColor = r == 0 ? Brushes.White : textColor;
                    var cellFontWeight = r == 0 ? FontWeights.Bold : FontWeights.Normal;

                    // 支持行内 markdown
                    AddInlineSpans(para, cellText, cellTextColor, codeBg, new SolidColorBrush(Color.FromRgb(0xC7, 0x25, 0x4E)), linkColor);

                    // 给每个 inline 加粗（表头）
                    if (r == 0)
                    {
                        foreach (var inline in para.Inlines)
                        {
                            if (inline is Run run)
                                run.FontWeight = FontWeights.Bold;
                        }
                    }

                    var cell = new TableCell(para)
                    {
                        BorderBrush = borderBrush,
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(4, 2, 4, 2),
                    };
                    tr.Cells.Add(cell);
                }

                rowGroup.Rows.Add(tr);
            }

            doc.Blocks.Add(table);
        }

        /// <summary>行内 span：**加粗** *斜体* `代码` [链接](url)</summary>
        private void AddInlineSpans(Paragraph para, string text,
            Brush textColor, Brush codeBg, Brush codeColor, Brush linkColor)
        {
            var pattern = @"(\*\*(.+?)\*\*|\*(.+?)\*|`(.+?)`|\[([^\]]+)\]\(([^)]+)\))";
            var matches = Regex.Matches(text, pattern);
            int lastEnd = 0;

            foreach (Match m in matches)
            {
                if (m.Index > lastEnd)
                    para.Inlines.Add(new Run(text.Substring(lastEnd, m.Index - lastEnd)) { Foreground = textColor });

                if (m.Groups[2].Success) // **bold**
                    para.Inlines.Add(new Run(m.Groups[2].Value) { FontWeight = FontWeights.Bold, Foreground = textColor });
                else if (m.Groups[3].Success) // *italic*
                    para.Inlines.Add(new Run(m.Groups[3].Value) { FontStyle = FontStyles.Italic, Foreground = textColor });
                else if (m.Groups[4].Success) // `code`
                    para.Inlines.Add(new Run(m.Groups[4].Value)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        Foreground = codeColor,
                        Background = codeBg,
                    });
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

            if (lastEnd < text.Length)
                para.Inlines.Add(new Run(text.Substring(lastEnd)) { Foreground = textColor });
        }
    }
}
