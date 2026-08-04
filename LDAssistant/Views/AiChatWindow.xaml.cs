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
            AddMessage("AI", "你好！我是 AI 助手。\n\n点击工具栏的 **OCR** 识别文字，**检查** 规范编号，结果都会显示在这里。你可以在消息上右键复制。");
        }

        public void SetContext(string context) => _context = context;

        // ═══════════════ 添加消息 ═══════════════

        /// <summary>添加消息：用户用气泡，AI/系统用无气泡富文本</summary>
        public void AddMessage(string role, string text)
        {
            var isUser = role == "我";

            if (isUser)
            {
                // 用户消息 — 蓝色气泡，右对齐
                var bubble = new Border
                {
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(40, 4, 4, 4),
                    CornerRadius = new CornerRadius(12),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Background = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)),
                };
                var para = new Paragraph { Margin = new Thickness(0) };
                para.Inlines.Add(new Run(text) { Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("微软雅黑") });
                var doc = new FlowDocument { PagePadding = new Thickness(4), TextAlignment = TextAlignment.Left };
                doc.Blocks.Add(para);
                var rtb = CreateRichText(doc, Brushes.Transparent, Brushes.White);

                // 用户消息也有操作按钮
                var userContainer = new StackPanel();
                userContainer.Children.Add(bubble);

                var userBtnBar = CreateButtonBar(text, role);
                userBtnBar.HorizontalAlignment = HorizontalAlignment.Right;
                userBtnBar.Margin = new Thickness(40, 2, 4, 0);
                userContainer.Children.Add(userBtnBar);

                MsgPanel.Children.Add(userContainer);
            }
            else
            {
                // AI / 系统消息 — 无气泡，左对齐，带角色标签
                var container = new StackPanel { Margin = new Thickness(4, 8, 4, 4) };

                // 角色标签
                var label = new TextBlock
                {
                    Text = role == "系统" ? "📋 系统" : "🤖 AI",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                    Margin = new Thickness(4, 0, 0, 2),
                };
                container.Children.Add(label);

                // 富文本内容
                var codeBg = role == "系统"
                    ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9))
                    : new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
                var codeColor = new SolidColorBrush(Color.FromRgb(0xC7, 0x25, 0x4E));
                var linkColor = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
                var textColor = Brushes.Black;

                var doc = new FlowDocument
                {
                    PagePadding = new Thickness(4, 0, 4, 0),
                    TextAlignment = TextAlignment.Left,
                    LineHeight = 22,
                };
                RenderMarkdown(doc, text, textColor, codeBg, codeColor, linkColor);

                var rtb = CreateRichText(doc, Brushes.White, textColor);
                rtb.Margin = new Thickness(0);
                container.Children.Add(rtb);

                // 操作按钮栏
                var btnBar = CreateButtonBar(text, role);
                container.Children.Add(btnBar);

                MsgPanel.Children.Add(container);
            }

            MsgPanel.UpdateLayout();
            MsgScroll.ScrollToBottom();
        }

        /// <summary>创建复制/导出按钮栏</summary>
        private StackPanel CreateButtonBar(string rawText, string role)
        {
            var btnBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 2, 0, 0),
            };

            var btnCopy = new Button
            {
                Content = "📋 复制",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            btnCopy.Click += (s, e) =>
            {
                try
                {
                    // 去掉 Markdown 标记，复制纯文本
                    var plain = StripMarkdown(rawText);
                    Clipboard.SetText(plain);
                }
                catch { }
            };

            var btnExport = new Button
            {
                Content = "📄 导出Word",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            btnExport.Click += (s, e) => ExportMessageToWord(rawText, role);

            btnBar.Children.Add(btnCopy);
            btnBar.Children.Add(btnExport);
            return btnBar;
        }

        /// <summary>去掉 Markdown 标记，返回纯文本</summary>
        private string StripMarkdown(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            var text = md;
            text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
            text = Regex.Replace(text, @"\*(.+?)\*", "$1");
            text = Regex.Replace(text, @"`(.+?)`", "$1");
            text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "$1");
            text = Regex.Replace(text, @"^#{1,4}\s+", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"^[\s]*[-*+]\s+", "• ", RegexOptions.Multiline);
            text = Regex.Replace(text, @"^>\s*", "", RegexOptions.Multiline);
            text = text.Replace("```", "");
            return text;
        }

        private RichTextBox CreateRichText(FlowDocument doc, Brush bg, Brush fg)
        {
            return new RichTextBox
            {
                Background = bg,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                FontSize = 13,
                FontFamily = new FontFamily("微软雅黑"),
                Foreground = fg,
                Padding = new Thickness(2),
                Margin = new Thickness(0),
                Document = doc,
            };
        }

        private void ExportMessageToWord(string text, string role)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Word 文档|*.docx",
                FileName = $"AI对话_{role}_{DateTime.Now:HHmmss}",
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(dlg.FileName, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
                    var mainPart = doc.AddMainDocumentPart();
                    mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
                    var body = new DocumentFormat.OpenXml.Wordprocessing.Body();
                    mainPart.Document.Append(body);

                    // 标题
                    var titlePara = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
                    var titleRun = new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text($"{role} 消息"))
                    {
                        RunProperties = new DocumentFormat.OpenXml.Wordprocessing.RunProperties { Bold = new DocumentFormat.OpenXml.Wordprocessing.Bold(), FontSize = new DocumentFormat.OpenXml.Wordprocessing.FontSize { Val = "32" } }
                    };
                    titlePara.Append(titleRun);
                    body.Append(titlePara);

                    // 时间
                    var timePara = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
                    var timeRun = new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
                    timePara.Append(timeRun);
                    body.Append(timePara);

                    // 分隔线
                    body.Append(new DocumentFormat.OpenXml.Wordprocessing.Paragraph());

                    // 内容（按行写入，保留表格）
                    var plain = StripMarkdown(text);
                    var lines = plain.Split('\n');
                    bool inTable = false;
                    var tableRows = new List<string[]>();

                    foreach (var line in lines)
                    {
                        var trimmed = line.TrimEnd();
                        // 表格行
                        if (trimmed.StartsWith("|") && trimmed.EndsWith("|"))
                        {
                            // 跳过分隔行
                            if (Regex.IsMatch(trimmed, @"^\|[\s\-:|]+\|$"))
                                continue;
                            var cells = trimmed.Trim('|').Split('|');
                            for (int i = 0; i < cells.Length; i++)
                                cells[i] = cells[i].Trim();
                            tableRows.Add(cells);
                            inTable = true;
                            continue;
                        }
                        else if (inTable)
                        {
                            // 表格结束，写入
                            if (tableRows.Count > 0)
                            {
                                WriteTableToBody(body, tableRows);
                                tableRows.Clear();
                            }
                            inTable = false;
                        }

                        var p = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
                        p.Append(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(trimmed)));
                        body.Append(p);
                    }

                    if (tableRows.Count > 0)
                        WriteTableToBody(body, tableRows);

                    body.Append(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("— end —"))));

                    mainPart.Document.Save();
                    MessageBox.Show($"已导出到:\n{dlg.FileName}", "导出成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>写入表格到 Word body</summary>
        private void WriteTableToBody(DocumentFormat.OpenXml.Wordprocessing.Body body, List<string[]> rows)
        {
            var tbl = new DocumentFormat.OpenXml.Wordprocessing.Table();
            var tblPr = new DocumentFormat.OpenXml.Wordprocessing.TableProperties(
                new DocumentFormat.OpenXml.Wordprocessing.TableBorders(
                    new DocumentFormat.OpenXml.Wordprocessing.TopBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 1 },
                    new DocumentFormat.OpenXml.Wordprocessing.BottomBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 1 },
                    new DocumentFormat.OpenXml.Wordprocessing.LeftBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 1 },
                    new DocumentFormat.OpenXml.Wordprocessing.RightBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 1 },
                    new DocumentFormat.OpenXml.Wordprocessing.InsideHorizontalBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 1 },
                    new DocumentFormat.OpenXml.Wordprocessing.InsideVerticalBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 1 }
                )
            );
            tbl.Append(tblPr);

            for (int r = 0; r < rows.Count; r++)
            {
                var tr = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
                foreach (var cell in rows[r])
                {
                    var tc = new DocumentFormat.OpenXml.Wordprocessing.TableCell();
                    var p = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
                    var run = new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(cell));
                    if (r == 0)
                        run.RunProperties = new DocumentFormat.OpenXml.Wordprocessing.RunProperties { Bold = new DocumentFormat.OpenXml.Wordprocessing.Bold() };
                    p.Append(run);
                    tc.Append(p);
                    tr.Append(tc);
                }
                tbl.Append(tr);
            }
            body.Append(tbl);
            body.Append(new DocumentFormat.OpenXml.Wordprocessing.Paragraph());
        }

        // ═══════════════ 按钮事件 ═══════════════

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

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            MsgPanel.Children.Clear();
            _history.Clear();
            AddMessage("AI", "对话已清空。");
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

                // 表格行
                if (line.TrimStart().StartsWith("|") && line.TrimEnd().EndsWith("|"))
                {
                    if (Regex.IsMatch(line, @"^\|[\s\-:|]+\|$"))
                    { inTable = true; continue; }
                    inTable = true;
                    tableLines.Add(line);
                    continue;
                }
                else if (inTable)
                {
                    FlushTable(doc, tableLines, textColor, codeBg, linkColor);
                    tableLines.Clear();
                    inTable = false;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 2, 0, 2) });
                    continue;
                }

                // 标题
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

                // 无序列表
                var listMatch = Regex.Match(line, @"^[\s]*[-*+]\s+(.+)$");
                if (listMatch.Success)
                {
                    var para = new Paragraph { Margin = new Thickness(16, 1, 0, 1) };
                    para.Inlines.Add(new Run("• ") { Foreground = textColor });
                    AddInlineSpans(para, listMatch.Groups[1].Value, textColor, codeBg, codeColor, linkColor);
                    doc.Blocks.Add(para);
                    continue;
                }

                // 有序列表
                var orderedMatch = Regex.Match(line, @"^[\s]*(\d+)\.\s+(.+)$");
                if (orderedMatch.Success)
                {
                    var para = new Paragraph { Margin = new Thickness(16, 1, 0, 1) };
                    para.Inlines.Add(new Run($"{orderedMatch.Groups[1].Value}. ") { Foreground = textColor });
                    AddInlineSpans(para, orderedMatch.Groups[2].Value, textColor, codeBg, codeColor, linkColor);
                    doc.Blocks.Add(para);
                    continue;
                }

                // 引用
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

            if (inTable && tableLines.Count > 0)
                FlushTable(doc, tableLines, textColor, codeBg, linkColor);
        }

        // ═══════════════════════════════════════════════════════════
        //  Markdown 表格 → WPF Table
        // ═══════════════════════════════════════════════════════════

        private void FlushTable(FlowDocument doc, List<string> tableLines,
            Brush textColor, Brush codeBg, Brush linkColor)
        {
            if (tableLines.Count == 0) return;

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

            for (int i = 0; i < colCount; i++)
                table.Columns.Add(new TableColumn());

            var borderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            var headerBg = new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5));
            var altBg = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));

            var rowGroup = new TableRowGroup();
            table.RowGroups.Add(rowGroup);

            for (int r = 0; r < rows.Count; r++)
            {
                var tr = new TableRow();

                if (r == 0)
                    tr.Background = headerBg;
                else if (r % 2 == 0)
                    tr.Background = altBg;

                for (int c = 0; c < colCount; c++)
                {
                    var cellText = c < rows[r].Count ? rows[r][c] : "";
                    var para = new Paragraph { Margin = new Thickness(2, 1, 2, 1) };

                    var cellTextColor = r == 0 ? Brushes.White : textColor;

                    AddInlineSpans(para, cellText, cellTextColor, codeBg, new SolidColorBrush(Color.FromRgb(0xC7, 0x25, 0x4E)), linkColor);

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

                if (m.Groups[2].Success)
                    para.Inlines.Add(new Run(m.Groups[2].Value) { FontWeight = FontWeights.Bold, Foreground = textColor });
                else if (m.Groups[3].Success)
                    para.Inlines.Add(new Run(m.Groups[3].Value) { FontStyle = FontStyles.Italic, Foreground = textColor });
                else if (m.Groups[4].Success)
                    para.Inlines.Add(new Run(m.Groups[4].Value)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        Foreground = codeColor,
                        Background = codeBg,
                    });
                else if (m.Groups[5].Success)
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
