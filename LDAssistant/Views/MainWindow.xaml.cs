using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using LDAssistant.Models;
using LDAssistant.Services;

namespace LDAssistant.Views
{
    public partial class MainWindow : Window
    {
        // ═════════════ 服务 ═════════════
        private FilePreviewService _preview = new();
        private OcrService _ocr;
        private StandardChecker _checker;
        private readonly AiService _ai = new();

        // ═════════════ 数据 ═════════════
        public ObservableCollection<PageThumbItem> PageThumbs { get; } = new();

        // 内部数据（不绑定到 UI，推送到 AI 窗口）
        private List<CheckResult> _lastCodes = new();
        private List<CheckResult> _lastResults = new();
        private string _lastOcrText = "";
        private List<FileBatchItem> _batchFiles = new();

        // ═════════════ 状态 ═════════════
        private string _currentFilePath;
        private int _currentPage;
        private double _zoom = 1.0;
        private int _rotation;
        private bool _isBatchRunning;

        public MainWindow()
        {
            InitializeComponent();

            ThumbList.ItemsSource = PageThumbs;

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

            PreviewCanvas.MouseWheel += OnMouseWheelZoom;
        }

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

        // ═════════════ AI 窗口 ═════════════
        private AiChatWindow _aiWindow;

        private void ShowAiWindow()
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
        }

        /// <summary>推送系统消息到 AI 窗口</summary>
        private void PushToAi(string title, string content)
        {
            ShowAiWindow();
            var msg = $"## {title}\n\n{content}";
            _aiWindow.AddMessage("系统", msg);
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
                _batchFiles.Clear();
                foreach (var path in dlg.FileNames)
                    _batchFiles.Add(new FileBatchItem
                    {
                        FilePath = path,
                        FileName = Path.GetFileName(path),
                        FileType = FilePreviewService.DetectFileType(path)
                    });

                if (_batchFiles.Count > 0)
                    LoadFile(_batchFiles[0].FilePath);
            }
        }

        private void BtnFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FolderPicker
            {
                Description = "选择文件夹（递归扫描所有支持的文件）"
            };
            if (dlg.ShowDialog())
            {
                var exts = new HashSet<string> { ".pdf", ".docx", ".txt", ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp", ".dxf" };
                string[] files;
                try { files = Directory.GetFiles(dlg.SelectedPath, "*.*", SearchOption.AllDirectories); }
                catch { return; }

                _batchFiles.Clear();
                int added = 0;
                foreach (var f in files.OrderBy(x => x))
                {
                    if (exts.Contains(Path.GetExtension(f).ToLower()))
                    {
                        _batchFiles.Add(new FileBatchItem
                        {
                            FilePath = f,
                            FileName = Path.GetFileName(f),
                            FileType = FilePreviewService.DetectFileType(f)
                        });
                        added++;
                    }
                }
                StatusText.Text = $"已扫描并添加 {added} 个文件";
                if (_batchFiles.Count > 0)
                    LoadFile(_batchFiles[0].FilePath);
            }
        }

        // ═════════════ 加载文件 → 生成页面缩略图 ═════════════
        private void LoadFile(string path)
        {
            _currentFilePath = path;
            _preview?.Close();
            _preview = new FilePreviewService();
            _preview.Open(path);
            _preview.CurrentPath = path;

            _currentPage = 0;
            _zoom = 1.0;
            _rotation = 0;

            // 生成页面缩略图
            PageThumbs.Clear();
            int pages = _preview.TotalPages;
            ThumbTitle.Text = pages > 1 ? $"页面缩略图 ({pages} 页)" : "页面缩略图";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < pages; i++)
                {
                    try
                    {
                        var thumb = new FilePreviewService();
                        thumb.Open(path);
                        var img = thumb.RenderPage(i, 150);
                        thumb.Close();

                        if (img != null)
                        {
                            img.Freeze();
                            var item = new PageThumbItem
                            {
                                PageIndex = i,
                                Label = pages > 1 ? $"第 {i + 1} 页" : Path.GetFileName(path),
                                Thumbnail = img,
                            };
                            Dispatcher.Invoke(() => PageThumbs.Add(item));
                        }
                    }
                    catch { }
                }

                Dispatcher.Invoke(() =>
                {
                    if (PageThumbs.Count > 0)
                    {
                        PageThumbs[0].IsActive = true;
                        DisplayCurrentPage();
                    }
                });
            });
        }

        // ═════════════ 显示当前页 ═════════════
        private void DisplayCurrentPage()
        {
            if (_currentFilePath == null) return;

            try
            {
                var img = _preview.RenderPage(_currentPage, 1200);
                if (img != null)
                {
                    PreviewImage.Source = img;
                    ScaleTransform.ScaleX = _zoom;
                    ScaleTransform.ScaleY = _zoom;
                    RotateTransform.Angle = _rotation;

                    Canvas.SetLeft(PreviewImage, 0);
                    Canvas.SetTop(PreviewImage, 0);

                    int pages = _preview.TotalPages;
                    PageInfo.Text = pages > 1
                        ? $"{Path.GetFileName(_currentFilePath)} — 第 {_currentPage + 1}/{pages} 页"
                        : Path.GetFileName(_currentFilePath);
                    StatusText.Text = $"已加载: {Path.GetFileName(_currentFilePath)}";
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
            if (sender is FrameworkElement fe && fe.DataContext is PageThumbItem item)
            {
                foreach (var p in PageThumbs) p.IsActive = false;
                item.IsActive = true;
                _currentPage = item.PageIndex;
                DisplayCurrentPage();
            }
        }

        // ═════════════ 翻页 ═════════════
        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_preview?.TotalPages <= 1) return;
            if (_currentPage > 0) _currentPage--;
            else _currentPage = _preview.TotalPages - 1;
            UpdateActiveThumb();
            DisplayCurrentPage();
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_preview?.TotalPages <= 1) return;
            if (_currentPage < _preview.TotalPages - 1) _currentPage++;
            else _currentPage = 0;
            UpdateActiveThumb();
            DisplayCurrentPage();
        }

        private void UpdateActiveThumb()
        {
            foreach (var p in PageThumbs) p.IsActive = false;
            if (_currentPage < PageThumbs.Count)
                PageThumbs[_currentPage].IsActive = true;
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

        // ═════════════ OCR — 结果推送到 AI 窗口 ═════════════
        private void BtnOcr_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFilePath == null) return;
            if (_ocr == null)
            {
                MessageBox.Show("OCR 引擎未安装。\n需要 PaddleOCR-json.exe，请安装 UmiOCR 或将 OCR 引擎放入程序目录/ocr/。",
                    "OCR 不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dispatcher.Invoke(() => { StatusText.Text = "正在 OCR 识别..."; Progress.Value = 0; });

                string tempImg = null;
                try
                {
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
                            _lastOcrText = result.FullText;
                            StatusText.Text = $"OCR 完成 — {result.Items.Count} 行";

                            // 推送到 AI 窗口
                            var preview = result.FullText.Length > 2000
                                ? result.FullText.Substring(0, 2000) + "\n\n... (文本已截断，完整文本已保存)"
                                : result.FullText;
                            PushToAi("OCR 识别结果", $"识别到 **{result.Items.Count}** 行文字：\n\n```\n{preview}\n```");
                        }
                        else
                        {
                            StatusText.Text = result.FullText;
                            PushToAi("OCR 失败", result.FullText);
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

        // ═════════════ 规范检查 — 结果推送到 AI 窗口 ═════════════
        private void BtnCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_checker == null)
            {
                MessageBox.Show("标准数据库未加载。请确保 standards.db 在程序目录中。",
                    "数据库不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var text = _lastOcrText;
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("请先进行 OCR 识别，再执行规范检查。",
                    "无文本", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var codes = CodeExtractor.Extract(text);
            _lastCodes = codes.ToList();
            _lastResults.Clear();

            if (codes.Count == 0)
            {
                StatusText.Text = "未识别到规范编号";
                PushToAi("规范检查", "未识别到任何规范编号。");
                return;
            }

            StatusText.Text = $"识别到 {codes.Count} 个编号，正在检查...";
            Progress.Value = 0;
            var codesArray = codes.ToList();
            var total = codesArray.Count;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var sb = new StringBuilder();
                sb.Append($"识别到 **{total}** 个规范编号，检查结果如下：\n\n");
                sb.Append("| 序号 | 编号 | 名称 | 状态 | 替代信息 |\n");
                sb.Append("|------|------|------|------|----------|\n");

                for (int i = 0; i < total; i++)
                {
                    var c = codesArray[i];
                    var result = _checker.CheckCode(c.Code, c.Name);
                    _lastResults.Add(result);

                    var name = string.IsNullOrEmpty(result.Name) ? c.Name ?? "" : result.Name;
                    sb.Append($"| {i + 1} | `{result.Code}` | {name} | {result.Status} | {result.Replacement} |\n");

                    Dispatcher.Invoke(() =>
                    {
                        Progress.Value = (i + 1.0) / total * 100;
                        StatusText.Text = $"检查中: {i + 1}/{total}";
                    });
                }

                // 汇总统计
                var valid = _lastResults.Count(r => r.Status == "现行");
                var obsolete = _lastResults.Count(r => r.Status == "作废" || r.Status == "废止");
                var replaced = _lastResults.Count(r => r.Status == "被代替" || r.Status == "被替代");
                var notFound = _lastResults.Count(r => r.Status == "未找到");

                sb.Append($"\n### 汇总\n");
                sb.Append($"- ✅ 现行: **{valid}**\n");
                sb.Append($"- ❌ 作废: **{obsolete}**\n");
                sb.Append($"- 🔄 被替代: **{replaced}**\n");
                sb.Append($"- ❓ 未找到: **{notFound}**\n");

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"检查完成 — 现行:{valid} 作废:{obsolete} 替代:{replaced} 未找到:{notFound}";
                    PushToAi("规范检查结果", sb.ToString());
                });
            });
        }

        // ═════════════ 批量处理 ═════════════
        private void BtnBatch_Click(object sender, RoutedEventArgs e)
        {
            if (_batchFiles.Count == 0) return;
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
            var allFiles = _batchFiles.ToList();

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
                        _preview?.Close();
                        _preview = new FilePreviewService();
                        _preview.Open(file.FilePath);
                        _preview.CurrentPath = file.FilePath;

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

                var seen = new HashSet<string>();
                var unique = new List<CheckResult>();
                foreach (var c in allCodes)
                {
                    if (seen.Add(c.Code))
                        unique.Add(c);
                }

                Dispatcher.Invoke(() =>
                {
                    _lastCodes = unique;
                    _lastResults.Clear();

                    var sb = new StringBuilder();
                    sb.Append($"批量处理完成，共扫描 **{allFiles.Count}** 个文件，识别到 **{unique.Count}** 个唯一规范编号。\n\n");

                    if (_checker != null)
                    {
                        sb.Append("| 序号 | 编号 | 名称 | 状态 | 替代信息 |\n");
                        sb.Append("|------|------|------|------|----------|\n");

                        for (int i = 0; i < unique.Count; i++)
                        {
                            var c = unique[i];
                            var r = _checker.CheckCode(c.Code, c.Name);
                            _lastResults.Add(r);

                            var name = string.IsNullOrEmpty(r.Name) ? c.Name ?? "" : r.Name;
                            sb.Append($"| {i + 1} | `{r.Code}` | {name} | {r.Status} | {r.Replacement} |\n");
                        }

                        var valid = _lastResults.Count(r => r.Status == "现行");
                        var obsolete = _lastResults.Count(r => r.Status == "作废" || r.Status == "废止");
                        var replaced = _lastResults.Count(r => r.Status == "被代替" || r.Status == "被替代");
                        var notFound = _lastResults.Count(r => r.Status == "未找到");

                        sb.Append($"\n### 汇总\n");
                        sb.Append($"- ✅ 现行: **{valid}**\n");
                        sb.Append($"- ❌ 作废: **{obsolete}**\n");
                        sb.Append($"- 🔄 被替代: **{replaced}**\n");
                        sb.Append($"- ❓ 未找到: **{notFound}**\n");

                        StatusText.Text = $"批量完成 — {unique.Count} 个编号 | 现行:{valid} 作废:{obsolete} 替代:{replaced}";
                    }
                    else
                    {
                        sb.Append("（数据库未加载，未检查状态）\n\n");
                        for (int i = 0; i < unique.Count; i++)
                            sb.Append($"{i + 1}. `{unique[i].Code}` — {unique[i].Name}\n");
                        StatusText.Text = $"批量完成 — {unique.Count} 个编号（未检查）";
                    }

                    Progress.Value = 100;
                    _isBatchRunning = false;
                    PushToAi("批量检查结果", sb.ToString());
                });
            });
        }

        // ═════════════ 导出 ═════════════
        private void BtnExportWord_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResults.Count == 0)
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
                    var fileName = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "批量文件";
                    ExportService.ExportWord(dlg.FileName, fileName, _lastResults);
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
            if (_lastResults.Count == 0)
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
                    var fileName = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "批量文件";
                    ExportService.ExportExcel(dlg.FileName, fileName, _lastResults);
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

        // ═════════════ AI 按钮 ═════════════
        private void BtnAi_Click(object sender, RoutedEventArgs e)
        {
            ShowAiWindow();

            // 设置上下文
            if (!string.IsNullOrEmpty(_lastOcrText) || _lastResults.Count > 0)
            {
                var context = "";
                if (_lastResults.Count > 0)
                {
                    var codes = _lastResults.Select(r => $"{r.Code} {r.Name} [{r.Status}]").ToList();
                    context += $"已识别到以下规范编号及检查结果:\n{string.Join("\n", codes)}\n\n";
                }
                if (!string.IsNullOrEmpty(_lastOcrText))
                    context += $"OCR 文本:\n{_lastOcrText}";
                _aiWindow.SetContext(context);
            }
        }

        // ═════════════ 规范查询窗口 ═════════════
        private StandardQueryWindow _queryWindow;

        private void BtnQuery_Click(object sender, RoutedEventArgs e)
        {
            if (_checker == null)
            {
                MessageBox.Show("标准数据库未加载。请确保 standards.db 在程序目录中。",
                    "数据库不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_queryWindow == null || !_queryWindow.IsVisible)
            {
                _queryWindow = new StandardQueryWindow(_checker);
                _queryWindow.Show();
            }
            else
            {
                _queryWindow.Activate();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _preview?.Close();
            _checker?.Dispose();
            base.OnClosed(e);
        }
    }
}
