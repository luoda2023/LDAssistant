using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LDAssistant.Services;

namespace LDAssistant.Views
{
    public partial class AiChatWindow : Window
    {
        private readonly AiService _ai;
        private readonly List<(string role, string content)> _history = new();
        private string _context = "";
        private string _lastContextSent = "";       // 已发送给 AI 的上下文（内容变化才重发，避免每轮重复占用 token）
        private string _contextSummary = null;       // 长上下文自动总结结果（压缩后发送，减少 token）
        private Task _summaryTask;                   // 后台总结任务（发送消息前若未完成则等待）
        private const int ContextSummaryThreshold = 1200;  // 超过该长度的上下文自动总结
        private const int MaxContextChars = 6000;   // 总结失败/未启用时的原文上限
        private const int MaxHistoryTurns = 30;     // 对话历史上限（轮），防止长会话内存无限增长
        private bool _sending;                   // 防重复发送：等待回复期间忽略再次点击/回车
        private CancellationTokenSource _streamCts;  // 流式输出取消（窗口关闭时中止网络请求）
        private DispatcherTimer _streamTimer;        // 打字机推进定时器（窗口关闭时停止）

        /// <summary>请求主窗口执行规范检查（标题栏按钮触发）</summary>
        public event Action CheckSpecRequested;

        public AiChatWindow(AiService ai)
        {
            InitializeComponent();
            _ai = ai;
            ModelLabel.Text = $"模型: {ai.Model ?? "hermesAPI"}";
            // 窗口尺寸固定，但确保圆角裁剪与实际尺寸一致
            UpdateRootClip();
        }

        /// <summary>窗口尺寸变化时同步圆角裁剪区域（内容四角保持真圆角）</summary>

        private void BtnSpecCheck_Click(object sender, RoutedEventArgs e)
        {
            try { CheckSpecRequested?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"规范检查触发失败: {ex.Message}"); }
        }
        private void RootPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateRootClip();
        }

        private void UpdateRootClip()
        {
            if (RootClip == null) return;
            RootClip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        }

        public void SetContext(string context)
        {
            context ??= "";
            // 上下文未变且已有总结（或总结进行中）时不重复触发
            if (context == _context && (_contextSummary != null || _summaryTask != null))
                return;
            _context = context;
            _contextSummary = null;
            _summaryTask = null;
            // 长上下文（主要是大段 OCR 文本）自动总结压缩，减少后续发送的 token 占用
            if (_context.Length > ContextSummaryThreshold)
            {
                UpdateCtxStatus("📝 正在总结上下文…");
                _summaryTask = SummarizeContextAsync();
            }
            else
            {
                UpdateCtxStatus("");
            }
        }

        /// <summary>后台总结长上下文，完成后缓存压缩版</summary>
        private async Task SummarizeContextAsync()
        {
            var ctxAtStart = _context;
            try
            {
                var summary = await _ai.SummarizeAsync(_context);
                if (_context != ctxAtStart || string.IsNullOrWhiteSpace(summary)) return;
                _contextSummary = summary;
                UpdateCtxStatus($"上下文已压缩 {ctxAtStart.Length}→{summary.Length} 字");
            }
            catch
            {
                if (_context == ctxAtStart)
                    UpdateCtxStatus("上下文总结失败，将发送原文");
            }
        }

        /// <summary>更新头部上下文状态栏</summary>
        private void UpdateCtxStatus(string text)
        {
            if (CtxStatusLabel != null)
                CtxStatusLabel.Text = text ?? "";
        }

        // ═══════════════ 添加消息 ═══════════════

        /// <summary>隐藏欢迎态（收到第一条消息后）</summary>
 private void HideWelcome()
 {
 if (WelcomePanel != null && WelcomePanel.Visibility == Visibility.Visible)
 WelcomePanel.Visibility = Visibility.Collapsed;
 }

 /// <summary>添加消息：用户用气泡，AI/系统用无气泡富文本</summary>
 public void AddMessage(string role, string text)
 {
 HideWelcome();
 // 限制消息数量，防止内存无限增长（保留最近50条）
 while (MsgPanel.Children.Count > 50)
 {
 var old = MsgPanel.Children[0];
 if (old is FrameworkElement fe)
 fe.DataContext = null;
 MsgPanel.Children.RemoveAt(0);
 }

 var isUser = role == "我";

 if (isUser)
 {
 // 用户消息 — 灰色气泡，右对齐
 var para = new Paragraph { Margin = new Thickness(0) };
 para.Inlines.Add(new Run(text) { Foreground = Brushes.Black, FontSize = 13, FontFamily = new FontFamily("微软雅黑") });
 var doc = new FlowDocument { PagePadding = new Thickness(4), TextAlignment = TextAlignment.Left };
 doc.Blocks.Add(para);
 var rtb = CreateRichText(doc, Brushes.Transparent, Brushes.Black);

 var bubble = new Border
 {
 Padding = new Thickness(12, 8, 12, 8),
 Margin = new Thickness(40, 4, 4, 4),
 CornerRadius = new CornerRadius(12),
 HorizontalAlignment = HorizontalAlignment.Right,
 Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF5)),
 Child = rtb,
 };

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
                var container = CreateAiContainer(role, out var rtbMsg, text);

                // 操作按钮栏
                var btnBar = CreateButtonBar(text, role);
                container.Children.Add(btnBar);

                MsgPanel.Children.Add(container);
            }

            MsgPanel.UpdateLayout();
            MsgScroll.ScrollToBottom();
        }

        /// <summary>创建 AI/系统消息容器（角色标签 + 富文本），返回可流式更新的 RichTextBox</summary>
        private StackPanel CreateAiContainer(string role, out RichTextBox rtb, string text)
        {
            var container = new StackPanel { Margin = new Thickness(4, 8, 4, 4) };

            // 角色标签
            var label = new TextBlock
            { Text = role == "系统" ? "系统" : "AI",
 FontSize = 11,
 FontWeight = FontWeights.Bold,
 Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
 Margin = new Thickness(4, 0, 0, 2),
 };
            container.Children.Add(label);

            // 富文本内容
            var codeBg = role == "系统"
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9))
                : new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
            var codeColor = new SolidColorBrush(Color.FromRgb(0xC7, 0x25, 0x4E));
            var linkColor = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            var textColor = Brushes.Black;

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(4, 0, 4, 0),
                TextAlignment = TextAlignment.Left,
                LineHeight = 22,
            };
            RenderMarkdown(doc, text ?? "", textColor, codeBg, codeColor, linkColor);

            rtb = CreateRichText(doc, Brushes.White, textColor);
            rtb.Margin = new Thickness(0);
            container.Children.Add(rtb);
            return container;
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
            if (_sending) return;   // 上一轮还没回复，忽略重复点击/回车

            _sending = true;
            BtnSend.IsEnabled = false;
            try
            {
                await SendMessageCore(text);
            }
            finally
            {
                _sending = false;
                BtnSend.IsEnabled = true;
            }
        }

        private async Task SendMessageCore(string text)
        {
            InputBox.Clear();
            HideWelcome();
            AddMessage("我", text);

            // 创建 AI 消息容器，内容在流式到达时原地更新（打字机效果）
            var container = CreateAiContainer("AI", out var rtb, "");
            MsgPanel.Children.Add(container);
            SetPlaceholder(rtb, "正在思考...");
            MsgPanel.UpdateLayout();
            MsgScroll.ScrollToBottom();

            // 上下文只在首次或内容变化时随消息发送一次，避免每轮重复携带大段 OCR 文本
            // （占 token、易超限；后续轮次 AI 已从历史中知道上下文）
            // 长上下文（>1200 字）在 SetContext 时已自动总结，这里等待总结完成并使用压缩版
            var fullMessage = text;
            if (!string.IsNullOrEmpty(_context) && _context != _lastContextSent)
            {
                if (_summaryTask != null)
                {
                    SetPlaceholder(rtb, "正在总结上下文...");
                    try { await _summaryTask; } catch { }
                    SetPlaceholder(rtb, "正在思考...");
                }
                var ctx = _contextSummary
                    ?? (_context.Length > MaxContextChars ? _context[..MaxContextChars] + "…（过长已截断）" : _context);
                fullMessage = $"[上下文]\n{ctx}\n\n[问题]\n{text}";
                _lastContextSent = _context;
            }

            var full = new StringBuilder();          // 已从网络收到的完整内容
            int shown = 0;                           // 已“打字”显示出的字符数
            bool streamDone = false;
            DispatcherTimer timer = null;            // 打字机定时器（本地引用，避免多轮并发互相干扰）
            _streamCts = new CancellationTokenSource();
            _streamTimer = null;

            void RenderText(string text)
            {
                var doc = new FlowDocument
                {
                    PagePadding = new Thickness(4, 0, 4, 0),
                    TextAlignment = TextAlignment.Left,
                    LineHeight = 22,
                };
                RenderMarkdown(doc, text, Brushes.Black,
                    new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
                    new SolidColorBrush(Color.FromRgb(0xC7, 0x25, 0x4E)),
                    new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)));
                rtb.Document = doc;
                MsgScroll.ScrollToBottom();
            }

            void FinishStream()
            {
                timer?.Stop();
                _streamTimer = null;
                var reply = full.ToString();
                if (shown < reply.Length)
                {
                    shown = reply.Length;
                    RenderText(reply);
                }
                container.Children.Add(CreateButtonBar(reply, "AI"));
                MsgScroll.ScrollToBottom();
                _history.Add(("user", text));
                _history.Add(("assistant", reply));
                // 历史上限：保留最近 MaxHistoryTurns 轮，超出丢弃最旧轮次
                while (_history.Count > MaxHistoryTurns * 2)
                    _history.RemoveRange(0, 2);
            }

            try
            {
                await _ai.ChatStreamAsync(fullMessage,
                    _history.Count > 10 ? _history.GetRange(_history.Count - 10, 10) : _history,
                    delta =>
                    {
                        full.Append(delta);
                        // 打字机定时器：25ms 推进若干字符，长回复自动提速
                        if (timer == null)
                        {
                            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                            timer.Tick += (s, e) =>
                            {
                                if (shown >= full.Length)
                                {
                                    if (streamDone) FinishStream();
                                    return;
                                }
                                var step = Math.Max(3, full.Length / 120);
                                shown = Math.Min(full.Length, shown + step);
                                RenderText(full.ToString(0, shown));
                            };
                            timer.Start();
                            _streamTimer = timer;
                        }
                    },
                    _streamCts.Token);

                streamDone = true;
                if (timer == null || shown >= full.Length)
                    FinishStream();
            }
            catch (Exception ex)
            {
                timer?.Stop();
                _streamTimer = null;
                // 保留已流出的部分内容，追加错误提示
                var partial = full.ToString();
                var msg = string.IsNullOrEmpty(partial)
                    ? $"请求失败: {ex.Message}"
                    : $"{partial}\n\n⚠️ 请求失败: {ex.Message}";
                RenderText(msg);
                container.Children.Add(CreateButtonBar(msg, "AI"));
            }
            finally
            {
                _streamCts?.Dispose();
                _streamCts = null;
            }
        }

        /// <summary>设置消息占位符（正在思考/正在总结）</summary>
        private void SetPlaceholder(RichTextBox rtb, string text)
        {
            rtb.Document = new FlowDocument(
                new Paragraph(new Run(text)
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                })
                { Margin = new Thickness(0) });
        }

        protected override void OnClosed(EventArgs e)
        {
            // 关闭窗口时中止进行中的流式请求与打字机动画
            try { _streamCts?.Cancel(); } catch { }
            _streamTimer?.Stop();
            base.OnClosed(e);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            MsgPanel.Children.Clear();
            _history.Clear();
            _streamCts?.Cancel();
            _streamTimer?.Stop();
            if (WelcomePanel != null) WelcomePanel.Visibility = Visibility.Visible;
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>标题栏拖动（无边框窗口移动）；按钮区域不触发拖动</summary>
        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.Button) return;
            try { DragMove(); } catch { }
        }

        /// <summary>快捷指令：总结文件</summary>
        private async void Pill_Summary(object sender, MouseButtonEventArgs e)
        {
            InputBox.Text = "请总结当前打开文件的内容要点，按条目列出。";
            await SendMessage();
        }

        /// <summary>快捷指令：规范检查</summary>
        private async void Pill_Check(object sender, MouseButtonEventArgs e)
        {
            InputBox.Text = "请检查当前文件内容是否符合相关规范，列出涉及的规范编号与结论。";
            await SendMessage();
        }

        /// <summary>快捷指令：校对文字</summary>
        private async void Pill_Proof(object sender, MouseButtonEventArgs e)
        {
            InputBox.Text = "请校对当前文件内容中的错别字和表述问题，并给出修改建议。";
            await SendMessage();
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
            var headerBg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
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

                    var cellTextColor = r == 0 ? Brushes.Black : textColor;

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
 var link = new Hyperlink(new Run(m.Groups[5].Value) { Foreground = linkColor });
 if (Uri.TryCreate(m.Groups[6].Value, UriKind.Absolute, out var uri))
 link.NavigateUri = uri;
 link.RequestNavigate += (s, e) =>
 {
 try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); }
 catch { }
 };
 para.Inlines.Add(link);
 }

                lastEnd = m.Index + m.Length;
            }

            if (lastEnd < text.Length)
                para.Inlines.Add(new Run(text.Substring(lastEnd)) { Foreground = textColor });
        }
    }
}
