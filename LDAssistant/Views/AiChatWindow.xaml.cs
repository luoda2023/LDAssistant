using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LDAssistant.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;

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

            AddMessage("AI", "你好！我是 AI 助手。你可以问我关于规范编号的问题，我会帮你分析。");
        }

        public void SetContext(string context)
        {
            _context = context;
        }

        private void AddMessage(string role, string text)
        {
            var isUser = role == "我";
            var bubble = new Border
            {
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(4, 4, 4, 4),
            CornerRadius = new CornerRadius(12),
            MaxWidth = 380,
            HorizontalAlignment = isUser ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left,
                Background = isUser
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x21, 0x96, 0xF3))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF5)),
            };

            var tb = new TextBlock
            {
                Text = text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = isUser
                    ? System.Windows.Media.Brushes.White
                    : System.Windows.Media.Brushes.Black,
            };

            bubble.Child = tb;
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
    }
}
