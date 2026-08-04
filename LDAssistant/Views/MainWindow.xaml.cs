using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using LDAssistant.Models;
using LDAssistant.Services;

// 消除 WPF 和 WinForms 类型歧义
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Keyboard = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButton = System.Windows.Input.MouseButton;
using Application = System.Windows.Application;

namespace LDAssistant.Views
{
    public partial class MainWindow : Window
    {
        // ═════════════ 服务 ═════════════
        private readonly FilePreviewService _preview = new();
        private OcrService _ocr;
        private StandardChecker _checker;
        private readonly AiService _ai = new();

        // ═════════════ 数据 ═════════════
        public ObservableCollection<FileQueueItem> FileQueue { get; } = new();
        public ObservableCollection<CheckResult> CodeList { get; } = new();
        public ObservableCollection<CheckResult> CheckList { get; } = new();

        // ═════════════ 状态 ═════════════
        private FileQueueItem _currentFile;
        private int _currentPage;
        private double _zoom = 1.0;
        private int _rotation;
        private bool _isBatchRunning;

        public MainWindow()
        {
            InitializeComponent();

            // 绑定数据
            ThumbList.ItemsSource = FileQueue;
            CodeGrid.ItemsSource = CodeList;
            CheckGrid.ItemsSource = CheckList;

            // 初始化 OCR
            var (exe, dir) = OcrService.FindOcrPath();
            if (exe != null)
            {
                _ocr = new OcrService(exe, dir);
                StatusText.Text = "就绪 — OCR 已就绪";
            }
            else
            {
                StatusText.Text = "就绪 — OCR 未安装（需 PaddleOCR-json.exe）";
            }

            // 初始化标准数据库
            var dbPath = FindDatabasePath();
            if (dbPath != null)
            {
                try
                {
                    _checker = new StandardChecker(dbPath);
                    StatusText.Text += " | 数据库已加载";
                }
                catch (Exception ex)
                {
                    StatusText.Text += $" | 数据库加载失败: {ex.Message}";
                }
            }

            // 鼠标滚轮缩放
            PreviewCanvas.MouseWheel += OnMouseWheelZoom;
        }

        // ═════════════ 数据库路径 ═════════════
        private string FindDatabasePath()
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(appDir, "standards.db"),
                Path.Combine(Directory.GetCurrentDirectory(), "standards.db"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "standards.db"),
            };
            foreach (var p in candidates)
                if (File.Exists(p)) return Path.GetFullPath(p);
            return null;
        }

        // ═════════════ 打开文件 ═════════════
        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择文件",
                Multiselect = true,
                Filter = "所有支持的文件|*.pdf;*.docx;*.txt;*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif;*.webp;*.dxf;*.dwg|" +
                         "PDF|*.pdf|Word|*.docx|文本|*.txt|图片|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif;*.webp|CAD|*.dxf;*.dwg|所有文件|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (var path in dlg.FileNames)
                    AddFileToQueue(path);
                if (FileQueue.Count > 0)
                    SelectFile(FileQueue[FileQueue.Count - 1]);
            }
        }

        private void BtnFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择文件夹（递归扫描所有支持的文件）"
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var exts = new HashSet<string> { ".pdf", ".docx", ".txt", ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp", ".dxf" };
                string[] files;
                try { files = Directory.GetFiles(dlg.SelectedPath, "*.*", SearchOption.AllDirectories); }
                catch { return; }

                int added = 0;
                foreach (var f in files.OrderBy(x => x))
                {
                    if (exts.Contains(Path.GetExtension(f).ToLower()))
                    {
                        AddFileToQueue(f);
                        added++;
                    }
                }
                StatusText.Text = $"已扫描并添加 {added} 个文件";
                if (FileQueue.Count > 0)
                    SelectFile(FileQueue[0]);
            }
        }

        private void AddFileToQueue(string path)
        {
            // 避免重复
            if (FileQueue.Any(f => f.FilePath == path)) return;

            var item = new FileQueueItem
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                FileType = FilePreviewService.DetectFileType(path)
            };

            // 生成缩略图
            try
            {
                var ft = item.FileType;
                if (ft == "image" || ft == "pdf" || ft == "docx" || ft == "txt")
                {
                    var preview = new FilePreviewService();
                    preview.Open(path);
                    preview.CurrentPath = path;
                    var img = preview.RenderPage(0, 150);
                    if (img != null)
                        item.Thumbnail = img;
                    preview.Close();
                }
            }
            catch { }

            FileQueue.Add(item);
        }

        // ═════════════ 选择文件并显示 ═════════════
        private void SelectFile(FileQueueItem item)
        {
            if (item == null) return;

            // 取消旧文件选中
            foreach (var f in FileQueue) f.IsActive = false;
            item.IsActive = true;
            _currentFile = item;

            // 加载文件
            StatusText.Text = $"正在加载: {item.FileName}...";
            _preview.Open(item.FilePath);
            _preview.CurrentPath = item.FilePath;

            _currentPage = 0;
            _zoom = 1.0;
            _rotation = 0;

            DisplayCurrentPage();
        }

        private void DisplayCurrentPage()
        {
            if (_currentFile == null) return;

            try
            {
                var img = _preview.RenderPage(_currentPage, 1200);
                if (img != null)
                {
                    PreviewImage.Source = img;
                    ScaleTransform.ScaleX = _zoom;
                    ScaleTransform.ScaleY = _zoom;
                    RotateTransform.Angle = _rotation;

                    // 居中
                    Canvas.SetLeft(PreviewImage, 0);
                    Canvas.SetTop(PreviewImage, 0);

                    PageInfo.Text = _preview.TotalPages > 1
                        ? $"{_currentFile.FileName} — 第 {_currentPage + 1}/{_preview.TotalPages} 页"
                        : _currentFile.FileName;
                    StatusText.Text = $"已加载: {_currentFile.FileName}";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"显示失败: {ex.Message}";
            }
        }

        // ═════════════ 缩略图点击 ═════════════
        private void ThumbItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is FileQueueItem item)
                SelectFile(item);
        }

        // ═════════════ 翻页 ═════════════
        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_preview.TotalPages <= 1) return;
            if (_currentPage > 0) _currentPage--;
            else _currentPage = _preview.TotalPages - 1;
            DisplayCurrentPage();
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_preview.TotalPages <= 1) return;
            if (_currentPage < _preview.TotalPages - 1) _currentPage++;
            else _currentPage = 0;
            DisplayCurrentPage();
        }

        // ═════════════ 缩放/旋转 ═════════════
        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Min(_zoom * 1.25, 5.0);
            ScaleTransform.ScaleX = _zoom;
            ScaleTransform.ScaleY = _zoom;
        }

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Max(_zoom / 1.25, 0.2);
            ScaleTransform.ScaleX = _zoom;
            ScaleTransform.ScaleY = _zoom;
        }

        private void BtnRotate_Click(object sender, RoutedEventArgs e)
        {
            _rotation = (_rotation + 90) % 360;
            RotateTransform.Angle = _rotation;
        }

        private void OnMouseWheelZoom(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Delta > 0) BtnZoomIn_Click(null, null);
                else BtnZoomOut_Click(null, null);
            }
        }

        // ═════════════ OCR ═════════════
        private void BtnOcr_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFile == null) return;
            if (_ocr == null)
            {
                MessageBox.Show("OCR 引擎未安装。\n需要 PaddleOCR-json.exe，请安装 UmiOCR 或将 OCR 引擎放入程序目录/ocr/。",
                    "OCR 不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 先渲染当前页为临时图片，再 OCR
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dispatcher.Invoke(() => { StatusText.Text = "正在 OCR 识别..."; Progress.Value = 0; });

                string tempImg = null;
                try
                {
                    // 渲染当前页为图片
                    var img = _preview.RenderPage(_currentPage, 2000);
                    if (img == null)
                    {
                        Dispatcher.Invoke(() => StatusText.Text = "渲染页面失败");
                        return;
                    }

                    tempImg = Path.GetTempFileName() + ".png";
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                    using var fs = File.OpenWrite(tempImg);
                    encoder.Save(fs);

                    Dispatcher.Invoke(() => Progress.Value = 50);

                    var result = _ocr.Recognize(tempImg);

                    Dispatcher.Invoke(() =>
                    {
                        Progress.Value = 100;
                        if (result.Success)
                        {
                            OcrTextBox.Text = result.FullText;
                            OcrInfo.Text = $"识别到 {result.Items.Count} 行文字";
                            StatusText.Text = $"OCR 完成 — {result.Items.Count} 行";
                        }
                        else
                        {
                            StatusText.Text = result.FullText;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => StatusText.Text = $"OCR 错误: {ex.Message}");
                }
                finally
                {
                    if (tempImg != null && File.Exists(tempImg))
                        try { File.Delete(tempImg); } catch { }
                }
            });
        }

        // ═════════════ 规范检查 ═════════════
        private void BtnCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_checker == null)
            {
                MessageBox.Show("标准数据库未加载。请确保 standards.db 在程序目录中。",
                    "数据库不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var text = OcrTextBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("请先进行 OCR 识别或粘贴文本，再执行规范检查。",
                    "无文本", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 提取编号
            var codes = CodeExtractor.Extract(text);
            CodeList.Clear();
            CheckList.Clear();
            foreach (var c in codes)
                CodeList.Add(c);

            if (codes.Count == 0)
            {
                StatusText.Text = "未识别到规范编号";
                return;
            }

            StatusText.Text = $"识别到 {codes.Count} 个编号，正在检查...";
            Progress.Value = 0;
            var codesArray = codes.ToList();
            var total = codesArray.Count;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < total; i++)
                {
                    var c = codesArray[i];
                    var result = _checker.CheckCode(c.Code, c.Name);

                    Dispatcher.Invoke(() =>
                    {
                        CheckList.Add(result);
                        Progress.Value = (i + 1.0) / total * 100;
                        StatusText.Text = $"检查中: {i + 1}/{total}";
                    });
                }

                Dispatcher.Invoke(() =>
                {
                    var valid = CheckList.Count(r => r.Status == "现行");
                    var obsolete = CheckList.Count(r => r.Status == "作废" || r.Status == "废止");
                    var replaced = CheckList.Count(r => r.Status == "被代替" || r.Status == "被替代");
                    var notFound = CheckList.Count(r => r.Status == "未找到");
                    StatusText.Text = $"检查完成 — 现行:{valid} 作废:{obsolete} 替代:{replaced} 未找到:{notFound}";
                });
            });
        }

        // ═════════════ 批量处理 ═════════════
        private void BtnBatch_Click(object sender, RoutedEventArgs e)
        {
            if (FileQueue.Count == 0) return;
            if (_ocr == null && _checker == null)
            {
                MessageBox.Show("OCR 或数据库未就绪。", "不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_isBatchRunning)
            {
                _isBatchRunning = false;
                StatusText.Text = "批量处理已中止";
                return;
            }

            _isBatchRunning = true;
            var allFiles = FileQueue.ToList();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var allCodes = new List<CheckResult>();
                for (int i = 0; i < allFiles.Count; i++)
                {
                    if (!_isBatchRunning) break;
                    var file = allFiles[i];

                    Dispatcher.Invoke(() => StatusText.Text = $"批量处理: {i + 1}/{allFiles.Count} — {file.FileName}");

                    try
                    {
                        // 加载文件
                        _preview.Open(file.FilePath);
                        _preview.CurrentPath = file.FilePath;

                        // 渲染每页并 OCR
                        var text = "";
                        for (int pg = 0; pg < _preview.TotalPages; pg++)
                        {
                            if (!_isBatchRunning) break;

                            var img = _preview.RenderPage(pg, 2000);
                            if (img == null) continue;

                            var tempImg = Path.GetTempFileName() + ".png";
                            try
                            {
                                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                                using var fs = File.OpenWrite(tempImg);
                                encoder.Save(fs);

                                if (_ocr != null)
                                {
                                    var result = _ocr.Recognize(tempImg);
                                    if (result.Success)
                                        text += result.FullText + "\n";
                                }
                            }
                            finally { try { File.Delete(tempImg); } catch { } }

                            Dispatcher.Invoke(() => Progress.Value = (i + (double)pg / _preview.TotalPages) / allFiles.Count * 100);
                        }

                        // 提取编号
                        var codes = CodeExtractor.Extract(text);
                        foreach (var c in codes)
                        {
                            c.Source = file.FileName;
                            allCodes.Add(c);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"批量处理 {file.FileName} 失败: {ex.Message}");
                    }
                }

                // 去重
                var seen = new HashSet<string>();
                var unique = new List<CheckResult>();
                foreach (var c in allCodes)
                {
                    if (seen.Add(c.Code))
                        unique.Add(c);
                }

                Dispatcher.Invoke(() =>
                {
                    CodeList.Clear();
                    CheckList.Clear();
                    foreach (var c in unique)
                        CodeList.Add(c);

                    // 检查
                    if (_checker != null)
                    {
                        foreach (var c in unique)
                        {
                            var r = _checker.CheckCode(c.Code, c.Name);
                            CheckList.Add(r);
                        }

                        var valid = CheckList.Count(r => r.Status == "现行");
                        var obsolete = CheckList.Count(r => r.Status == "作废" || r.Status == "废止");
                        var replaced = CheckList.Count(r => r.Status == "被代替" || r.Status == "被替代");
                        StatusText.Text = $"批量完成 — 共 {unique.Count} 个编号 | 现行:{valid} 作废:{obsolete} 替代:{replaced}";
                    }
                    else
                    {
                        StatusText.Text = $"批量完成 — 共 {unique.Count} 个编号（数据库未加载，未检查）";
                    }

                    Progress.Value = 100;
                    _isBatchRunning = false;
                });
            });
        }

        // ═════════════ 导出 ═════════════
        private void BtnExportWord_Click(object sender, RoutedEventArgs e)
        {
            if (CheckList.Count == 0)
            {
                MessageBox.Show("没有检查结果可导出。请先执行规范检查。",
                    "无数据", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog { Filter = "Word 文档|*.docx", FileName = "规范检查报告" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    ExportService.ExportWord(dlg.FileName, _currentFile?.FileName ?? "批量文件", CheckList.ToList());
                    StatusText.Text = $"已导出: {Path.GetFileName(dlg.FileName)}";
                    MessageBox.Show($"已导出到:\n{dlg.FileName}", "导出成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败:\n{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnExportExcel_Click(object sender, RoutedEventArgs e)
        {
            if (CheckList.Count == 0)
            {
                MessageBox.Show("没有检查结果可导出。请先执行规范检查。",
                    "无数据", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog { Filter = "Excel 表格|*.xlsx", FileName = "规范检查报告" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    ExportService.ExportExcel(dlg.FileName, _currentFile?.FileName ?? "批量文件", CheckList.ToList());
                    StatusText.Text = $"已导出: {Path.GetFileName(dlg.FileName)}";
                    MessageBox.Show($"已导出到:\n{dlg.FileName}", "导出成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败:\n{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ═════════════ AI 对话窗口 ═════════════
        private AiChatWindow _aiWindow;

        private void BtnAi_Click(object sender, RoutedEventArgs e)
        {
            if (_aiWindow == null || !_aiWindow.IsVisible)
            {
                _aiWindow = new AiChatWindow(_ai);
                _aiWindow.Show();
            }
            else
            {
                _aiWindow.Activate();
            }

            // 把 OCR 结果发给 AI
            if (!string.IsNullOrEmpty(OcrTextBox.Text))
            {
                var codes = CodeList.Select(c => $"{c.Code} {c.Name}").ToList();
                if (codes.Count > 0)
                {
                    var context = $"已识别到以下规范编号:\n{string.Join("\n", codes)}\n\nOCR 文本:\n{OcrTextBox.Text}";
                    _aiWindow.SetContext(context);
                }
            }
        }

        // ═════════════ 窗口关闭 ═════════════
        protected override void OnClosed(EventArgs e)
        {
            _preview?.Close();
            _checker?.Dispose();
            base.OnClosed(e);
        }
    }
}
