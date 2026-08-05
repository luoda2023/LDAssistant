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
 public ObservableCollection<FileBatchItem> FileListItems { get; } = new();
 
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
 FileList.ItemsSource = FileListItems;

 // 初始化 OCR（内置 ONNX 模型，无需外部 exe）
 _ocr = OcrService.Create();
 if (_ocr != null)
 StatusText.Text = "就绪 — OCR 已就绪";
 else
 StatusText.Text = "就绪 — OCR 未安装";

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
 _aiWindow.Closed += (s, e) => _aiWindow = null;
 _aiWindow.Show();
 }
 _aiWindow.Activate();
 }
 
 /// <summary>推送消息到AI窗口（带异常保护）</summary>
 private void PushToAi(string title, string content)
 {
 try
 {
 ShowAiWindow();
 var msg = $"## {title}\n\n{content}";
 _aiWindow.AddMessage("系统", msg);
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"PushToAi失败: {ex.Message}");
 StatusText.Text = $"AI窗口推送失败: {ex.Message}";
 }
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
 FileListItems.Clear();
 foreach (var path in dlg.FileNames)
 {
 var item = new FileBatchItem
 {
 FilePath = path,
 FileName = Path.GetFileName(path),
 FileType = FilePreviewService.DetectFileType(path)
 };
 _batchFiles.Add(item);
 FileListItems.Add(item);
 }
 
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
                var exts = new HashSet<string> { ".pdf", ".docx", ".txt", ".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".webp", ".dxf", ".dwg" };
                string[] files;
                try { files = Directory.GetFiles(dlg.SelectedPath, "*.*", SearchOption.AllDirectories); }
                catch { return; }

 _batchFiles.Clear();
 FileListItems.Clear();
 int added = 0;
 foreach (var f in files.OrderBy(x => x))
 {
 if (exts.Contains(Path.GetExtension(f).ToLower()))
 {
 var item = new FileBatchItem
 {
 FilePath = f,
 FileName = Path.GetFileName(f),
 FileType = FilePreviewService.DetectFileType(f)
 };
 _batchFiles.Add(item);
 FileListItems.Add(item);
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

 // 更新文件列表高亮
 foreach (var f in FileListItems)
 f.IsActive = (f.FilePath == path);
 FileListTitle.Text = FileListItems.Count > 1 ? $"文件列表 ({FileListItems.Count})" : "文件列表";

 _currentPage = 0;
            _zoom = 1.0;
            _rotation = 0;

 // 释放旧缩略图资源
 foreach (var thumb in PageThumbs)
 {
 if (thumb.Thumbnail is System.Windows.Media.Imaging.BitmapSource bmp)
 bmp = null; // Freeze 过的位图无法 Dispose，但可以解除引用
 }
 PageThumbs.Clear();
            int pages = _preview.TotalPages;
            ThumbTitle.Text = pages > 1 ? $"页面缩略图 ({pages} 页)" : "页面缩略图";

 ThreadPool.QueueUserWorkItem(_ =>
{
for (int i = 0; i < pages; i++)
{
// 检查是否已切换到其他文件（取消旧渲染）
if (_currentFilePath != path) return;

try
{
// 72 DPI 缩略图（内存极小）
var img = _preview.RenderPage(i, 0, 72);

if (img != null)
{
img.Freeze();

// 标签：CAD 文件用空间名称，其他用页码
string label;
if (_preview.FileType == "cad" && _preview.PageNames.Count > i)
 label = _preview.PageNames[i];
else if (pages > 1)
 label = $"第 {i + 1} 页";
else
 label = Path.GetFileName(path);

var item = new PageThumbItem
{
PageIndex = i,
Label = label,
Thumbnail = img,
};
// 逐页添加，避免一次性大量UI更新
Dispatcher.Invoke(() =>
{
if (_currentFilePath == path)
PageThumbs.Add(item);
});
}
}
catch (Exception ex)
{
System.Diagnostics.Debug.WriteLine($"缩略图渲染失败 page {i}: {ex.Message}");
}
}

Dispatcher.Invoke(() =>
{
if (_currentFilePath != path) return;
if (PageThumbs.Count > 0)
{
PageThumbs[0].IsActive = true;
DisplayCurrentPage();
}
else
{
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
 // 150 DPI 渲染（减少内存占用，用户可缩放查看细节）
 var img = _preview.RenderPage(_currentPage, 0, 150);
 if (img != null)
 {
                    PreviewImage.Source = img;
                    ScaleTransform.ScaleX = _zoom;
                    ScaleTransform.ScaleY = _zoom;
                    RotateTransform.Angle = _rotation;

                    Canvas.SetLeft(PreviewImage, 0);
                    Canvas.SetTop(PreviewImage, 0);

 int pages = _preview.TotalPages;
 // CAD 文件显示空间名称
 if (_preview.FileType == "cad" && _preview.PageNames.Count > _currentPage)
 PageInfo.Text = $"{Path.GetFileName(_currentFilePath)} — [{_preview.PageNames[_currentPage]}]";
 else
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

 // ═════════════ 文件列表点击 ═════════════
 private void FileItem_Click(object sender, MouseButtonEventArgs e)
 {
 if (sender is FrameworkElement fe && fe.DataContext is FileBatchItem item)
 {
 LoadFile(item.FilePath);
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

 // ═══════════════ 鼠标拖拽 + 区域选择 ═══════════════
 private bool _isDragging = false;
 private bool _isMiddleDragging = false;
 private bool _isSelecting = false;
 private bool _isAreaOcrMode = false;
 private Point _dragStartScreenPos;   // 相对窗口的坐标（不随滚动变化）
 private double _dragStartHOffset;
 private double _dragStartVOffset;
 private Point _selectStartScreenPos; // 选框起点（相对窗口）

 /// <summary>进入区域OCR模式</summary>
 private void BtnOcrArea_Click(object sender, RoutedEventArgs e)
 {
 if (_currentFilePath == null) return;
 if (_ocr == null)
 {
 MessageBox.Show("OCR 引擎未安装。", "OCR 不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
 return;
 }
 _isAreaOcrMode = !_isAreaOcrMode;
 if (_isAreaOcrMode)
 {
 ModeHintText.Text = "🔲 区域OCR模式：在预览区拖拽选择矩形区域";
 ModeHint.Visibility = Visibility.Visible;
 PreviewGrid.Cursor = Cursors.Cross;
 }
 else
 {
 ModeHint.Visibility = Visibility.Collapsed;
 PreviewGrid.Cursor = null;
 SelectionRect.Visibility = Visibility.Collapsed;
 }
 }

 private void PreviewArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
 {
 if (_currentFilePath == null) return;

 if (_isAreaOcrMode)
 {
 // 区域选择模式 — 用相对窗口坐标
 _isSelecting = true;
 _selectStartScreenPos = e.GetPosition(this);
 SelectionRect.Visibility = Visibility.Visible;
 var gridPos = e.GetPosition(PreviewGrid);
 Canvas.SetLeft(SelectionRect, gridPos.X);
 Canvas.SetTop(SelectionRect, gridPos.Y);
 SelectionRect.Width = 0;
 SelectionRect.Height = 0;
 PreviewGrid.CaptureMouse();
 e.Handled = true;
 }
 else
 {
 // 拖拽模式 — 用相对窗口坐标（不受ScrollViewer滚动影响）
 _isDragging = true;
 _dragStartScreenPos = e.GetPosition(this);
 _dragStartHOffset = PreviewScroll.HorizontalOffset;
 _dragStartVOffset = PreviewScroll.VerticalOffset;
 PreviewGrid.CaptureMouse();
 e.Handled = true;
 }
 }

 private void PreviewArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
 {
 if (_isSelecting)
 {
 _isSelecting = false;
 PreviewGrid.ReleaseMouseCapture();

 var rect = GetSelectionRectangle();
 if (rect.Width > 10 && rect.Height > 10)
 DoAreaOcr(rect);

 SelectionRect.Visibility = Visibility.Collapsed;
 _isAreaOcrMode = false;
 ModeHint.Visibility = Visibility.Collapsed;
 PreviewGrid.Cursor = null;
 }
 else if (_isDragging)
 {
 _isDragging = false;
 PreviewGrid.ReleaseMouseCapture();
 }
 }

 private void PreviewArea_MouseMove(object sender, MouseEventArgs e)
 {
 if (_isSelecting)
 {
 // 选框 — 用相对窗口坐标计算偏移，再转到PreviewGrid坐标
 var screenPos = e.GetPosition(this);
 var gridPos = e.GetPosition(PreviewGrid);
 var startGrid = new Point(
 _selectStartScreenPos.X - (screenPos.X - gridPos.X),
 _selectStartScreenPos.Y - (screenPos.Y - gridPos.Y));
 var x = Math.Min(startGrid.X, gridPos.X);
 var y = Math.Min(startGrid.Y, gridPos.Y);
 var w = Math.Abs(gridPos.X - startGrid.X);
 var h = Math.Abs(gridPos.Y - startGrid.Y);
 Canvas.SetLeft(SelectionRect, x);
 Canvas.SetTop(SelectionRect, y);
 SelectionRect.Width = w;
 SelectionRect.Height = h;
 }
 else if (_isDragging || _isMiddleDragging)
 {
 // 拖拽 — 用相对窗口坐标（不随ScrollViewer滚动变化，避免飘移）
 var screenPos = e.GetPosition(this);
 double dx = screenPos.X - _dragStartScreenPos.X;
 double dy = screenPos.Y - _dragStartScreenPos.Y;
 PreviewScroll.ScrollToHorizontalOffset(_dragStartHOffset - dx);
 PreviewScroll.ScrollToVerticalOffset(_dragStartVOffset - dy);
 }
 }

 // ═══════════════ 中键拖拽 ═══════════════
 private void PreviewArea_MouseDown(object sender, MouseButtonEventArgs e)
 {
 if (_currentFilePath == null) return;
 if (e.ChangedButton == MouseButton.Middle)
 {
 _isMiddleDragging = true;
 _dragStartScreenPos = e.GetPosition(this);
 _dragStartHOffset = PreviewScroll.HorizontalOffset;
 _dragStartVOffset = PreviewScroll.VerticalOffset;
 PreviewGrid.CaptureMouse();
 e.Handled = true;
 }
 }

 private void PreviewArea_MouseUp(object sender, MouseButtonEventArgs e)
 {
 if (_isMiddleDragging && e.ChangedButton == MouseButton.Middle)
 {
 _isMiddleDragging = false;
 PreviewGrid.ReleaseMouseCapture();
 }
 }

 /// <summary>滚轮缩放（直接缩放，无需Ctrl）</summary>
 private void PreviewArea_MouseWheel(object sender, MouseWheelEventArgs e)
 {
 if (e.Delta > 0) BtnZoomIn_Click(null, null);
 else BtnZoomOut_Click(null, null);
 e.Handled = true;
 }

 /// <summary>获取选框在 PreviewGrid 坐标系中的矩形</summary>
 private Rect GetSelectionRectangle()
 {
 double x = Canvas.GetLeft(SelectionRect);
 double y = Canvas.GetTop(SelectionRect);
 return new Rect(x, y, SelectionRect.Width, SelectionRect.Height);
 }

 /// <summary>区域OCR：将选框映射到原图坐标，裁剪后OCR</summary>
 private void DoAreaOcr(Rect screenRect)
 {
 ThreadPool.QueueUserWorkItem(_ =>
 {
 string tempImg = null;
 try
 {
 Dispatcher.Invoke(() => { StatusText.Text = "正在区域OCR识别..."; Progress.Value = 0; });

 // 渲染高清原图（200 DPI，平衡清晰度和内存）
 var fullImg = _preview.RenderPage(_currentPage, 0, 200);
 if (fullImg == null)
 {
 Dispatcher.Invoke(() => StatusText.Text = "渲染页面失败");
 return;
 }

 // 将屏幕坐标映射到原图坐标
 // 需要考虑缩放因子和滚动偏移
 double scale = _zoom * (200.0 / 96.0); // 200DPI渲染，96DPI是WPF默认
 var imgRect = new Rect(
 screenRect.X / scale + PreviewScroll.HorizontalOffset / scale,
 screenRect.Y / scale + PreviewScroll.VerticalOffset / scale,
 screenRect.Width / scale,
 screenRect.Height / scale
 );

 // 裁剪
 var bmp = System.Windows.Media.Imaging.BitmapFrame.Create(fullImg);
 int cropX = Math.Max(0, (int)imgRect.X);
 int cropY = Math.Max(0, (int)imgRect.Y);
 int cropW = Math.Min((int)imgRect.Width, bmp.PixelWidth - cropX);
 int cropH = Math.Min((int)imgRect.Height, bmp.PixelHeight - cropY);

 if (cropW <= 0 || cropH <= 0)
 {
 Dispatcher.Invoke(() => StatusText.Text = "选区无效");
 return;
 }

 var cropped = new System.Windows.Int32Rect(cropX, cropY, cropW, cropH);
 var croppedBitmap = new System.Windows.Media.Imaging.CroppedBitmap(fullImg, cropped);

 tempImg = Path.GetTempFileName() + ".png";
 var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
 encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(croppedBitmap));
 using var fs = File.OpenWrite(tempImg);
 encoder.Save(fs);

 Dispatcher.Invoke(() => Progress.Value = 50);

 var result = _ocr.Recognize(tempImg);

 Dispatcher.Invoke(() =>
 {
 Progress.Value = 100;
 if (result.Success)
 {
 _lastOcrText = TextNormalizer.Normalize(result.FullText);
 StatusText.Text = $"区域OCR完成 — {result.Items.Count} 行";
 var preview = _lastOcrText.Length > 2000
 ? _lastOcrText.Substring(0, 2000) + "\n\n... (文本已截断)"
 : _lastOcrText;
 PushToAi("区域OCR识别结果", $"识别到 **{result.Items.Count}** 行文字：\n\n```\n{preview}\n```");
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
 Dispatcher.Invoke(() => StatusText.Text = $"区域OCR错误: {ex.Message}");
 }
 finally
 {
 if (tempImg != null && File.Exists(tempImg))
 try { File.Delete(tempImg); } catch { }
 }
 });
 }

 // ═════════════ 缩放/旋转 ═════════════
 private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
 {
 _zoom = Math.Min(_zoom * 1.25, 10.0);
 ScaleTransform.ScaleX = _zoom;
 ScaleTransform.ScaleY = _zoom;
 }
 
 private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
 {
 _zoom = Math.Max(_zoom / 1.25, 0.1);
 ScaleTransform.ScaleX = _zoom;
 ScaleTransform.ScaleY = _zoom;
 }
 
 private void BtnRotate_Click(object sender, RoutedEventArgs e)
 {
 _rotation = (_rotation + 90) % 360;
 RotateTransform.Angle = _rotation;
 }

        // ═════════════ OCR — 结果推送到 AI 窗口 ═════════════
        private void BtnOcr_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFilePath == null) return;
            if (_ocr == null)
            {
 MessageBox.Show("OCR 引擎未安装。\n请确保 models/v5/ 目录存在且包含模型文件。",
 "OCR 不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dispatcher.Invoke(() => { StatusText.Text = "正在 OCR 识别..."; Progress.Value = 0; });

                string tempImg = null;
                try
                {
 var img = _preview.RenderPage(_currentPage, 0, 200);
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
_lastOcrText = TextNormalizer.Normalize(result.FullText);
StatusText.Text = $"OCR 完成 — {result.Items.Count} 行";

// 推送到 AI 窗口
var preview = _lastOcrText.Length > 2000
? _lastOcrText.Substring(0, 2000) + "\n\n... (文本已截断，完整文本已保存)"
: _lastOcrText;
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

 var img = _preview.RenderPage(pg, 0, 200);
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
text += TextNormalizer.Normalize(result.FullText) + "\n";
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
