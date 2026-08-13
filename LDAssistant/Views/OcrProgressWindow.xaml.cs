using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LDAssistant.Views
{
    /// <summary>
    /// 独立置顶的 OCR 进度提示窗口。
    /// 独立窗口而非 WPF 覆盖层的原因：WebView2 是独立 HWND，会盖住同容器内的 WPF 元素
    /// （airspace 限制），导致 PDF/MD/docx 等用 WebView2 渲染的格式看不到进度条。
    /// 置顶窗口天然在 WebView2 之上，任何格式都能显示。
    /// </summary>
    public partial class OcrProgressWindow : Window
    {
        private DispatcherTimer _hideTimer;
        private Action _afterHide;

        public OcrProgressWindow()
        {
            InitializeComponent();
            // 点击穿透 + 不抢焦点：OCR 期间用户仍可操作主窗口
            SourceInitialized += (_, __) => MakeClickThrough();
        }

        private void MakeClickThrough()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            }
            catch { }
        }

        public void ShowProgress(string message)
        {
            _afterHide = null;
            _hideTimer?.Stop();
            Dispatcher.Invoke(() =>
            {
                ProgressText.Text = message;
                ProgressText.Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                ProgressPercent.Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                ProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xE8));
                ProgressBar.Value = 0;
                ProgressPercent.Text = "0%";
                if (!IsVisible) Show();
            });
        }

        public void UpdateProgress(double percent, string message = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (message != null) ProgressText.Text = message;
                ProgressBar.Value = Math.Min(100, Math.Max(0, percent));
                ProgressPercent.Text = $"{(int)Math.Min(100, Math.Max(0, percent))}%";
            });
        }

        /// <summary>完成/失败提示：100% + 彩色状态文字，停留 3 秒后隐藏并执行 after</summary>
        public void ShowDone(bool success, string detail, Action after = null)
        {
            _afterHide = after;
            _hideTimer?.Stop();
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = 100;
                ProgressPercent.Text = "100%";
                var brush = new SolidColorBrush(success
                    ? Color.FromRgb(0x16, 0xA3, 0x4A)
                    : Color.FromRgb(0xDC, 0x26, 0x26));
                ProgressBar.Foreground = brush;
                ProgressText.Foreground = brush;
                ProgressPercent.Foreground = brush;
                ProgressText.Text = success
                    ? $"✅ 识别完成 — {detail}"
                    : $"❌ 识别失败 — {detail}";
                if (!IsVisible) Show();
                _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _hideTimer.Tick += (s, e) =>
                {
                    _hideTimer.Stop();
                    Hide();
                    var cb = _afterHide;
                    _afterHide = null;
                    cb?.Invoke();
                };
                _hideTimer.Start();
            });
        }

        public void HideProgress()
        {
            _afterHide = null;
            _hideTimer?.Stop();
            Dispatcher.Invoke(() =>
            {
                if (IsVisible) Hide();
            });
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
