using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LDAssistant.Models;

namespace LDAssistant.Views
{
    /// <summary>导出筛选对话框：按来源 + 状态多选过滤检查结果</summary>
    public partial class ExportFilterWindow : Window
    {
        private readonly List<CheckBox> _sourceBoxes = new();
        private readonly List<CheckBox> _statusBoxes = new();

        public ExportFilterWindow(List<CheckResult> results)
        {
            InitializeComponent();
            BuildSourceOptions(results);
            BuildStatusOptions(results);
        }

        private void BuildSourceOptions(List<CheckResult> results)
        {
            foreach (var g in results
                .GroupBy(r => CheckResult.SourceLabel(r.Source))
                .OrderByDescending(g => g.Count()))
            {
                var cb = new CheckBox
                {
                    Content = $"{g.Key}（{g.Count()}）",
                    Tag = g.Key,
                    IsChecked = true,
                    Margin = new Thickness(0, 3, 0, 3),
                    FontSize = 13,
                };
                SourcePanel.Children.Add(cb);
                _sourceBoxes.Add(cb);
            }
        }

        private void BuildStatusOptions(List<CheckResult> results)
        {
            foreach (var g in results
                .GroupBy(r => CheckResult.NormStatus(r.Status))
                .OrderByDescending(g => g.Count()))
            {
                var cb = new CheckBox
                {
                    Content = $"{g.Key}（{g.Count()}）",
                    Tag = g.Key,
                    IsChecked = true,
                    Margin = new Thickness(0, 3, 0, 3),
                    FontSize = 13,
                };
                StatusPanel.Children.Add(cb);
                _statusBoxes.Add(cb);
            }
        }

        /// <summary>按对话框当前勾选条件过滤结果</summary>
        public List<CheckResult> Filter(List<CheckResult> results)
        {
            var sources = _sourceBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToHashSet();
            var statuses = _statusBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToHashSet();
            return results
                .Where(r => sources.Contains(CheckResult.SourceLabel(r.Source))
                         && statuses.Contains(CheckResult.NormStatus(r.Status)))
                .ToList();
        }

        private void BtnAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var b in _sourceBoxes.Concat(_statusBoxes)) b.IsChecked = true;
        }

        private void BtnNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var b in _sourceBoxes.Concat(_statusBoxes)) b.IsChecked = false;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
