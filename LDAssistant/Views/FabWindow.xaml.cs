using System;
using System.Windows;

namespace LDAssistant.Views
{
    /// <summary>
    /// AI 助手悬浮球独立窗口。
    /// WebView2 是原生 HWND 子窗口，会盖住同窗口内的所有 WPF 元素（airspace 限制），
    /// 因此悬浮球必须放在独立的无边框透明窗口中才能始终显示在预览内容之上。
    /// </summary>
    public partial class FabWindow : Window
    {
        public event EventHandler FabClicked;

        public FabWindow()
        {
            InitializeComponent();
        }

        private void FabBtn_Click(object sender, RoutedEventArgs e)
        {
            FabClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
