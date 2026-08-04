using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LDAssistant.Models;
using LDAssistant.Services;

namespace LDAssistant.Views
{
    public partial class StandardQueryWindow : Window
    {
        private readonly StandardChecker _checker;
        public ObservableCollection<StandardRecord> Results { get; } = new();

        public StandardQueryWindow(StandardChecker checker)
        {
            InitializeComponent();
            _checker = checker;
            ResultGrid.ItemsSource = Results;

            // 加载分类
            LoadCategories();
        }

        private void LoadCategories()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var cats = _checker.GetCategories();
                var total = _checker.GetTotalCount();
                var stats = _checker.GetStatusStats();

                Dispatcher.Invoke(() =>
                {
                    DbInfo.Text = $"共 {total:N0} 条记录 | {cats.Count} 个分类";

                    CategoryCombo.Items.Clear();
                    CategoryCombo.Items.Add(new ComboBoxItem { Content = "全部分类", Tag = "" });
                    foreach (var (type, count) in cats)
                    {
                        CategoryCombo.Items.Add(new ComboBoxItem
                        {
                            Content = $"{type} ({count:N0})",
                            Tag = type,
                        });
                    }
                    CategoryCombo.SelectedIndex = 0;
                });
            });
        }

        private void BtnSearch_Click(object sender, RoutedEventArgs e) => DoSearch();

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                DoSearch();
            }
        }

        private void DoSearch()
        {
            var keyword = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                MessageBox.Show("请输入编号或名称进行查询。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 获取选中的分类
            string category = "";
            if (CategoryCombo.SelectedItem is ComboBoxItem catItem && catItem.Tag is string tag)
                category = tag;

            // 获取选中的状态
            string status = "";
            if (StatusCombo.SelectedItem is ComboBoxItem statusItem)
            {
                var statusText = statusItem.Content?.ToString();
                if (statusText != "全部")
                    status = statusText ?? "";
            }

            Results.Clear();
            ResultInfo.Text = "查询中...";
            BtnSearch.IsEnabled = false;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<StandardRecord> results;
                if (!string.IsNullOrEmpty(status))
                    results = _checker.SearchByStatus(keyword, status, category, 500);
                else
                    results = _checker.Search(keyword, category, 500);

                Dispatcher.Invoke(() =>
                {
                    foreach (var r in results)
                        Results.Add(r);

                    ResultInfo.Text = $"查询到 {results.Count} 条记录"
                        + (string.IsNullOrEmpty(category) ? "" : $" | 分类: {category}")
                        + (string.IsNullOrEmpty(status) ? "" : $" | 状态: {status}");
                    BtnSearch.IsEnabled = true;
                });
            });
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (ResultGrid.SelectedItem is StandardRecord record)
            {
                var text = $"编号: {record.Code}\n名称: {record.Name}\n状态: {record.Status}\n发布单位: {record.Publisher}\n实施日期: {record.ImplementDate}\n替代信息: {record.ReplacementRaw}\n来源: {record.SourceType}";
                try
                {
                    Clipboard.SetText(text);
                    MessageBox.Show("已复制到剪贴板", "复制成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
            }
            else if (Results.Count > 0)
            {
                // 复制全部
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("序号\t编号\t名称\t状态\t发布单位\t实施日期\t替代信息\t来源");
                foreach (var r in Results)
                    sb.AppendLine($"{r.Id}\t{r.Code}\t{r.Name}\t{r.Status}\t{r.Publisher}\t{r.ImplementDate}\t{r.ReplacementRaw}\t{r.SourceType}");
                try
                {
                    Clipboard.SetText(sb.ToString());
                    MessageBox.Show($"已复制 {Results.Count} 条记录到剪贴板", "复制成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
            }
            else
            {
                MessageBox.Show("没有可复制的数据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnExportWord_Click(object sender, RoutedEventArgs e)
        {
            if (Results.Count == 0)
            {
                MessageBox.Show("没有数据可导出。请先查询。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Word 文档|*.docx",
                FileName = "规范查询结果",
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    // 转换为 CheckResult 列表
                    var list = new List<CheckResult>();
                    for (int i = 0; i < Results.Count; i++)
                    {
                        var r = Results[i];
                        list.Add(new CheckResult
                        {
                            No = i + 1,
                            Code = r.Code,
                            Name = r.Name,
                            Status = r.Status,
                            Replacement = r.ReplacementRaw,
                            Publisher = r.Publisher,
                            Source = r.SourceType,
                        });
                    }
                    ExportService.ExportWord(dlg.FileName, "规范查询结果", list);
                    MessageBox.Show($"已导出 {Results.Count} 条记录到:\n{dlg.FileName}", "导出成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
