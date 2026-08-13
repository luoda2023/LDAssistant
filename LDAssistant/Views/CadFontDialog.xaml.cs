using System;
using System.Windows;

namespace LDAssistant.Views
{
	/// <summary>
	/// CAD字宽设置对话框（简化版）—— 只设置字宽（宽度因子），
	/// 字体由图纸自带样式决定，不再由用户覆盖。
	/// </summary>
	public partial class CadFontDialog : Window
	{
		// ═══ 输出参数 ═══
		public double WidthFactor { get; private set; } = 1.0;
		public bool IsApplied { get; private set; } = false;

		public CadFontDialog(double widthFactor = 1.0)
		{
			InitializeComponent();

			// 字宽预设值（可编辑，允许输入任意值）
			var presets = new[] { "0.60", "0.70", "0.80", "1.00", "1.20", "1.50", "2.00" };
			foreach (var p in presets)
				CboWidthFactor.Items.Add(p);
			CboWidthFactor.Text = widthFactor.ToString("F2");
		}

		// ═══ 按钮 ═══
		private void BtnApply_Click(object sender, RoutedEventArgs e)
		{
			WidthFactor = (double.TryParse(CboWidthFactor.Text, out var v) && v > 0) ? v : 1.0;
			IsApplied = true;
			DialogResult = true;
		}

		private void BtnCancel_Click(object sender, RoutedEventArgs e)
		{
			IsApplied = false;
			DialogResult = false;
		}
	}
}
