using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using LDAssistant.Models;
using LDAssistant.Services;

namespace LDAssistant.Views
{
 public partial class MainWindow : Window
 {
 // ═════════════ 服务 ═════════════
 private UmiOcrService _umiOcr = new();
 private StandardChecker _checker;
 private readonly AiService _ai = new();
 private readonly UpdateService _updater = new();
 private readonly FilePreviewService _previewSvc = new();

 // ═════════════ 数据 ═════════════
 public ObservableCollection<FileBatchItem> FileListItems { get; } = new();
 public ObservableCollection<PageThumbItem> PageThumbItems { get; } = new();
 
 // 内部数据（不绑定到 UI，推送到 AI 窗口）
 private List<CheckResult> _lastCodes = new();
 private List<CheckResult> _lastResults = new();
 private string _lastOcrText = "";
 private List<FileBatchItem> _batchFiles = new();
        private int _pdfSidebarNavSeq; // PDF 导航序号：只保留最后一次导航的侧栏点击

// ═════════════ 状态 ═════════════
private string _currentFilePath;
private int _currentPage;
private int _totalPages;
	 private double _zoom = 1.0;
	 private bool _isBatchRunning;
	 private double _panX = 0, _panY = 0; // WebView2 平移偏移
 private bool _isCadMode; // CAD 直接渲染模式
 // cad-viewer 网页查看器（mlightcad/cad-viewer）集成
 private bool _cadHostMapped;          // 是否已建立 WebView2 虚拟主机映射
 private string _pendingCadFilePath;
 private string _pendingPreviewFileUrl; // 待推送给 file-viewer 的本地文件 URL
 private string _pendingPreviewFileName; // 待推送的文件名
 // CAD文字渲染参数
 private double _cadWidthFactor = 1.0; // 字宽因子
 private double _cadLineFactor = 1.2; // 行距因子
 private double _cadCharSpacing = 1.0; // 字符间距因子
 private string _cadFontName = "仿宋"; // TTF主字体名（西文）
 private string _cadBigFontName = "仿宋"; // TTF大字体名（中文）
 private string _cadFontFilePath = ""; // SHX主字体文件路径
 private string _cadBigFontFilePath = ""; // SHX大字体文件路径
 private string _cadShxFontName = "Tssdeng"; // SHX主字体名
 private string _cadBigShxFontName = "hztxt"; // SHX大字体名
 private bool _cadUseBigFont = true; // 是否使用大字体
 private double _cadObliqueAngle = 0; // 倾斜角度
 private bool _cadUpsideDown = false; // 颠倒
 private bool _cadBackwards = false; // 反向
 private bool _cadIsDarkBg = true; // CAD背景：true=黑底白字, false=白底黑字
 private readonly ScaleTransform _cadScale = new(1, 1);
 private readonly TranslateTransform _cadTranslate = new(0, 0);    private double _cadBakeZoom = 1.0;   // 当前烘焙图像对应的缩放倍数（元素变换 = _zoom/_cadBakeZoom）
    private double _cadBakePanX;         // 当前烘焙图像对应的平移（视口渲染：图像内容=该状态的窗口）
    private bool _cadFitToFull;          // 用户点了「显示全部」：渲染时禁用初始智能放大，保持整图显示
    private double _cadBakePanY;
    private Image _cadImg;               // 当前 CAD 矢量图控件（拖动快照用）
    private DrawingImage _cadVectorSource; // 当前矢量源（快照结束后恢复）
    private bool _cadDragSnapActive;     // 拖动期间是否处于位图快照模式
 private CancellationTokenSource _cadReBakeCts; // 缩放重烘焙防抖

 // ═══ 位图预览（本地PDF / 图片）的持久变换，保证缩放与平移互不覆盖 ═══
 private readonly ScaleTransform _imgScale = new(1, 1);
 private readonly TranslateTransform _imgTranslate = new(0, 0);
 private bool _imgTransformReady;
 // ═══ 本地PDF（Pdfium位图渲染）模式 ═══
 private bool _isLocalPdfMode;      // true=当前用 ImagePreview 显示本地渲染的PDF页
 private int _localPdfPage;         // 当前选中页（0-based）
 private const int LocalPdfRenderWidth = 1600; // 本地PDF渲染宽度（像素）
 // ═══ WebView2 拖拽平移用的上一帧位置（增量滚动） ═══
 private Point _lastDragPos;
 // ═══ 区域OCR时 ImagePreview 内是否为 WebView2 截图（退出时才需要还原WebView2） ═══
 private bool _ocrSnapshotActive;

 /// <summary>
 /// 注入到 WebView2 页面里的「按住左键拖动平移」脚本。
 /// WebView2 是 HWND 子窗口会吞掉 WPF 鼠标事件，所以拖拽必须在页面内实现。
 /// 对 pdf.js（#viewerContainer）和本地 DOCX HTML（document 滚动）都生效。
 /// </summary>
 private const string LdDragPanScript = @"
(function(){
  if (window.__ldDragPan) return 'already';
  window.__ldDragPan = true;
  var dragging=false, moved=false, sx=0, sy=0, cx=0, cy=0, box=null;
  function scroller(){
    var c=document.getElementById('viewerContainer');
    if(c && (c.scrollHeight>c.clientHeight+2 || c.scrollWidth>c.clientWidth+2)) return c;
    return document.scrollingElement || document.documentElement || document.body;
  }
  function isInteractive(el){
    while(el && el!==document.body){
      var t=(el.tagName||'').toUpperCase();
      if(t==='INPUT'||t==='TEXTAREA'||t==='SELECT'||t==='BUTTON'||t==='A') return true;
      if(el.isContentEditable) return true;
      el=el.parentElement;
    }
    return false;
  }
  document.addEventListener('mousedown', function(e){
    if(e.button!==0) return;
    if(e.altKey||e.ctrlKey||e.shiftKey) return;   // 按住修饰键时保留选中文字
    if(isInteractive(e.target)) return;
    box=scroller();
    dragging=true; moved=false;
    sx=e.clientX; sy=e.clientY;
    cx=box.scrollLeft; cy=box.scrollTop;
    document.body.style.cursor='grabbing';
  }, true);
  document.addEventListener('mousemove', function(e){
    if(!dragging||!box) return;
    var dx=e.clientX-sx, dy=e.clientY-sy;
    if(!moved && Math.abs(dx)<3 && Math.abs(dy)<3) return;
    moved=true;
    box.scrollLeft = cx-dx;
    box.scrollTop  = cy-dy;
    if(e.preventDefault) e.preventDefault();
    var s=window.getSelection && window.getSelection();
    if(s && s.removeAllRanges) s.removeAllRanges();
  }, true);
  function stop(){ if(!dragging) return; dragging=false; document.body.style.cursor=''; }
  document.addEventListener('mouseup', stop, true);
  document.addEventListener('mouseleave', stop, true);
  window.addEventListener('blur', stop);
  try{
    var st=document.createElement('style');
    st.textContent='body{cursor:grab;}';
    (document.head||document.documentElement).appendChild(st);
  }catch(err){}
  return 'ok';
})();";

 /// <summary>确保 ImagePreview 使用持久的 Scale+Translate 变换组</summary>
 private void EnsureImageTransform()
 {
     if (_imgTransformReady) return;
     var g = new System.Windows.Media.TransformGroup();
     g.Children.Add(_imgScale);
     g.Children.Add(_imgTranslate);
     ImagePreview.RenderTransformOrigin = new Point(0.5, 0.5);
     ImagePreview.RenderTransform = g;
     _imgTransformReady = true;
 }

 /// <summary>把 _zoom/_panX/_panY 应用到位图预览</summary>
 private void ApplyImageTransform()
 {
     EnsureImageTransform();
     _imgScale.ScaleX = _zoom;
     _imgScale.ScaleY = _zoom;
     _imgTranslate.X = _panX;
     _imgTranslate.Y = _panY;
 }

 public MainWindow()
 {
 InitializeComponent();
 // 恢复上次保存的CAD字体/字宽设置（~/.ldassistant_cadfont.json）
 LoadCadFontSettings();
 _previewSvc.UpdateCadFontSettings(_cadFontName, _cadBigFontName, _cadFontFilePath, _cadBigFontFilePath,
 _cadShxFontName, _cadBigShxFontName, _cadUseBigFont,
 _cadWidthFactor, _cadLineFactor, _cadCharSpacing,
 _cadObliqueAngle, _cadUpsideDown, _cadBackwards, _cadIsDarkBg);

 // 加载层一开始就显示（XAML中已设Visible）
 StatusText.Text = "正在启动预览服务...";

 // AI 助手悬浮球放在独立无边框窗口中：WebView2 是原生 HWND，会盖住同窗口的 WPF 元素
 // （airspace 限制），独立窗口才能始终显示在 PDF/DOCX/MD 等 WebView2 预览之上。
 _fabWin = new FabWindow { ShowActivated = false };
 _fabWin.FabClicked += (s, e) => OpenAiWithContext();

 // OCR 进度提示也用独立置顶窗口：否则 WebView2（PDF/MD/docx 等）会盖住 WPF 覆盖层，
 // 导致只有 CAD（纯 WPF 渲染）能看到进度条。
 _ocrProgressWin = new Views.OcrProgressWindow { ShowActivated = false };
 LocationChanged += (s, e) => PositionOcrProgressWindow();
 SizeChanged += (s, e) => PositionOcrProgressWindow();
 StateChanged += (s, e) =>
 {
 // 主窗口最小化时隐藏进度窗口，避免残留屏幕
 if (WindowState == WindowState.Minimized) _ocrProgressWin?.HideProgress();
 };
 Closed += (s, e) => { try { _ocrProgressWin?.Close(); } catch { } };

 // 窗口加载后启动初始化流程（Owner 必须在主窗口显示后才能设置）
 Loaded += async (s, e) =>
 {
 try { _fabWin.Owner = this; } catch { }
 UpdateFabWindowPosition();
 _fabWin.Show();

 // 诊断：HwndSource 钩子记录 WM_MOUSEWHEEL（0x020A）到达主窗口的情况
 try
 {
 var helper = new System.Windows.Interop.WindowInteropHelper(this);
 var src = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
 if (src != null)
 {
 src.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled2) =>
 {
 if (msg == 0x020A)
 {
 int delta = (short)((wp.ToInt64() >> 16) & 0xFFFF);
 int sx = (short)(lp.ToInt64() & 0xFFFF);
 int sy = (short)((lp.ToInt64() >> 16) & 0xFFFF);
 }
 return IntPtr.Zero;
 });
 }
 }
 catch { }

 await InitializeAsync();
 };

 // 窗口移动/缩放/状态变化时跟随悬浮球位置
 SizeChanged += (s, e) => UpdateFabWindowPosition();
 LocationChanged += (s, e) => UpdateFabWindowPosition();
 StateChanged += (s, e) =>
 {
 if (WindowState == WindowState.Minimized) { _fabWin.Hide(); }
 else { _fabWin.Show(); UpdateFabWindowPosition(); }
 };
 Closed += (s, e) =>
 {
 try { _fabWin?.Close(); } catch { }
 };

 // 初始化标准数据库
 var dbPath = FindDatabasePath();
 if (dbPath != null)
 {
 try
 {
 _checker = new StandardChecker(dbPath);
 }
 catch { }
 }
 }

 /// <summary>悬浮球跟随主窗口右下角（主窗口客户区坐标 → 屏幕坐标）</summary>
 private void UpdateFabWindowPosition()
 {
 try
 {
 if (_fabWin == null) return;
 var dpi = VisualTreeHelper.GetDpi(this);
 // 悬浮球从右下角内缩（不再贴角）：右下角留出 36px 边距，再往左上方偏移
 // 位置 = 右下角 - (76+36, 86+40) = 比原来往左上方移动 36/40px
 var pt = PointToScreen(new Point(ActualWidth - 112, ActualHeight - 126));
 _fabWin.Left = pt.X / dpi.DpiScaleX;
 _fabWin.Top = pt.Y / dpi.DpiScaleY;
 }
 catch { }
 }

 /// 全流程初始化：file-viewer 预览 + WebView2，完成后隐藏加载层
 private async Task InitializeAsync()
 {
 var logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
 try
 {
 await File.AppendAllTextAsync(logFile, $"[{DateTime.Now:HH:mm:ss}] 开始初始化\n");
 // 分阶段更新加载提示（独立后台任务）
 _ = Task.Run(async () =>
 {
 var stages = new[] { "初始化 PDF 渲染器", "加载 CAD 转换引擎", "初始化文件预览组件", "正在完成最后准备" };
 for (int i = 0; i < stages.Length; i++)
 {
 await Task.Delay(1500);
 try { Dispatcher.Invoke(() => { if (LoadingOverlay.Visibility == Visibility.Visible) LoadingSubText.Text = stages[i]; }); } catch { }
 }
 });

 // 1. 初始化 WebView2（UI线程）
 await File.AppendAllTextAsync(logFile, $"[{DateTime.Now:HH:mm:ss}] 初始化WebView2\n");
 await PreviewWebView.EnsureCoreWebView2Async(null);

 // file-viewer 虚拟主机映射（随程序部署的纯前端预览组件）
 PreviewWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
 TryMapCadViewerHost();
 TryMapFileViewerHost();
 TryMapHtmlHost();

 // 注册 file-viewer 消息接收。必须在任何导航之前注册：
 // 宿主页(file-viewer.html)加载时会立即 postMessage ready，
 // 若等 NavigationCompleted 之后再挂监听，ready 已被丢弃，
 // 文件永远不会推送 —— PPT/Excel 打不开的根因之一。
 PreviewWebView.CoreWebView2.WebMessageReceived += FileViewerMessageReceived;

 // 注册导航完成事件——注入CSS隐藏Luckysheet logo等元素
 PreviewWebView.CoreWebView2.NavigationCompleted += (s, e) =>
 {
 if (!e.IsSuccess) return;
 // 隐藏Luckysheet左上角logo图标、品牌信息等
 PreviewWebView.CoreWebView2.ExecuteScriptAsync(@"
 (function(){
 var style = document.createElement('style');
 style.textContent = `
 /* Luckysheet logo 图标 */
 .luckysheet-logo, .luckysheet-logo-small, [class*='logo']{ display:none !important; }
 /* 品牌信息区 */
 .luckysheet-info, .luckysheet-sheet-area-buttonContainer,
 .luckysheet-powerhouse, .sheet-name-box { display:none !important; }
 /* 右上角设置按钮 */
 .luckysheet-mousedown-canvas-showByCtrl, .luckysheet-postil { display:none !important; }
 /* file-viewer 工具栏深色适配 */
 flyfish-file-viewer{--fv-toolbar-bg:#2b2b30;}
 `;
 document.head.appendChild(style);
 })();
 ");
 // 通用「按住左键拖动页面」——WebView2 是 HWND 子窗口会吞掉 WPF 鼠标事件，
 // 所以拖拽平移必须在页面内部用 JS 实现（对 pdf.js 与本地 DOCX HTML 均生效）
 PreviewWebView.CoreWebView2.ExecuteScriptAsync(LdDragPanScript);

 // PDF：默认自动展开查看器原生侧栏并显示「第1个图标=缩略图」，同时删除最左侧的「☰ 侧栏开关」图标。
 // 注：NavigationCompleted 事件参数无 Uri，用 _currentFilePath 判断当前文档类型。
 // Chromium 内置 PDF 查看器的 DOM 位于 <embed type="application/pdf"> 的 Shadow DOM 内，
 // 顶层 frame 的 ExecuteScriptAsync 可穿透 shadowRoot 直接操作（诊断探测确认可达则走此路，
 // 否则退回 CDP 模拟点击查看器按钮兜底）。
        bool isPdfNav = _currentFilePath != null && Path.GetExtension(_currentFilePath).ToLower() == ".pdf";
        if (isPdfNav)
        {
            // PDF 打开可能触发多次 NavigationCompleted（同一文件），
            // 只保留最后一次的任务，避免重复执行把侧栏开→关抵消
            var navFile = _currentFilePath;
            var mySeq = ++_pdfSidebarNavSeq;
            // 在 UI 线程上捕获 CoreWebView2，避免后台线程跨线程访问控件
            var pdfWeb = PreviewWebView?.CoreWebView2;
            _ = Task.Run(async () =>
            {
                try
                {
                    // 等查看器工具栏就绪（PDF 插件加载 + 布局）
                    await Task.Delay(6000);
                    if (pdfWeb == null) return;
                    if (mySeq != _pdfSidebarNavSeq) return;
                    if (!string.Equals(navFile, _currentFilePath, StringComparison.OrdinalIgnoreCase)) return;
                    var logF = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
                    try
                    {
                        // A) CDP DOM 域穿透（DevTools 可访问封闭 shadowRoot）：真删 ☰ + 切缩略图 + 展开侧栏
                        bool domFixed = false;
                        try { domFixed = await Dispatcher.InvokeAsync(() => TryPdfDomFixAsync(pdfWeb, logF)).Task.Unwrap(); }
                        catch (Exception ex) { File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: DOM 修复异常 {ex.GetType().Name}: {ex.Message}\n"); }
                        if (!domFixed)
                        {
                            // B) 兜底：CDP 模拟点击 ☰（28,19）打开侧栏 + 点缩略图图标
                            File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: 走 CDP 点击兜底\n");
                            await Dispatcher.InvokeAsync(() => ClickPdfAtAsync(pdfWeb, 28, 19)).Task.Unwrap();
                            File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: CDP 点击侧栏 (28,19)\n");
                            await Task.Delay(1200);
                            await Dispatcher.InvokeAsync(() => ClickPdfAtAsync(pdfWeb, 40, 52)).Task.Unwrap();
                            File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: CDP 点击缩略图 (40,52)\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        try { File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: 侧栏处理失败 {ex.GetType().Name}: {ex.Message}\n"); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss}] PDF: 侧栏任务异常 {ex.GetType().Name}: {ex.Message}\n"); } catch { }
                }
            });
        }
    };

    // 2. 隐藏加载层
    await File.AppendAllTextAsync(logFile, $"[{DateTime.Now:HH:mm:ss}] 全部就绪，隐藏加载层\n");
    StatusText.Text = "本地预览已就绪（file-viewer 全格式）";
    LoadingOverlay.Visibility = Visibility.Collapsed;

    // 2b. 无文件时显示欢迎页（背景示意图），避免大片空白；打开文件后会被导航覆盖
    if (string.IsNullOrEmpty(App.StartupOpenPath) || !File.Exists(App.StartupOpenPath))
    {
        try
        {
            if (!_htmlHostMapped) TryMapHtmlHost();
            if (_htmlHostMapped && PreviewWebView?.CoreWebView2 != null)
                SafeNavigate($"https://{HtmlVirtualHost}/home.html");
        }
        catch { }
    }

    // 调试通道：--selfshot <秒> —— 应用自截图（WPF窗口 + WebView2内容），绕过外部屏幕捕获
    if (App.StartupSelfShotSecs > 0)
    {
        _ = SelfShotAsync(App.StartupSelfShotSecs);
    }

    // 命令行 --open 指定文件：启动完成后自动打开（复现/定位加载问题用）
    if (!string.IsNullOrEmpty(App.StartupOpenPath) && File.Exists(App.StartupOpenPath))
    {
        var openPath = App.StartupOpenPath;
        App.StartupOpenPath = null;
        LoadFile(openPath);
    }

    // 3. 后台检查更新
    _ = CheckForUpdatesAsync();
}
catch (Exception ex)
{
    await File.AppendAllTextAsync(logFile, $"[{DateTime.Now:HH:mm:ss}] 初始化失败: {ex}\n");
    Dispatcher.Invoke(() =>
    {
        var msg = ex.Message;
        if (ex.InnerException != null) msg += $"\n→ {ex.InnerException.Message}";
        StatusText.Text = $"启动失败: {msg}";
        LoadingText.Text = "启动失败";
        LoadingSubText.Text = msg;
    });
}
}

 /// <summary>
 /// 查找 file-viewer 预览服务安装目录
 /// </summary>
 /// <summary>
 /// 解析 file-viewer 静态资源目录（随程序部署的 dist）
 /// </summary>
 private string FindFileViewerDir()
 {
 var appDir = AppDomain.CurrentDomain.BaseDirectory;
 var candidates = new[]
 {
 Path.Combine(appDir, "file-viewer"),
 Path.Combine(appDir, "..", "file-viewer"),
 Path.Combine(Directory.GetCurrentDirectory(), "file-viewer"),
 };
 foreach (var p in candidates)
 if (Directory.Exists(p) && File.Exists(Path.Combine(p, "flyfish-file-viewer-web-full.iife.js")))
 return Path.GetFullPath(p);
 return null;
 }

 /// <summary>Html 目录虚拟主机映射（欢迎页 home.html 等静态资源）</summary>
 private bool _htmlHostMapped;
 private const string HtmlVirtualHost = "ldhtml.local";
 private void TryMapHtmlHost()
 {
 if (_htmlHostMapped) return;
 try
 {
 var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Html");
 if (Directory.Exists(dir))
 {
 PreviewWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
 HtmlVirtualHost, dir, CoreWebView2HostResourceAccessKind.Allow);
 _htmlHostMapped = true;
 }
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"[Html] 虚拟主机映射失败: {ex.Message}");
 }
 }

 /// <summary>file-viewer 虚拟主机映射</summary>
 private bool _fileViewerHostMapped;
 private const string FileViewerVirtualHost = "fileviewer.local";
 private void TryMapFileViewerHost()
 {
 if (_fileViewerHostMapped) return;
 var dir = FindFileViewerDir();
 if (string.IsNullOrEmpty(dir)) return;
 try
 {
 PreviewWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
 FileViewerVirtualHost, dir, CoreWebView2HostResourceAccessKind.Allow);
 _fileViewerHostMapped = true;
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"[FileViewer] 虚拟主机映射失败: {ex.Message}");
 }
 }

 /// <summary>file-viewer 宿主页 URL（Html 目录需放进 file-viewer 静态目录）</summary>
 private string FileViewerPageUrl => $"https://{FileViewerVirtualHost}/file-viewer.html";

 /// <summary>
 /// 启动时检查应用更新（GitHub Releases）
 /// </summary>
 /// <summary>
 /// 后台检查更新 — 从 GitHub Releases 获取清单，有更新则状态栏提示
 /// </summary>
 private async Task CheckForUpdatesAsync()
 {
 try
 {
 await Task.Delay(3000); // 启动 3 秒后检查，避免抢资源
 var updates = await _updater.CheckForUpdatesAsync();
 if (updates != null && updates.Length > 0)
 {
 var u = updates[0]; // 优先显示第一个更新
 var compName = u.Component switch
 {
 UpdateComponent.Wpf => "主程序",
 UpdateComponent.Standards => "标准库",
 _ => "组件"
 };
 StatusText.Text = $"🔄 {compName}发现新版本 v{u.RemoteVersion}（当前 v{u.CurrentVersion}）— 点击状态栏更新";
 StatusText.MouseLeftButtonDown -= OnStatusTextClick;
 _pendingUpdate = u;
 StatusText.MouseLeftButtonDown += OnStatusTextClick;
 }
 }
 catch { /* 更新检查失败静默忽略 */ }
 }

 /// <summary>
 /// 下载并应用更新
 /// </summary>
 /// 待处理的更新（状态栏点击时触发）
 private UpdateCheckResult _pendingUpdate;
 private async void OnStatusTextClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
 {
 if (_pendingUpdate == null) return;
 var u = _pendingUpdate;
 _pendingUpdate = null;
 StatusText.MouseLeftButtonDown -= OnStatusTextClick;
 await DownloadAndApplyUpdate(u);
 }

 private async Task DownloadAndApplyUpdate(UpdateCheckResult update)
 {
 try
 {
 var compName = update.Component switch
 {
 UpdateComponent.Wpf => "主程序",
 UpdateComponent.Standards => "标准库",
 _ => "组件"
 };
 StatusText.Text = $"⬇ 正在下载{compName}更新 v{update.RemoteVersion}...";

 var progress = new Progress<int>(pct =>
 {
 StatusText.Text = $"⬇ 下载{compName}更新 {pct}%";
 });

 var tempFile = await _updater.DownloadUpdateAsync(update, progress);
 if (tempFile == null)
 {
 StatusText.Text = "❌ 下载失败，请稍后重试";
 return;
 }

 StatusText.Text = $"📦 正在安装{compName}更新...";

 switch (update.Component)
 {
 case UpdateComponent.Wpf:
 _updater.ApplyWpfUpdate(tempFile);
 _updater.UpdateLocalVersion(UpdateComponent.Wpf, update.RemoteVersion);
 MessageBox.Show($"{compName}已更新到 v{update.RemoteVersion}，需要重启程序生效。", "更新完成",
 MessageBoxButton.OK, MessageBoxImage.Information);
 System.Diagnostics.Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location);
 Application.Current.Shutdown();
 break;

 case UpdateComponent.Standards:
 _updater.ApplyStandardsUpdate(tempFile);
 _updater.UpdateLocalVersion(UpdateComponent.Standards, update.RemoteVersion);
 var dbPath = FindDatabasePath();
 if (dbPath != null)
 _checker = new StandardChecker(dbPath);
 StatusText.Text = $"✅ {compName}已更新到 v{update.RemoteVersion}";
 break;
 }

 try { File.Delete(tempFile); } catch { }
 }
 catch (Exception ex)
 {
 StatusText.Text = $"❌ 更新失败: {ex.Message}";
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

 /// <summary>
 /// 简单文件类型检测（替代 FilePreviewService.DetectFileType）
 /// </summary>
 private static string DetectFileTypeSimple(string path)
 {
 var ext = Path.GetExtension(path).ToLower();
 return ext switch
 {
 ".pdf" => "pdf",
 ".docx" => "docx",
 ".txt" => "txt",
 ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tiff" or ".tif" or ".webp" => "image",
 ".dwg" or ".dxf" => "cad",
 _ => "unknown"
 };
 }

        // ═════════════ AI 窗口 ═════════════
        private AiChatWindow _aiWindow;

 private void ShowAiWindow()
 {
 if (_aiWindow == null || !_aiWindow.IsVisible)
 {
 _aiWindow = new AiChatWindow(_ai);
 _aiWindow.CheckSpecRequested += () => { try { BtnCheck_Click(null, null); } catch { } };
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

 // ═════════════ OCR 进度条：独立置顶窗口（WebView2 airspace 下 WPF 覆盖层不可见） ═════════════
 private Views.OcrProgressWindow _ocrProgressWin;

 /// <summary>进度窗口跟随主窗口预览区中央</summary>
 private void PositionOcrProgressWindow()
 {
 try
 {
 if (_ocrProgressWin == null || !_ocrProgressWin.IsVisible) return;
 var pt = PreviewGrid.PointToScreen(new Point(PreviewGrid.ActualWidth / 2, PreviewGrid.ActualHeight / 2));
 double sx = 1, sy = 1;
 var src = PresentationSource.FromVisual(PreviewGrid);
 if (src?.CompositionTarget != null)
 {
 sx = src.CompositionTarget.TransformToDevice.M11;
 sy = src.CompositionTarget.TransformToDevice.M22;
 }
 _ocrProgressWin.Left = pt.X / sx - _ocrProgressWin.Width / 2;
 _ocrProgressWin.Top = pt.Y / sy - _ocrProgressWin.Height / 2;
 }
 catch { }
 }

 private void ShowOcrProgress(string message = "正在识别...")
 {
 if (_ocrProgressWin == null) return;
 _ocrProgressWin.ShowProgress(message);
 Dispatcher.BeginInvoke(new Action(() => PositionOcrProgressWindow()));
 }

 private void UpdateOcrProgress(double percent, string message = null)
 {
 _ocrProgressWin?.UpdateProgress(percent, message);
 }

 private void HideOcrProgress()
 {
 _ocrProgressWin?.HideProgress();
 }

 /// <summary>OCR 完成/失败提示：进度条 100% + 彩色状态文字，停留 3 秒后自动隐藏</summary>
 private void ShowOcrDone(bool success, string detail = "", Action after = null)
 {
 if (_ocrProgressWin == null) { after?.Invoke(); return; }
 _ocrProgressWin.ShowDone(success, detail, after);
 Dispatcher.BeginInvoke(new Action(() => PositionOcrProgressWindow()));
 }

 // ═════════════ 在线OCR配置窗口 ═════════════
 private OnlineOcrService _onlineOcr = new();

 private void BtnOcrOnlineConfig_Click(object sender, RoutedEventArgs e)
 {
 var win = new OcrConfigWindow(_onlineOcr) { Owner = this };
 win.ShowDialog();
 }

 // ═════════════ OCR执行辅助 ═════════════
 // 路由策略：forceOnline=true → 强制在线（不回退）; forceLocal=true → 强制本地; 否则自动（在线优先，失败回退本地）
 private async Task<(bool Success, string FullText, string Error)> RunOcrAsync(string imagePath, bool invert, bool forceOnline, bool forceLocal)
 {
 if (forceOnline)
 {
 if (!_onlineOcr.IsConfigured)
 return (false, null, "在线OCR未配置，请先点击「🌐 设置」配置在线OCR");
 var r = await _onlineOcr.RecognizeAsync(imagePath, true);
 return (r.Success, r.FullText, r.Error);
 }
 if (forceLocal)
 {
 var r = await _umiOcr.RecognizeAsync(imagePath, "text", invert);
 return (r.Success, r.FullText, r.Error);
 }
 // 自动模式：在线优先，失败回退本地
 if (_onlineOcr.IsConfigured)
 {
 var r = await _onlineOcr.RecognizeAsync(imagePath, true);
 if (r.Success) return (true, r.FullText, null);
 try { File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log"),
 $"[Fallback] 在线OCR失败: {r.Error}, 回退到本地OCR\n"); } catch { }
 }
 var local = await _umiOcr.RecognizeAsync(imagePath, "text", invert);
 return (local.Success, local.FullText, local.Error);
 }

 // ═════════════ 打开文件 ═════════════
        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择文件",
                Multiselect = true,
 Filter =
 "所有支持的文件|*.doc;*.docx;*.xls;*.xlsx;*.xlsm;*.ppt;*.pptx;*.csv;*.tsv;*.dotm;*.xlt;*.xltm;*.dot;*.dotx;*.xlam;*.xla;" +
 "*.wps;*.dps;*.et;*.ett;*.wpt;" +
 "*.odt;*.ods;*.ots;*.odp;*.otp;*.six;*.ott;*.fodt;*.fods;" +
 "*.vsd;*.vsdx;*.wmf;*.emf;*.psd;*.pdf;*.ofd;*.rtf;*.xmind;*.bpmn;*.mmd;*.drawio;*.dio;*.plantuml;*.puml;*.excalidraw;*.eml;*.epub;" +
 "*.obj;*.3ds;*.stl;*.ply;*.gltf;*.glb;*.off;*.3dm;*.fbx;*.dae;*.wrl;*.3mf;*.ifc;*.brep;*.step;*.iges;*.fcstd;*.bim;" +
 "*.dwg;*.dxf;" +
 "*.txt;*.md;*.markdown;*.html;*.htm;*.json;*.xml;*.yaml;*.yml;*.csv;*.log;*.ini;*.cfg;*.conf;*.toml;*.tex;*.typst;" +
 "*.java;*.php;*.py;*.js;*.ts;*.jsx;*.tsx;*.css;*.scss;*.less;*.go;*.rs;*.c;*.cpp;*.h;*.hpp;*.cs;*.rb;*.kt;*.swift;*.sh;*.bat;*.ps1;" +
 "*.zip;*.rar;*.jar;*.tar;*.gzip;*.7z;" +
 "*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.ico;*.jfif;*.webp;*.tif;*.tiff;*.tga;*.svg;" +
 "*.mp3;*.wav;*.mp4;*.flv;*.avi;*.mov;*.rm;*.webm;*.ts;*.mkv;*.mpeg;*.ogg;*.mpg;*.rmvb;*.wmv;*.3gp;*.swf|" +
 "Office文档|*.doc;*.docx;*.xls;*.xlsx;*.xlsm;*.ppt;*.pptx;*.csv;*.tsv;*.dotm;*.xlt;*.xltm;*.dot;*.dotx;*.xlam;*.xla|" +
 "WPS文档|*.wps;*.dps;*.et;*.ett;*.wpt|" +
 "OpenOffice文档|*.odt;*.ods;*.ots;*.odp;*.otp;*.six;*.ott;*.fodt;*.fods|" +
 "Visio|*.vsd;*.vsdx|" +
 "文档|*.pdf;*.ofd;*.rtf;*.epub;*.eml;*.xmind;*.bpmn;*.mmd;*.drawio;*.dio;*.plantuml;*.puml;*.excalidraw|" +
 "CAD|*.dwg;*.dxf|" +
 "3D模型|*.obj;*.3ds;*.stl;*.ply;*.gltf;*.glb;*.off;*.3dm;*.fbx;*.dae;*.wrl;*.3mf;*.ifc;*.brep;*.step;*.iges;*.fcstd;*.bim|" +
 "图片|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.ico;*.jfif;*.webp;*.tif;*.tiff;*.tga;*.svg;*.wmf;*.emf;*.psd|" +
 "文本/代码|*.txt;*.md;*.markdown;*.html;*.htm;*.json;*.xml;*.yaml;*.yml;*.log;*.ini;*.cfg;*.conf;*.toml;*.tex;*.typst;*.java;*.php;*.py;*.js;*.ts;*.jsx;*.tsx;*.css;*.scss;*.less;*.go;*.rs;*.c;*.cpp;*.h;*.hpp;*.cs;*.rb;*.kt;*.swift;*.sh;*.bat;*.ps1|" +
 "压缩包|*.zip;*.rar;*.jar;*.tar;*.gzip;*.7z|" +
 "音视频|*.mp3;*.wav;*.mp4;*.flv;*.avi;*.mov;*.rm;*.webm;*.ts;*.mkv;*.mpeg;*.ogg;*.mpg;*.rmvb;*.wmv;*.3gp;*.swf|" +
 "所有文件|*.*"
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
 FileType = DetectFileTypeSimple(path)
 };
 _batchFiles.Add(item);
 FileListItems.Add(item);
 }
 
 if (_batchFiles.Count > 0)
 LoadFile(_batchFiles[0].FilePath);
 }
}

// ═════════════ 打开图片 / Ctrl+V 粘贴图片 ═════════════
private void BtnOpenImage_Click(object sender, RoutedEventArgs e)
{
var dlg = new Microsoft.Win32.OpenFileDialog
{
Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif;*.webp|所有文件|*.*",
Title = "选择图片文件"
};
if (dlg.ShowDialog() == true)
{
LoadImageForOcr(dlg.FileName);
}
}

/// 从文件加载图片，显示到预览区并可直接OCR
private void LoadImageForOcr(string imagePath)
{
try
{
// 图片用 WPF Image 控件直接显示（不走 WebView2，避免 HWND 鼠标事件问题）
_currentImageForOcr = imagePath;
_currentFilePath = imagePath;
_zoom = 1.0;
_isCadMode = false;
_isLocalPdfMode = false;
_ocrSnapshotActive = false; // 隐藏 WebView2，显示 ImagePreview
 PreviewWebView.Visibility = Visibility.Collapsed;
 CadScrollViewer.Visibility = Visibility.Collapsed;
 CadFontGroup.Visibility = Visibility.Collapsed;
 CadLayoutBar.Visibility = Visibility.Collapsed;
 // 图片无侧栏：清掉上一个文档的缩略图并收起侧栏
 PageThumbItems.Clear();
 CollapseSidebar();
 HideSidebarTab();

 StatusText.Text = $"正在加载图片: {Path.GetFileName(imagePath)}...";

// 在后台线程加载图片
_ = Task.Run(() =>
{
 try
 {
 var bmp = new System.Windows.Media.Imaging.BitmapImage();
 bmp.BeginInit();
 bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
 bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
 bmp.EndInit();
 bmp.Freeze();

 Dispatcher.Invoke(() =>
 {
 ImagePreview.Source = bmp;
 ImagePreview.Visibility = Visibility.Visible;
 // 重置缩放和平移
 _zoom = 1.0;
 _panX = 0; _panY = 0;
 ApplyZoom();
 StatusText.Text = $"已加载图片: {Path.GetFileName(imagePath)}";
 _ = GenerateThumbnailsAsync();
 });
 }
 catch (Exception ex)
 {
 Dispatcher.Invoke(() => { StatusText.Text = $"加载图片失败: {ex.Message}"; });
 }
});
}
catch (Exception ex)
{
StatusText.Text = $"加载图片失败: {ex.Message}";
}
}

private string _currentImageForOcr;

/// 把原始 SVG 包裹成带缩放/平移/适配 的完整 HTML 页面（供 WebView2 展示 CAD 矢量图）
/// 安全导航WebView2（防止CoreWebView2为null时崩溃）
private void SafeNavigate(string url)
{
	if (PreviewWebView?.CoreWebView2 != null)
		PreviewWebView.CoreWebView2.Navigate(url);
}

private DateTime _logRtLast;
private string _logRtLastMsg;
/// <summary>运行时诊断日志（节流：同一消息 1 秒内最多写 1 条，避免缩放/拖动时高频写盘造成文件句柄反复开关）。</summary>
private void LogRt(string msg)
{
	try
	{
		var now = DateTime.UtcNow;
		if (msg == _logRtLastMsg && (now - _logRtLast).TotalSeconds < 1.0) return;
		_logRtLastMsg = msg;
		_logRtLast = now;
		var f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime.log");
		File.AppendAllText(f, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
	}
	catch { }
}

/// Ctrl+V 粘贴剪贴板图片
/// 文件加载旋转动画 —— 纯代码驱动，不依赖 XAML EventTrigger（Collapsed→Visible 时 Loaded 早已 firing 过）
private System.Windows.Media.Animation.Storyboard _loadingStoryboard;
 // ═══ 文件加载覆盖层辅助（统一进度提示，含最小展示时间防闪烁） ═══
 private DateTime _fileLoadingShownUtc;
 private void ShowFileLoadingOverlay(string text, string sub)
 {
 _fileLoadingShownUtc = DateTime.UtcNow;
 FileLoadingText.Text = text;
 FileLoadingSubText.Text = sub;
 FileLoadingOverlay.Visibility = Visibility.Visible;
 }
 private void HideFileLoadingOverlay()
 {
 var elapsedMs = (DateTime.UtcNow - _fileLoadingShownUtc).TotalMilliseconds;
 if (elapsedMs >= 800)
 {
 FileLoadingOverlay.Visibility = Visibility.Collapsed;
 }
 else
 {
 var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800 - Math.Max(0, elapsedMs)) };
 timer.Tick += (s, e) => { timer.Stop(); FileLoadingOverlay.Visibility = Visibility.Collapsed; };
 timer.Start();
 }
 }

private void FileLoadingOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
{
 if ((bool)e.NewValue)
 {
  // 用 BeginInvoke 确保布局完成后再启动，避免动画起始抖动
  Dispatcher.BeginInvoke(new Action(() =>
  {
   try
   {
    var rotate = FileLoadingSpinner;
    if (rotate == null) return;
    // 先停掉旧的（防止快速切换时叠加）
    _loadingStoryboard?.Stop();
    _loadingStoryboard = new System.Windows.Media.Animation.Storyboard();
    var anim = new System.Windows.Media.Animation.DoubleAnimation
    {
     From = 0, To = 360,
     Duration = TimeSpan.FromSeconds(0.8),
     RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
    };
    System.Windows.Media.Animation.Storyboard.SetTarget(anim, rotate);
    System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim,
     new System.Windows.PropertyPath(RotateTransform.AngleProperty));
    _loadingStoryboard.Children.Add(anim);
    _loadingStoryboard.Begin(FileLoadingSpinnerPath, true);  // isControllable=true，允许 Stop
   }
   catch { }
  }), System.Windows.Threading.DispatcherPriority.ContextIdle);
 }
 else
 {
  try { _loadingStoryboard?.Stop(); } catch { }
 }
}

private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
{
// Ctrl+V 粘贴图片
if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
{
if (Clipboard.ContainsImage())
{
var clipImg = Clipboard.GetImage();
if (clipImg != null)
{
// 保存到临时文件
var tempImg = Path.GetTempFileName() + ".png";
var encoder = new PngBitmapEncoder();
encoder.Frames.Add(BitmapFrame.Create(clipImg));
using (var fs = File.OpenWrite(tempImg))
encoder.Save(fs);

LoadImageForOcr(tempImg);
StatusText.Text = "已从剪贴板粘贴图片，可直接点击OCR识别";
e.Handled = true;
}
}
}
}

// ═════════════ 加载文件 → WebView2 加载本地预览 ═════════════
private void LoadFile(string path)
{
 // 切换文件：释放上一个 CAD 图纸的模型几何缓存（大图纸可达数十MB），
 // 并作废未完成的 CAD 重烘焙，避免旧任务把结果贴到新文件上。
 if (!string.Equals(_currentFilePath, path, StringComparison.OrdinalIgnoreCase))
 {
 Services.CadWpfRenderer.ClearModelCache();
 _cadReBakeCts?.Cancel();
 _cadVectorToken++;
 }
 _currentFilePath = path;
 _currentPage = 0;
 _totalPages = 1; // 默认单页，各格式打开后设置真实页数
 _zoom = 1.0;
 _isLocalPdfMode = false;
 _localPdfPage = 0;
 _ocrSnapshotActive = false;
 _currentImageForOcr = null;

 // ═══ 清空上一个文档的内容 ═══
 // 收起缩略图侧边栏（同时隐藏 Visibility，避免 Width=0 时子项仍在左上角布局闪现）
 PageThumbItems.Clear();
 CollapseSidebar();
 HideSidebarTab();
 // 清空音视频播放
 try { MediaPreview.Stop(); } catch { }
 MediaPreview.Source = null;
 MediaPreview.Visibility = Visibility.Collapsed;
 // 清空 WebView 内容
 if (PreviewWebView?.CoreWebView2 != null)
 {
 _pendingCadFilePath = null;
 PreviewWebView.CoreWebView2.Navigate("about:blank");
 }
 // 清空 CAD 画布
 CadHostCanvas.Children.Clear();
 // 清空文件预览服务状态
 try { _previewSvc?.Close(); } catch { }

 // 更新文件列表高亮
 foreach (var f in FileListItems)
 f.IsActive = (f.FilePath == path);

 // 重置平移偏移
 _panX = 0; _panY = 0;

 var ext = Path.GetExtension(path).ToLower();
 // 非 MD 文件恢复缩略图模板（MD 大纲模板不残留）
 // .ts 双义：MPEG-TS 视频（二进制，0x47 同步字节）vs TypeScript 源码（文本）——
 // 若前 4 个 188 字节包同步位均为 0x47，判定为视频
 bool isTsVideo = ext == ".ts" && IsMpegTsBinary(path);
 if (!(ext == ".md" || ext == ".markdown"))
 ThumbList.ItemTemplate = (DataTemplate)FindResource("ThumbGridTemplate");

 // CAD 文件走矢量 SVG 渲染（ACadSharp 解析 + CadSvgRenderer 输出矢量 SVG）
 if (ext == ".dwg" || ext == ".dxf")
 {
 _isCadMode = true;
 PreviewWebView.Visibility = Visibility.Visible;
 ImagePreview.Visibility = Visibility.Collapsed;
 ImagePreview.Source = null;
 CadScrollViewer.Visibility = Visibility.Collapsed;
 CadHostCanvas.Children.Clear();
 CadFontGroup.Visibility = Visibility.Collapsed;
 CadLayoutBar.Visibility = Visibility.Collapsed;

 StatusText.Text = $"正在加载CAD文件: {Path.GetFileName(path)}...";
 FileLoadingText.Text = "正在加载CAD文件...";
 FileLoadingSubText.Text = Path.GetFileName(path);
 FileLoadingOverlay.Visibility = Visibility.Visible;

 _ = Task.Run(() =>
 {
 try
 {
 _previewSvc.Open(path);
 _currentPage = 0;
 _totalPages = _previewSvc.TotalPages;
 var entities = _previewSvc.GetCadEntities(0);
 Dispatcher.BeginInvoke(new Action(() =>
 {
 PreviewWebView.Visibility = Visibility.Collapsed;
 CadScrollViewer.Visibility = Visibility.Visible;
 CadFontGroup.Visibility = Visibility.Visible; // 显示 CAD 字体设置按钮
 FileLoadingOverlay.Visibility = Visibility.Collapsed;
 PageInfo.Text = $"{Path.GetFileName(path)}";
 StatusText.Text = $"已加载: {Path.GetFileName(path)}";
 // 填充布局/模型空间切换栏
 PopulateCadLayoutBar();
 // WPF 矢量渲染（DrawingVisual 直接绘制，缩放不失真）
 _ = DisplayCadVectorPageAsync(0);
 // 调试通道：--cadzoom 预设缩放（验证文字渲染用）
 if (App.StartupCadZoom > 0 && _zoom == 1.0)
 {
 _zoom = Math.Max(0.1, Math.Min(100.0, App.StartupCadZoom));
 _ = Dispatcher.BeginInvoke(new Action(() =>
 {
 try { UpdateCadHostTransform(CadHostCanvas.ActualWidth / 2.0, CadHostCanvas.ActualHeight / 2.0); ScheduleCadReBake(); } catch { }
 }));
 }
 }));
 LogRt($"CAD OK: {Path.GetFileName(path)} ent={(entities == null ? 0 : entities.Count)}");
			}
			catch (Exception ex)
			{
				LogRt($"CAD FAIL: {Path.GetFileName(path)} {ex.GetType().Name}: {ex.Message}");
				Dispatcher.Invoke(() => { StatusText.Text = $"CAD加载失败: {ex.Message}"; FileLoadingOverlay.Visibility = Visibility.Collapsed; });
			}
 });
 return;
 }

 // 其他格式走本地预览或 file-viewer
 _isCadMode = false;
 PreviewWebView.Visibility = Visibility.Visible;
 ImagePreview.Visibility = Visibility.Collapsed;
 ImagePreview.Source = null;
 CadScrollViewer.Visibility = Visibility.Collapsed;
 CadHostCanvas.Children.Clear();
 CadFontGroup.Visibility = Visibility.Collapsed; // 隐藏CAD字体设置按钮
 CadLayoutBar.Visibility = Visibility.Collapsed; // 隐藏模型/布局切换栏

 // 本地能搞定的格式走 LoadLocalPreview（docx/txt/图片），其余走 file-viewer 或 WebView2
 // PDF：用 WebView2 内置 Chromium PDF 查看器（矢量渲染，支持缩放/选择/翻页）
 if (ext == ".pdf")
 {
 _isCadMode = false;
 _isLocalPdfMode = false;
 PreviewWebView.Visibility = Visibility.Visible;
 ImagePreview.Visibility = Visibility.Collapsed;
 CadScrollViewer.Visibility = Visibility.Collapsed;
 CadHostCanvas.Children.Clear();
 CadFontGroup.Visibility = Visibility.Collapsed;
 CadLayoutBar.Visibility = Visibility.Collapsed;            StatusText.Text = $"正在加载PDF: {Path.GetFileName(path)}...";
 ShowFileLoadingOverlay("正在加载PDF...", Path.GetFileName(path));
            LogRt($"PDF OPEN start: {Path.GetFileName(path)}");
            _ = Task.Run(async () =>
            {
                try
                {
                    _previewSvc.Open(path);
                    _totalPages = _previewSvc.TotalPages;
                    LogRt($"PDF parsed pages={_totalPages}");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // WebView2 内置 PDF 查看器：直接导航到 file:// 路径
                        // Chromium 自带 pdf.js，矢量渲染，支持文字选择/缩放/打印
                        var fileUri = new Uri(path).AbsoluteUri;
                        LogRt($"PDF navigate to: {fileUri} (wv ready={PreviewWebView?.CoreWebView2 != null})");
                        SafeNavigate(fileUri);
                        PageInfo.Text = $"{Path.GetFileName(path)} ({_totalPages}页)";
                        StatusText.Text = $"已加载: {Path.GetFileName(path)}";
 HideFileLoadingOverlay();
                        // PDF 缩略图侧边栏：默认展开缩略图视图
                        _ = GenerateThumbnailsAsync();
                        // 临时诊断：探测 Chromium PDF viewer DOM 结构
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(4000);
                            try
                            {
                                var domInfo = await Dispatcher.InvokeAsync(async () =>
                                {
                                    if (PreviewWebView?.CoreWebView2 == null) return "no wv";
                                    var js = "JSON.stringify({" +
                                        "embed: !!document.querySelector('embed[type=\"application/pdf\"]')," +
                                        "pageEls: document.querySelectorAll('.page').length," +
                                        "bodyChildren: document.body ? document.body.children.length : 0," +
                                        "vw: window.innerWidth, vh: window.innerHeight" +
                                        "})";
                                    var r = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(js);
                                    return r ?? "null";
                                }).Task.Unwrap();
                                LogRt($"PDF DOM PROBE: {domInfo}");
                                // 探测 CDP PageDown 是否翻页：发送后截图对比（仅诊断）
                                var shot1 = await Dispatcher.InvokeAsync(() =>
                                {
                                    if (PreviewWebView?.CoreWebView2 == null) return Task.FromResult<string>(null);
                                    return PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}");
                                }).Task.Unwrap();
                                await Task.Delay(800);
                                var key = System.Text.Json.JsonSerializer.Serialize(new { type = "keyDown", key = "PageDown", code = "PageDown", windowsVirtualKeyCode = 34 });
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    if (PreviewWebView?.CoreWebView2 != null)
                                        return PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", key);
                                    return Task.FromResult<string>(null);
                                }).Task.Unwrap();
                                var keyUp = System.Text.Json.JsonSerializer.Serialize(new { type = "keyUp", key = "PageDown", code = "PageDown", windowsVirtualKeyCode = 34 });
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    if (PreviewWebView?.CoreWebView2 != null)
                                        return PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUp);
                                    return Task.FromResult<string>(null);
                                }).Task.Unwrap();
                                await Task.Delay(1200);
                                var shot2 = await Dispatcher.InvokeAsync(() =>
                                {
                                    if (PreviewWebView?.CoreWebView2 == null) return Task.FromResult<string>(null);
                                    return PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}");
                                }).Task.Unwrap();
                                bool same = shot1 == shot2;
                                LogRt($"PDF PAGEDOWN PROBE: changed={!same} len1={shot1?.Length} len2={shot2?.Length}");
                            }
                            catch (Exception ex) { LogRt($"PDF DOM PROBE FAIL: {ex.Message}"); }
                        });
                    });
                }
                catch (Exception ex)
                {
                    LogRt($"PDF FAIL: {ex.GetType().Name}: {ex.Message}");
                    Dispatcher.Invoke(() => { StatusText.Text = $"PDF加载失败: {ex.Message}"; HideFileLoadingOverlay(); });
                }
            });
            return;
 }

 // 其他本地格式走 LoadLocalPreview（docx/txt/markdown/html/代码/图片/Visio vsdx/ODF 等）
 // 注：.vsd（二进制 Visio）不支持本地解析，交给 file-viewer（flyfish 会给出明确提示）；.vsdx 走本地文本提取
 if (ext == ".docx" || ext == ".txt" || ext == ".xps" || ext == ".vsdx" ||
 ext == ".md" || ext == ".markdown" || ext == ".html" || ext == ".htm" ||
 ext == ".json" || ext == ".xml" || ext == ".yaml" || ext == ".yml" ||
 ext == ".csv" || ext == ".log" || ext == ".bpmn" ||
 ext == ".java" || ext == ".php" || ext == ".py" || ext == ".js" || (ext == ".ts" && !isTsVideo) ||
 ext == ".css" || ext == ".scss" || ext == ".less" || ext == ".go" || ext == ".rs" ||
 ext == ".c" || ext == ".cpp" || ext == ".h" || ext == ".hpp" || ext == ".cs" ||
 ext == ".rb" || ext == ".kt" || ext == ".swift" || ext == ".sh" || ext == ".bat" ||
 ext == ".ps1" || ext == ".ini" || ext == ".cfg" || ext == ".conf" || ext == ".toml" ||
 // ODF 模板/扁平格式走本地解析（odt/ods/odp/fods 是 file-viewer 官方支持，由其渲染更佳；本地作兜底）
 ext == ".ott" || ext == ".ots" || ext == ".otp" || ext == ".fodt" || ext == ".six" ||
 ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" ||
 ext == ".tiff" || ext == ".tif" || ext == ".webp" || ext == ".gif" ||
 ext == ".ico" || ext == ".jfif" || ext == ".wmf" || ext == ".emf" || ext == ".tga")
 {
 LoadLocalPreview(path, ext);
 return;
 }

 // SVG：WPF BitmapImage 不支持 SVG，直接用 WebView2（Chromium）渲染
 if (ext == ".svg")
 {
 StatusText.Text = $"正在加载: {Path.GetFileName(path)}...";
 PreviewWebView.Visibility = Visibility.Visible;
 ImagePreview.Visibility = Visibility.Collapsed;
 CadScrollViewer.Visibility = Visibility.Collapsed;
 CadLayoutBar.Visibility = Visibility.Collapsed;
 SafeNavigate(new Uri(path).AbsoluteUri);
 PageInfo.Text = Path.GetFileName(path);
 StatusText.Text = $"已加载: {Path.GetFileName(path)}";
 _ = GenerateThumbnailsAsync();
 return;
 }

 // OFF 3D 模型：转成 OBJ 再交给 file-viewer 渲染（file-viewer 支持 obj）
 if (ext == ".off")
 {
 _ = Task.Run(async () =>
 {
 try
 {
 var objPath = ConvertOffToObj(path);
 if (string.IsNullOrEmpty(objPath))
 {
 Dispatcher.Invoke(() => StatusText.Text = "OFF 文件解析失败（格式异常）");
 return;
 }
 await Dispatcher.InvokeAsync(() =>
 {
 if (!_fileViewerHostMapped) TryMapFileViewerHost();
 if (_fileViewerHostMapped && PreviewWebView?.CoreWebView2 != null)
 {
 SafeNavigate(FileViewerPageUrl);
 _pendingPreviewFileUrl = objPath;
 _pendingPreviewFileName = Path.GetFileNameWithoutExtension(path) + ".obj";
 }
 else
 {
 StatusText.Text = "OFF 预览需要 file-viewer 组件（部署目录缺少 file-viewer 文件夹）";
 }
 PageInfo.Text = Path.GetFileName(path);
 _ = GenerateThumbnailsAsync();
 });
 }
 catch (Exception ex)
 {
 Dispatcher.Invoke(() => StatusText.Text = $"OFF 预览失败: {ex.Message}");
 }
 });
 return;
 }

 // 旧视频格式本地播放：file-viewer 只支持 mp4/webm/m3u8，
 // 其余（avi/mov/mkv/flv/wmv/rmvb/3gp/mpeg/mpg/ts/rm）用系统解码器（MediaElement）
 if (ext == ".avi" || ext == ".mov" || ext == ".mkv" || ext == ".flv" || ext == ".wmv" ||
 ext == ".rmvb" || ext == ".3gp" || ext == ".mpeg" || ext == ".mpg" || (ext == ".ts" && isTsVideo) ||
 ext == ".rm")
 {
 PreviewWebView.Visibility = Visibility.Collapsed;
 ImagePreview.Visibility = Visibility.Collapsed;
 ImagePreview.Source = null;
 CadScrollViewer.Visibility = Visibility.Collapsed;
 CadFontGroup.Visibility = Visibility.Collapsed;
 CadLayoutBar.Visibility = Visibility.Collapsed;
 PageInfo.Text = Path.GetFileName(path);
 StatusText.Text = $"正在播放: {Path.GetFileName(path)}...";
 try
 {
 MediaPreview.Source = new Uri(path);
 MediaPreview.Visibility = Visibility.Visible;
 MediaPreview.Play();
 _ = GenerateThumbnailsAsync();
 StatusText.Text = $"正在播放: {Path.GetFileName(path)}（若无画面，请安装对应解码器）";
 }
 catch (Exception ex)
 {
 StatusText.Text = $"视频播放失败: {ex.Message}（可能需要安装解码器）";
 }
 return;
 }	 // 无内嵌渲染器的格式：给出明确提示，避免静默空白
	 if (ext == ".vsd" || ext == ".bim" || ext == ".swf")
	 {
		 string tip = ext switch
		 {
			 ".vsd" => "旧版二进制 Visio（.vsd）暂不支持内嵌预览，请另存为 .vsdx 后打开",
			 ".bim" => "BIM 专有格式（.bim）暂不支持内嵌预览",
			 _ => "Flash（.swf）格式已停止支持，无法内嵌播放",
		 };
		 PreviewWebView.Visibility = Visibility.Collapsed;
		 ImagePreview.Visibility = Visibility.Collapsed;
		 CadScrollViewer.Visibility = Visibility.Collapsed;
		 PageInfo.Text = Path.GetFileName(path);
		 StatusText.Text = tip;
		 return;
	 }

	 // 其余格式（xlsx/pptx/zip/mp4/等）用 file-viewer 预览
	 _ = Task.Run(async () =>
 {
 try
 {
 await Dispatcher.InvokeAsync(() =>
 {
 if (!_fileViewerHostMapped) TryMapFileViewerHost();
 if (_fileViewerHostMapped && PreviewWebView?.CoreWebView2 != null)
 {
 // 导航到 file-viewer 宿主页，导航完成后 postMessage 传入文件
 SafeNavigate(FileViewerPageUrl);
 // 存本地路径（FileViewerMessageReceived 里会读文件转 base64 推送）
 // WPS 二进制（wps/dps/et/ett/wpt）按文件魔数重映射为 Office 兼容格式，file-viewer 才能渲染
 var fvPath = RemapWpsForPreview(path, ext);
 _pendingPreviewFileUrl = fvPath;
 _pendingPreviewFileName = Path.GetFileName(fvPath);
 }
 else
 {
 // file-viewer 不可用，回退本地预览
 LoadLocalPreview(path, ext);
 }
 PageInfo.Text = Path.GetFileName(path);
 StatusText.Text = $"已加载: {Path.GetFileName(path)}";
 _ = GenerateThumbnailsAsync();
 });
 }
 catch (Exception ex)
 {
 Dispatcher.Invoke(() => { StatusText.Text = $"预览失败: {ex.Message}"; FileLoadingOverlay.Visibility = Visibility.Collapsed; });
 }
 });
 }

 /// <summary>
 /// file-viewer 宿主页消息处理：收到 ready 消息后推送待预览文件
 /// </summary>
 private void FileViewerMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
 {
 try
 {
 var msg = e.TryGetWebMessageAsString();
 if (msg == null) return;
 if (msg.Contains("ready"))
 {
 if (!string.IsNullOrEmpty(_pendingPreviewFileUrl) && File.Exists(_pendingPreviewFileUrl))
 {
 var bytes = File.ReadAllBytes(_pendingPreviewFileUrl);
 var b64 = Convert.ToBase64String(bytes);
 var ext = Path.GetExtension(_pendingPreviewFileUrl).TrimStart('.').ToLower();
 var payload = System.Text.Json.JsonSerializer.Serialize(new
 {
 type = "preview",
 fileData = b64,
 fileName = _pendingPreviewFileName ?? "",
 fileType = ext
 });
 PreviewWebView.CoreWebView2.PostWebMessageAsJson(payload);
 }
 }
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"[FileViewer] 推送文件失败: {ex.Message}");
 }
 }

 // ═════════════ 直接渲染（WPF 本地，file-viewer 不可用时的兜底） ═════════════
 private void LoadLocalPreview(string path, string ext)
{
	_isCadMode = false;
	PreviewWebView.Visibility = Visibility.Collapsed;
	ImagePreview.Visibility = Visibility.Collapsed;
	ImagePreview.Source = null;
	CadScrollViewer.Visibility = Visibility.Collapsed;
	CadHostCanvas.Children.Clear();
	CadFontGroup.Visibility = Visibility.Collapsed;
	CadLayoutBar.Visibility = Visibility.Collapsed;

	if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" ||
		ext == ".tiff" || ext == ".tif" || ext == ".webp" || ext == ".gif" || ext == ".svg" ||
		ext == ".ico" || ext == ".jfif" || ext == ".wmf" || ext == ".emf" || ext == ".tga")
	{
		StatusText.Text = $"正在加载图片: {Path.GetFileName(path)}...";
 ShowFileLoadingOverlay("正在加载图片...", Path.GetFileName(path));
		_ = Task.Run(() =>
		{
			try
			{
				System.Windows.Media.Imaging.BitmapSource bmp;
				if (ext == ".tga")
				{
					// TGA：WPF/Chromium 均无解码器，使用内置解码器
					bmp = DecodeTga(path);
				}
				else
				{
					var bi = new System.Windows.Media.Imaging.BitmapImage();
					bi.BeginInit();
					bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
					bi.UriSource = new Uri(path, UriKind.Absolute);
					bi.EndInit();
					bi.Freeze();
					bmp = bi;
				}
				Dispatcher.Invoke(() =>
				{
					_currentImageForOcr = path; // 图片模式：区域OCR可直接从原图高清裁剪
					ImagePreview.Source = bmp;
					ImagePreview.Visibility = Visibility.Visible;
					_zoom = 1.0; _panX = 0; _panY = 0; ApplyImageTransform();
					PageInfo.Text = Path.GetFileName(path);
					StatusText.Text = $"本地预览: {Path.GetFileName(path)}";
					HideFileLoadingOverlay();
				_ = GenerateThumbnailsAsync();
				});
			}
			catch (Exception ex)
			{
				if (ext == ".tga")
				{
					// TGA：WPF/Chromium 均无解码器，解码失败直接提示
					Dispatcher.Invoke(() => { StatusText.Text = $"TGA 解码失败: {ex.Message}"; HideFileLoadingOverlay(); });
				}
				else
				{
					// WPF 解码失败（webp 缺解码器 / wmf / emf / 损坏文件）时，回退到 WebView2（Chromium）渲染
					Dispatcher.Invoke(() =>
					{
						try
						{
							PreviewWebView.Visibility = Visibility.Visible;
							SafeNavigate(new Uri(path, UriKind.Absolute).AbsoluteUri);
							PageInfo.Text = Path.GetFileName(path);
							StatusText.Text = $"已加载: {Path.GetFileName(path)}";
 HideFileLoadingOverlay();
							_ = GenerateThumbnailsAsync();
						}
						catch { StatusText.Text = $"图片加载失败: {ex.Message}"; HideFileLoadingOverlay(); }
					});
				}
			}
		});
		return;
	}

	if (ext == ".docx")
	{
		StatusText.Text = $"正在本地转换: {Path.GetFileName(path)}...";
		_ = Task.Run(async () =>
		{
			try
			{
				var html = DocxToHtmlConverter.Convert(path);
				var tempHtml = Path.Combine(Path.GetTempPath(), "ld_docx_preview.html");
				File.WriteAllText(tempHtml, html, Encoding.UTF8);
				await Dispatcher.InvokeAsync(() =>
				{
					// 不移动 WebView 直接导航：margin 大幅移动后 WebView2 的本地渲染
					// 区域会停在屏幕外不恢复（白屏）。未适配页面的闪现由
					// FileLoadingOverlay 加载层遮挡。
					PreviewWebView.Visibility = Visibility.Visible;
 SafeNavigate(new Uri(tempHtml).AbsoluteUri);
 PageInfo.Text = Path.GetFileName(path);
 StatusText.Text = $"本地预览: {Path.GetFileName(path)}";
 ShowSidebarTab();
 FileLoadingText.Text = "正在渲染 Word 文档...";
					FileLoadingSubText.Text = Path.GetFileName(path);
					FileLoadingOverlay.Visibility = Visibility.Visible;
				});
				await WaitForDocxPaginationAsync();
				await GenerateThumbnailsAsync();
				// 离屏截图兜底：若全部缩略图为空（离屏渲染受限），恢复原位重新生成
				bool thumbsAllEmpty = await Dispatcher.InvokeAsync(() =>
					PageThumbItems.Count > 0 && PageThumbItems.All(p => p.Thumbnail == null));
				if (thumbsAllEmpty)
				{
					await GenerateThumbnailsAsync();
				}
				// 缩略图就绪、侧栏展开后，按当前视口整页适配（与 PDF 打开行为一致）
				await FitDocxAfterLoadAsync();
				// 恢复显示：WebView 一直在预览区（未移屏），无渲染区域错位问题
				await Dispatcher.InvokeAsync(() =>
				{
					FileLoadingOverlay.Visibility = Visibility.Collapsed;
				});
				LogRt($"DOCX OK(local): {Path.GetFileName(path)}");
			}
			catch (Exception ex)
			{
				LogRt($"DOCX FAIL(local): {Path.GetFileName(path)} {ex.GetType().Name}: {ex.Message}");
				Dispatcher.Invoke(() =>
				{
					StatusText.Text = $"docx 本地转换失败: {ex.Message}";
					FileLoadingOverlay.Visibility = Visibility.Collapsed;
				});
			}
		});
 return;
	}

	// ═══ Visio vsdx：vsdx 是 ZIP 包，提取各页图形名称与文本 ═══
	if (ext == ".vsdx")
	{
		StatusText.Text = $"正在解析 Visio: {Path.GetFileName(path)}...";
		_ = Task.Run(() =>
		{
			try
			{
				var html = BuildVisioVsdxHtml(path);
				var tmpHtml = Path.Combine(Path.GetTempPath(), $"ld_vsdx_{Guid.NewGuid():N}.html");
				File.WriteAllText(tmpHtml, html, Encoding.UTF8);
				Dispatcher.Invoke(() =>
				{
					PreviewWebView.Visibility = Visibility.Visible;
					SafeNavigate(new Uri(tmpHtml).AbsoluteUri);
					PageInfo.Text = Path.GetFileName(path);
					StatusText.Text = $"已加载: {Path.GetFileName(path)}（Visio 文本模式）";
				_ = GenerateThumbnailsAsync();
				});
				LogRt($"VSDX OK(local): {Path.GetFileName(path)}");
			}
			catch (Exception ex)
			{
				LogRt($"VSDX FAIL(local): {Path.GetFileName(path)} {ex.GetType().Name}: {ex.Message}");
				Dispatcher.Invoke(() => StatusText.Text = $"Visio 解析失败: {ex.Message}");
			}
		});
		return;
	}

	// ═══ ODF 文档本地解析（odt/ott/fodt 文本、ods/ots/fods 表格、odp/otp 演示文本）═══
	if (ext == ".odt" || ext == ".ott" || ext == ".fodt" || ext == ".ods" || ext == ".ots" ||
		ext == ".fods" || ext == ".odp" || ext == ".otp" || ext == ".six")
	{
		StatusText.Text = $"正在解析 ODF: {Path.GetFileName(path)}...";
		_ = Task.Run(() =>
		{
			try
			{
				var html = BuildOdfHtml(path, ext);
				var tmpHtml = Path.Combine(Path.GetTempPath(), $"ld_odf_{Guid.NewGuid():N}.html");
				File.WriteAllText(tmpHtml, html, Encoding.UTF8);
				Dispatcher.Invoke(() =>
				{
					PreviewWebView.Visibility = Visibility.Visible;
					SafeNavigate(new Uri(tmpHtml).AbsoluteUri);
					PageInfo.Text = Path.GetFileName(path);
					StatusText.Text = $"已加载: {Path.GetFileName(path)}（ODF 文本模式）";
				_ = GenerateThumbnailsAsync();
				});
				LogRt($"ODF OK(local): {Path.GetFileName(path)}");
			}
			catch (Exception ex)
			{
				LogRt($"ODF FAIL(local): {Path.GetFileName(path)} {ex.GetType().Name}: {ex.Message}");
				Dispatcher.Invoke(() => StatusText.Text = $"ODF 解析失败: {ex.Message}");
			}
		});
		return;
	}

	// ═══ Excel 本地兜底：file-viewer 不可用时用 ClosedXML 渲染第一个工作表 ═══
	if (ext == ".xlsx" || ext == ".xlsm" || ext == ".xltx" || ext == ".xltm")
	{
		StatusText.Text = $"正在本地渲染表格: {Path.GetFileName(path)}...";
		_ = Task.Run(() =>
		{
			try
			{
				var html = BuildSpreadsheetHtml(path, ext);
				var tmpHtml = Path.Combine(Path.GetTempPath(), $"ld_xlsx_{Guid.NewGuid():N}.html");
				File.WriteAllText(tmpHtml, html, Encoding.UTF8);
				Dispatcher.Invoke(() =>
				{
					_totalPages = 1; // Excel 本地渲染为单页
					PreviewWebView.Visibility = Visibility.Visible;
					SafeNavigate(new Uri(tmpHtml).AbsoluteUri);
					PageInfo.Text = Path.GetFileName(path);
					StatusText.Text = $"本地预览: {Path.GetFileName(path)}";
				_ = GenerateThumbnailsAsync();
				});
				LogRt($"XLSX OK(local): {Path.GetFileName(path)}");
			}
			catch (Exception ex)
			{
				LogRt($"XLSX FAIL(local): {Path.GetFileName(path)} {ex.GetType().Name}: {ex.Message}");
				Dispatcher.Invoke(() => StatusText.Text = $"表格预览失败: {ex.Message}");
			}
		});
		return;
	}

 // ═══ Markdown / HTML / 代码文件：WebView2 渲染（marked.js + highlight.js） ═══
 if (ext == ".md" || ext == ".markdown" || ext == ".html" || ext == ".htm" ||
 ext == ".json" || ext == ".xml" || ext == ".yaml" || ext == ".yml" ||
 ext == ".csv" || ext == ".log" || ext == ".bpmn" || ext == ".java" || ext == ".php" || ext == ".py" ||
 ext == ".js" || ext == ".ts" || ext == ".jsx" || ext == ".tsx" || ext == ".css" ||
 ext == ".scss" || ext == ".less" || ext == ".go" || ext == ".rs" || ext == ".c" ||
 ext == ".cpp" || ext == ".h" || ext == ".hpp" || ext == ".cs" || ext == ".rb" ||
 ext == ".kt" || ext == ".swift" || ext == ".sh" || ext == ".bat" || ext == ".ps1" ||
 ext == ".ini" || ext == ".cfg" || ext == ".conf" || ext == ".toml" || ext == ".tex" || ext == ".mmd")
 {
 StatusText.Text = $"正在加载: {Path.GetFileName(path)}...";
 ShowFileLoadingOverlay("正在加载文件...", Path.GetFileName(path));
 _ = Task.Run(() =>
 {
 try
 {
 var content = File.ReadAllText(path, Encoding.UTF8);
 string html;
 if (ext == ".md" || ext == ".markdown")
 html = BuildMarkdownHtml(content);
 else if (ext == ".html" || ext == ".htm")
 html = content; // 直接渲染原始 HTML
 else
 html = BuildCodeHtml(content, ext);

 var tmpHtml = Path.Combine(Path.GetTempPath(), $"ld_text_{Guid.NewGuid():N}.html");
 File.WriteAllText(tmpHtml, html, Encoding.UTF8);

 Dispatcher.Invoke(() =>
 {
 _totalPages = 1; // MD/代码为单页滚动文档
 PreviewWebView.Visibility = Visibility.Visible;
 SafeNavigate(new Uri(tmpHtml).AbsoluteUri);
 PageInfo.Text = Path.GetFileName(path);
 StatusText.Text = $"已加载: {Path.GetFileName(path)}";
 HideFileLoadingOverlay();
 if (ext == ".md" || ext == ".markdown")
 _ = BuildMarkdownOutlineAsync();
 else
 _ = GenerateThumbnailsAsync();
 });
 }
 catch (Exception ex)
 {
 Dispatcher.Invoke(() => { StatusText.Text = $"加载失败: {ex.Message}"; HideFileLoadingOverlay(); });
 }
 });
 return;
 }

 if (ext == ".txt" || ext == ".xps")
 {
 StatusText.Text = $"正在本地渲染: {Path.GetFileName(path)}...";
 ShowFileLoadingOverlay("正在渲染文本...", Path.GetFileName(path));
		_ = Task.Run(async () =>
		{
			try
			{
				_previewSvc.Open(path);
				int pageCount = Math.Max(1, _previewSvc.TotalPages);
				var bmp = _previewSvc.RenderPage(0, LocalPdfRenderWidth, 150);
				await Dispatcher.InvokeAsync(() =>
				{
 _isLocalPdfMode = true;
					_localPdfPage = 0;
					_totalPages = pageCount;
					ImagePreview.Source = bmp;
					ImagePreview.Visibility = Visibility.Visible;
					_zoom = 1.0; _panX = 0; _panY = 0; ApplyImageTransform();
					PageInfo.Text = pageCount > 1
						? $"{Path.GetFileName(path)} (1/{pageCount})"
						: $"{Path.GetFileName(path)} (本地预览)";
					StatusText.Text = $"本地预览: {Path.GetFileName(path)}";
					HideFileLoadingOverlay();
				});
				// 生成缩略图侧边栏（与 PDF 一致），支持选中页
					await GenerateLocalPdfThumbnailsAsync(pageCount);
			}
			catch (Exception ex)
			{
				Dispatcher.Invoke(() => { StatusText.Text = $"本地渲染失败: {ex.Message}"; HideFileLoadingOverlay(); });
			}
		});
		return;
	}

 StatusText.Text = "此格式暂不支持预览";
 PageInfo.Text = Path.GetFileName(path);
}

// ═════════════ Markdown / 代码 → HTML 渲染 ═════════════
private static string BuildMarkdownHtml(string content)
{
var escaped = System.Web.HttpUtility.HtmlEncode(content);
return @"<!DOCTYPE html><html><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:'Fira Sans','Microsoft YaHei',sans-serif;background:#1E293B;color:#E2E8F0;padding:32px 48px;line-height:1.7;font-size:15px}
h1,h2,h3,h4,h5,h6{margin:24px 0 12px;font-weight:600;color:#F8FAFC;line-height:1.3}
h1{font-size:28px;border-bottom:1px solid #334155;padding-bottom:8px}
h2{font-size:22px;border-bottom:1px solid #334155;padding-bottom:6px}
h3{font-size:18px}
p{margin:12px 0}
a{color:#60A5FA;text-decoration:none}
a:hover{text-decoration:underline}
code{font-family:'Fira Code',Consolas,monospace;background:#334155;color:#93C5FD;padding:2px 6px;border-radius:4px;font-size:13px}
pre{background:#0F172A;border:1px solid #334155;border-radius:8px;padding:16px;overflow-x:auto;margin:16px 0}
pre code{background:none;color:#E2E8F0;padding:0;font-size:13px}
blockquote{border-left:4px solid #2563EB;margin:16px 0;padding:8px 16px;background:#334155/30;border-radius:0 6px 6px 0;color:#94A3B8}
table{border-collapse:collapse;width:100%;margin:16px 0}
th,td{border:1px solid #334155;padding:8px 12px;text-align:left}
th{background:#334155;font-weight:600}
tr:nth-child(even){background:#1E293B/50}
ul,ol{margin:12px 0;padding-left:28px}
li{margin:4px 0}
img{max-width:100%;border-radius:8px}
hr{border:none;border-top:1px solid #334155;margin:24px 0}
</style>
</head><body>
<div id='md'>" + escaped + @"</div>
<script src='https://cdn.jsdelivr.net/npm/marked/marked.min.js'></script>
<script>
(function(){
var raw=document.getElementById('md').textContent;
var html=marked.parse(raw,{breaks:true,gfm:true});
document.body.innerHTML=html;
})();
</script>
</body></html>";
}

private static string BuildCodeHtml(string content, string ext)
{
string lang = ext.TrimStart('.').ToLower();
var escaped = System.Web.HttpUtility.HtmlEncode(content);
return @"<!DOCTYPE html><html><head><meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:'Fira Code',Consolas,monospace;background:#0F172A;color:#E2E8F0;padding:24px 32px;line-height:1.6;font-size:13px;white-space:pre-wrap;word-break:break-all}
</style>
</head><body><pre><code class='language-" + lang + @"'>" + escaped + @"</code></pre>
<link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/highlight.js@11/styles/github-dark.min.css'>
<script src='https://cdn.jsdelivr.net/npm/highlight.js@11/lib/common.min.js'></script>
<script>
hljs.highlightAll();
</script>
</body></html>";
}

/// <summary>
/// Excel 本地兜底渲染：用 ClosedXML 把第一个工作表转成 HTML 表格。
/// 只在 file-viewer 组件不可用时启用（file-viewer 的 Excel 渲染更好）。
/// </summary>
private static string BuildSpreadsheetHtml(string path, string ext)
{
const int MaxRows = 500;
const int MaxCols = 40;
var sb = new StringBuilder();
sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
sb.Append("<style>");
sb.Append("*{margin:0;padding:0;box-sizing:border-box}");
sb.Append("body{font-family:'Microsoft YaHei',sans-serif;background:#fff;color:#222;padding:16px}");
sb.Append("h1{font-size:15px;margin:0 0 4px;color:#444}");
sb.Append(".sheets{margin:0 0 12px;font-size:12px;color:#1565C0}");
sb.Append("table{border-collapse:collapse;width:auto;font-size:13px}");
sb.Append("th,td{border:1px solid #D0D7DE;padding:4px 8px;white-space:nowrap;min-width:60px;text-align:left}");
sb.Append("th{background:#F3F4F6;font-weight:600;position:sticky;top:0}");
sb.Append("tr:nth-child(even) td{background:#FAFBFC}");
sb.Append("</style></head><body>");
using (var wb = new ClosedXML.Excel.XLWorkbook(path))
{
var ws = wb.Worksheets.FirstOrDefault();
if (ws == null) { sb.Append("<p>工作簿中没有工作表</p></body></html>"); return sb.ToString(); }
var sheetNames = string.Join(" / ", wb.Worksheets.Select(w => w.Name));
sb.Append($"<h1>{HtmlEsc(ws.Name)}</h1>");
sb.Append($"<div class='sheets'>工作表：{HtmlEsc(sheetNames)}</div>");
int maxRow = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 1, MaxRows);
int maxCol = Math.Min(ws.LastColumnUsed()?.ColumnNumber() ?? 1, MaxCols);
if (maxRow < 1) maxRow = 1;
if (maxCol < 1) maxCol = 1;
sb.Append("<table><thead><tr>");
for (int c = 1; c <= maxCol; c++)
sb.Append($"<th>{HtmlEsc(ws.Cell(1, c).GetFormattedString())}</th>");
sb.Append("</tr></thead><tbody>");
for (int r = 2; r <= maxRow; r++)
{
bool rowEmpty = true;
for (int c = 1; c <= maxCol; c++)
if (!string.IsNullOrWhiteSpace(ws.Cell(r, c).GetFormattedString())) { rowEmpty = false; break; }
if (rowEmpty) continue;
sb.Append("<tr>");
for (int c = 1; c <= maxCol; c++)
sb.Append($"<td>{HtmlEsc(ws.Cell(r, c).GetFormattedString())}</td>");
sb.Append("</tr>");
}
sb.Append("</tbody></table>");
}
sb.Append("</body></html>");
return sb.ToString();
}

private static string HtmlEsc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

/// <summary>
/// OFF 3D 模型 → OBJ 文本转换（OFF 是简单文本格式：顶点+面，转成 OBJ 交给 file-viewer 渲染）
/// 返回临时 .obj 文件路径；解析失败返回 null。
/// </summary>
private static string ConvertOffToObj(string offPath)
{
	try
	{
		var lines = File.ReadAllLines(offPath);
		int i = 0;
		// 跳过空行，识别可选的 OFF 头
		while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
		if (i >= lines.Length) return null;
		var first = lines[i].Trim();
		if (first.StartsWith("OFF", StringComparison.OrdinalIgnoreCase))
			i++;
		else if (first.StartsWith("STL", StringComparison.OrdinalIgnoreCase))
			return null; // STL 二进制/ASCII 不走此路径
		// 跳过 STL/COFF/NOFF 等前缀行（COFF/NOFF 带颜色，顶点数行在后面）
		while (i < lines.Length && lines[i].Trim().Length > 0 && !char.IsDigit(lines[i].Trim()[0])) i++;
		// 读取顶点/面/边计数
		string countsLine = null;
		while (i < lines.Length)
		{
			var t = lines[i].Trim();
			if (t.Length > 0) { countsLine = t; break; }
			i++;
		}
		if (countsLine == null) return null;
		var parts = countsLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2) return null;
		if (!int.TryParse(parts[0], out int nVerts) || !int.TryParse(parts[1], out int nFaces)) return null;
		if (nVerts <= 0 || nVerts > 5_000_000 || nFaces < 0 || nFaces > 10_000_000) return null;
		i++;
		var sb = new StringBuilder();
		sb.AppendLine("o off_model");
		int verts = 0, faces = 0;
		// 顶点
		while (i < lines.Length && verts < nVerts)
		{
			var t = lines[i].Trim();
			if (t.Length > 0)
			{
				var vp = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (vp.Length >= 3)
				{
					sb.Append($"v {vp[0]} {vp[1]} {vp[2]}\r\n");
					verts++;
				}
			}
			i++;
		}
		// 面（OFF 索引从 0 开始，OBJ 从 1 开始；面行可带顶点颜色，只取索引部分）
		while (i < lines.Length && faces < nFaces)
		{
			var t = lines[i].Trim();
			if (t.Length > 0)
			{
				var fp = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (fp.Length >= 1 && int.TryParse(fp[0], out int cnt) && cnt >= 3 && fp.Length >= cnt + 1)
				{
					sb.Append("f");
					for (int k = 1; k <= cnt; k++)
					{
						if (int.TryParse(fp[k], out int idx))
							sb.Append($" {idx + 1}");
					}
					sb.AppendLine();
					faces++;
				}
			}
			i++;
		}
		if (verts == 0) return null;
		var objPath = Path.Combine(Path.GetTempPath(), $"ld_off_{Guid.NewGuid():N}.obj");
		File.WriteAllText(objPath, sb.ToString(), Encoding.UTF8);
		return objPath;
	}
	catch
	{
		return null;
	}
}

/// <summary>
/// Visio vsdx → HTML：vsdx 是 ZIP 包，页面在 visio/pages/page*.xml，
/// 提取每个 Shape 的名称与文本按页展示（纯文本模式，图形以文本结构呈现）
/// </summary>
private static string BuildVisioVsdxHtml(string path)
{
	var sb = new StringBuilder();
	sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
	sb.Append("<style>*{margin:0;padding:0;box-sizing:border-box}body{font-family:'Microsoft YaHei',sans-serif;background:#fff;color:#222;padding:24px}h1{font-size:18px;margin:0 0 4px;color:#333}h2{font-size:14px;margin:20px 0 8px;color:#1565C0;border-bottom:1px solid #E0E0E0;padding-bottom:4px}.shape{margin:8px 0 8px 12px;padding:8px 12px;background:#FAFAFA;border:1px solid #EEE;border-radius:6px}.shape b{color:#555;font-size:12px;display:block;margin-bottom:4px}.shape span{white-space:pre-wrap;line-height:1.6}.empty{color:#999;font-style:italic}</style></head><body>");
	using (var zip = ZipFile.OpenRead(path))
	{
		var pageEntries = zip.Entries
			.Where(e => e.FullName.StartsWith("visio/pages/", StringComparison.OrdinalIgnoreCase)
				&& e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
			.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (pageEntries.Count == 0)
		{
			sb.Append("<p class='empty'>未找到 Visio 页面（文件可能损坏或不是 vsdx）</p></body></html>");
			return sb.ToString();
		}
		foreach (var entry in pageEntries)
		{
			XDocument doc;
			using (var s = entry.Open())
			using (var reader = new StreamReader(s, Encoding.UTF8, true))
				doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
			var pageName = doc.Descendants().FirstOrDefault(d => d.Name.LocalName == "Page")?.Attribute("Name")?.Value
				?? Path.GetFileNameWithoutExtension(entry.Name);
			sb.Append($"<h2>📄 {HtmlEsc(pageName)}</h2>");
			int shown = 0;
			foreach (var shape in doc.Descendants().Where(d => d.Name.LocalName == "Shape"))
			{
				var text = string.Concat(shape.Descendants().Where(t => t.Name.LocalName == "Text").Select(t => t.Value));
				var name = shape.Attribute("Name")?.Value ?? shape.Attribute("NameU")?.Value ?? "";
				text = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
				if (text.Length == 0 && name.Length == 0) continue;
				sb.Append("<div class='shape'>");
				if (name.Length > 0) sb.Append($"<b>{HtmlEsc(name)}</b>");
				sb.Append($"<span>{HtmlEsc(text.Length > 0 ? text : "（无文本）")}</span></div>");
				shown++;
			}
			if (shown == 0) sb.Append("<div class='empty'>（本页无文本内容）</div>");
		}
	}
	sb.Append("</body></html>");
	return sb.ToString();
}

/// <summary>
/// ODF 文档 → HTML（文本模式）：odt/ott/fodt 取正文段落与标题，ods/ots/fods 取表格，odp/otp 取各页文本
/// </summary>
private static string BuildOdfHtml(string path, string ext)
{
	bool isTable = ext == ".ods" || ext == ".ots" || ext == ".fods";
	bool isPres = ext == ".odp" || ext == ".otp";
	string contentXml;
	if (ext == ".fodt" || ext == ".fods")
	{
		contentXml = File.ReadAllText(path, Encoding.UTF8);
	}
	else
	{
		using (var zip = ZipFile.OpenRead(path))
		{
			var entry = zip.GetEntry("content.xml");
			if (entry == null) throw new Exception("ODF 包中缺少 content.xml");
			using (var s = entry.Open())
			using (var reader = new StreamReader(s, Encoding.UTF8, true))
				contentXml = reader.ReadToEnd();
		}
	}
	var doc = XDocument.Parse(contentXml);
	XNamespace textNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
	XNamespace tableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
	XNamespace drawNs = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
	var sb = new StringBuilder();
	sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
	sb.Append("<style>*{margin:0;padding:0;box-sizing:border-box}body{font-family:'Microsoft YaHei',sans-serif;background:#fff;color:#222;padding:24px;line-height:1.7}h1{font-size:18px;margin:0 0 12px;color:#333}h2{font-size:15px;color:#1565C0;margin:20px 0 8px;border-bottom:1px solid #E0E0E0;padding-bottom:4px}p{margin:6px 0}.slide{margin:0 0 20px;padding:0 0 12px;border-bottom:1px solid #EEE}table{border-collapse:collapse;font-size:13px}th,td{border:1px solid #D0D7DE;padding:4px 8px;white-space:nowrap;text-align:left}th{background:#F3F4F6}tr:nth-child(even) td{background:#FAFBFC}.empty{color:#999;font-style:italic}</style></head><body>");

	if (isTable)
	{
		var tables = doc.Descendants().Where(d => d.Name == tableNs + "table").ToList();
		if (tables.Count == 0)
		{
			sb.Append("<p class='empty'>未找到表格内容</p></body></html>");
			return sb.ToString();
		}
		foreach (var table in tables.Take(1))
		{
			var name = table.Attribute(tableNs + "name")?.Value ?? "工作表";
			sb.Append($"<h1>{HtmlEsc(name)}</h1>");
			sb.Append("<table>");
			foreach (var row in table.Elements().Where(e => e.Name == tableNs + "table-row").Take(500))
			{
				sb.Append("<tr>");
				foreach (var cell in row.Elements().Where(e => e.Name == tableNs + "table-cell").Take(40))
				{
					var cellText = string.Concat(cell.Descendants().Where(d => d.Name == textNs + "p").Select(p => p.Value));
					sb.Append($"<td>{HtmlEsc(cellText)}</td>");
				}
				sb.Append("</tr>");
			}
			sb.Append("</table>");
		}
	}
	else
	{
		var slides = doc.Descendants(drawNs + "page").ToList();
		if (isPres && slides.Count > 0)
		{
			int i = 0;
			foreach (var slide in slides)
			{
				i++;
				var name = slide.Attribute(drawNs + "name")?.Value ?? $"第 {i} 页";
				sb.Append($"<div class='slide'><h2>📽 {HtmlEsc(name)}</h2>");
				foreach (var el in slide.Descendants().Where(d => d.Name == textNs + "p" || d.Name == textNs + "h"))
				{
					var v = el.Value;
					if (string.IsNullOrWhiteSpace(v)) continue;
					sb.Append(el.Name == textNs + "h" ? $"<h3>{HtmlEsc(v)}</h3>" : $"<p>{HtmlEsc(v)}</p>");
				}
				sb.Append("</div>");
			}
		}
		else
		{
			int paraCount = 0;
			foreach (var el in doc.Descendants().Where(d => d.Name == textNs + "p" || d.Name == textNs + "h"))
			{
				var v = el.Value;
				if (string.IsNullOrWhiteSpace(v)) continue;
				sb.Append(el.Name == textNs + "h" ? $"<h2>{HtmlEsc(v)}</h2>" : $"<p>{HtmlEsc(v)}</p>");
				paraCount++;
			}
			if (paraCount == 0) sb.Append("<p class='empty'>未找到正文内容</p>");
		}
	}
	sb.Append("</body></html>");
	return sb.ToString();
}

/// <summary>
/// WPS 二进制（wps/dps/et/ett/wpt）→ Office 兼容格式重映射：
/// WPS 的 .wps/.dps/.et/.ett/.wpt 实际是 OLE 复合文档，结构与 Word/PPT/Excel 二进制一致，
/// 按魔数重命名后 file-viewer 的二进制渲染器可直接解析；RTF/HTML 魔数也顺手识别。
/// 返回用于预览的文件路径（可能是临时副本）；无匹配时原样返回。
/// </summary>
private static string RemapWpsForPreview(string path, string ext)
{
	try
	{
		byte[] head = new byte[8];
		using (var fs = File.OpenRead(path))
		{
			int n = fs.Read(head, 0, 8);
			if (n < 4) return path;
		}
		// OLE 复合文档魔数 D0 CF 11 E0 A1 B1 1A E1
		bool isOle = head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0 &&
					 head[4] == 0xA1 && head[5] == 0xB1 && head[6] == 0x1A && head[7] == 0xE1;
		// RTF 魔数 {\rtf
		bool isRtf = head[0] == '{' && head[1] == '\\' && head[2] == 'r' && head[3] == 't' && head[4] == 'f';
		// ZIP 魔数 PK\x03\x04（xlam/fcstd 是 ZIP 包）
		bool isZip = head[0] == 0x50 && head[1] == 0x4B && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07);
		string newExt = null;
		switch (ext)
		{
			case ".wps": newExt = isOle ? ".doc" : isRtf ? ".rtf" : null; break;
			case ".wpt": newExt = isOle ? ".dot" : null; break;
			case ".dps": newExt = isOle ? ".ppt" : null; break;
			case ".et": newExt = isOle ? ".xls" : null; break;
			case ".ett": newExt = isOle ? ".xlt" : null; break;
			// Excel 加载项：xla（OLE）按 xls 渲染，xlam（ZIP）按 xlsx 渲染
			case ".xla": newExt = isOle ? ".xls" : null; break;
			case ".xlam": newExt = isZip ? ".xlsx" : null; break;
			// FreeCAD 工程文件本质是 ZIP 包（含 brep/step 等），按压缩包浏览
			case ".fcstd": newExt = isZip ? ".zip" : null; break;
		}
		if (newExt == null) return path;
		var tmp = Path.Combine(Path.GetTempPath(), $"ld_wps_{Guid.NewGuid():N}{newExt}");
		File.Copy(path, tmp, true);
		return tmp;
	}
	catch
	{
		return path;
	}
}

        /// <summary>检测 MPEG-TS 视频（188 字节包同步位 0x47）——与 TypeScript 源码 .ts 区分</summary>
        private static bool IsMpegTsBinary(string path)
        {
            try
            {
                byte[] head = new byte[752];
                using (var fs = File.OpenRead(path))
                {
                    int n = fs.Read(head, 0, head.Length);
                    if (n < 188) return false;
                    // 至少前 4 个包同步字节都必须是 0x47
                    for (int i = 0; i < 4 && i * 188 + 1 < n; i++)
                        if (head[i * 188] != 0x47) return false;
                    return true;
                }
            }
            catch { return false; }
        }

/// <summary>解码 TGA 图像（支持 24/32 位无压缩与 RLE 压缩；WPF/Chromium 均不支持 TGA，需自解码）</summary>
private static System.Windows.Media.Imaging.BitmapSource DecodeTga(string path)
{
	byte[] data = File.ReadAllBytes(path);
	if (data.Length < 18) throw new InvalidDataException("TGA 文件头不完整");
	int type = data[2];
	int w = data[12] | (data[13] << 8);
	int h = data[14] | (data[15] << 8);
	int depth = data[16];
	bool topDown = (data[17] & 0x20) != 0;
	if (w <= 0 || h <= 0 || w > 16384 || h > 16384) throw new InvalidDataException("TGA 尺寸无效");
	if (type != 2 && type != 10) throw new InvalidDataException($"不支持的 TGA 类型 {type}（仅支持无压缩/RLE 真彩色）");
	if (depth != 24 && depth != 32) throw new InvalidDataException($"不支持的 TGA 位深 {depth}");

	int bytesPerPixel = depth / 8;
	int stride = w * bytesPerPixel;
	byte[] raw = new byte[stride * h];
	int pos = 18; // 类型 2/10 无调色板/ID，数据从第 18 字节起
	if (type == 2)
	{
		int need = stride * h;
		if (data.Length - pos < need) throw new InvalidDataException("TGA 数据不足");
		Buffer.BlockCopy(data, pos, raw, 0, need);
	}
	else
	{
		int idx = 0;
		while (idx < raw.Length)
		{
			if (pos >= data.Length) throw new InvalidDataException("TGA RLE 数据不足");
			byte packet = data[pos++];
			int count = (packet & 0x7F) + 1;
			if ((packet & 0x80) != 0)
			{
				if (pos + bytesPerPixel > data.Length) throw new InvalidDataException("TGA RLE 像素不足");
				byte[] px = new byte[bytesPerPixel];
				Buffer.BlockCopy(data, pos, px, 0, bytesPerPixel);
				pos += bytesPerPixel;
				for (int i = 0; i < count && idx < raw.Length; i++)
				{
					Buffer.BlockCopy(px, 0, raw, idx, bytesPerPixel);
					idx += bytesPerPixel;
				}
			}
			else
			{
				int need = count * bytesPerPixel;
				if (pos + need > data.Length) throw new InvalidDataException("TGA RLE 原始段不足");
				Buffer.BlockCopy(data, pos, raw, idx, need);
				pos += need;
				idx += need;
			}
		}
	}

	// BGR(A) → BGRA 逐像素转换
	byte[] bgra = new byte[w * h * 4];
	for (int i = 0; i < w * h; i++)
	{
		int s = i * bytesPerPixel;
		int d = i * 4;
		bgra[d + 2] = raw[s];       // R
		bgra[d + 1] = raw[s + 1];   // G
		bgra[d] = raw[s + 2];       // B
		bgra[d + 3] = depth == 32 ? raw[s + 3] : (byte)255;
	}
	// TGA 默认自下而上存储，按需翻转行序
	if (!topDown)
	{
		byte[] flipped = new byte[w * h * 4];
		int row = w * 4;
		for (int y = 0; y < h; y++)
			Buffer.BlockCopy(bgra, (h - 1 - y) * row, flipped, y * row, row);
		bgra = flipped;
	}
	var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96,
		System.Windows.Media.PixelFormats.Bgra32, null, bgra, w * 4);
	bmp.Freeze();
	return bmp;
}

/// <summary>本地PDF：生成左侧缩略图并默认选中第1页</summary>
private async Task GenerateLocalPdfThumbnailsAsync(int pageCount)
{
 try
 {
 // 先填充占位项（侧栏宽度仍为 0 且隐藏，不占空间、不渲染，避免左上角闪现）
 await Dispatcher.InvokeAsync(() =>
 {
 PageThumbItems.Clear();
 CollapseSidebar();
 for (int i = 0; i < pageCount; i++)
 PageThumbItems.Add(new PageThumbItem { PageIndex = i, Label = (i + 1).ToString(), Thumbnail = null });
 if (PageThumbItems.Count > 0) ThumbList.SelectedIndex = 0;
 });

 // 并行渲染缩略图
 var tasks = new Task<System.Windows.Media.Imaging.BitmapSource>[pageCount];
 for (int i = 0; i < pageCount; i++)
 {
 var idx = i;
 tasks[idx] = Task.Run(() => _previewSvc.RenderPage(idx, 140, 72));
 }
 // 等待 ListBox 完成首次布局
 await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
 for (int i = 0; i < pageCount; i++)
 {
 var idx = i;
 try
 {
 var thumb = await tasks[idx];
 await Dispatcher.InvokeAsync(() =>
 {
 if (idx < PageThumbItems.Count) PageThumbItems[idx].Thumbnail = thumb;
 });
 }
 catch { }
 }
 // 缩略图全部就绪后，一次性展开侧栏（避免左上角闪现）
 await Dispatcher.InvokeAsync(() =>
 {
 ShowSidebarTab();
 ExpandSidebar();
 });
 }
 catch { }
}

/// <summary>本地PDF：渲染并显示指定页（0-based），自动整页适应视口</summary>
private void DisplayLocalPdfPage(int pageIndex, bool fit = true)
{
	if (!_isLocalPdfMode) return;
	if (pageIndex < 0) pageIndex = 0;
	if (_totalPages > 0 && pageIndex >= _totalPages) pageIndex = _totalPages - 1;
	_localPdfPage = pageIndex;
	_currentPage = pageIndex;

	var name = Path.GetFileName(_currentFilePath);
	StatusText.Text = $"正在渲染第{pageIndex + 1}页...";
	_ = Task.Run(() =>
	{
		System.Windows.Media.Imaging.BitmapSource bmp = null;
		try { bmp = _previewSvc.RenderPage(pageIndex, LocalPdfRenderWidth, 150); }
		catch { }
		Dispatcher.Invoke(() =>
		{
			if (bmp == null) { StatusText.Text = $"第{pageIndex + 1}页渲染失败"; return; }
			// 应用该页在缩略图里设置的旋转角度
			try
			{
				int rot = (pageIndex < PageThumbItems.Count) ? PageThumbItems[pageIndex].Rotation : 0;
				if (rot % 360 != 0)
				{
					var tb = new System.Windows.Media.Imaging.TransformedBitmap(
						bmp, new System.Windows.Media.RotateTransform(rot));
					tb.Freeze();
					bmp = tb;
				}
			}
			catch { }
			ImagePreview.Source = bmp;
			ImagePreview.Visibility = Visibility.Visible;
			PreviewWebView.Visibility = Visibility.Collapsed;
			if (fit) FitImageToViewport();
			else ApplyImageTransform();
			PageInfo.Text = $"{name} ({pageIndex + 1}/{_totalPages})";
			StatusText.Text = $"第{pageIndex + 1}/{_totalPages}页";
		});
	});
}

private void PopulateCadLayoutBar()
 {
 CadLayoutPanel.Children.Clear();
 var names = _previewSvc?.PageNames;
 if (names == null || names.Count <= 1) { CadLayoutBar.Visibility = Visibility.Collapsed; return; }
 CadLayoutBar.Visibility = Visibility.Visible;
 for (int i = 0; i < names.Count; i++)
 {
 var idx = i;
 var name = names[i];
 var btn = new Button
 {
 Content = name,
 Height = 26,
 Padding = new Thickness(12, 3, 12, 3),
 Margin = new Thickness(1, 0, 1, 0),
 FontSize = 12,
 Cursor = Cursors.Hand,
 BorderThickness = new Thickness(0),
 Tag = idx,
 };
 if (idx == _currentPage)
 {
 btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB));
 btn.Foreground = System.Windows.Media.Brushes.White;
 btn.FontWeight = FontWeights.Bold;
 }
 else
 {
 btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x41, 0x55));
 btn.Foreground = System.Windows.Media.Brushes.White;
 }
 btn.Click += (s, e) =>
 {
 _currentPage = idx;
 DisplayCadPage(idx);
 PopulateCadLayoutBar();
 PageInfo.Text = $"{Path.GetFileName(_currentFilePath)} — {name} ({idx + 1}/{_totalPages})";
 };
 CadLayoutPanel.Children.Add(btn);
 }
 }

 /// <summary>CAD 渲染请求令牌——用户快速切换图纸时丢弃过期结果</summary>
 private int _cadRenderToken = 0;

 /// <summary>CAD 渲染目标长边像素。5000px 对 A1 图纸约等于每毫米 6 像素，放大后仍清晰。</summary>
 private const int CadRenderLongSidePx = 5000;

 /// <summary>用矢量 SVG 渲染指定 CAD 页面并显示在 WebView2 中</summary>
 private void DisplayCadSvgPage(int pageIndex)
{
 _ = Task.Run(() =>
 {
  try
  {
   var entities = _previewSvc.GetCadEntities(pageIndex);
   if (entities == null || entities.Count == 0)
   {
   var msg = "该布局没有可显示的内容";
   var tipHtml = $"<html><body style='background:#2A2A2E;color:#9aa;font-family:SimSun;font-size:16px;display:flex;align-items:center;justify-content:center;height:100%'>" + msg + "</body></html>";
   var url = "data:text/html;charset=utf-8," + Uri.EscapeDataString(tipHtml);
   Dispatcher.BeginInvoke(() => SafeNavigate(url));
   return;
   }
   var svgRes = Services.CadSvgRenderer.Render(entities);
   if (!svgRes.Success) return;

   var html = Services.CadSvgRenderer.WrapHtml(svgRes.Svg, System.IO.Path.GetFileName(_currentFilePath ?? "CAD"));
   var tmpHtml = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ld_cad_" + Guid.NewGuid().ToString("N") + ".html");
   System.IO.File.WriteAllText(tmpHtml, html, System.Text.Encoding.UTF8);

   Dispatcher.BeginInvoke(new Action(() =>
   {
    SafeNavigate(new Uri(tmpHtml).AbsoluteUri);
    PageInfo.Text = System.IO.Path.GetFileName(_currentFilePath ?? "") + " (" + svgRes.PrimitiveCount.ToString() + " 图元)";
    StatusText.Text = "已加载: " + System.IO.Path.GetFileName(_currentFilePath ?? "");
   }));
  }
  catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine("[DisplayCadSvgPage] " + ex.Message); }
 });
}
 private void DisplayCadPage(int pageIndex)
 {
 // CAD 全走 WPF 矢量渲染（RenderVector），不用 SVG/WebView2/cad-viewer
 _ = DisplayCadVectorPageAsync(pageIndex);
 }

 /// <summary>CAD 矢量渲染：后台线程构建 WPF Canvas（DrawingVisual），缩放不失真。</summary>
 private int _cadVectorToken = 0;
 private async Task DisplayCadVectorPageAsync(int pageIndex, double zoom = 1.0, bool rebake = false)
 {
 int token = ++_cadVectorToken;
 double targetZoom = zoom;
 double targetPanX = _panX;
 double targetPanY = _panY;
 // 渲染器初始视图的自动放大/定位结果（后台线程写入，await 后读取）
 double lastInitZoom = 1.0, lastInitPanX = 0, lastInitPanY = 0;

 if (!rebake)
 {
 // 初始加载：清空画布并显示渲染中提示
 CadHostCanvas.Children.Clear();
 CadHostCanvas.Width = double.NaN;
 CadHostCanvas.Height = double.NaN;
 var busy = new TextBlock
 {
 Text = "正在渲染图纸…",
 Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB8)),
 FontSize = 15,
 };
 Canvas.SetLeft(busy, 24);
 Canvas.SetTop(busy, 24);
 CadHostCanvas.Children.Add(busy);
 }

 // WPF UI 元素必须在 STA 线程创建，线程池(MTA)会抛"调用线程必须为 STA"。
 // 渲染线程产出 Freeze 的 DrawingImage（可跨线程），UI 线程再包装 Canvas。
 // 缩放重烘焙保留旧图直到新图就绪，避免缩放时画面闪烁/空白。
 System.Windows.Media.DrawingImage vecDrawing = null;
 System.Windows.Media.Imaging.BitmapSource vecBitmap = null;
 double vecW = 0, vecH = 0;
 // 视口尺寸必须在 UI 线程读取（DispatcherObject 跨线程非法）；先强制布局避免取到 0
 CadScrollViewer.UpdateLayout();
 double viewW = Math.Max(200, CadScrollViewer.ActualWidth);
 double viewH = Math.Max(200, CadScrollViewer.ActualHeight);
 // 全局字体覆盖：CAD 字体对话框选定的大字体/主字体在实体样式缺失时兜底生效
 Services.CadWpfRenderer.OverrideShxFont = _cadShxFontName;
 Services.CadWpfRenderer.OverrideBigShxFont = _cadBigShxFontName;
 Services.CadWpfRenderer.OverrideUseBigFont = _cadUseBigFont;

 var vecTcs = new TaskCompletionSource<bool>();
 var vecThread = new Thread(() =>
 {
 try
 {                var ents = _previewSvc?.GetCadEntities(pageIndex);
                // cacheKey：同一文件+布局的模型几何复用（重烘焙只重组装，秒级→0.1秒级）
                // 必须包含字体覆盖设置——SHX 字体在模型构建时解析并烘焙进几何，
                // 字体改了而 key 不变会命中旧缓存导致"改了不生效"。
                var cacheKey = string.IsNullOrEmpty(_currentFilePath) ? null :
                    $"{_currentFilePath}|{pageIndex}|{_cadShxFontName}|{_cadBigShxFontName}|{_cadUseBigFont}";
                // 视口渲染：画布恒为视口大小，窗口随 缩放×平移 移动（AutoCAD 式，拖动永远顺滑）
                var res = ents == null ? null : Services.CadWpfRenderer.RenderViewport(ents, viewW, viewH, targetZoom, targetPanX, targetPanY, cacheKey, !_cadFitToFull);
 if (res != null)
 {
 vecDrawing = res.Image;
 vecW = res.Width;
 vecH = res.Height;
 lastInitZoom = res.InitZoom;
 lastInitPanX = res.InitPanX;
 lastInitPanY = res.InitPanY;
 // 后台线程预先栅格化为位图：换图时一次到位，不在 UI 线程逐帧重绘矢量（消除闪烁）
 try
 {
 if (vecDrawing.Drawing != null)
 {
 var dv = new System.Windows.Media.DrawingVisual();
 using (var dc = dv.RenderOpen())
 dc.DrawDrawing(vecDrawing.Drawing);
 var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
 (int)Math.Max(1, vecW), (int)Math.Max(1, vecH), 96, 96,
 System.Windows.Media.PixelFormats.Pbgra32);
 rtb.Render(dv);
 rtb.Freeze();
 vecBitmap = rtb;
 }
 }
 catch { }
 }
 vecTcs.TrySetResult(true);
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"[DisplayCadVectorPage] 渲染异常: {ex}");
 vecTcs.TrySetResult(false);
 }
 });
 vecThread.SetApartmentState(ApartmentState.STA);
 vecThread.IsBackground = true;
 vecThread.Start();
 await vecTcs.Task;
 LogRt($"CAD VECTOR: page={pageIndex} zoom={targetZoom:F2} ok={vecDrawing != null} w={vecW:F0} h={vecH:F0}");

 if (token != _cadVectorToken) return;
 // 重烘焙期间用户又缩放/平移了 → 丢弃本次结果，新的调度会再渲染
 if (rebake && (Math.Abs(_zoom - targetZoom) > 1e-9
                 || Math.Abs(_panX - targetPanX) > 0.5
                 || Math.Abs(_panY - targetPanY) > 0.5)) return;

 if (vecDrawing == null)
 {
 // 重烘焙失败：保留旧图不动；初始加载失败才显示错误
 if (rebake) return;
 CadHostCanvas.Children.Clear();
 var emptyEnts = _previewSvc?.GetCadEntities(pageIndex);
 var msg = (emptyEnts == null || emptyEnts.Count == 0) ? "该布局没有可显示的内容" : "该图纸无法渲染（文件可能已损坏）";
 var err = new TextBlock
 {
 Text = msg,
 Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x70, 0x70)),
 FontSize = 14,
 };
 Canvas.SetLeft(err, 24);
 Canvas.SetTop(err, 24);
 CadHostCanvas.Children.Add(err);
 return;
 }

 var newSource = (System.Windows.Media.ImageSource)vecBitmap ?? vecDrawing;

 if (rebake && _cadImg != null)
 {
 // ── 重烘焙：原位换图（不重建画布、无空白帧/闪烁），位图已在后台线程就绪 ──
 _cadBakeZoom = targetZoom;
 _cadBakePanX = targetPanX;
 _cadBakePanY = targetPanY;
 ResetCadHostTransform();
 _cadImg.Source = newSource;
 _cadImg.Width = vecW;
 _cadImg.Height = vecH;
 _cadImg.Stretch = Stretch.None;
 if (_cadImg.CacheMode != null) _cadImg.CacheMode = null;
 if (Math.Abs(CadHostCanvas.Width - vecW) > 0.5) CadHostCanvas.Width = vecW;
 if (Math.Abs(CadHostCanvas.Height - vecH) > 0.5) CadHostCanvas.Height = vecH;
 if (FileLoadingOverlay.Visibility == Visibility.Visible)
 FileLoadingOverlay.Visibility = Visibility.Collapsed;
 return;
 }

 CadHostCanvas.Children.Clear();

 if (rebake)
 {
 // 重烘焙（初始渲染被作废后的兜底）：图像已含当前窗口，烘焙状态归位
 _cadBakeZoom = targetZoom;
 _cadBakePanX = targetPanX;
 _cadBakePanY = targetPanY;
 }
 else
 {
 // ── 初始加载：重置缩放/平移，烘焙状态归位 ──
 // 渲染器初始视图会自动放大到典型文字可读并定位到文字密集区（InitZoom/InitPan），
 // 此处同步到缩放/平移状态，保证用户后续缩放/平移时画面连续不跳变。
 _zoom = lastInitZoom > 1.0 ? lastInitZoom : 1.0;
 _panX = lastInitZoom > 1.0 ? lastInitPanX : 0;
 _panY = lastInitZoom > 1.0 ? lastInitPanY : 0;
 _cadBakeZoom = _zoom;
 _cadBakePanX = _panX; _cadBakePanY = _panY;
 }
 ResetCadHostTransform();

 var host = new Canvas
 {
 Width = vecW,
 Height = vecH,
 Background = Brushes.Transparent,
 };    var img = new Image
    {
        Source = newSource,
        Width = vecW,
        Height = vecH,
        Stretch = Stretch.None,
    };
    _cadImg = img;
    _cadVectorSource = vecDrawing;
    // 视口渲染：画布恒为视口尺寸；位图已在后台线程栅格化，换图一次到位，无需 BitmapCache
    Canvas.SetLeft(img, 0);
    Canvas.SetTop(img, 0);
    host.Children.Add(img);

 var group = new TransformGroup();
 group.Children.Add(_cadScale);
 group.Children.Add(_cadTranslate);
 host.RenderTransform = group;

 CadHostCanvas.Width = vecW;
 CadHostCanvas.Height = vecH;
 CadHostCanvas.Children.Add(host);

 if (!rebake)
 {
 // 初始加载完成：智能初始视图的 _zoom/_pan/_cadBakeZoom 已在上面同步，host 变换已归位，
 // 图像本身已含初始窗口内容。这里不能再调 CadFitToWindow —— 它会重置 _zoom=1 而不更新
 // _cadBakeZoom，导致 k=_zoom/_cadBakeZoom 错误、画面缩小数十倍、拖动 transform 全乱。
 FileLoadingOverlay.Visibility = Visibility.Collapsed;
 StatusText.Text = $"已加载: {Path.GetFileName(_currentFilePath ?? "")} ({_totalPages}个空间)";
 }
 else
 {
 // 若初始加载期间用户就缩放了（首次重烘焙抢先完成），补上加载提示关闭
 if (FileLoadingOverlay.Visibility == Visibility.Visible)
 FileLoadingOverlay.Visibility = Visibility.Collapsed;
 }
 }

 /// <summary>
 /// CAD 缩放后防抖重烘焙：把当前 _zoom 烘焙进矢量图（线宽=1屏幕像素），
 /// 元素变换归位，保证缩放后线条始终细线不重叠。
 /// 防抖 300ms，期间用户继续缩放会取消并重新计时；
 /// 正在进行的旧渲染由 _cadVectorToken 作废。
 /// </summary>
 private void ScheduleCadReBake()
 {
 try
 {
 if (!_isCadMode || _currentFilePath == null) return;
 _cadReBakeCts?.Cancel();
 _cadVectorToken++;   // 作废正在进行的重烘焙（防抖取消时旧渲染结果丢弃）
 var cts = new CancellationTokenSource();
 _cadReBakeCts = cts;
 _ = ScheduleCadReBakeCoreAsync(cts);
 }
 catch { }
 }

 private async Task ScheduleCadReBakeCoreAsync(CancellationTokenSource cts)
 {
 try
 {
 await Task.Delay(300, cts.Token);
 }
 catch (TaskCanceledException) { return; }
 if (cts.IsCancellationRequested) return;
 await Dispatcher.InvokeAsync(() =>
 {
 if (cts.IsCancellationRequested || !_isCadMode || _currentFilePath == null) return;
 _ = DisplayCadVectorPageAsync(_currentPage, _zoom, true);
 });
 }

 // ── cad-viewer 网页查看器（mlightcad/cad-viewer）集成 ──
 /// <summary>用 cad-viewer 网页查看器渲染当前 DWG/DXF，成功发起导航返回 true。</summary>
 private bool TryDisplayWebCadViewer(int pageIndex)
 {
 if (!_cadHostMapped) TryMapCadViewerHost();
 if (!_cadHostMapped) return false;
 if (string.IsNullOrEmpty(_currentFilePath)) return false;
 var ext = Path.GetExtension(_currentFilePath).ToLower();
 if (ext != ".dwg" && ext != ".dxf") return false;

 _pendingCadFilePath = _currentFilePath;
 SafeNavigate(CadViewerHost.Url);
 PageInfo.Text = System.IO.Path.GetFileName(_currentFilePath ?? "") + " — 正在加载查看器…";
 StatusText.Text = "已加载: " + System.IO.Path.GetFileName(_currentFilePath ?? "");
 return true;
 }

 /// <summary>将本地 DWG/DXF 以 base64 经 WebMessage 推送给查看器页面</summary>
 private void PostCadFileToViewer(string path)
 {
 try
 {
 var bytes = File.ReadAllBytes(path);
 var b64 = Convert.ToBase64String(bytes);
 var payload = new { type = "loadCadFile", name = System.IO.Path.GetFileName(path), data = b64 };
 var json = System.Text.Json.JsonSerializer.Serialize(payload);
 PreviewWebView.CoreWebView2.PostWebMessageAsJson(json);
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"[CadViewer] 推送文件失败: {ex.Message}");
 }
 }

 /// <summary>建立 WebView2 虚拟主机映射，将已构建的 cad-viewer 静态站点映射为 https://cadviewer.local/</summary>
 private void TryMapCadViewerHost()
 {
 if (_cadHostMapped) return;
 var dir = CadViewerHost.ResolveViewerDir();
 if (string.IsNullOrEmpty(dir)) return;
 try
 {
 PreviewWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
 CadViewerHost.VirtualHost, dir, CoreWebView2HostResourceAccessKind.Allow);
 _cadHostMapped = true;
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"[CadViewer] 虚拟主机映射失败: {ex.Message}");
 }
 }

 /// <summary>
 /// CAD 页面显示（异步）。
 /// 渲染在后台线程完成 —— 大型建筑/市政图纸动辄数万实体，
 /// 若在 UI 线程绘制会直接冻结整个界面（旧版卡死的根因）。
 /// </summary>
 private async Task DisplayCadPageAsync(int pageIndex)
 {
 int token = ++_cadRenderToken;

 // ── 渲染中提示 ──
 CadHostCanvas.Children.Clear();
 CadHostCanvas.Width = double.NaN;
 CadHostCanvas.Height = double.NaN;
 var busy = new TextBlock
 {
 Text = "正在渲染图纸…",
 Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB8)),
 FontSize = 15,
 };
 Canvas.SetLeft(busy, 24);
 Canvas.SetTop(busy, 24);
 CadHostCanvas.Children.Add(busy);

 BitmapSource bmp = null;
 try
 {
 bmp = await Task.Run(() => _previewSvc?.RenderCadSkia(pageIndex, CadRenderLongSidePx, true));
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"[DisplayCadPage] 渲染异常: {ex}");
 }

 // 期间用户切换了图纸/页面，丢弃本次结果
 if (token != _cadRenderToken) return;

 CadHostCanvas.Children.Clear();

 if (bmp == null)
 {
 var emptyEnts = _previewSvc?.GetCadEntities(pageIndex);
 var errMsg = (emptyEnts == null || emptyEnts.Count == 0) ? "该布局没有可显示的内容" : "该图纸无法渲染（文件可能已损坏）";
 var err = new TextBlock
 {
 Text = errMsg,
 Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0x70, 0x70)),
 FontSize = 14,
 };
 Canvas.SetLeft(err, 24);
 Canvas.SetTop(err, 24);
 CadHostCanvas.Children.Add(err);
 return;
 }

 // ── 重置缩放/平移 ──
 _zoom = 1.0;
 _panX = 0; _panY = 0;
 _cadScale.ScaleX = 1; _cadScale.ScaleY = 1;
 _cadScale.CenterX = 0; _cadScale.CenterY = 0;
 _cadTranslate.X = 0; _cadTranslate.Y = 0;

 var img = new Image
 {
 Source = bmp,
 Width = bmp.PixelWidth,
 Height = bmp.PixelHeight,
 Stretch = Stretch.None,
 };
 RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

 var host = new Canvas
 {
 Width = bmp.PixelWidth,
 Height = bmp.PixelHeight,
 Background = Brushes.Transparent,
 };
 Canvas.SetLeft(img, 0);
 Canvas.SetTop(img, 0);
 host.Children.Add(img);

 var group = new TransformGroup();
 group.Children.Add(_cadScale);
 group.Children.Add(_cadTranslate);
 host.RenderTransform = group;

 CadHostCanvas.Width = bmp.PixelWidth;
 CadHostCanvas.Height = bmp.PixelHeight;
 CadHostCanvas.Children.Add(host);

 CadFitToWindow();

 // ── 渲染完成，关闭加载提示 ──
 FileLoadingOverlay.Visibility = Visibility.Collapsed;
 StatusText.Text = $"已加载: {Path.GetFileName(_currentFilePath ?? "")} ({_totalPages}个空间)";
 }        /// CAD自适应窗口：显示全部（视口渲染 = 缩放 1.0 + 平移 0）
        private void CadFitToWindow(bool scheduleRebake = true)
        {
            try
            {
                // 视口渲染：显示全部 = 缩放 1.0 + 平移 0（渲染器按适配比例把整图放进视口）
                _zoom = 1.0;
                _panX = 0; _panY = 0;
                ResetCadHostTransform();
                if (scheduleRebake) ScheduleCadReBake();
            }
            catch { }
        }

        /// <summary>CAD 视口图元素变换归位（图像内容已含最新窗口时调用）。</summary>
        private void ResetCadHostTransform()
        {
            _cadScale.ScaleX = 1; _cadScale.ScaleY = 1;
            _cadScale.CenterX = 0; _cadScale.CenterY = 0;
            _cadTranslate.X = 0; _cadTranslate.Y = 0;
        }

        /// <summary>
        /// CAD 缩放/平移过渡变换：在旧视口图上叠加（绕锚点 ax,ay 缩放 + 平移），
        /// 与重烘焙后的最终窗口精确衔接 —— 缩放时画面平滑放大、平移时位图跟随鼠标，
        /// 期间不重绘矢量，重烘焙完成即替换为新窗口图。
        /// </summary>
        private void UpdateCadHostTransform(double ax, double ay)
        {
            double k = _zoom / _cadBakeZoom;
            _cadScale.ScaleX = k; _cadScale.ScaleY = k;
            _cadScale.CenterX = ax; _cadScale.CenterY = ay;
            _cadTranslate.X = _panX - _cadBakePanX * k + ax * (k - 1);
            _cadTranslate.Y = _panY - _cadBakePanY * k + ay * (k - 1);
        }

		// ═════════════ 缩略图侧边栏 ═════════════

		/// <summary>
		/// 在 UI 线程上安全执行 WebView2 脚本，可从任意线程调用。
		///
		/// 【为什么不能直接 Dispatcher.Invoke(() =&gt; task.Wait())】
		/// ExecuteScriptAsync 的结果要靠 WebView2 往 UI 线程消息泵投递回调才能拿到。
		/// 一旦在 UI 线程上 Wait()/.Result 同步等待，消息泵就被自己堵死，
		/// 回调永远送不进来 —— 任务永不完成，整个界面彻底冻结（按钮点不动、侧边栏不出）。
		/// 正确做法是全程 await，把控制权交还消息泵。
		/// </summary>
		private async Task<string> EvalJsAsync(string script)
		{
			var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (Dispatcher.CheckAccess())
				_ = EvalJsCoreAsync(script, tcs);
			else
				Dispatcher.BeginInvoke(new Action(() => { _ = EvalJsCoreAsync(script, tcs); }));
			// 超时保护：WebView2 未就绪 / 页面加载失败 / 渲染进程无响应时 ExecuteScriptAsync
			// 可能永不回调，tcs 永久挂起 → docx 打开流程卡死在"开始"（界面加载层一直转）。
			// 2 秒超时返回 null，调用方按失败处理（快速跳过），不再无限等待。
			var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
			return done == tcs.Task ? await tcs.Task : null;
		}

		private async Task EvalJsCoreAsync(string script, TaskCompletionSource<string> tcs)
		{
			try
			{
				if (PreviewWebView?.CoreWebView2 == null) { tcs.TrySetResult(null); return; }
				var r = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(script);
				tcs.TrySetResult(r);
			}
			catch { tcs.TrySetResult(null); }
		}

		/// <summary>等待 DOCX 的 JS 分页脚本执行完毕（转换器完成后会打 data-paged 标记）</summary>
		private async Task WaitForDocxPaginationAsync()
		{
			try
			{
				// 1. 等文档 load 完成（最多 6 秒）；WebView2 异常（EvalJs 返回 null）立即退出
				for (int i = 0; i < 60; i++)
				{
					var rs = await EvalJsAsync("document.readyState");
					if (rs != null && rs.Contains("complete")) break;
					if (rs == null) return;   // WebView2 未就绪/页面无响应：避免无限空转
					await Task.Delay(100);
				}
				// 2. 等分页完成标记（最多 15 秒，大文档分页耗时长）
				//    转换器保证即使分页抛异常也会打上标记，不会无限等待
				for (int i = 0; i < 150; i++)
				{
					var paged = await EvalJsAsync(
						"(document.body&&document.body.getAttribute('data-paged'))||''");
					if (paged != null && paged.Contains("1")) return;
					if (paged == null) return; // WebView2 异常：快速失败
					await Task.Delay(100);
				}
			}
			catch { }
		}

		/// <summary>DOCX 打开后自动整页适配当前选中页（等价于“显示全部”按钮），需在 UI 线程执行</summary>
		private async Task FitDocxAfterLoadAsync()
		{
			await Dispatcher.InvokeAsync(() => FitWebViewCurrentPageAsync()).Task.Unwrap();
		}

 /// <summary>MD 大纲：等 marked 渲染完成后从 DOM 提取标题，构建目录大纲侧边栏。</summary>
 private async Task BuildMarkdownOutlineAsync()
 {
 try
 {
 System.Collections.Generic.List<(int i, string t, int l)> headings = null;
 for (int attempt = 0; attempt < 50; attempt++)
 {
 var json = await EvalJsAsync(
 "JSON.stringify(Array.from(document.querySelectorAll('h1,h2,h3,h4,h5,h6')).map(function(h,i){return{i:i,t:(h.textContent||'').trim(),l:parseInt(h.tagName[1])};}))");
 if (!string.IsNullOrEmpty(json) && json != "null" && json.Length > 4)
 {
 try
 {
 if (json.StartsWith("\"")) json = System.Text.Json.JsonSerializer.Deserialize<string>(json);
 using var jd = System.Text.Json.JsonDocument.Parse(json);
 if (jd.RootElement.GetArrayLength() > 0)
 {
 headings = new System.Collections.Generic.List<(int, string, int)>();
 foreach (var el in jd.RootElement.EnumerateArray())
 headings.Add((el.GetProperty("i").GetInt32(), el.GetProperty("t").GetString() ?? "", el.GetProperty("l").GetInt32()));
 break;
 }
 }
 catch { }
 }
 await Task.Delay(300);
 }
 if (headings == null || headings.Count == 0) return;

 Dispatcher.Invoke(() =>
 {
 PageThumbItems.Clear();
 foreach (var (i, t, l) in headings)
 {
 PageThumbItems.Add(new PageThumbItem
 {
 PageIndex = i,
 HeadingIndex = i,
 HeadingLevel = l,
 // 全角空格按层级缩进（无需 Thickness Converter）
 Label = new string('　', Math.Max(0, l - 1)) + t,
 Thumbnail = null
 });
 }
 ThumbList.ItemTemplate = (DataTemplate)FindResource("OutlineTemplate");
 CollapseSidebar();
 ShowSidebarTab();
 ExpandSidebar();
 });
 }
 catch { }
 }
 private async Task GenerateThumbnailsAsync()
 {
 var path = _currentFilePath;
 if (string.IsNullOrEmpty(path)) return;
 var ext = Path.GetExtension(path).ToLower();

 // Excel 表格不需要左侧边栏（缩略图对电子表格无意义，保持预览区全宽）
 if (ext == ".xlsx" || ext == ".xlsm" || ext == ".xltx" || ext == ".xltm" ||
 ext == ".xls" || ext == ".xlam" || ext == ".xla" || ext == ".et" || ext == ".ett")
 {
 Dispatcher.Invoke(() =>
 {
 PageThumbItems.Clear();
 CollapseSidebar();
 HideSidebarTab();
 });
 return;
 }

 // 所有文件类型都显示缩略图侧边栏（样式与功能与 PDF 一致）

 Dispatcher.Invoke(() =>
 {
 PageThumbItems.Clear();
 // 侧栏宽度保持 0 且隐藏，等缩略图全部就绪后再展开（避免左上角闪现）
 CollapseSidebar();
 });

 try
 {
 if (ext == ".pdf")
 {
 // PDF 缩略图：用 PdfiumViewer 逐页渲染（与本地 PDF 预览同一渲染器，含缓存）
 int totalPages = Math.Max(1, _previewSvc.TotalPages);
 for (int i = 0; i < totalPages; i++)
 {
 var idx = i;
 Dispatcher.Invoke(() =>
 {
 PageThumbItems.Add(new PageThumbItem
 {
 PageIndex = idx,
 Label = (idx + 1).ToString(),
 Thumbnail = null
 });
 });
 }
 await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
 var thumbTasks = new Task<System.Windows.Media.Imaging.BitmapSource>[totalPages];
 for (int i = 0; i < totalPages; i++)
 {
 var idx = i;
 thumbTasks[idx] = Task.Run(() => _previewSvc.RenderPage(idx, 116, 150));
 }
 for (int i = 0; i < totalPages; i++)
 {
 try
 {
 var bmp = await thumbTasks[i];
 if (bmp != null)
 {
 var idx = i;
 Dispatcher.Invoke(() =>
 {
 if (idx < PageThumbItems.Count)
 PageThumbItems[idx].Thumbnail = bmp;
 });
 }
 }
 catch { }
 }
 // 缩略图全部就绪后，一次性展开侧栏
 Dispatcher.Invoke(() => { ShowSidebarTab(); ExpandSidebar(); });
 }
 else if (ext == ".docx")
 {
// DOCX 缩略图：用 CDP 直接截取每个 .page 的完整区域。
// 旧版用 CapturePreview 只能截到视口内的半页，再按整页比例拉伸，
// 导致缩略图内容缺失且纵向变形 —— 这是"缩略图和详情不一样"的根因。
//
// 注意：CoreWebView2 是 STA 亲和对象，这里是后台线程，
// 任何直接访问都会抛跨线程异常，必须经 Dispatcher 调度。
bool webReady = await Dispatcher.InvokeAsync(() => PreviewWebView?.CoreWebView2 != null);
if (!webReady)
{ await Dispatcher.InvokeAsync(() => { CollapseSidebar(); }); return; }

// 分页后正文容器用了 content-visibility:auto（滚动提速），
// 离屏页不会被渲染，直接截图会得到空白。截图期间先整体放开。
await EvalJsAsync("window.__cvOff&&window.__cvOff()");

// 截图前临时移除 .page 的 box-shadow，让缩略图干净无阴影（和 PDF 矢量缩略图一致）
await EvalJsAsync(
 "var __s=document.createElement('style');" +
 "__s.id='__thumbNoShadow';" +
 "__s.textContent='.page{box-shadow:none!important;}';" +
 "document.head.appendChild(__s);");

// 分页在 WaitForDocxPaginationAsync 已等待完成，这里直接取版面矩形
var pageRects = await GetDocxPageRectsAsync();
int totalPages = pageRects?.Count ?? 0;
if (totalPages <= 0)
{
await EvalJsAsync("var __s=document.getElementById('__thumbNoShadow');if(__s)__s.remove();window.__cvOn&&window.__cvOn()");
await Dispatcher.InvokeAsync(() => { CollapseSidebar(); });
return;
}

 // 先添加占位项（侧栏宽度仍为 0，不渲染）
 for (int i = 0; i < totalPages; i++)
 {
 var idx = i;
 Dispatcher.Invoke(() =>
 {
 PageThumbItems.Add(new PageThumbItem
 {
 PageIndex = idx,
 Label = (idx + 1).ToString(),
 Thumbnail = null
 });
 });
 }
 // 等待 ListBox 完成首次布局
 await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

 for (int i = 0; i < totalPages; i++)
 {
 var idx = i;
 try
 {
 var thumb = await CaptureDocxPageThumbnail(pageRects[idx]);
 if (thumb != null)
 {
 Dispatcher.Invoke(() =>
 {
 if (idx < PageThumbItems.Count)
 PageThumbItems[idx].Thumbnail = thumb;
 });
 }
}
catch { }
}

// 截图完毕恢复 content-visibility 和 box-shadow
 await EvalJsAsync("var __s=document.getElementById('__thumbNoShadow');if(__s)__s.remove();window.__cvOn&&window.__cvOn()");
// 缩略图全部就绪后，一次性展开侧栏
Dispatcher.Invoke(() => { ShowSidebarTab(); ExpandSidebar(); });
}
 else if (ext == ".dwg" || ext == ".dxf")
 {
  // CAD 缩略图：按模型空间/布局逐页渲染（与 PDF 缩略图一致）
  int totalPages = _previewSvc.TotalPages;
  if (totalPages <= 0) totalPages = 1;
  var names = _previewSvc.PageNames;
  for (int i = 0; i < totalPages; i++)
  {
  var idx = i;
  Dispatcher.Invoke(() =>
  {
  PageThumbItems.Add(new PageThumbItem
  {
  PageIndex = idx,
  Label = (names != null && idx < names.Count && !string.IsNullOrEmpty(names[idx]))
  ? names[idx]
  : (idx + 1).ToString(),
  Thumbnail = null
  });
  });
  }
  // 并行生成缩略图
  var thumbTasks = new Task<System.Windows.Media.Imaging.BitmapSource>[totalPages];
  for (int i = 0; i < totalPages; i++)
  {
  var idx = i;
  thumbTasks[idx] = Task.Run(() => _previewSvc.RenderPage(idx, 140, 72));
  }
  // 等待 ListBox 完成首次布局
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
  for (int i = 0; i < totalPages; i++)
  {
  var idx = i;
  try
  {
  var thumb = await thumbTasks[idx];
  Dispatcher.Invoke(() =>
  {
  if (idx < PageThumbItems.Count)
  PageThumbItems[idx].Thumbnail = thumb;
  });
  }
  catch { }
  }
  // 缩略图全部就绪后，一次性展开侧栏
  Dispatcher.Invoke(() => { ShowSidebarTab(); ExpandSidebar(); });
  }
 else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tiff" || ext == ".tif" || ext == ".webp")
 {
 // 图片缩略图
 var thumb = await Task.Run(() =>
 {
 try
 {
 var bmp = new System.Windows.Media.Imaging.BitmapImage();
 bmp.BeginInit();
 bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
 bmp.UriSource = new Uri(path);
 bmp.DecodePixelWidth = 140;
 bmp.EndInit();
 bmp.Freeze();
 return (System.Windows.Media.ImageSource)bmp;
 }
 catch { return null; }
 });
 Dispatcher.Invoke(() =>
 {
 PageThumbItems.Add(new PageThumbItem { PageIndex = 0, Label = "1", Thumbnail = thumb });
 // 缩略图就绪后展开侧栏
 ShowSidebarTab();
 ExpandSidebar();
 });
 }
  else
  {
  // 其他类型（文本/代码/表格/Office/file-viewer 等）：截取预览首屏为单个缩略图
  System.Windows.Media.Imaging.BitmapSource thumb = null;
  await Task.Delay(800); // 等待 WebView2 导航完成，避免截到上一个文件
  for (int i = 0; i < 20; i++)
  {
  var cap = await CaptureWebViewAsync();
  if (cap != null && !IsBitmapBlank(cap)) { thumb = cap; break; }
  await Task.Delay(300);
  }
  if (thumb == null) thumb = await CaptureWebViewAsync();
  Dispatcher.Invoke(() =>
  {
  PageThumbItems.Add(new PageThumbItem { PageIndex = 0, Label = "1", Thumbnail = thumb });
  // 缩略图就绪后展开侧栏
  ShowSidebarTab();
  ExpandSidebar();
  });
  }
 }
 catch { }
 }

        /// <summary>
        /// 读取分页完成后每个 .page 在文档坐标系中的矩形（不是视口坐标）。
        /// </summary>
        private async Task<List<(double x, double y, double w, double h)>> GetDocxPageRectsAsync()
        {
            // 经 EvalJsAsync 走 UI 线程，可从后台线程安全调用
            var json = await EvalJsAsync(
                "JSON.stringify(Array.prototype.map.call(document.querySelectorAll('.page')," +
                "function(p){var r=p.getBoundingClientRect();" +
                "return{x:r.left+window.scrollX,y:r.top+window.scrollY,w:r.width,h:r.height};}))");

            if (string.IsNullOrEmpty(json) || json == "null") return null;
            try
            {
                if (json.StartsWith("\"")) json = JsonSerializer.Deserialize<string>(json);
                using var jd = JsonDocument.Parse(json);
                var list = new List<(double, double, double, double)>();
                foreach (var el in jd.RootElement.EnumerateArray())
                {
                    list.Add((
                        el.GetProperty("x").GetDouble(),
                        el.GetProperty("y").GetDouble(),
                        el.GetProperty("w").GetDouble(),
                        el.GetProperty("h").GetDouble()));
                }
                return list;
            }
            catch { return null; }
        }

        /// <summary>
        /// 用 DevTools Protocol 的 Page.captureScreenshot 截取指定文档区域。
        /// captureBeyondViewport=true 让浏览器渲染视口外的内容，
        /// 因此整页（哪怕比窗口高很多）都能完整截到，缩略图与正文严格一致。
        /// </summary>
        /// <summary>粗略检测位图是否基本为空白（用于等待页面渲染完成）</summary>
        private static bool IsBitmapBlank(System.Windows.Media.Imaging.BitmapSource bmp)
        {
            try
            {
                if (bmp == null || bmp.PixelWidth < 4 || bmp.PixelHeight < 4) return true;
                int stride = bmp.PixelWidth * 4;
                var pixels = new byte[stride * bmp.PixelHeight];
                bmp.CopyPixels(pixels, stride, 0);
                var colors = new HashSet<int>();
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                int sx = Math.Max(1, w / 8), sy = Math.Max(1, h / 8);
                for (int y = 0; y < h; y += sy)
                {
                    for (int x = 0; x < w; x += sx)
                    {
                        int i = y * stride + x * 4;
                        colors.Add(pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16));
                    }
                }
                return colors.Count <= 2;
            }
            catch { return false; }
        }
        private async Task<System.Windows.Media.Imaging.BitmapSource> CaptureDocxPageThumbnail(
            (double x, double y, double w, double h) rect)
        {
            if (rect.w <= 0 || rect.h <= 0) return null;

            const double ThumbW = 140.0;
            // 2 倍采样，WPF 里按 140 显示，高 DPI 下不糊
            double scale = Math.Min(2.0, (ThumbW * 2.0) / rect.w);
            if (scale <= 0) scale = 0.2;

            var args = JsonSerializer.Serialize(new
            {
                format = "png",
                captureBeyondViewport = true,
                clip = new
                {
                    x = rect.x,
                    y = rect.y,
                    width = rect.w,
                    height = rect.h,
                    scale = scale
                }
            });

            // CDP 调用同样必须在 UI 线程发起（InvokeAsync 在 UI 线程执行，全程 await 不阻塞消息泵）
            // 4 秒超时：WebView2 渲染进程无响应 / 页面过于复杂时 CDP 可能永不回调，
            // 缩略图生成会永久 await → docx 打开流程卡死在"正在渲染 Word 文档"覆盖层。
            var cdpTask = Dispatcher.InvokeAsync(() =>
            {
                if (PreviewWebView?.CoreWebView2 == null) return Task.FromResult<string>(null);
                return PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args);
            }).Task.Unwrap();
            var done = await Task.WhenAny(cdpTask, Task.Delay(4000));
            if (done != cdpTask) return null;
            var result = await cdpTask;

            if (string.IsNullOrEmpty(result)) return null;

            byte[] pngBytes;
            try
            {
                using var jd = JsonDocument.Parse(result);
                var b64 = jd.RootElement.GetProperty("data").GetString();
                if (string.IsNullOrEmpty(b64)) return null;
                pngBytes = Convert.FromBase64String(b64);
            }
            catch { return null; }

            try
            {
                using var ms = new System.IO.MemoryStream(pngBytes);
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>调试通道：--selfshot <秒> —— 应用自截图（WPF 窗口 + WebView2 内容）</summary>
        /// <summary>CDP 模拟鼠标点击查看器内部坐标（PDF 原生查看器不可 JS 触达时的兜底）</summary>
        private static async System.Threading.Tasks.Task ClickPdfAtAsync(Microsoft.Web.WebView2.Core.CoreWebView2 wv, double x, double y)
        {
            string mv = "{\"type\":\"mouseMoved\",\"x\":" + x + ",\"y\":" + y + ",\"button\":\"left\",\"pointerType\":\"mouse\"}";
            string pr = "{\"type\":\"mousePressed\",\"x\":" + x + ",\"y\":" + y + ",\"button\":\"left\",\"clickCount\":1,\"pointerType\":\"mouse\"}";
            string rl = "{\"type\":\"mouseReleased\",\"x\":" + x + ",\"y\":" + y + ",\"button\":\"left\",\"clickCount\":1,\"pointerType\":\"mouse\"}";
            await wv.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", mv);
            await wv.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", pr);
            await wv.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", rl);
        }

        /// <summary>
        /// CDP DOM 域穿透（DevTools 可访问封闭 shadowRoot）：直接操作 PDF 查看器 DOM ——
        /// 删除 ☰ 侧栏开关、切换到缩略图视图、展开侧栏。返回 true 表示 DOM 修复成功。
        /// </summary>
        private static async System.Threading.Tasks.Task<bool> TryPdfDomFixAsync(Microsoft.Web.WebView2.Core.CoreWebView2 wv, string logF)
        {
            try
            {
                await wv.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
                var docJson = await wv.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":-1,\"pierce\":true}");
                File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: DOM tree bytes={docJson.Length}\n");
                using var jd = System.Text.Json.JsonDocument.Parse(docJson);
                var root = jd.RootElement.GetProperty("root");
                int toggleId = 0, thumbId = 0, sidebarId = 0, mainId = 0;
                void Walk(System.Text.Json.JsonElement node, int depth)
                {
                    if (depth > 40) return;
                    if (toggleId != 0 && thumbId != 0 && sidebarId != 0 && mainId != 0) return;
                    int nid = node.TryGetProperty("nodeId", out var ni) ? ni.GetInt32() : 0;
                    var attrs = new System.Collections.Generic.List<string>();
                    if (node.TryGetProperty("attributes", out var at) && at.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        for (int i = 0; i + 1 < at.GetArrayLength(); i += 2)
                            attrs.Add(at[i].GetString() + "=" + at[i + 1].GetString());
                    }
                    string a = string.Join(" ", attrs);
                    if (toggleId == 0 && a.Contains("id=sidebarToggle")) toggleId = nid;
                    if (thumbId == 0 && a.Contains("id=viewThumbnail")) thumbId = nid;
                    if (sidebarId == 0 && a.Contains("id=sidebar")) sidebarId = nid;
                    if (mainId == 0 && a.Contains("id=main")) mainId = nid;
                    if (node.TryGetProperty("shadowRoots", out var srs) && srs.ValueKind == System.Text.Json.JsonValueKind.Array)
                        for (int i = 0; i < srs.GetArrayLength(); i++) Walk(srs[i], depth + 1);
                    if (node.TryGetProperty("children", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.Array)
                        for (int i = 0; i < ch.GetArrayLength(); i++) Walk(ch[i], depth + 1);
                }
                Walk(root, 0);
                File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: DOM 探测 toggle={toggleId} thumb={thumbId} sidebar={sidebarId} main={mainId}\n");
                if (toggleId != 0)
                {
                    await wv.CallDevToolsProtocolMethodAsync("DOM.setAttributeValue", "{\"nodeId\":" + toggleId + ",\"name\":\"style\",\"value\":\"display:none !important\"}");
                    File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: ☰ 已隐藏 (node {toggleId})\n");
                }
                if (thumbId != 0)
                {
                    var rn = await wv.CallDevToolsProtocolMethodAsync("DOM.resolveNode", "{\"nodeId\":" + thumbId + "}");
                    using var rj = System.Text.Json.JsonDocument.Parse(rn);
                    var oid = rj.RootElement.GetProperty("object").GetProperty("objectId").GetString();
                    if (!string.IsNullOrEmpty(oid))
                        await wv.CallDevToolsProtocolMethodAsync("Runtime.callFunctionOn", "{\"objectId\":\"" + oid + "\",\"functionDeclaration\":\"function(){ this.click(); }\"}");
                    File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: 已切缩略图 (node {thumbId})\n");
                }
                if (sidebarId != 0)
                {
                    var rn = await wv.CallDevToolsProtocolMethodAsync("DOM.resolveNode", "{\"nodeId\":" + sidebarId + "}");
                    using var rj = System.Text.Json.JsonDocument.Parse(rn);
                    var oid = rj.RootElement.GetProperty("object").GetProperty("objectId").GetString();
                    if (!string.IsNullOrEmpty(oid))
                        await wv.CallDevToolsProtocolMethodAsync("Runtime.callFunctionOn", "{\"objectId\":\"" + oid + "\",\"functionDeclaration\":\"function(){ this.classList.add(\\\"open\\\"); var p=this.parentElement; if(p) p.classList.add(\\\"sidebarOpen\\\"); }\"}");
                    File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: 侧栏已展开 (node {sidebarId})\n");
                }
                return toggleId != 0 || thumbId != 0;
            }
            catch (Exception ex)
            {
                File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] PDF: DOM 修复失败 {ex.GetType().Name}: {ex.Message}\n");
                return false;
            }
        }


        private async System.Threading.Tasks.Task SelfShotAsync(int secs)
        {
            try
            {
                var logF = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.log");
                await File.AppendAllTextAsync(logF, $"[{DateTime.Now:HH:mm:ss}] SELFSHOT: scheduled delay={secs}s\n");
                await System.Threading.Tasks.Task.Delay(secs * 1000);
                await File.AppendAllTextAsync(logF, $"[{DateTime.Now:HH:mm:ss}] SELFSHOT: delay done\n");
                string wpfInfo = "";
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        int w = (int)Math.Max(1, ActualWidth), h = (int)Math.Max(1, ActualHeight);
                        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(this);
                        File.AppendAllText(logF, $"[{DateTime.Now:HH:mm:ss}] SELFSHOT: rendered {w}x{h}\n");
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using var fs = File.Create(@"D:\ZCODE\_gui_test\selfshot.png");
                        enc.Save(fs);
                        wpfInfo = $"wpf={w}x{h}";

                        // 元素布局诊断：关键控件的位置/可见性
                        var dbg = new System.Text.StringBuilder();
                        void RectOf(string name, System.Windows.FrameworkElement el)
                        {
                            if (el == null) { dbg.Append(name + ":null "); return; }
                            var p = el.TranslatePoint(new System.Windows.Point(0, 0), this);
                            dbg.Append($"{name}:vis={el.Visibility} at=({p.X:F0},{p.Y:F0}) {el.ActualWidth:F0}x{el.ActualHeight:F0} ");
                        }
                        RectOf("Toolbar", ToolbarBorder);
                        RectOf("BtnOpen", BtnOpen);
                        RectOf("BtnFitAll", BtnFitAll);
                        RectOf("Sidebar", ThumbSidebar);
                        RectOf("WebView", PreviewWebView);
                        RectOf("ImgPrev", ImagePreview);
                        RectOf("CadScroll", CadScrollViewer);
                        wpfInfo += " || " + dbg.ToString().Trim();
                    }
                    catch (Exception ex) { wpfInfo = "wpf FAIL " + ex.GetType().Name + ":" + ex.Message; }
                });

                // WebView2 内容（PDF 查看器内部）用 CDP 截图；图片/CAD 等收起 WebView 时跳过（避免 CDP 调用挂起）
                string webInfo = "";
                if (PreviewWebView?.CoreWebView2 != null && PreviewWebView.Visibility == Visibility.Visible)
                {
                    try
                    {
                        var args = System.Text.Json.JsonSerializer.Serialize(new { format = "png" });
                        var result = await PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args);
                        if (!string.IsNullOrEmpty(result))
                        {
                            using var jd = System.Text.Json.JsonDocument.Parse(result);
                            var b64 = jd.RootElement.GetProperty("data").GetString();
                            if (!string.IsNullOrEmpty(b64))
                            {
                                var bytes = Convert.FromBase64String(b64);
                                File.WriteAllBytes(@"D:\ZCODE\_gui_test\selfshot_web.png", bytes);
                                webInfo = $"web={bytes.Length}B";
                            }
                        }
                        // DOM 状态：URL / 标题 / PDF 查看器存在性（诊断 PDF 是否真正渲染）
                        try
                        {
                            var state = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                                "JSON.stringify({url:location.href,title:document.title," +
                                "emb:!!document.querySelector('embed[type=\\\"application/pdf\\\"]')," +
                                "pdfApp:typeof window.PDFViewerApplication!=='undefined'," +
                                "bodyText:(document.body?document.body.innerText||'':'').slice(0,80)})");
                            webInfo += " dom=" + (state ?? "null");
                        }
                        catch { }
                    }
                    catch (Exception ex) { webInfo = "web FAIL " + ex.GetType().Name + ":" + ex.Message; }
                }
                else webInfo = "web n/a";

                await File.WriteAllTextAsync(@"D:\ZCODE\_gui_test\selfshot_done.txt",
                    $"{DateTime.Now:HH:mm:ss} {wpfInfo} {webInfo}\n");
            }
            catch (Exception ex)
            {
                try { await File.WriteAllTextAsync(@"D:\ZCODE\_gui_test\selfshot_done.txt", "SELFSHOT FAIL " + ex); } catch { }
            }
        }

 /// 用WPF DrawingVisual 渲染DOCX为仿Word页面缩略图（白底+文字行）
 /// 按分页符分割内容，每页生成一个缩略图，按真实页面比例和分栏渲染
 private List<System.Windows.Media.ImageSource> CreateDocxPageThumbnails(string docxPath)
 {
 try
 {
 // 按分页符分割段落为多页，并读取页面布局
 var pages = new List<List<string>>();
 var currentPage = new List<string>();
 double pageWtwips = 11906, pageHtwips = 16838; // 默认A4
 int colCount = 1;
 double colGapTwips = 425;

 using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(docxPath, false))
 {
 var body = doc.MainDocumentPart?.Document?.Body;
 if (body == null) return null;

 // 读取最后一个 sectPr 的页面布局
 var sectPr = body.Elements<DocumentFormat.OpenXml.Wordprocessing.SectionProperties>().LastOrDefault();
 if (sectPr != null)
 {
 var pgSz = sectPr.Elements<DocumentFormat.OpenXml.Wordprocessing.PageSize>().FirstOrDefault();
 if (pgSz != null)
 {
 var wProp = pgSz.GetType().GetProperty("Width");
 var hProp = pgSz.GetType().GetProperty("Height");
 if (wProp != null)
 {
 var wv = wProp.GetValue(pgSz);
 if (wv != null && int.TryParse(wv.ToString(), out int w)) pageWtwips = w;
 }
 if (hProp != null)
 {
 var hv = hProp.GetValue(pgSz);
 if (hv != null && int.TryParse(hv.ToString(), out int h)) pageHtwips = h;
 }
 }
 var cols = sectPr.Elements<DocumentFormat.OpenXml.Wordprocessing.Columns>().FirstOrDefault();
 if (cols != null)
 {
 var ncProp = cols.GetType().GetProperty("ColumnCount") ?? cols.GetType().GetProperty("NumberColumns");
 if (ncProp != null)
 {
 var ncv = ncProp.GetValue(cols);
 if (ncv != null && int.TryParse(ncv.ToString(), out int nc) && nc > 0) colCount = nc;
 }
 var spProp = cols.GetType().GetProperty("Space");
 if (spProp != null)
 {
 var spv = spProp.GetValue(cols);
 if (spv != null && int.TryParse(spv.ToString(), out int sp) && sp > 0) colGapTwips = sp;
 }
 }
 }

 foreach (var elem in body.ChildElements)
 {
 if (elem is DocumentFormat.OpenXml.Wordprocessing.Paragraph p)
 {
 var text = string.Join("", p.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text));
 text = string.IsNullOrWhiteSpace(text) ? "" : text.Trim();

 bool hasPageBreak = false;
 foreach (var run in p.Elements<DocumentFormat.OpenXml.Wordprocessing.Run>())
 foreach (var child in run.ChildElements)
 if (child is DocumentFormat.OpenXml.Wordprocessing.Break br && br.Type?.Value == DocumentFormat.OpenXml.Wordprocessing.BreakValues.Page)
 hasPageBreak = true;

 currentPage.Add(text);
 if (hasPageBreak)
 {
 pages.Add(currentPage);
 currentPage = new List<string>();
 }
 }
 }
 if (currentPage.Count > 0) pages.Add(currentPage);
 }

 if (pages.Count == 0) pages.Add(new List<string>());
 if (pages.Count > 20) pages = pages.Take(20).ToList();

 var result = new List<System.Windows.Media.ImageSource>();
 for (int pageIdx = 0; pageIdx < pages.Count; pageIdx++)
 {
 var textLines = pages[pageIdx].Take(80).ToList();
 var thumb = RenderDocxThumbnailPage(textLines, pageIdx + 1, pageWtwips, pageHtwips, colCount, colGapTwips);
 if (thumb != null) result.Add(thumb);
 }
 return result.Count > 0 ? result : null;
 }
 catch { return null; }
 }

 /// 渲染单个DOCX缩略图页面——按真实页面比例和分栏数
 private System.Windows.Media.ImageSource RenderDocxThumbnailPage(List<string> textLines, int pageNum,
 double pageWtwips, double pageHtwips, int colCount, double colGapTwips)
 {
 try
 {
 // 按真实页面比例计算缩略图尺寸（固定宽度126px，高度按比例）
 double aspectRatio = pageWtwips / pageHtwips;
 double W = 126;
 double H = W / aspectRatio;
 // 最小高度限制，避免太扁
 if (H < 60) H = 60;
 if (H > 220) H = 220;

 const double pad = 6;
 double contentW = W - pad * 2;
 // 分栏宽度
 double colGapPx = Math.Max(2, colGapTwips / (1440.0 / 96.0) * (W / (pageWtwips / (1440.0 / 96.0))));
 double colW = colCount > 1 ? (contentW - colGapPx * (colCount - 1)) / colCount : contentW;
 double lineH = 3.5;
 double fontSize = 2.5;

 var bgColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
 var textColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 50, 50));
 var titleColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 180, 180));
 var colLineColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220));

 var vis = new System.Windows.Media.DrawingVisual();
 using (var dc = vis.RenderOpen())
 {
 // 白色页面背景
 dc.DrawRoundedRectangle(bgColor, null, new System.Windows.Rect(0, 0, W, H), 3, 3);

 // 分栏中线
 if (colCount > 1)
 {
 for (int c = 1; c < colCount; c++)
 {
 double lineX = pad + colW * c + colGapPx * (c - 1) + colGapPx / 2;
 dc.DrawLine(new System.Windows.Media.Pen(colLineColor, 0.5),
 new System.Windows.Point(lineX, pad), new System.Windows.Point(lineX, H - pad));
 }
 }

 // 将文字行分配到各栏（模拟Word分栏：先填满第1栏再第2栏）
 int linesPerCol = (int)((H - pad * 2) / lineH);
 if (linesPerCol < 1) linesPerCol = 1;
 var typeface = new System.Windows.Media.Typeface("宋体, SimSun");
 bool isFirst = true;

 for (int col = 0; col < colCount; col++)
 {
 double colStartX = pad + col * (colW + colGapPx);
 double y = pad;
 int startIdx = col * linesPerCol;

 for (int i = startIdx; i < textLines.Count && i < startIdx + linesPerCol; i++)
 {
 if (y + lineH > H - pad) break;
 var line = textLines[i];
 if (string.IsNullOrEmpty(line)) { y += lineH * 0.6; continue; }
 bool isTitle = isFirst && line.Length < 30;
 isFirst = false;
 int maxChars = (int)(colW / (fontSize * (isTitle ? 0.6 : 0.55)));
 if (maxChars < 2) maxChars = 2;
 string displayText = line.Length > maxChars ? line.Substring(0, maxChars) + "…" : line;
 double textWidth = displayText.Length * fontSize * 0.55;
 double startX = isTitle ? colStartX + (colW - textWidth) / 2 : colStartX;
 var lineBrush = isTitle ? textColor : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80));
 dc.DrawRectangle(lineBrush, null, new System.Windows.Rect(startX, y, textWidth, fontSize * 0.7));
 y += lineH;
 }
 }

 // 空文档占位线
 if (textLines.Count == 0)
 {
 for (int col = 0; col < colCount; col++)
 {
 double colStartX = pad + col * (colW + colGapPx);
 double y = pad;
 for (int i = 0; i < 15 && y + lineH < H - pad; i++)
 { dc.DrawRectangle(titleColor, null, new System.Windows.Rect(colStartX + 2, y, colW - 4, fontSize * 0.7)); y += lineH; }
 }
 }

 // 页码标签
 var pageLabel = new System.Windows.Media.FormattedText(pageNum.ToString(), System.Globalization.CultureInfo.CurrentCulture,
 System.Windows.FlowDirection.LeftToRight, typeface, 5,
 new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 140, 140)), 1);
 dc.DrawText(pageLabel, new System.Windows.Point(W / 2 - 3, H - 14));
 }

 int bmpW = (int)Math.Ceiling(W);
 int bmpH = (int)Math.Ceiling(H);
 var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(bmpW, bmpH, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
 bmp.Render(vis);
 bmp.Freeze();
 return bmp;
 }
 catch { return null; }
 }

 /// 创建 DOCX 缩略图图标（文字预览）
 private System.Windows.Media.ImageSource CreateDocxThumbnailIcon(string textPreview)
 {
 try
 {
 var vis = new System.Windows.Media.DrawingVisual();
 using (var dc = vis.RenderOpen())
 {
 // 背景
 dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
 null, new System.Windows.Rect(0, 0, 140, 180), 4, 4);
 // 顶部蓝色条
 dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 118, 210)),
 null, new System.Windows.Rect(0, 0, 140, 24));
 // "DOCX" 文字
 var ft = new System.Windows.Media.FormattedText("DOCX",
 System.Globalization.CultureInfo.CurrentCulture,
 System.Windows.FlowDirection.LeftToRight,
 new System.Windows.Media.Typeface("Segoe UI"),
 12, System.Windows.Media.Brushes.White, 1);
 dc.DrawText(ft, new System.Windows.Point(45, 4));
 // 文字预览
 if (!string.IsNullOrEmpty(textPreview))
 {
 var lines = textPreview.Split('\n');
 for (int i = 0; i < lines.Length && i < 5; i++)
 {
 var line = lines[i].Length > 14 ? lines[i].Substring(0, 14) + "…" : lines[i];
 var ft2 = new System.Windows.Media.FormattedText(line,
 System.Globalization.CultureInfo.CurrentCulture,
 System.Windows.FlowDirection.LeftToRight,
 new System.Windows.Media.Typeface("Segoe UI"),
 10, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 80, 80)), 1);
 dc.DrawText(ft2, new System.Windows.Point(8, 32 + i * 16));
 }
 }
 // 底部装饰线
 for (int i = 0; i < 4; i++)
 dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)),
 null, new System.Windows.Rect(8, 120 + i * 12, 124, 4));
 }
 var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(140, 180, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
 bmp.Render(vis);
 bmp.Freeze();
 return bmp;
 }
 catch { return null; }
 }

 /// 缩略图点击
 private void ThumbList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
 {
 // 多选时不导航（Shift/Ctrl多选时仅更新选中状态，不跳转页面）
 if (ThumbList.SelectedItems.Count > 1) return;
 if (ThumbList.SelectedItem is PageThumbItem item)
 {
 if (_isCadMode)
 {
 _currentPage = item.PageIndex;
 DisplayCadPage(item.PageIndex);
 PageInfo.Text = $"{Path.GetFileName(_currentFilePath)} — {_previewSvc.PageNames[item.PageIndex]} ({item.PageIndex + 1}/{_totalPages})";
 }
 else if (_isLocalPdfMode)
 {
 // 本地PDF：切换到选中页并整页适应视口
 if (item.PageIndex != _localPdfPage || ImagePreview.Source == null)
 DisplayLocalPdfPage(item.PageIndex);
 }
 else if (PreviewWebView?.CoreWebView2 != null)
 {
 var ext = Path.GetExtension(_currentFilePath).ToLower();
 if (ext == ".docx")
 {
 // DOCX（HTML预览）：滚动到对应 .page div
 PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 $"var pages=document.querySelectorAll('.page');if(pages.length>{item.PageIndex}){{pages[{item.PageIndex}].scrollIntoView({{behavior:'smooth',block:'start'}});}}");
 }
 else if (ext == ".md" || ext == ".markdown")
 {
 // MD 大纲：滚动到对应标题
 PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 $"var hs=document.querySelectorAll('h1,h2,h3,h4,h5,h6');if(hs.length>{item.PageIndex}){{hs[{item.PageIndex}].scrollIntoView({{behavior:'smooth',block:'start'}});}}");
 }
 else if (ext == ".pdf")
 {
 // PDF 翻页
 PreviewWebView.CoreWebView2.ExecuteScriptAsync($"if(typeof PDFViewerApplication!=='undefined'){{PDFViewerApplication.page={item.PageIndex + 1};}}");
 }
 }
 }
 }

 // ═════════════ 缩略图右键旋转 ═════════════
 private void ThumbRotateRight_Click(object sender, RoutedEventArgs e)
 {
 RotateThumbSelection(90);
 }

 private void ThumbRotateLeft_Click(object sender, RoutedEventArgs e)
 {
 RotateThumbSelection(-90);
 }

 /// 缩略图右键菜单路由事件处理（通过Tag区分旋转方向）
 private void ThumbMenuItem_Click(object sender, RoutedEventArgs e)
 {
 if (e is not System.Windows.RoutedEventArgs re) return;
 if (re.OriginalSource is not System.Windows.FrameworkElement fe) return;
 var tag = fe.Tag as string;
 if (tag == "RotateRight") RotateThumbSelection(90);
 else if (tag == "RotateLeft") RotateThumbSelection(-90);
 }

 /// 旋转选中的缩略图（支持多选）并同步PDF显示页旋转
 private void RotateThumbSelection(double angle)
 {
 var selectedItems = ThumbList.SelectedItems.OfType<PageThumbItem>().ToList();
 if (selectedItems.Count == 0 && ThumbList.SelectedItem is PageThumbItem single)
 selectedItems.Add(single);
 if (selectedItems.Count == 0) return;

 foreach (var item in selectedItems)
 {
 // 更新旋转状态
 item.Rotation = ((item.Rotation + (int)angle) % 360 + 360) % 360;

 // 旋转缩略图位图
 if (item.Thumbnail is System.Windows.Media.Imaging.BitmapSource bmp)
 {
 try
 {
 var rotated = new System.Windows.Media.Imaging.TransformedBitmap(
 bmp, new System.Windows.Media.RotateTransform(angle));
 rotated.Freeze();
 item.Thumbnail = rotated;
 }
 catch { }
 }
 }

 // 本地PDF：当前显示页若被旋转，立即重新渲染
 if (_isLocalPdfMode)
 {
 if (selectedItems.Any(it => it.PageIndex == _localPdfPage))
 DisplayLocalPdfPage(_localPdfPage);
 return;
 }

 // 同步 PDF 显示页旋转
 if (PreviewWebView?.CoreWebView2 != null && !_isCadMode)
 {
 var ext = Path.GetExtension(_currentFilePath).ToLower();
 if (ext == ".pdf")
 {
 foreach (var item in selectedItems)
 {
 int pageIndex = item.PageIndex; // 0-based
 int targetRotation = item.Rotation; // 0/90/180/270
 // pdf.js 页面旋转：PDFViewerApplication.pdfViewer.pagesRotation 是全局的，
 // 单页旋转需通过 pageView 的 viewport.rotation 设置
 var js = $@"(function(){{
try {{
 var pv = PDFViewerApplication.pdfViewer.getPageView({pageIndex});
 if (pv && pv.viewport) {{
 pv.viewport.rotation = {targetRotation};
 if (pv.canvas) {{ PDFViewerApplication.pdfViewer.update(); }}
 }}
}} catch(e) {{ console.log('rotate err:', e); }}
}})();";
 PreviewWebView.CoreWebView2.ExecuteScriptAsync(js);
 }
 }
 }
 }

 // ═════════════ CAD字体设置 ═════════════
 private void BtnCadFont_Click(object sender, RoutedEventArgs e)
 {
 var dlg = new CadFontDialog(_cadWidthFactor) { Owner = this };
 if (dlg.ShowDialog() == true && dlg.IsApplied)
 {
 // 简化版对话框只设置字体与字宽，其余CAD渲染参数保持原值
 _cadWidthFactor = dlg.WidthFactor;
 // 字体由图纸自带样式决定，不再覆盖
 SaveCadFontSettings();
 // 更新FilePreviewService中的参数
 _previewSvc.UpdateCadFontSettings(_cadFontName, _cadBigFontName, _cadFontFilePath, _cadBigFontFilePath,
 _cadShxFontName, _cadBigShxFontName, _cadUseBigFont,
 _cadWidthFactor, _cadLineFactor, _cadCharSpacing,
 _cadObliqueAngle, _cadUpsideDown, _cadBackwards, _cadIsDarkBg);            // 重新渲染当前页
            if (_isCadMode)
            {
                LogRt($"CAD字宽应用: {_cadWidthFactor:F2}");
                DisplayCadPage(_currentPage);
                StatusText.Text = $"CAD字宽: {_cadWidthFactor:F2}";
            }
 }
 }
 // ═════════════ CAD字体/字宽持久化 ═════════════
 private static string CadFontConfigPath =>
 Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ldassistant_cadfont.json");

 private void LoadCadFontSettings()
 {
 try
 {
 if (!File.Exists(CadFontConfigPath)) return;
 using var doc = JsonDocument.Parse(File.ReadAllText(CadFontConfigPath));
 var root = doc.RootElement;
 if (root.TryGetProperty("width_factor", out var wf) && wf.TryGetDouble(out var w) && w > 0)
 _cadWidthFactor = w;
 }
 catch { }
 }

 private void SaveCadFontSettings()
 {
 try
 {
 var opts = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
 var json = new { width_factor = _cadWidthFactor };
 File.WriteAllText(CadFontConfigPath, JsonSerializer.Serialize(json, opts));
 }
 catch { }
 }

 // ═════════════ 翻页（通过 JS 注入 pdf.js 翻页） ═════════════
 private void BtnPrev_Click(object sender, RoutedEventArgs e)
 {
 if (_isCadMode) { if (_currentPage > 0) { ThumbList.SelectedIndex = _currentPage - 1; } return; }
 if (_isLocalPdfMode)
 {
 if (_localPdfPage > 0)
 {
 if (ThumbList.Items.Count > _localPdfPage - 1) ThumbList.SelectedIndex = _localPdfPage - 1;
 else DisplayLocalPdfPage(_localPdfPage - 1);
 }
 return;
 }
 if (PreviewWebView?.CoreWebView2 == null) return;
 PreviewWebView.CoreWebView2.ExecuteScriptAsync("if(typeof PDFViewerApplication!=='undefined'&&PDFViewerApplication.page){PDFViewerApplication.page--;}else{window.scrollBy(0,-window.innerHeight*0.9);}");
 }

 private void BtnNext_Click(object sender, RoutedEventArgs e)
 {
 if (_isCadMode) { if (_currentPage < _totalPages - 1) { ThumbList.SelectedIndex = _currentPage + 1; } return; }
 if (_isLocalPdfMode)
 {
 if (_localPdfPage < _totalPages - 1)
 {
 if (ThumbList.Items.Count > _localPdfPage + 1) ThumbList.SelectedIndex = _localPdfPage + 1;
 else DisplayLocalPdfPage(_localPdfPage + 1);
 }
 return;
 }
 if (PreviewWebView?.CoreWebView2 == null) return;
 PreviewWebView.CoreWebView2.ExecuteScriptAsync("if(typeof PDFViewerApplication!=='undefined'&&PDFViewerApplication.page){PDFViewerApplication.page++;}else{window.scrollBy(0,window.innerHeight*0.9);}");
 }

 // ═════════════ 缩放（WebView2 ZoomFactor）+ 旋转 ═════════════
 private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
 {
 double maxZoom = _isCadMode ? 100.0 : 10.0;
            _zoom = Math.Min(_zoom * 1.25, maxZoom);
 ApplyZoom();
 }

 private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
 {
 _zoom = Math.Max(_zoom / 1.25, 0.1);
 ApplyZoom();
 }    private void ApplyZoom()
    {
        if (_isCadMode)
        {
            // 缩放按钮：绕视口中心缩放过渡
            _cadFitToFull = false;
            UpdateCadHostTransform(CadHostCanvas.ActualWidth / 2.0, CadHostCanvas.ActualHeight / 2.0);
            ScheduleCadReBake();
        }
            else if (ImagePreview.Visibility == Visibility.Visible)
            {
                // 位图模式（图片 / 本地PDF）：持久变换组，缩放与平移共存
                ApplyImageTransform();
            }
 else if (PreviewWebView != null)
 PreviewWebView.ZoomFactor = _zoom;
 }

 // ═════════════ 显示全部/恢复视口 ═════════════
 // ═════════════ 侧边栏开关 ═════════════
 private const double SidebarWidth = 240;

 /// <summary>显示左侧深蓝色竖条把手（仅 PDF / DOCX 等有侧栏的文件类型）</summary>
 private void ShowSidebarTab()
 {
 }

 /// <summary>隐藏深蓝色竖条把手（CAD / 图片等无侧栏的文件类型）</summary>
 private void HideSidebarTab()
 {
 }

 /// <summary>展开侧栏面板</summary>
 private void ExpandSidebar()
 {
 ThumbColumn.MinWidth = SidebarWidth;
 ThumbColumn.Width = new GridLength(SidebarWidth);
 ThumbSidebar.Visibility = Visibility.Visible;
 }

 /// <summary>收起侧栏面板（竖条把手保留，可再展开）</summary>
 private void CollapseSidebar()
 {
 ThumbColumn.MinWidth = 0;
 ThumbColumn.Width = new GridLength(0);
 ThumbSidebar.Visibility = Visibility.Collapsed;
 }

 private void BtnCloseSidebar_Click(object sender, RoutedEventArgs e)
 {
 CollapseSidebar();
 }

 /// <summary>侧栏视图切换：缩略图 / 列表</summary>
 private void SidebarTab_Click(object sender, RoutedEventArgs e)
 {
 if (sender is not Button btn) return;
 // MD 大纲模式固定，不做缩略图/列表视图切换
 if (Path.GetExtension(_currentFilePath).ToLower() == ".md" || Path.GetExtension(_currentFilePath).ToLower() == ".markdown") return;
 bool isList = (btn.Tag as string) == "list";
 ThumbList.ItemTemplate = (DataTemplate)FindResource(isList ? "ThumbListTemplate" : "ThumbGridTemplate");
 var selBg = new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF));
 var selFg = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
 var normBg = Brushes.Transparent;
 var normFg = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
 if (isList)
 {
 BtnThumbTab.Background = normBg; BtnThumbTab.Foreground = normFg;
 BtnListTab.Background = selBg; BtnListTab.Foreground = selFg;
 }
 else
 {
 BtnListTab.Background = normBg; BtnListTab.Foreground = normFg;
 BtnThumbTab.Background = selBg; BtnThumbTab.Foreground = selFg;
 }
 }

 /// <summary>
 /// 显示全部 / 恢复视口 — 对 CAD、本地PDF（位图）、图片、DOCX(HTML)、pdf.js 全部生效。
 /// PDF / DOCX 只针对"当前选中的那一页"做适配，不影响其他页。
 /// </summary>
 private void BtnFitAll_Click(object sender, RoutedEventArgs e)
 {
     if (_currentFilePath == null && !_isCadMode) { StatusText.Text = "未打开文件"; return; }

// ═══ 1. CAD：整图适应视口 ═══
 if (_isCadMode)
 {
 // CAD 通过 WebView2 渲染 SVG 时，触发页面内建的 fit()（通过 resize 事件）；CadHostCanvas 位图模式走 FitCadToViewport
 if (PreviewWebView.Visibility == Visibility.Visible && PreviewWebView?.CoreWebView2 != null)
 {
 _ = Task.Run(async () =>
 {
 try
 {
 await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 "window.dispatchEvent(new Event('resize'));");
 await Dispatcher.BeginInvoke(new Action(() =>
 {
 StatusText.Text = "已恢复视口 — CAD 整图适应";
 }));
 }
 catch (Exception ex)
 {
 await Dispatcher.BeginInvoke(new Action(() =>
 StatusText.Text = $"恢复视口失败: {ex.Message}"));
 }
 });
 return;
 }
 _cadFitToFull = true;
 if (!FitCadToViewport())
 StatusText.Text = "恢复视口失败：CAD内容尺寸未知";
 else
 StatusText.Text = "已恢复视口 — 显示全部";
 return;
 }

     // ═══ 2. 本地PDF / 图片（ImagePreview 位图）：当前页整页适应视口 ═══
     if (ImagePreview.Visibility == Visibility.Visible)
     {
         FitImageToViewport();
         StatusText.Text = _isLocalPdfMode
             ? $"已恢复视口 — 第{_localPdfPage + 1}页 缩放 {_zoom:P0}"
             : $"已恢复视口 — 缩放 {_zoom:P0}";
         return;
     }

     // ═══ 3. WebView2：DOCX(HTML) / pdf.js ═══
     if (PreviewWebView?.CoreWebView2 == null) { StatusText.Text = "预览未就绪"; return; }
     _ = FitWebViewCurrentPageAsync();
 }

 /// <summary>CAD「显示全部」按钮：整图一次性适配窗口（禁用初始智能放大，真全图显示）。</summary>
 private void BtnCadFitAll_Click(object sender, RoutedEventArgs e)
 {
     if (!_isCadMode) return;
     _cadFitToFull = true;
     if (!FitCadToViewport())
         StatusText.Text = "恢复视口失败：CAD内容尺寸未知";
     else
         StatusText.Text = "已恢复视口 — 显示全部";
 }

 /// <summary>CAD 整图适应视口（尺寸未知时回退到 ActualWidth）</summary>
 private bool FitCadToViewport()
 {
     try
     {
         // 视口渲染：显示全部 = 缩放 1.0 + 平移 0（渲染器按适配比例把整图放进视口）
         _zoom = 1.0;
         _panX = 0; _panY = 0;
         ResetCadHostTransform();
         CadScrollViewer.ScrollToHorizontalOffset(0);
         CadScrollViewer.ScrollToVerticalOffset(0);
         ScheduleCadReBake();
         return true;
     }
     catch { return false; }
 }

 /// <summary>
 /// 位图预览（本地PDF当前页 / 图片）整页适应视口。
 /// ImagePreview 用 Stretch=Uniform，zoom=1 时已是"等比铺满"，
 /// 这里显式按图像自然尺寸与视口重算，保证旋转/异形页也能完整显示。
 /// </summary>
 private void FitImageToViewport()
 {
     _panX = 0; _panY = 0;
     _zoom = 1.0;
     try
     {
         if (ImagePreview.Source is System.Windows.Media.Imaging.BitmapSource bs)
         {
             double viewW = PreviewGrid.ActualWidth - 16;
             double viewH = PreviewGrid.ActualHeight - 16;
             double imgW = bs.PixelWidth, imgH = bs.PixelHeight;
             if (viewW > 0 && viewH > 0 && imgW > 0 && imgH > 0)
             {
                 // Uniform 已把图缩放到 min(viewW/imgW, viewH/imgH)，
                 // 因此 fit 缩放系数恒为 1；若容器留白过多（如旋转后），按比例微调。
                 double uniform = Math.Min(PreviewGrid.ActualWidth / imgW, PreviewGrid.ActualHeight / imgH);
                 double target = Math.Min(viewW / imgW, viewH / imgH);
                 if (uniform > 0) _zoom = Math.Max(0.05, Math.Min(20.0, target / uniform));
             }
         }
     }
     catch { _zoom = 1.0; }
     ApplyImageTransform();
 }

 /// <summary>WebView2 模式：对"当前选中页"做整页适应 + 滚动定位</summary>
 private async Task FitWebViewCurrentPageAsync()
 {
     try
     {
         var ext = string.IsNullOrEmpty(_currentFilePath) ? "" : Path.GetExtension(_currentFilePath).ToLower();
         // 先复位平移（历史遗留的 CSS transform）
         _panX = 0; _panY = 0;
         PreviewWebView.ZoomFactor = 1.0;
         await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
             "document.documentElement.style.transform='';document.documentElement.style.transformOrigin='';");

         int sel = ThumbList?.SelectedIndex ?? -1;

         if (ext == ".pdf")
         {
             // pdf.js：只对当前选中页做 page-fit
             int page = sel >= 0 ? sel + 1 : 0;
             var js = $@"(function(){{
try {{
  if (typeof PDFViewerApplication === 'undefined') return 'nopdfjs';
  var p = {page};
  if (p > 0) PDFViewerApplication.page = p;
  PDFViewerApplication.pdfViewer.currentScaleValue = 'page-fit';
  var cur = PDFViewerApplication.page;
  var el = document.querySelector('.page[data-page-number=""'+cur+'""]');
  if (el) el.scrollIntoView({{block:'start'}});
  return 'ok:'+cur;
}} catch(e) {{ return 'err:'+e.message; }}
}})();";
             var r = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(js);
             StatusText.Text = r != null && r.Contains("ok:")
                 ? $"已恢复视口 — 当前页整页显示"
                 : "已恢复视口";
             return;
         }

         // DOCX / 其他 HTML：按选中的 .page 元素整页适应
         {
             int idx = sel >= 0 ? sel : -1;
             var js = $@"(function(){{
try {{
  var pages = document.querySelectorAll('.page');
  if (!pages.length) {{ window.scrollTo(0,0); return 'nopage'; }}
  var i = {idx};
  if (i < 0 || i >= pages.length) {{
    // 未选中时取视口内最靠上的那一页
    i = 0;
    for (var k = 0; k < pages.length; k++) {{
      var rr = pages[k].getBoundingClientRect();
      if (rr.bottom > 0) {{ i = k; break; }}
    }}
  }}
  var el = pages[i];
  var r = el.getBoundingClientRect();
  var vw = window.innerWidth, vh = window.innerHeight;
  var pw = r.width, ph = r.height;
  if (pw <= 0 || ph <= 0) return 'badsize';
  var ratio = Math.min((vw - 24) / pw, (vh - 24) / ph);
  el.setAttribute('data-ld-current','1');
  return JSON.stringify({{ratio: ratio, index: i, total: pages.length}});
}} catch(e) {{ return 'err:'+e.message; }}
}})();";
             var res = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(js);
             double ratio = 1.0; int fitIdx = 0;
             try
             {
                 var json = res;
                 if (json != null && json.StartsWith("\"")) json = JsonSerializer.Deserialize<string>(json);
                 if (!string.IsNullOrEmpty(json) && json.StartsWith("{"))
                 {
                     using var doc = JsonDocument.Parse(json);
                     ratio = doc.RootElement.GetProperty("ratio").GetDouble();
                     fitIdx = doc.RootElement.GetProperty("index").GetInt32();
                 }
             }
             catch { }

             if (ratio > 0 && !double.IsNaN(ratio) && !double.IsInfinity(ratio))
             {
                 _zoom = Math.Max(0.1, Math.Min(10.0, ratio));
                 PreviewWebView.ZoomFactor = _zoom;
                 // 缩放后页面几何变化，重新滚动到该页顶部
                 await Task.Delay(60);
                 await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                     $"(function(){{var ps=document.querySelectorAll('.page');if(ps.length>{fitIdx})ps[{fitIdx}].scrollIntoView({{block:'start'}});}})();");
                 StatusText.Text = $"已恢复视口 — 第{fitIdx + 1}页整页显示 缩放 {_zoom:P0}";
             }
             else
             {
                 _zoom = 1.0;
                 PreviewWebView.ZoomFactor = 1.0;
                 StatusText.Text = "已恢复视口";
             }
         }
     }
     catch (Exception ex)
     {
         StatusText.Text = $"恢复视口失败: {ex.Message}";
     }
 }

 ///<summary>
 /// 接收 WebView2 返回的等宽/等高计算结果
 /// </summary>
 private void PreviewWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
 {
 var msg = e.TryGetWebMessageAsString();
 if (msg != null && msg.StartsWith("fitw:"))
 {
 if (double.TryParse(msg.Substring(5), out var ratio))
 {
 _zoom = Math.Min(ratio, 10.0);
 ApplyZoom();
 StatusText.Text = $"等宽显示 — 缩放 {_zoom:P0}";
 }
 }
 else if (msg != null && msg.StartsWith("fith:"))
 {
 if (double.TryParse(msg.Substring(5), out var ratio))
 {
 _zoom = Math.Min(ratio, 10.0);
 ApplyZoom();
 StatusText.Text = $"等高显示 — 缩放 {_zoom:P0}";
 }
 }
 else if (msg != null && msg.Contains("\"type\":\"ready\""))
 {
 // cad-viewer 页面已就绪，推送待加载的 DWG/DXF
 if (!string.IsNullOrEmpty(_pendingCadFilePath) && File.Exists(_pendingCadFilePath))
 PostCadFileToViewer(_pendingCadFilePath);
 }
 }

 // ═════════════ 鼠标拖拽 + 区域选择 ═══════════════
 private FabWindow _fabWin; // AI 助手悬浮球（独立窗口，避免被 WebView2 HWND 盖住）
 private bool _isDragging = false;
 private bool _isMiddleDragging = false;
 private bool _isSelecting = false;
 private bool _isAreaOcrMode = false;
 private Point _dragStartScreenPos; // 相对窗口的坐标（不随滚动变化）
 private double _dragStartHOffset;
 private double _dragStartVOffset;
 private Point _selectStartScreenPos; // 选框起点（相对窗口）

 // ═══ OCR模式记忆 + 框选归一化坐标（供批量OCR复用） ═══
 private enum OcrMode { None, FullPageAuto, AreaAuto, FullPageOnline, AreaOnline }
 private OcrMode _lastOcrMode = OcrMode.None;
 private bool _areaOcrForceOnline = false; // 框选OCR是否强制在线
 // 框选区域归一化坐标（相对于页面宽高的比例，0~1）
 private double _lastAreaNormX, _lastAreaNormY, _lastAreaNormW, _lastAreaNormH;

 /// <summary>进入区域OCR模式</summary>
 private async void BtnOcrArea_Click(object sender, RoutedEventArgs e)
 {
 if (_currentFilePath == null) return;
 _isAreaOcrMode = !_isAreaOcrMode;
 if (_isAreaOcrMode)
 {
 // 框选期间隐藏 AI 悬浮球，避免挡住选框区域或被误点
 _fabWin?.Hide();
 // 进入框选模式时确定是自动还是在线
 if (!_areaOcrForceOnline)
 _areaOcrForceOnline = false; // 默认自动模式

 ModeHintText.Text = _areaOcrForceOnline
 ? "☁️ 在线区域OCR模式：在预览区拖拽选择矩形区域"
 : "🔲 区域OCR模式：在预览区拖拽选择矩形区域";
 ModeHint.Visibility = Visibility.Visible;
 PreviewGrid.Cursor = Cursors.Cross;

 // WebView2是HWND子窗口，WPF元素无法可靠拦截其鼠标事件
 // 方案：截取当前WebView2画面，隐藏WebView2，用WPF Image显示截图
 // 这样鼠标事件就是纯WPF的，可以正常框选
 // 若加载覆盖层还显示着（如刚打开文件），先隐藏避免截到转圈动画
 if (FileLoadingOverlay.Visibility == Visibility.Visible)
 FileLoadingOverlay.Visibility = Visibility.Collapsed;
 if (!_isCadMode && ImagePreview.Visibility != Visibility.Visible && PreviewWebView.Visibility == Visibility.Visible)
 {
 var snap = await CaptureWebViewAsync();
 if (snap != null)
 {
 ImagePreview.Source = snap;
 ImagePreview.Visibility = Visibility.Visible;
 PreviewWebView.Visibility = Visibility.Collapsed;
 _ocrSnapshotActive = true; // 标记：当前 ImagePreview 是WebView2截图，退出时需还原
 _zoom = 1.0;
 _panX = 0; _panY = 0;
 ApplyZoom();
 }
 }

 ModeHintText.Text = _areaOcrForceOnline
 ? "☁️ 在线区域OCR模式：在预览区拖拽选择矩形区域"
 : "🔲 区域OCR模式：在预览区拖拽选择矩形区域";
 OcrHitBlocker.IsHitTestVisible = true;
 OcrHitBlocker.Visibility = Visibility.Visible;
 }
 else
 {
 _areaOcrForceOnline = false; // 退出时重置
 _fabWin?.Show(); _fabWin?.Dispatcher.BeginInvoke((Action)UpdateFabWindowPosition); // 恢复 AI 悬浮球
 ModeHint.Visibility = Visibility.Collapsed;
 PreviewGrid.Cursor = null;
 SelectionRect.Visibility = Visibility.Collapsed;
 OcrHitBlocker.IsHitTestVisible = false;
 OcrHitBlocker.Visibility = Visibility.Collapsed;

 // 恢复WebView2显示（仅当 ImagePreview 中是WebView2截图时）
 if (_ocrSnapshotActive && ImagePreview.Visibility == Visibility.Visible && !_isCadMode)
 {
 _ocrSnapshotActive = false;
 ImagePreview.Visibility = Visibility.Collapsed;
 ImagePreview.Source = null;
 PreviewWebView.Visibility = Visibility.Visible;
 }
 }
 }

 private void PreviewArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
 {
 if (_currentFilePath == null) return;

 // 如果点击源是CadLayoutBar或其子元素（模型/布局切换按钮），不处理拖拽
 if (CadLayoutBar.Visibility == Visibility.Visible && CadLayoutBar.IsMouseOver)
 return;

 // 区域OCR模式下，确保OcrHitBlocker正在拦截WebView2
 if (_isAreaOcrMode && !OcrHitBlocker.IsHitTestVisible)
 {
 OcrHitBlocker.IsHitTestVisible = true;
 OcrHitBlocker.Visibility = Visibility.Visible;
 }

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
 else if (!_isCadMode && ImagePreview.Visibility != Visibility.Visible)
 {
 // WebView2 文档（PDF/DOCX/XLSX/HTML 等）：左键拖动透传给 Chromium 原生处理
 // （内置 PDF 查看器在 embed 隔离上下文中运行，应用 JS 平移无效；
 //   原生支持文本选择/拖动滚动，劫持反而让页面完全无法交互）
 _isDragging = false;
 return;
 }
 else
 {            // 拖拽模式 — 记录起点
            _isDragging = true;
            _dragStartScreenPos = e.GetPosition(this);
            _lastDragPos = _dragStartScreenPos;
            _dragStartHOffset = _panX;
            _dragStartVOffset = _panY;
            // 拖动前取消正在进行的重烘焙：后台栅格化会与 UI 拖动重绘抢渲染资源，
            // 导致拖动画面抖动/卡顿。取消后拖动全程只做位图贴图变换，结束再重烘焙。
            if (_isCadMode)
            {
                // 拖动前取消正在进行的重烘焙：后台栅格化会与 UI 拖动重绘抢渲染资源，
                // 导致拖动画面抖动/卡顿。取消后拖动全程只做位图贴图变换，结束再重烘焙。
                _cadReBakeCts?.Cancel();
                StartCadDragSnapshot();
            }
 // WebView2是HWND子窗口，会吞掉WPF鼠标事件
 // 在拖拽期间显示透明拦截层，让WPF接收鼠标移动事件
 if (!_isCadMode && ImagePreview.Visibility != Visibility.Visible && PreviewWebView?.CoreWebView2 != null)
 {
 OcrHitBlocker.IsHitTestVisible = true;
 OcrHitBlocker.Visibility = Visibility.Visible;
 }
 PreviewGrid.Cursor = Cursors.ScrollAll; // 抓手反馈
 PreviewGrid.CaptureMouse();
 e.Handled = true;
 }
 }

 private async void PreviewArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
 {
 if (_isSelecting)
 {
 _isSelecting = false;
 PreviewGrid.ReleaseMouseCapture();

 var rect = GetSelectionRectangle();
 LogRt($"MouseUp rect={rect.Width:F0}x{rect.Height:F0} selecting={_isSelecting}");
 if (rect.Width > 10 && rect.Height > 10)
 {
 // 获取真实页数
 LogRt("MouseUp before pagecount");
 int totalPages = await GetPdfPageCountAsync();
 LogRt($"MouseUp totalPages={totalPages}");
 if (totalPages > 1)
 {
 var ask = AskOcrAllPages(totalPages, "框选区域已确定。", "区域OCR范围");
 if (ask == null) return;   // 取消
 DoAreaOcr(rect, ask.Value);
 }
 else
 {
 DoAreaOcr(rect, false);
 }
 }

 SelectionRect.Visibility = Visibility.Collapsed;
 _isAreaOcrMode = false;
 _fabWin?.Show(); _fabWin?.Dispatcher.BeginInvoke((Action)UpdateFabWindowPosition); // 恢复 AI 悬浮球
 ModeHint.Visibility = Visibility.Collapsed;
 PreviewGrid.Cursor = null;
 OcrHitBlocker.IsHitTestVisible = false;
 OcrHitBlocker.Visibility = Visibility.Collapsed;

 // 恢复WebView2显示（仅当 ImagePreview 中是WebView2截图时）
 if (_ocrSnapshotActive && ImagePreview.Visibility == Visibility.Visible && !_isCadMode)
 {
 _ocrSnapshotActive = false;
 ImagePreview.Visibility = Visibility.Collapsed;
 ImagePreview.Source = null;
 PreviewWebView.Visibility = Visibility.Visible;
 }
 }            else if (_isDragging)
            {
                _isDragging = false;
                PreviewGrid.ReleaseMouseCapture();
                if (!_isAreaOcrMode) PreviewGrid.Cursor = null;
                // 隐藏WebView2拦截层（恢复正常交互）
                OcrHitBlocker.IsHitTestVisible = false;
                OcrHitBlocker.Visibility = Visibility.Collapsed;
                if (_isCadMode) { _cadFitToFull = false; EndCadDragSnapshot(); ScheduleCadReBake(); }
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
 // 拖拽平移
 var screenPos = e.GetPosition(this);
 double dx = screenPos.X - _dragStartScreenPos.X;
 double dy = screenPos.Y - _dragStartScreenPos.Y;
 double stepX = screenPos.X - _lastDragPos.X;
 double stepY = screenPos.Y - _lastDragPos.Y;
 _lastDragPos = screenPos;
 _panX = _dragStartHOffset + dx;
 _panY = _dragStartVOffset + dy;
 if (_isCadMode)
 {
 // CAD WebView2 SVG 模式：通过 JS 平移 SVG，不碰 WPF Transform（避免跨线程异常）
 if (PreviewWebView.Visibility == Visibility.Visible && PreviewWebView?.CoreWebView2 != null)
 {
 PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 $"(function(){{var svg=document.getElementById('cad-svg')||document.querySelector('#cad-wrap svg');if(!svg)return;" +
 $"svg.style.transform='translate({_panX.ToString(System.Globalization.CultureInfo.InvariantCulture)}px,{_panY.ToString(System.Globalization.CultureInfo.InvariantCulture)}px) scale({(1.0).ToString(System.Globalization.CultureInfo.InvariantCulture)})';" +
 $"var h=document.getElementById('hud');if(h)h.textContent='平移 {_panX:F0},{_panY:F0}';}})();");
 }            else
            {
                // CAD 视口图：平移 = 相对烘焙窗口的偏移（k=1，锚点无关）
                UpdateCadHostTransform(0, 0);
            }
 }
 else if (ImagePreview.Visibility == Visibility.Visible)
 {
 // 位图平移（本地PDF / 图片）：复用持久变换组，不覆盖缩放
 // 纵向边界：图片顶部不能盖住顶部菜单工具栏（≥预览区顶），小图不拖出视口；
 // 横向不限——允许图片拖到左侧侧栏（菜单）范围之上
 ClampImagePanY();
 ApplyImageTransform();
 }
 else if (PreviewWebView?.CoreWebView2 != null)
 {
 // WebView2（DOCX/pdf.js）：拖拽等于反向滚动，保持原生滚动条与渲染正确
 if (Math.Abs(stepX) > 0.01 || Math.Abs(stepY) > 0.01)
 {
 PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 $"(function(){{var sx={(-stepX).ToString(System.Globalization.CultureInfo.InvariantCulture)},sy={(-stepY).ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
 "var c=document.getElementById('viewerContainer');" +
 "if(c&&c.scrollHeight>c.clientHeight){c.scrollLeft+=sx;c.scrollTop+=sy;}else{window.scrollBy(sx,sy);}})();");
 }
 }
 }
 }

 // ═══════════════ 中键拖拽 ═══════════════
 private void PreviewArea_MouseDown(object sender, MouseButtonEventArgs e)
 {
 if (_currentFilePath == null) return;
 // 如果点击源是CadLayoutBar，不处理
 if (CadLayoutBar.Visibility == Visibility.Visible && CadLayoutBar.IsMouseOver)
 return;
 // WebView2 文档（PDF/DOCX/XLSX 等）：中键也透传给 Chromium 原生处理（自动滚动）
 if (!_isCadMode && ImagePreview.Visibility != Visibility.Visible && e.ChangedButton == MouseButton.Middle)
 {
 _isMiddleDragging = false;
 return;
 }
        if (e.ChangedButton == MouseButton.Middle)
        {
            _isMiddleDragging = true;
            _dragStartScreenPos = e.GetPosition(this);
            _lastDragPos = _dragStartScreenPos;
            _dragStartHOffset = _panX;
            _dragStartVOffset = _panY;
            if (_isCadMode) StartCadDragSnapshot();
 // WebView2是HWND子窗口，拖拽期间显示拦截层
 if (!_isCadMode && ImagePreview.Visibility != Visibility.Visible && PreviewWebView?.CoreWebView2 != null)
 {
 OcrHitBlocker.IsHitTestVisible = true;
 OcrHitBlocker.Visibility = Visibility.Visible;
 }
 PreviewGrid.CaptureMouse();
 e.Handled = true;
 }
 }

 /// <summary>限制图片/本地PDF 纵向平移：顶部不盖菜单工具栏，小图不拖出视口（横向自由）</summary>
 private void ClampImagePanY()
 {
 try
 {
 if (!(ImagePreview.Source is System.Windows.Media.Imaging.BitmapSource bs) || bs.PixelWidth <= 0 || bs.PixelHeight <= 0)
 return;
 double cw = PreviewGrid.ActualWidth, ch = PreviewGrid.ActualHeight;
 if (cw <= 0 || ch <= 0) return;
 double cx = cw / 2.0, cy = ch / 2.0;
 double us = Math.Min(cw / bs.PixelWidth, ch / bs.PixelHeight);
 double h = bs.PixelHeight * us * _zoom;
 double topMin = h / 2.0 - cy; // 顶部恰好贴预览区顶（再小会盖住顶部菜单工具栏）
 double botMax = ch - cy - h / 2.0; // 底部恰好贴预览区底
 if (h <= ch)
 {
 // 小图：整体限制在视口内
 _panY = Math.Max(topMin, Math.Min(_panY, botMax));
 }
 else
 {
 // 大图：只限制不能盖顶部菜单，允许向下滚动查看底部
 _panY = Math.Max(_panY, topMin);
 }
 }
 catch { }
 }

 private void PreviewArea_MouseUp(object sender, MouseButtonEventArgs e)
 {        if (_isMiddleDragging && e.ChangedButton == MouseButton.Middle)
        {
            _isMiddleDragging = false;
            PreviewGrid.ReleaseMouseCapture();
            if (!_isAreaOcrMode) PreviewGrid.Cursor = null;
            // 隐藏WebView2拦截层（恢复正常交互）
            OcrHitBlocker.IsHitTestVisible = false;
            OcrHitBlocker.Visibility = Visibility.Collapsed;
            if (_isCadMode) { EndCadDragSnapshot(); ScheduleCadReBake(); }
        }
    }

    /// <summary>超大画布（无 BitmapCache）拖动时栅格化位图快照，拖动只移动位图；松开恢复矢量图。</summary>
    private void StartCadDragSnapshot()
    {
        if (_cadImg == null || _cadDragSnapActive) return;
        if (_cadImg.CacheMode != null) return;   // 已有位图缓存，拖动本来就快
        if (_cadImg.Source is System.Windows.Media.Imaging.BitmapSource) return;  // 源已是位图，拖动只做贴图变换，无需再栅格化
        try
        {
            if (!(_cadImg.Source is DrawingImage di) || di.Drawing == null) return;
            double w = _cadImg.Width, h = _cadImg.Height;
            if (w <= 0 || h <= 0) return;
            double s = Math.Min(1.0, 6144.0 / Math.Max(w, h));
            int W = Math.Max(1, (int)(w * s)), H = Math.Max(1, (int)(h * s));
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(s, s));
                dc.DrawDrawing(di.Drawing);
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            if (rtb.PixelWidth <= 0 || rtb.PixelHeight <= 0) return;
            rtb.Freeze();
            _cadVectorSource = di;
            _cadImg.Source = rtb;
            _cadImg.Stretch = Stretch.Fill;   // 拉伸回原尺寸，拖动只移动位图
            _cadDragSnapActive = true;
        }
        catch { }
    }

    private void EndCadDragSnapshot()
    {
        if (!_cadDragSnapActive) return;
        _cadDragSnapActive = false;
        try
        {
            if (_cadImg != null && _cadVectorSource != null)
            {
                _cadImg.Source = _cadVectorSource;
                _cadImg.Stretch = Stretch.None;
            }
        }
        catch { }
        _cadVectorSource = null;
    }

 /// <summary>滚轮缩放（直接缩放，无需Ctrl）</summary>
 private void PreviewArea_MouseWheel(object sender, MouseWheelEventArgs e)
 {
 if (_isCadMode)
 {            // CAD 位图/WPF矢量模式：滚轮直接缩放（无需Ctrl），锚定光标位置
            // 先即时更新元素变换（反馈快），再防抖重烘焙使线条恢复 1px 细线
            double oldZoom = _zoom;
            double newZoom = e.Delta > 0 ? oldZoom * 1.15 : oldZoom / 1.15;
            newZoom = Math.Max(0.05, Math.Min(100.0, newZoom));
            if (Math.Abs(newZoom - oldZoom) > 1e-9)
            {
                var mousePos = e.GetPosition(CadHostCanvas);
                // 保持光标下的点不动：pan' = m - (m - pan) × z'/z
                _panX = mousePos.X - (mousePos.X - _panX) * newZoom / oldZoom;
                _panY = mousePos.Y - (mousePos.Y - _panY) * newZoom / oldZoom;
                _zoom = newZoom;
                _cadFitToFull = false;
                UpdateCadHostTransform(mousePos.X, mousePos.Y);
                ScheduleCadReBake();
                e.Handled = true;
            }
 }
 else if (ImagePreview.Visibility == Visibility.Visible)
 {
 // 位图模式（本地PDF/图片）：滚轮缩放并锚定光标位置
 double oldZoom = _zoom;
 double newZoom = e.Delta > 0 ? oldZoom * 1.15 : oldZoom / 1.15;
 newZoom = Math.Max(0.05, Math.Min(20.0, newZoom));
 if (Math.Abs(newZoom - oldZoom) > 1e-6 && oldZoom > 0)
 {
 var g = e.GetPosition(PreviewGrid);
 double cx = PreviewGrid.ActualWidth / 2.0;
 double cy = PreviewGrid.ActualHeight / 2.0;
 // 保持光标下的点不动：T' = g - C - (g - C - T) * z'/z
 _panX = g.X - cx - (g.X - cx - _panX) * newZoom / oldZoom;
 _panY = g.Y - cy - (g.Y - cy - _panY) * newZoom / oldZoom;
 }
 _zoom = newZoom;
 ApplyImageTransform();
 e.Handled = true;
 }
 else
 {
 // WebView2模式：Ctrl+滚轮缩放；普通滚轮转发给 Chromium 原生滚动
 if (Keyboard.Modifiers == ModifierKeys.Control)
 {
 if (e.Delta > 0) BtnZoomIn_Click(null, null);
 else BtnZoomOut_Click(null, null);
 e.Handled = true;
 }
 else
 {
 ForwardWheelToWebView(e);
 }
 }
 }

 /// <summary>
 /// 把 WPF 滚轮事件转发给 WebView2（CDP Input.dispatchMouseEvent）。
 /// WM_MOUSEWHEEL 只发给焦点窗口（WPF 主窗口），WebView2 收不到 → PDF/DOCX 无法滚轮滚动。
 /// 用 CDP 直达渲染管线，行为与浏览器一致：普通滚轮逐像素平滑滚动，
 /// 触摸板高频小增量自动保持惯性手感。120 刻度 = 40px（Chromium 同款换算）。
 /// </summary>
 private async void ForwardWheelToWebView(MouseWheelEventArgs e)
 {
 var wv = PreviewWebView;
 if (wv?.CoreWebView2 == null) return;
 if (_isCadMode || ImagePreview.Visibility == Visibility.Visible) return;
 if (CadScrollViewer.Visibility == Visibility.Visible) return;

 try
 {
 var pos = e.GetPosition(PreviewGrid);
 double deltaY = e.Delta / 3.0; // 120 → 40px，与 Chromium 换算一致
 var payload = System.Text.Json.JsonSerializer.Serialize(new
 {
 type = "mouseWheel",
 x = Math.Max(0, pos.X),
 y = Math.Max(0, pos.Y),
 deltaX = 0.0,
 deltaY = deltaY,
 deltaMode = 0 // 0 = 像素
 });
 try { System.IO.File.AppendAllText(@"D:\ZCODE\_gui_test\wheel_fwd.log", $"[{DateTime.Now:HH:mm:ss.fff}] wheel delta={e.Delta} pos={pos.X:F0},{pos.Y:F0}\n"); } catch { }
 var resp = await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", payload);
 try { System.IO.File.AppendAllText(@"D:\ZCODE\_gui_test\wheel_fwd.log", $"[{DateTime.Now:HH:mm:ss.fff}] cdp resp={resp}\n"); } catch { }
 e.Handled = true;
 }
 catch (Exception ex) { try { System.IO.File.AppendAllText(@"D:\ZCODE\_gui_test\wheel_fwd.log", $"[{DateTime.Now:HH:mm:ss.fff}] EX {ex.Message}\n"); } catch { } }
 }

 /// <summary>获取选框在 PreviewGrid 坐标系中的矩形</summary>
 private Rect GetSelectionRectangle()
 {
 double x = Canvas.GetLeft(SelectionRect);
 double y = Canvas.GetTop(SelectionRect);
 return new Rect(x, y, SelectionRect.Width, SelectionRect.Height);
 }

 /// <summary>区域OCR：将选框映射到原图坐标，裁剪后OCR</summary>
 private void DoAreaOcr(Rect screenRect, bool allPages = false)
 {
 ThreadPool.QueueUserWorkItem(async _ =>
 {
 var tempFiles = new List<string>();
 try
 {
 LogRt($"DoAreaOcr enter rect={screenRect} allPages={allPages}");
 // 获取总页数和当前页
 int totalPages = allPages
 ? await Dispatcher.Invoke(async () => await GetPdfPageCountAsync())
 : 1;
 int currentPage = await Dispatcher.Invoke(async () => await GetCurrentPdfPageAsync());
 LogRt($"DoAreaOcr pages={totalPages} current={currentPage}");

 var pagesToOcr = allPages
 ? Enumerable.Range(1, totalPages).ToList()
 : new List<int> { currentPage };

 var allText = new StringBuilder();
 int totalLines = 0;

 bool forceOnline = _areaOcrForceOnline;
 string ocrLabel = forceOnline ? "在线区域OCR" : "区域OCR";
 ShowOcrProgress(allPages ? $"{ocrLabel}（全部 {totalPages} 页）..." : $"{ocrLabel}...");

 // 选框坐标（PreviewGrid 坐标系，即 WebView2 内坐标）
 int selX = (int)screenRect.X;
 int selY = (int)screenRect.Y;
 int selW = Math.Max(10, (int)screenRect.Width);
 int selH = Math.Max(10, (int)screenRect.Height);

 // ═══ 记录归一化选区坐标（相对于页面宽高的比例），供批量OCR复用 ═══
 // 注意：WPF/WebView2 控件只能从 UI 线程访问，先把需要的状态封送到 UI 线程取回
 bool cadMode = false, imgVisible = false, wvReady = false;
 Dispatcher.Invoke(() =>
 {
 cadMode = _isCadMode;
 imgVisible = ImagePreview.Visibility == Visibility.Visible;
 wvReady = PreviewWebView?.CoreWebView2 != null;
 });
 if (!cadMode && wvReady)
 {
 try
 {
 var pageRectOpt = await Dispatcher.Invoke(async () => await GetPageRectAsync(currentPage));
 if (pageRectOpt.HasValue)
 {
 var pr = pageRectOpt.Value;
 if (pr.Width > 0 && pr.Height > 0)
 {
 _lastAreaNormX = Math.Max(0, Math.Min(1, (selX - pr.X) / pr.Width));
 _lastAreaNormY = Math.Max(0, Math.Min(1, (selY - pr.Y) / pr.Height));
 _lastAreaNormW = Math.Max(0.01, Math.Min(1, selW / pr.Width));
 _lastAreaNormH = Math.Max(0.01, Math.Min(1, selH / pr.Height));
 }
 }
 }
 catch { }
 }
 else if (cadMode)
 {
 double canvasW = 0, canvasH = 0;
 Dispatcher.Invoke(() => { canvasW = CadHostCanvas.ActualWidth; canvasH = CadHostCanvas.ActualHeight; });
 if (canvasW > 0 && canvasH > 0)
 {
 _lastAreaNormX = selX / canvasW;
 _lastAreaNormY = selY / canvasH;
 _lastAreaNormW = Math.Max(0.01, selW / canvasW);
 _lastAreaNormH = Math.Max(0.01, selH / canvasH);
 }
 }
 else if (imgVisible)
 {
 double dispW = 0, dispH = 0;
 Dispatcher.Invoke(() => { dispW = PreviewGrid.ActualWidth; dispH = PreviewGrid.ActualHeight; });
 if (dispW > 0 && dispH > 0)
 {
 _lastAreaNormX = selX / dispW;
 _lastAreaNormY = selY / dispH;
 _lastAreaNormW = Math.Max(0.01, selW / dispW);
 _lastAreaNormH = Math.Max(0.01, selH / dispH);
 }
 }

 // 记录OCR模式
 _lastOcrMode = forceOnline ? OcrMode.AreaOnline : OcrMode.AreaAuto;

 for (int idx = 0; idx < pagesToOcr.Count; idx++)
 {
 int pageNum = pagesToOcr[idx];
 int progress = (int)((double)idx / pagesToOcr.Count * 100);

 if (pagesToOcr.Count > 1)
 {
 UpdateOcrProgress(progress, $"跳转到第 {pageNum}/{pagesToOcr.Count} 页...");
 // 批量OCR时需要恢复WebView2来翻页截图（本地PDF模式除外，它本身就是位图渲染）
 await Dispatcher.Invoke(async () =>
 {
 if (!_isLocalPdfMode && ImagePreview.Visibility == Visibility.Visible && !_isCadMode)
 {
 _ocrSnapshotActive = false;
 ImagePreview.Visibility = Visibility.Collapsed;
 ImagePreview.Source = null;
 PreviewWebView.Visibility = Visibility.Visible;
 }
 await NavigateToPdfPageAsync(pageNum);
 });
 // 等待页面渲染
 await Task.Delay(500);
 }

 UpdateOcrProgress(progress + 5, $"截取第 {pageNum} 页选区...");

 // 获取预览区屏幕尺寸
 int screenW = 0, screenH = 0;
 Dispatcher.Invoke(() =>
 {
 screenW = (int)PreviewGrid.ActualWidth;
 screenH = (int)PreviewGrid.ActualHeight;
 });

 string tempImg = null;

 // ═══ 图片模式：直接从原始文件裁剪（高分辨率，不经过屏幕截图） ═══
 if (!cadMode && imgVisible
 && !string.IsNullOrEmpty(_currentImageForOcr) && File.Exists(_currentImageForOcr))
 {
 tempImg = CropImageFromFile(_currentImageForOcr, selX, selY, selW, selH, screenW, screenH, tempFiles, 2);
 if (tempImg == null)
 {
 Dispatcher.Invoke(() => StatusText.Text = "选区无效或超出图片范围");
 continue;
 }
 }
 // ═══ CAD模式：高DPI位图渲染（300 DPI而非屏幕96 DPI） ═══
 else if (cadMode)
 {
 BitmapSource highResImg = null;
 Dispatcher.Invoke(() =>
 {
 try { highResImg = _previewSvc?.RenderCadPageToBitmap(pageNum, 300, 0); } catch { }
 });
 if (highResImg == null)
 highResImg = await CaptureCadAsync(); // 降级到屏幕截图
 if (highResImg == null)
 {
 Dispatcher.Invoke(() => StatusText.Text = "CAD渲染失败");
 continue;
 }
 // CAD高DPI渲染图需要按缩放比例换算选区坐标
 int imgW = highResImg.PixelWidth;
 int imgH = highResImg.PixelHeight;
 // 屏幕显示尺寸 vs 高DPI渲染尺寸的比例
 double cadDpiScale = (double)imgW / Math.Max(1, screenW);
 int cropX = (int)(selX * cadDpiScale);
 int cropY = (int)(selY * cadDpiScale);
 int cropW = (int)(selW * cadDpiScale);
 int cropH = (int)(selH * cadDpiScale);
 cropX = Math.Max(0, Math.Min(cropX, imgW - 10));
 cropY = Math.Max(0, Math.Min(cropY, imgH - 10));
 cropW = Math.Min(cropW, imgW - cropX);
 cropH = Math.Min(cropH, imgH - cropY);
 if (cropW <= 10 || cropH <= 10) continue;
 tempImg = CropAndUpscale(highResImg, cropX, cropY, cropW, cropH, tempFiles, 2);
 }   // ═══ WebView2/PDF模式：屏幕截图 + 放大3倍 ═══
   else
   {
   BitmapSource fullImg = await CaptureWebViewAsync();
   if (fullImg == null)
   {
   Dispatcher.Invoke(() => StatusText.Text = "截屏失败");
   continue;
   }   int imgW = fullImg.PixelWidth;
   int imgH = fullImg.PixelHeight;
   // 临时诊断：CDP截图尺寸 vs 选区坐标 vs PreviewGrid尺寸
   LogRt($"AREA-DIAG: cdp={imgW}x{imgH} sel={selX},{selY},{selW}x{selH} grid={screenW}x{screenH}");
   // 多页模式：用归一化选区坐标（相对页面矩形的比例）换算到当前页位置，
   // 兼容不同页面尺寸（docx 等自研分页器每页高度可能不同，绝对坐标会错位）
   int cropX, cropY, cropW, cropH;
   if (pagesToOcr.Count > 1)
   {
       var pr = await Dispatcher.Invoke(async () => await GetPageRectAsync(pageNum));
       if (pr.HasValue && pr.Value.Width > 0 && pr.Value.Height > 0)
       {
           var r = pr.Value;
           cropX = (int)(r.X + _lastAreaNormX * r.Width);
           cropY = (int)(r.Y + _lastAreaNormY * r.Height);
           cropW = (int)(_lastAreaNormW * r.Width);
           cropH = (int)(_lastAreaNormH * r.Height);
       }
       else { cropX = selX; cropY = selY; cropW = selW; cropH = selH; }
   }
   else { cropX = selX; cropY = selY; cropW = selW; cropH = selH; }
   cropX = Math.Max(0, Math.Min(cropX, imgW - 10));
   cropY = Math.Max(0, Math.Min(cropY, imgH - 10));
   cropW = Math.Min(cropW, imgW - cropX);
   cropH = Math.Min(cropH, imgH - cropY);
   if (cropW <= 10 || cropH <= 10) continue;
   tempImg = CropAndUpscale(fullImg, cropX, cropY, cropW, cropH, tempFiles, 3);
   try { SaveCropDebug(fullImg, cropX, cropY, cropW, cropH); } catch { }
   }

 if (tempImg == null) continue; UpdateOcrProgress(progress + 10, $"OCR 识别第 {pageNum} 页选区...");
  var (okP, textP, errP) = await RunOcrAsync(tempImg, false, forceOnline, false);

 // 识别完立即删除裁剪图
 try { File.Delete(tempImg); } catch { }
 tempFiles.Remove(tempImg);

 if (okP)
 {
 var pageText = TextNormalizer.Normalize(textP);
 if (!string.IsNullOrWhiteSpace(pageText))
 {
 if (pagesToOcr.Count > 1)
 allText.AppendLine($"═══ 第 {pageNum} 页 ═══");
 allText.AppendLine(pageText);
 var lines = pageText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
 totalLines += lines.Length;
 }
 }
 }

 UpdateOcrProgress(100, "识别完成");
 Dispatcher.Invoke(() =>
 {
 Progress.Value = 100;
 if (allText.Length > 0)
 {
 _lastOcrText = allText.ToString();									StatusText.Text = pagesToOcr.Count > 1
										? $"{ocrLabel}完成 — {totalLines} 行（{pagesToOcr.Count} 页）"
										: $"{ocrLabel}完成 — {totalLines} 行";
									LogRt($"OCR TEXT RESULT:\n{_lastOcrText}");
 var preview = _lastOcrText.Length > 3000
 ? _lastOcrText.Substring(0, 3000) + "\n\n... (文本已截断)"
 : _lastOcrText;
 ShowOcrDone(true, $"{totalLines} 行（{pagesToOcr.Count} 页）", () => PushToAi($"{ocrLabel}识别结果", $"{ocrLabel}识别完成（{pagesToOcr.Count} 页）：\n\n```\n{preview}\n```"));

 }
 else
{ StatusText.Text = "区域OCR未识别到文字"; ShowOcrDone(false, "未识别到文字"); }
 });
 }
 catch (Exception ex)
 {
 LogRt($"DoAreaOcr EXCEPTION: {ex}");
 Dispatcher.Invoke(() => StatusText.Text = $"区域OCR错误: {ex.Message}");
 HideOcrProgress();
 }
 finally
 {
 // 最终清理：确保所有临时文件删除
 foreach (var f in tempFiles)
 if (File.Exists(f)) try { File.Delete(f); } catch { }
 }
 });
 }

 // ═════════════ OCR — 截屏 WebView2 → PaddleOCR ═════════════
 private async void BtnOcr_Click(object sender, RoutedEventArgs e)
 {
 _lastOcrMode = OcrMode.FullPageAuto;
 await ExecuteOcrAsync(false, false);
 }

 // ☁️ 在线OCR按钮：强制在线，不回退本地
 private async void BtnOcrOnline_Click(object sender, RoutedEventArgs e)
 {
 if (!_onlineOcr.IsConfigured)
 {
 MessageBox.Show("在线OCR未配置。\n\n请先点击「🌐 设置」配置在线OCR的API地址和密钥。",
 "提示", MessageBoxButton.OK, MessageBoxImage.Information);
 return;
 }
 _lastOcrMode = OcrMode.FullPageOnline;
 await ExecuteOcrAsync(true, false);
 }

 // ☁️ 在线区域OCR按钮：进入框选模式，完成后强制在线识别
 private void BtnOcrOnlineArea_Click(object sender, RoutedEventArgs e)
 {
 if (!_onlineOcr.IsConfigured)
 {
 MessageBox.Show("在线OCR未配置。\n\n请先点击「🌐 设置」配置在线OCR的API地址和密钥。",
 "提示", MessageBoxButton.OK, MessageBoxImage.Information);
 return;
 }
 _areaOcrForceOnline = true;
 // 复用 BtnOcrArea_Click 的框选逻辑（async void，不能await）
 BtnOcrArea_Click(sender, e);
 }

 // ═══ 多页 OCR 范围询问：带超时自动默认当前页 + 记住上次选择 ═══
        // -2=首次未决策；0=上次选“仅当前页”；1=上次选“全部页”
        private static int _ocrRangeChoice = -2;
        private const int OcrRangeAskTimeoutSec = 6;
        private static string OcrRangeChoiceFile =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_range_choice.txt");

        private static int LoadOcrRangeChoice()
        {
            try
            {
                if (File.Exists(OcrRangeChoiceFile))
                {
                    var v = File.ReadAllText(OcrRangeChoiceFile).Trim();
                    _ocrRangeChoice = v == "all" ? 1 : 0;
                }
                else _ocrRangeChoice = -1; // 已初始化但从未选过 → 首次弹窗
            }
            catch { _ocrRangeChoice = -1; }
            return _ocrRangeChoice;
        }

        private static void SaveOcrRangeChoice(int choice)
        {
            try { File.WriteAllText(OcrRangeChoiceFile, choice == 1 ? "all" : "current"); } catch { }
        }

        /// <summary>
        /// 询问多页 OCR 范围：返回 true=全部页, false=仅当前页, null=取消。
        /// 已记住上次选择 → 直接复用不弹窗；首次 → 弹带倒计时的对话框，
        /// 超时（6 秒）自动默认“仅当前页”，避免用户干等。</summary>
        private bool? AskOcrAllPages(int totalPages, string messagePrefix, string title)
        {
            // 从记忆文件加载上次选择（未初始化时）
            if (_ocrRangeChoice < -1) LoadOcrRangeChoice();
            // 已记住 → 直接复用（无等待）
            if (_ocrRangeChoice >= 0)
            {
                LogRt($"OCR范围: 复用上次选择={( _ocrRangeChoice == 1 ? "全部页" : "仅当前页")}");
                return _ocrRangeChoice == 1;
            }

            // 首次：带倒计时自动默认的对话框
            var win = new Window
            {
                Title = title,
                Width = 470,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFC)),
            };
            var root = new StackPanel { Margin = new Thickness(18) };
            var hint = new TextBlock
            {
                Text = $"{messagePrefix}\n\n当前文档共 {totalPages} 页。\n\n选择后将在预览区显示识别进度条。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3B)),
            };
            root.Children.Add(hint);

            var options = new TextBlock
            {
                Text = $"  是 — 仅识别当前页\n  否 — 识别全部页（同一选区范围自动套用）",
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 4),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x4C)),
            };
            root.Children.Add(options);

            // 倒计时提示：超时自动默认“仅当前页”
            int remaining = OcrRangeAskTimeoutSec;
            var countdown = new TextBlock
            {
                Text = $"{remaining} 秒后自动选择「仅当前页」…",
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 12),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90)),
            };
            root.Children.Add(countdown);

            int _result = 0;   // 0=仅当前页, 1=全部页, -1=取消
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button MakeBtn(string text, int choice, bool primary = false)
            {
                var b = new Button
                {
                    Content = text,
                    MinWidth = 92,
                    MinHeight = 30,
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(10, 2, 10, 2),
                    FontSize = 13,
                };
                if (primary) b.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xE8));
                b.Click += (_, __) => { _result = choice; win.Close(); };
                btnRow.Children.Add(b);
                return b;
            }
            MakeBtn("是(Y) 当前页", 0, primary: true);
            MakeBtn("否(N) 全部页", 1);
            MakeBtn("取消", -1);
            root.Children.Add(btnRow);
            win.Content = root;

            // 倒计时定时器：归零自动选“仅当前页”并关闭
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, __) =>
            {
                remaining--;
                if (remaining <= 0)
                {
                    timer.Stop();
                    countdown.Text = "已自动选择「仅当前页」";
                    _result = 0;
                    win.Close();
                }
                else countdown.Text = $"{remaining} 秒后自动选择「仅当前页」…";
            };
            timer.Start();
            win.ShowDialog();
            timer.Stop();

            if (_result < 0) return null; // 取消
            SaveOcrRangeChoice(_result);
            LogRt($"OCR范围: 用户选择={( _result == 1 ? "全部页" : "仅当前页")}，已记住下次复用");
            return _result == 1;
        }

        // OCR执行核心：forceOnline=true强制在线；forceLocal=true强制本地；两者皆false=自动模式
        private async Task ExecuteOcrAsync(bool forceOnline, bool forceLocal)
 {
 if (_currentFilePath == null)
 {
 StatusText.Text = "请先打开文件，再进行 OCR 识别";
 return;
 }

 string modeLabel = forceOnline ? "（在线）" : (forceLocal ? "（本地）" : "");

 // 询问OCR范围：仅当前页 / 全部页
 int totalPages = 1;
 if (!_isCadMode && PreviewWebView?.CoreWebView2 != null)
 {
 totalPages = await GetPdfPageCountAsync();
 }
 else if (_isCadMode)
 {
 totalPages = _previewSvc?.PageNames?.Count ?? 1;
 }
 bool ocrAllPages = false;
 if (totalPages > 1)
 {
 var ask = AskOcrAllPages(totalPages, "当前文档为多页文档。", $"OCR范围{modeLabel}");
 if (ask == null) return;   // 取消
 ocrAllPages = ask.Value;
 }

 int startPage = ocrAllPages ? 1 : (_isCadMode ? _currentPage + 1 : await GetCurrentPdfPageAsync());
 int endPage = ocrAllPages ? totalPages : startPage;
 var pagesToOcr = Enumerable.Range(startPage, endPage - startPage + 1).ToList();

 ThreadPool.QueueUserWorkItem(async _ =>
 {
 Dispatcher.Invoke(() => { StatusText.Text = $"OCR 识别中{modeLabel}..."; Progress.Value = 0; });
 ShowOcrProgress($"正在截屏{modeLabel}...");

 var tempFiles = new List<string>();
try
{
 // 如果是直接加载的图片文件，直接 OCR（不走 WebView2）
 var ext = Path.GetExtension(_currentFilePath).ToLower();
 if (_currentImageForOcr != null && File.Exists(_currentImageForOcr) &&
 (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tiff" || ext == ".tif" || ext == ".webp"))
 {
 var tempImg = _currentImageForOcr;
 UpdateOcrProgress(50, $"OCR 识别中{modeLabel}...");

 var (ok0, text0, err0) = await RunOcrAsync(tempImg, false, forceOnline, forceLocal);

 UpdateOcrProgress(100, "识别完成");
 Dispatcher.Invoke(() =>
 {
 Progress.Value = 100;
 if (ok0)
 {
 _lastOcrText = TextNormalizer.Normalize(text0);
 var lines = _lastOcrText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
 StatusText.Text = $"OCR 完成{modeLabel} — {lines.Length} 行";
 var preview = _lastOcrText.Length > 2000 ? _lastOcrText.Substring(0, 2000) + "\n\n... (文本已截断)" : _lastOcrText;
 ShowOcrDone(true, $"{lines.Length} 行", () => PushToAi($"OCR 识别结果{modeLabel}", $"识别到 **{lines.Length}** 行文字：\n\n```\n{preview}\n```"));

 }
 else { StatusText.Text = $"OCR 失败{modeLabel}"; ShowOcrDone(false, err0 ?? "未识别到文字", () => PushToAi($"OCR 失败{modeLabel}", err0 ?? "未知错误")); }
 });
 }

 // CAD模式：高分辨率渲染 + 分条 OCR（整图送 OCR 会被压缩到 1024px 丢失小字）
 if (_isCadMode)
 {
 UpdateOcrProgress(20, "高分辨率CAD渲染中...");
 List<string> cadStrips = null;
 try { cadStrips = _previewSvc?.RenderCadOcrStrips(_currentPage, Path.GetTempPath(), _cadShxFontName, _cadBigShxFontName, _cadUseBigFont); } catch { }
 if (cadStrips == null || cadStrips.Count == 0)
 {
 // 降级：整页渲染 / 屏幕截图
 BitmapSource cadImg = null;
 Dispatcher.Invoke(() =>
 {
 try { cadImg = _previewSvc?.RenderCadPageToBitmap(_currentPage, 300, 0); } catch { }
 });
 if (cadImg == null) cadImg = await CaptureCadAsync();
 if (cadImg == null)
 {
 Dispatcher.Invoke(() => StatusText.Text = "CAD渲染失败");
 ShowOcrDone(false, "CAD渲染失败");
 return;
 }
 var tempImgPath = Path.GetTempFileName() + ".png";
 tempFiles.Add(tempImgPath);
 var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
 encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cadImg));
 using (var fs = File.OpenWrite(tempImgPath)) encoder.Save(fs);
 cadStrips = new List<string> { tempImgPath };
 }

 // 逐条 OCR + 去重合并
 var ocrText = new StringBuilder();
 var seenLines = new HashSet<string>(StringComparer.Ordinal);
 int stripTotal = cadStrips.Count;
 bool anyOk = false;
 for (int si = 0; si < stripTotal; si++)
 {
 UpdateOcrProgress(20 + 65 * (si + 1) / stripTotal,
 stripTotal > 1 ? $"OCR 识别中{modeLabel}（{si + 1}/{stripTotal}）..." : $"OCR 识别中{modeLabel}...");
 var (ok, text, err) = await RunOcrAsync(cadStrips[si], false, forceOnline, forceLocal);
 if (ok && !string.IsNullOrWhiteSpace(text))
 {
 anyOk = true;
 foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
 {
 var trimmed = line.Trim();
 if (trimmed.Length == 0) continue;
 if (seenLines.Add(trimmed)) ocrText.AppendLine(trimmed);
 }
 }
 }

 UpdateOcrProgress(100, "识别完成");
 Dispatcher.Invoke(() =>
 {
 Progress.Value = 100;
 if (anyOk && ocrText.Length > 0)
 {
 _lastOcrText = TextNormalizer.Normalize(ocrText.ToString());
 var lines = _lastOcrText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
 StatusText.Text = $"OCR 完成{modeLabel} — {lines.Length} 行";
 var preview = _lastOcrText.Length > 2000 ? _lastOcrText.Substring(0, 2000) + "\n\n... (文本已截断)" : _lastOcrText;
 ShowOcrDone(true, $"{lines.Length} 行", () => PushToAi($"OCR 识别结果{modeLabel}", $"识别到 **{lines.Length}** 行文字：\n\n```\n{preview}\n```"));

 }
 else { StatusText.Text = $"OCR 失败{modeLabel}"; ShowOcrDone(false, "未识别到文字", () => PushToAi($"OCR 失败{modeLabel}", "未识别到文字（图纸可能不含文字或文字过小）")); }
 });
 return;
 }

 // WebView2 模式：逐页截屏 + OCR
 var allText = new StringBuilder();
 int totalLines = 0;

 for (int idx = 0; idx < pagesToOcr.Count; idx++)
 {
 int pageNum = pagesToOcr[idx];
 int progress = (int)((double)idx / pagesToOcr.Count * 100);

 if (pagesToOcr.Count > 1)
 {
 UpdateOcrProgress(progress, $"跳转到第 {pageNum}/{pagesToOcr.Count} 页...");
 await Dispatcher.Invoke(async () => await NavigateToPdfPageAsync(pageNum));
 }

 UpdateOcrProgress(progress + 5, $"截取第 {pageNum} 页...");

 // 截取整个画面：CAD模式截CadScrollViewer，图片模式截PreviewGrid，其他截WebView2
 BitmapSource fullImg;
 if (_isCadMode)
 fullImg = await CaptureCadAsync();
 else if (ImagePreview.Visibility == Visibility.Visible)
 fullImg = await CapturePreviewAreaAsync();
 else
 fullImg = await CaptureWebViewAsync();
 if (fullImg == null)
 {
 Dispatcher.Invoke(() => StatusText.Text = "截屏失败");
 continue;
 }

 // 尝试获取该页在 WebView2 中的精确区域
 var pageRect = await Dispatcher.Invoke(async () => await GetPageRectAsync(pageNum));

 string tempImgPath;
 if (pageRect.HasValue)
 {
 // 按页面区域裁剪并放大3倍
 var r = pageRect.Value;
 int imgW = fullImg.PixelWidth;
 int imgH = fullImg.PixelHeight;
 int cropX = Math.Max(0, r.X);
 int cropY = Math.Max(0, r.Y);
 int cropW = Math.Min(r.Width, imgW - cropX);
 int cropH = Math.Min(r.Height, imgH - cropY);
 if (cropW > 10 && cropH > 10)
 tempImgPath = CropAndUpscale(fullImg, cropX, cropY, cropW, cropH, tempFiles, 3);
 else
 {
 // 区域无效，用全图（直接保存，不放大）
 tempImgPath = Path.GetTempFileName() + ".png";
 tempFiles.Add(tempImgPath);
 var enc3 = new System.Windows.Media.Imaging.PngBitmapEncoder();
 enc3.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(fullImg));
 using (var fs = File.OpenWrite(tempImgPath)) enc3.Save(fs);
 }
 }
 else
 {
 // 非pdf.js模式（DWG/XLSX等），用全图
 tempImgPath = Path.GetTempFileName() + ".png";
 tempFiles.Add(tempImgPath);
 var enc4 = new System.Windows.Media.Imaging.PngBitmapEncoder();
 enc4.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(fullImg));
 using (var fs = File.OpenWrite(tempImgPath)) enc4.Save(fs);
 }

 UpdateOcrProgress(progress + 10, $"OCR 识别第 {pageNum} 页{modeLabel}...");

 var (okP, textP, errP) = await RunOcrAsync(tempImgPath, false, forceOnline, forceLocal);

 if (okP)
 {
 var pageText = TextNormalizer.Normalize(textP);
 if (!string.IsNullOrWhiteSpace(pageText))
 {
 if (pagesToOcr.Count > 1)
 allText.AppendLine($"═══ 第 {pageNum} 页 ═══");
 allText.AppendLine(pageText);
 var lines = pageText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
 totalLines += lines.Length;
 }
 }
 }

 UpdateOcrProgress(100, "识别完成");
 Dispatcher.Invoke(() =>
 {
 Progress.Value = 100;
 if (allText.Length > 0)
 {
 _lastOcrText = allText.ToString();
 StatusText.Text = $"OCR 完成{modeLabel} — {totalLines} 行（{pagesToOcr.Count} 页）";
 var preview = _lastOcrText.Length > 3000 ? _lastOcrText.Substring(0, 3000) + "\n\n... (文本已截断)" : _lastOcrText;
 ShowOcrDone(true, $"{totalLines} 行（{pagesToOcr.Count} 页）", () => PushToAi($"OCR 识别结果{modeLabel}", $"识别到 **{totalLines}** 行文字（{pagesToOcr.Count} 页）：\n\n```\n{preview}\n```"));

 }
 else { StatusText.Text = $"OCR 未识别到文字{modeLabel}"; ShowOcrDone(false, "未识别到文字", () => PushToAi($"OCR 结果{modeLabel}", "未识别到文字")); }
 });
}
 catch (Exception ex)
 {
 try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_init.log"), $"[BtnOcr] Exception: {ex}\n"); } catch { }
 Dispatcher.Invoke(() => StatusText.Text = $"OCR 错误: {ex.Message}");
 }
 finally
 {
 // 清理所有临时文件
 foreach (var f in tempFiles)
 if (File.Exists(f) && f != _currentImageForOcr) try { File.Delete(f); } catch { }
}
});
}

 // ═════════════ 裁剪+放大辅助方法 ═════════════

 /// 裁剪截图的指定区域并放大（高质量插值），返回临时PNG文件路径
 private string CropAndUpscale(BitmapSource fullImg, int cropX, int cropY, int cropW, int cropH, List<string> tempFiles, int scale = 3)
 {
 var tempFull = Path.GetTempFileName() + ".png";
 tempFiles.Add(tempFull);
 var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
 encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(fullImg));
 using (var fs = File.OpenWrite(tempFull)) encoder.Save(fs);

 int upW = cropW * scale, upH = cropH * scale;
 var tempImg = Path.GetTempFileName() + ".png";
 tempFiles.Add(tempImg);
 using (var fullBmp = new System.Drawing.Bitmap(tempFull))
 using (var croppedBmp = new System.Drawing.Bitmap(upW, upH, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
 using (var g = System.Drawing.Graphics.FromImage(croppedBmp))
 {
 g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
 g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
 g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
 g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
 g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
 g.DrawImage(fullBmp,
 new System.Drawing.Rectangle(0, 0, upW, upH),
 new System.Drawing.Rectangle(cropX, cropY, cropW, cropH),
 System.Drawing.GraphicsUnit.Pixel);
 croppedBmp.Save(tempImg, System.Drawing.Imaging.ImageFormat.Png);
 }
 try { File.Delete(tempFull); } catch { }
 tempFiles.Remove(tempFull);
 return tempImg;
 }

 /// 从原始图片文件直接裁剪选区（高分辨率，不经过屏幕截图）
 private string CropImageFromFile(string imagePath, int selX, int selY, int selW, int selH,
 int screenW, int screenH, List<string> tempFiles, int scale = 2)
 {
 try
 {
 using (var origBmp = new System.Drawing.Bitmap(imagePath))
 {
 int origW = origBmp.Width;
 int origH = origBmp.Height;

 // 图片在屏幕上的显示尺寸（Stretch=Uniform，按PreviewGrid区域居中）
 double dispW, dispH, dispX, dispY;
 double imgRatio = (double)origW / origH;
 double screenRatio = (double)screenW / screenH;
 if (imgRatio > screenRatio)
 {
 dispW = screenW * _zoom;
 dispH = dispW / imgRatio;
 }
 else
 {
 dispH = screenH * _zoom;
 dispW = dispH * imgRatio;
 }
 dispX = (screenW - dispW) / 2.0 + _panX;
 dispY = (screenH - dispH) / 2.0 + _panY;

 // 选区在显示坐标系 → 原始图片坐标系
 double sx = (selX - dispX) / dispW * origW;
 double sy = (selY - dispY) / dispH * origH;
 double sw = selW / dispW * origW;
 double sh = selH / dispH * origH;

 int cropX = Math.Max(0, (int)sx);
 int cropY = Math.Max(0, (int)sy);
 int cropW = Math.Min((int)sw, origW - cropX);
 int cropH = Math.Min((int)sh, origH - cropY);
 if (cropW < 10 || cropH < 10) return null;

 int upW = cropW * scale, upH = cropH * scale;
 var tempImg = Path.GetTempFileName() + ".png";
 tempFiles.Add(tempImg);
 using (var croppedBmp = new System.Drawing.Bitmap(upW, upH, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
 using (var g = System.Drawing.Graphics.FromImage(croppedBmp))
 {
 g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
 g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
 g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
 g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
 g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
 g.DrawImage(origBmp,
 new System.Drawing.Rectangle(0, 0, upW, upH),
 new System.Drawing.Rectangle(cropX, cropY, cropW, cropH),
 System.Drawing.GraphicsUnit.Pixel);
 croppedBmp.Save(tempImg, System.Drawing.Imaging.ImageFormat.Png);
 }
 return tempImg;
 }
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"CropImageFromFile 失败: {ex.Message}");
 return null;
 }
 }

 /// <summary>
 /// 截取 WebView2 当前显示的画面（用 RenderTargetBitmap）
 /// </summary>
 private async Task<BitmapSource> CaptureWebViewAsync()
 {
 // 用 CDP Page.captureScreenshot 截 WebView2 视口内容：不依赖屏幕合成状态（CopyFromScreen 在窗口非激活/被遮挡时会截到空白）。
 // 选区坐标是 PreviewGrid 的 DIP，WebView2 填满 PreviewGrid，100% DPI 下与 CDP 像素 1:1。
 if (PreviewWebView == null) return null;
 try
 {
 var args = System.Text.Json.JsonSerializer.Serialize(new { format = "png" });
 var result = await Dispatcher.InvokeAsync(() =>
 {
 if (PreviewWebView?.CoreWebView2 == null) return Task.FromResult<string>(null);
 return PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args);
 }).Task.Unwrap();
 if (string.IsNullOrEmpty(result)) return null;

 byte[] pngBytes;
 try
 {
 using var jd = System.Text.Json.JsonDocument.Parse(result);
 var b64 = jd.RootElement.GetProperty("data").GetString();
 pngBytes = Convert.FromBase64String(b64 ?? "");
 }
 catch { return null; }
 if (pngBytes == null || pngBytes.Length == 0) return null;

 using var ms = new MemoryStream(pngBytes);
 var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
 ms, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
 System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
 var frame = decoder.Frames[0];
 frame.Freeze();
 return frame;
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"CaptureWebViewAsync(CDP) 失败: {ex.Message}");
 return null;
 }
 }

 /// 截取 PreviewGrid 可见区域画面（用于图片模式OCR）
 private Task<BitmapSource> CapturePreviewAreaAsync()
 {
 var tcs = new TaskCompletionSource<BitmapSource>();
 Dispatcher.Invoke(() =>
 {
 try
 {
 var topLeft = PreviewGrid.PointToScreen(new Point(0, 0));
 var w = (int)PreviewGrid.ActualWidth;
 var h = (int)PreviewGrid.ActualHeight;
 if (w <= 0 || h <= 0) { tcs.SetResult(null); return; }

 using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
 using (var g = System.Drawing.Graphics.FromImage(bmp))
 {
 g.CopyFromScreen((int)topLeft.X, (int)topLeft.Y, 0, 0, new System.Drawing.Size(w, h));
 }

 var bmpSrc = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
 bmp.GetHbitmap(),
 IntPtr.Zero,
 Int32Rect.Empty,
 BitmapSizeOptions.FromEmptyOptions());
 bmpSrc.Freeze();
 tcs.SetResult(bmpSrc);
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"CapturePreviewAreaAsync 失败: {ex.Message}");
 tcs.SetResult(null);
 }
 });
 return tcs.Task;
 }

 /// 截取 CadScrollViewer 可见区域画面（用于CAD模式OCR）
 private Task<BitmapSource> CaptureCadAsync()
 {
 var tcs = new TaskCompletionSource<BitmapSource>();
 Dispatcher.Invoke(() =>
 {
 try
 {
 // 获取 CadScrollViewer 在屏幕上的位置和尺寸
 var topLeft = CadScrollViewer.PointToScreen(new Point(0, 0));
 var w = (int)CadScrollViewer.ActualWidth;
 var h = (int)CadScrollViewer.ActualHeight;
 if (w <= 0 || h <= 0) { tcs.SetResult(null); return; }

 // 用 Graphics.CopyFromScreen 截屏
 using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
 using (var g = System.Drawing.Graphics.FromImage(bmp))
 {
 g.CopyFromScreen((int)topLeft.X, (int)topLeft.Y, 0, 0, new System.Drawing.Size(w, h));
 }

 // 转 BitmapSource
 var bmpSrc = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
 bmp.GetHbitmap(),
 IntPtr.Zero,
 Int32Rect.Empty,
 BitmapSizeOptions.FromEmptyOptions());
 bmpSrc.Freeze();
 tcs.SetResult(bmpSrc);
 }
 catch (Exception ex)
 {
 System.Diagnostics.Debug.WriteLine($"CaptureCadAsync 失败: {ex.Message}");
 tcs.SetResult(null);
 }
 });
 return tcs.Task;
 }
 private async Task<int> GetPdfPageCountAsync()
 {
 if (_isLocalPdfMode) return Math.Max(1, _totalPages);
 if (PreviewWebView?.CoreWebView2 == null) return Math.Max(1, _totalPages);
 try
 {
 // Chromium 内置 PDF 查看器的 pdf.js 运行在隔离 world，PDFViewerApplication 主 world 不可达。
 // 但渲染出的 .page DOM 元素是共享的，用 DOM 计数最可靠。
 var domResult = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 "document.querySelectorAll('.page[data-page-number]').length");
 var str = domResult?.Trim('"') ?? "";
 if (int.TryParse(str, out var domCount) && domCount > 0) return domCount;
 // 回退：docx 等自研 HTML 分页器（paginate() 设置 body[data-pagecount]）
 var pcResult = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 "document.body.getAttribute('data-pagecount')");
 var pcStr = pcResult?.Trim('"') ?? "";
 if (int.TryParse(pcStr, out var pcCount) && pcCount > 0) return pcCount;
 // 回退：解析阶段已知的页数
 if (_totalPages > 0) return _totalPages;
 // 最后回退：pdf.js 全局对象（旧模式）
 var result = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 "(typeof PDFViewerApplication!=='undefined'&&PDFViewerApplication.pagesCount)?PDFViewerApplication.pagesCount:0");
 str = result?.Trim('"') ?? "0";
 return int.TryParse(str, out var n) ? Math.Max(1, n) : 1;
 }
 catch { return Math.Max(1, _totalPages); }
 }

 /// 跳转到 PDF 指定页（1-based），滚动到视图顶部，等待渲染
 private async Task NavigateToPdfPageAsync(int page)
 {
 if (_isLocalPdfMode)
 {
 await Dispatcher.InvokeAsync(() =>
 {
 if (ThumbList.Items.Count >= page && page >= 1) ThumbList.SelectedIndex = page - 1;
 else DisplayLocalPdfPage(page - 1);
 });
 await Task.Delay(400);
 return;
 }
 if (PreviewWebView?.CoreWebView2 == null) return;
 try
 {
 // 先尝试 pdf.js 全局对象 + DOM 跳页（file-viewer 自托管 pdf.js 模式）
 var r = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 $"(function(){{var moved=false;if(typeof PDFViewerApplication!=='undefined'){{PDFViewerApplication.page={page};moved=true;}}var el=document.querySelector('.page[data-page-number=\"{page}\"]');if(el){{el.scrollIntoView({{block:'start'}});moved=true;}}return moved;}})()");
 if (r == "true" || r == "\"true\"") { await Task.Delay(1500); return; }

 // Chromium 内置 PDF viewer（embed 模式）：主 DOM 无 .page，PDFViewerApplication 不可达。
 // 用 CDP 键盘 PageDown/PageUp 翻页（每次一页，已验证有效）。
 var target = Math.Max(1, Math.Min(page, 1000));
 async Task PageKey(string type, string key, int vk)
 {
 var arg = System.Text.Json.JsonSerializer.Serialize(new { type, key, code = key, windowsVirtualKeyCode = vk });
 await Dispatcher.InvokeAsync(() =>
 {
 if (PreviewWebView?.CoreWebView2 != null)
 return PreviewWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", arg);
 return Task.FromResult<string>(null);
 }).Task.Unwrap();
 }
 // 先 Home 回到第一页（保证状态干净），再向下翻 (target-1) 次
 await PageKey("keyDown", "Home", 36); await PageKey("keyUp", "Home", 36);
 await Task.Delay(400);
 for (int i = 1; i < target; i++)
 {
 await PageKey("keyDown", "PageDown", 34); await PageKey("keyUp", "PageDown", 34);
 await Task.Delay(300);
 }
 await Task.Delay(1200); // 等待页面渲染
 }
 catch { }
 }

 private static void SaveCropDebug(BitmapSource fullImg, int cropX, int cropY, int cropW, int cropH)
 {
 var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
 var cropped = new System.Windows.Media.Imaging.CroppedBitmap(fullImg,
 new System.Windows.Int32Rect(cropX, cropY, cropW, cropH));
 encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cropped));
 using (var fs = File.Create(@"D:\ZCODE\_gui_test\area_debug_crop.png")) encoder.Save(fs);
 }

 /// 获取指定页在 WebView2 中的矩形区域（相对于 WebView2 左上角，像素）
 /// 返回 null 表示页面不可见或不存在
 private async Task<System.Drawing.Rectangle?> GetPageRectAsync(int pageNumber)
 {
 if (PreviewWebView?.CoreWebView2 == null) return null;
 try
 {
 var js = $@"
(function(){{
 var el=document.querySelector('.page[data-page-number=""{pageNumber}""]');
 if(!el) return null;
 var rect=el.getBoundingClientRect();
 return JSON.stringify({{x:Math.round(rect.x),y:Math.round(rect.y),w:Math.round(rect.width),h:Math.round(rect.height)}});
}})()";
 var result = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(js);
 if (string.IsNullOrEmpty(result) || result == "null" || result == "\"null\"")
 {
 // Chromium 内置 PDF viewer（embed 模式）主 DOM 无 .page 元素，页面铺满视口。
 // 回退：返回整个视口矩形，归一化坐标直接映射到视口。
 var vp = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 "JSON.stringify({x:0,y:0,w:window.innerWidth,h:window.innerHeight})");
 if (!string.IsNullOrEmpty(vp) && vp != "null")
 {
 var vpJson = vp.Trim('"');
 var m2 = System.Text.RegularExpressions.Regex.Match(vpJson, @"""x"":(\d+).*?""y"":(\d+).*?""w"":(\d+).*?""h"":(\d+)");
 if (m2.Success)
 {
 int vw = int.Parse(m2.Groups[3].Value);
 int vh = int.Parse(m2.Groups[4].Value);
 if (vw > 0 && vh > 0) return new System.Drawing.Rectangle(0, 0, vw, vh);
 }
 }
 return null;
 }
 var json = result.Trim('"');
 // 解析 JSON
 var match = System.Text.RegularExpressions.Regex.Match(json, @"""x"":(\d+).*?""y"":(\d+).*?""w"":(\d+).*?""h"":(\d+)");
 if (!match.Success) return null;
 int x = int.Parse(match.Groups[1].Value);
 int y = int.Parse(match.Groups[2].Value);
 int w = int.Parse(match.Groups[3].Value);
 int h = int.Parse(match.Groups[4].Value);
 if (w <= 0 || h <= 0) return null;
 return new System.Drawing.Rectangle(x, y, w, h);
 }
 catch { return null; }
 }

 /// 获取当前页码（1-based）
 private async Task<int> GetCurrentPdfPageAsync()
 {
 if (_isLocalPdfMode) return _localPdfPage + 1;
 if (PreviewWebView?.CoreWebView2 == null) return 1;
 try
 {
 // DOM 方式：取与视口相交面积最大的可见页（Chromium PDF 查看器 DOM 共享，全局对象不可达）
 var result = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 @"(function(){
var pages=document.querySelectorAll('.page[data-page-number]');
if(!pages.length) return '0';
var vh=window.innerHeight, best=0, bestScore=-1;
for(var i=0;i<pages.length;i++){
  var r=pages[i].getBoundingClientRect();
  var score=Math.min(r.bottom,vh)-Math.max(r.top,0);
  if(score>bestScore){bestScore=score; best=parseInt(pages[i].getAttribute('data-page-number'),10);}
}
return best;
})()");
 var str = result?.Trim('"') ?? "0";
 if (int.TryParse(str, out var n) && n > 0) return n;
 // 回退：pdf.js 全局对象（旧模式）
 var r2 = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
 "(typeof PDFViewerApplication!=='undefined'&&PDFViewerApplication.page)?PDFViewerApplication.page:1");
 str = r2?.Trim('"') ?? "1";
 return int.TryParse(str, out var m) ? Math.Max(1, m) : 1;
 }
 catch { return 1; }
 }

	/// Umi-OCR 本地引擎（Rapid OCR，速度快，中文识别好）
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
                sb.Append(CheckTableHeader);

                for (int i = 0; i < total; i++)
                {
                    var c = codesArray[i];
                    var result = _checker.CheckCode(c.Code, c.Name);
                    _lastResults.Add(result);

                    sb.Append(CheckTableRow(i + 1, result));

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

                // 按来源分类
                sb.Append(SourceBreakdown(_lastResults));

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"检查完成 — 现行:{valid} 作废:{obsolete} 替代:{replaced} 未找到:{notFound}";
                    PushToAi("规范检查结果", sb.ToString());
                });
            });
        }

// ═════════════ 批量OCR（沿用上一次OCR模式） ═════════════
 private void BtnBatch_Click(object sender, RoutedEventArgs e)
 {
 // 必须先执行一次OCR操作
 if (_lastOcrMode == OcrMode.None)
 {
 MessageBox.Show(
 "请先执行一次OCR操作（📝全页 / 🔲框选 / ☁️在线 / ☁️区域），\n" +
 "批量将沿用该模式的识别范围和方式。",
 "请先执行OCR", MessageBoxButton.OK, MessageBoxImage.Information);
 return;
 }

 if (_currentFilePath == null) return;

 if (_isBatchRunning)
 {
 _isBatchRunning = false;
 StatusText.Text = "批量OCR已中止";
 return;
 }

 // 确定批量参数
 bool isAreaMode = (_lastOcrMode == OcrMode.AreaAuto || _lastOcrMode == OcrMode.AreaOnline);
 bool forceOnline = (_lastOcrMode == OcrMode.FullPageOnline || _lastOcrMode == OcrMode.AreaOnline);
 string modeLabel = (forceOnline ? "在线" : "自动") + (isAreaMode ? "框选" : "全页");

 _isBatchRunning = true;

 ThreadPool.QueueUserWorkItem(async _ =>
 {
 var tempFiles = new List<string>();
 try
 {
 // WPF/WebView2 控件只能从 UI 线程访问，先把所需状态封送到 UI 线程取回
 bool cadMode = false, imgVisible = false, wvReady = false;
 string imgForOcr = null;
 Dispatcher.Invoke(() =>
 {
 cadMode = _isCadMode;
 imgVisible = ImagePreview.Visibility == Visibility.Visible;
 wvReady = PreviewWebView?.CoreWebView2 != null;
 imgForOcr = _currentImageForOcr;
 });
 LogRt($"Batch start cad={cadMode} img={imgVisible} wv={wvReady} imgForOcr={imgForOcr} mode={_lastOcrMode}");

 // 获取总页数
 int totalPages = 1;
 if (cadMode)
 {
 totalPages = _previewSvc?.TotalPages ?? 1;
 }
 else if (wvReady)
 {
 totalPages = await Dispatcher.Invoke(async () => await GetPdfPageCountAsync());
 }
 // 图片/DOCX 只有1页
 if (totalPages < 1) totalPages = 1;

 var allText = new StringBuilder();
 int totalLines = 0;

 ShowOcrProgress($"批量{modeLabel}OCR（共 {totalPages} 页）...");

 // 保存原始zoom，批量完成后恢复
 double savedZoom = _zoom;
 // 统一显示比例为1.0，确保每页渲染尺寸一致（框选坐标映射正确）
 Dispatcher.Invoke(() => { _zoom = 1.0; ApplyZoom(); });
 await Task.Delay(300);

 for (int pageIdx = 0; pageIdx < totalPages; pageIdx++)
 {
 if (!_isBatchRunning) break;

 int progress = (int)((double)pageIdx / totalPages * 100);
 int pageNum = pageIdx + 1; // PDF用1-based

 UpdateOcrProgress(progress, $"批量{modeLabel}OCR: 第 {pageNum}/{totalPages} 页...");

 try
 {					// 导航到该页（图片模式直接用原图裁剪，无需导航/翻页）
					if (cadMode)
					{
						await Dispatcher.InvokeAsync(() => DisplayCadPage(pageIdx));
						await Task.Delay(500);
					}
					else if (wvReady && string.IsNullOrEmpty(imgForOcr))
					{
						// 确保WebView2可见（可能被框选截图隐藏了），并跳页（都要在UI线程执行）
						await Dispatcher.Invoke(async () =>
						{
							if (ImagePreview.Visibility == Visibility.Visible)
							{
								ImagePreview.Visibility = Visibility.Collapsed;
								ImagePreview.Source = null;
								PreviewWebView.Visibility = Visibility.Visible;
							}
							await NavigateToPdfPageAsync(pageNum);
						});
						await Task.Delay(1500);
					}
					LogRt($"Batch page {pageNum} navigated cad={cadMode} img={imgForOcr} area={isAreaMode}");

 string tempImg = null;

 if (isAreaMode)
 {
 // ═══ 框选模式：用归一化坐标在当前页计算裁剪区域 ═══
 if (cadMode)
 {
 // CAD模式：高DPI渲染 + 归一化坐标裁剪
 BitmapSource highResImg = null;
 try { highResImg = _previewSvc?.RenderCadPageToBitmap(pageIdx, 300, 0); } catch { }
 if (highResImg == null) highResImg = await CaptureCadAsync();
 if (highResImg != null)
 {
 int imgW = highResImg.PixelWidth;
 int imgH = highResImg.PixelHeight;
 int cropX = (int)(_lastAreaNormX * imgW);
 int cropY = (int)(_lastAreaNormY * imgH);
 int cropW = (int)(_lastAreaNormW * imgW);
 int cropH = (int)(_lastAreaNormH * imgH);
 cropX = Math.Max(0, Math.Min(cropX, imgW - 10));
 cropY = Math.Max(0, Math.Min(cropY, imgH - 10));
 cropW = Math.Min(cropW, imgW - cropX);
 cropH = Math.Min(cropH, imgH - cropY);
 if (cropW > 10 && cropH > 10)
 tempImg = CropAndUpscale(highResImg, cropX, cropY, cropW, cropH, tempFiles, 2);
 }
 }
 else if (!string.IsNullOrEmpty(imgForOcr) && File.Exists(imgForOcr))
 {
 // 图片模式：从原始高分辨率文件裁剪
 using var orig = System.Drawing.Image.FromFile(_currentImageForOcr);
 int origW = orig.Width, origH = orig.Height;
 int cropX = (int)(_lastAreaNormX * origW);
 int cropY = (int)(_lastAreaNormY * origH);
 int cropW = (int)(_lastAreaNormW * origW);
 int cropH = (int)(_lastAreaNormH * origH);
 cropX = Math.Max(0, Math.Min(cropX, origW - 10));
 cropY = Math.Max(0, Math.Min(cropY, origH - 10));
 cropW = Math.Min(cropW, origW - cropX);
 cropH = Math.Min(cropH, origH - cropY);
 if (cropW > 10 && cropH > 10)
 {
 using var bmp = new System.Drawing.Bitmap(cropW, cropH);
 using var g = System.Drawing.Graphics.FromImage(bmp);
 g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
 g.DrawImage(orig, new System.Drawing.Rectangle(0, 0, cropW, cropH),
 new System.Drawing.Rectangle(cropX, cropY, cropW, cropH), System.Drawing.GraphicsUnit.Pixel);
 tempImg = Path.GetTempFileName() + ".png";
 bmp.Save(tempImg, System.Drawing.Imaging.ImageFormat.Png);
 tempFiles.Add(tempImg);
 }
 }
 else if (wvReady)
 {						// WebView2/PDF模式：截图 + 归一化坐标裁剪
						// 获取页面在WebView2中的位置和尺寸（GetPageRectAsync 内部访问 WebView2，需在UI线程执行）
						var pageRectOpt = await Dispatcher.Invoke(async () => await GetPageRectAsync(pageNum));
						LogRt($"Batch area pageRect={pageRectOpt.HasValue} norm=({_lastAreaNormX:F2},{_lastAreaNormY:F2},{_lastAreaNormW:F2},{_lastAreaNormH:F2})");
						if (pageRectOpt.HasValue)
						{
							var pageRect = pageRectOpt.Value;
							if (pageRect.Width > 0 && pageRect.Height > 0)
							{
								var fullImg = await CaptureWebViewAsync();
								if (fullImg != null)
								{
									int imgW = fullImg.PixelWidth;
									int imgH = fullImg.PixelHeight;
									// WebView2截图与屏幕1:1，pageRect也是屏幕坐标
									int cropX = (int)(pageRect.X + _lastAreaNormX * pageRect.Width);
									int cropY = (int)(pageRect.Y + _lastAreaNormY * pageRect.Height);
									int cropW = (int)(_lastAreaNormW * pageRect.Width);
									int cropH = (int)(_lastAreaNormH * pageRect.Height);
									cropX = Math.Max(0, Math.Min(cropX, imgW - 10));
									cropY = Math.Max(0, Math.Min(cropY, imgH - 10));
									cropW = Math.Min(cropW, imgW - cropX);
									cropH = Math.Min(cropH, imgH - cropY);
									if (cropW > 10 && cropH > 10)
										tempImg = CropAndUpscale(fullImg, cropX, cropY, cropW, cropH, tempFiles, 3);
									LogRt($"Batch area crop img={imgW}x{imgH} rect=({pageRect.X},{pageRect.Y},{pageRect.Width},{pageRect.Height}) crop=({cropX},{cropY},{cropW},{cropH})");
								}
							}
						}
					}
				}
 else
 {
 // ═══ 全页模式：截取整页 ═══
 if (cadMode)
 {
 BitmapSource highResImg = null;
 try { highResImg = _previewSvc?.RenderCadPageToBitmap(pageIdx, 300, 0); } catch { }
 if (highResImg == null) highResImg = await CaptureCadAsync();
 if (highResImg != null)
 {
 tempImg = Path.GetTempFileName() + ".png";
 var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
 encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(highResImg));
 using var fs = File.OpenWrite(tempImg);
 encoder.Save(fs);
 tempFiles.Add(tempImg);
 }
 }
 else if (!string.IsNullOrEmpty(imgForOcr) && File.Exists(imgForOcr))
 {
 // 图片模式：直接用原图（优先于 WebView2——图片用 ImagePreview 显示，WebView2 不在当前文档上）
 tempImg = imgForOcr;
 }
 else if (wvReady)
 {
 // PDF: 截图 + 裁剪到页面区域
 var pageRectOpt = await Dispatcher.Invoke(async () => await GetPageRectAsync(pageNum));
 if (pageRectOpt.HasValue)
 {
 var pageRect = pageRectOpt.Value;
 var fullImg = await CaptureWebViewAsync();
 if (fullImg != null)
 {
 int imgW = fullImg.PixelWidth;
 int imgH = fullImg.PixelHeight;
 int cropX = Math.Max(0, (int)pageRect.X);
 int cropY = Math.Max(0, (int)pageRect.Y);
 int cropW = Math.Min((int)pageRect.Width, imgW - cropX);
 int cropH = Math.Min((int)pageRect.Height, imgH - cropY);
 if (cropW > 10 && cropH > 10)
 tempImg = CropAndUpscale(fullImg, cropX, cropY, cropW, cropH, tempFiles, 3);
 }
 }
 }
 } // 关闭 else（全页模式）

 if (tempImg == null)
 {
 UpdateOcrProgress(progress + 5, $"第 {pageNum} 页截取失败，跳过");
 continue;
 }

 UpdateOcrProgress(progress + 8, $"OCR识别第 {pageNum}/{totalPages} 页...");
 var (ok, text, err) = await RunOcrAsync(tempImg, false, forceOnline, false);

 // 清理临时文件（但不删除原图）
 if (tempImg != imgForOcr)
 {
 try { File.Delete(tempImg); } catch { }
 tempFiles.Remove(tempImg);
 }

 if (ok && !string.IsNullOrWhiteSpace(text))
 {
 var pageText = TextNormalizer.Normalize(text);
 if (totalPages > 1)
 allText.AppendLine($"═══ 第 {pageNum} 页 ═══");
 allText.AppendLine(pageText);
 totalLines += pageText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
 }
 }
 catch (Exception ex)
 {
 LogRt($"Batch page {pageNum} fail: {ex.Message}");
 System.Diagnostics.Debug.WriteLine($"批量OCR第{pageNum}页失败: {ex.Message}");
 }
 }

 // 恢复zoom
 Dispatcher.Invoke(() => { _zoom = savedZoom; ApplyZoom(); });

 UpdateOcrProgress(100, "批量OCR完成");
 Dispatcher.Invoke(() =>
 {
 Progress.Value = 100;
 if (allText.Length > 0)
 {
 _lastOcrText = allText.ToString();						StatusText.Text = $"批量{modeLabel}OCR完成 — {totalLines} 行（{totalPages} 页）";
						LogRt($"BATCH OCR TEXT RESULT:\n{_lastOcrText}");
 var preview = _lastOcrText.Length > 3000
 ? _lastOcrText.Substring(0, 3000) + "\n\n... (文本已截断)"
 : _lastOcrText;
 ShowOcrDone(true, $"{totalLines} 行（{totalPages} 页）", () => PushToAi($"批量{modeLabel}OCR结果", $"批量{modeLabel}OCR完成（{totalPages} 页）：\n\n```\n{preview}\n```"));


 // 提取规范编号并检查
 var codes = CodeExtractor.Extract(_lastOcrText);
 if (codes.Count > 0 && _checker != null)
 {
 _lastCodes = codes;
 _lastResults.Clear();
 var sb = new StringBuilder();				 sb.Append($"批量{modeLabel}OCR后提取到 **{codes.Count}** 个规范编号。\n\n");
				 sb.Append(CheckTableHeader);
				 for (int i = 0; i < codes.Count; i++)
				 {
				 var c = codes[i];
				 var r = _checker.CheckCode(c.Code, c.Name);
				 _lastResults.Add(r);
				 sb.Append(CheckTableRow(i + 1, r));
				 }
				 sb.Append(SourceBreakdown(_lastResults));
 StatusText.Text += $" | {codes.Count}个编号";
 PushToAi("批量检查结果", sb.ToString());
 }
 }
 else
{ StatusText.Text = $"批量{modeLabel}OCR未识别到文字"; ShowOcrDone(false, "未识别到文字"); }
 });
 }
 catch (Exception ex)
 {
 LogRt($"Batch EXCEPTION: {ex}");
 Dispatcher.Invoke(() => StatusText.Text = $"批量OCR错误: {ex.Message}");
 HideOcrProgress();
 }
 finally
 {
 foreach (var f in tempFiles) { try { File.Delete(f); } catch { } }
 _isBatchRunning = false;
 }
 });
 }

 // ═════════════ 导出 ═════════════

        /// <summary>检查结果表格表头（含来源列）</summary>
        private static string CheckTableHeader =>
            "| 序号 | 编号 | 名称 | 状态 | 替代信息 | 来源 |\n" +
            "|------|------|------|------|----------|------|\n";

        /// <summary>检查结果表格行（含来源友好名称）</summary>
        private static string CheckTableRow(int no, CheckResult r) =>
            $"| {no} | `{r.Code}` | {(string.IsNullOrEmpty(r.Name) ? "—" : r.Name)} | {r.Status} | {r.Replacement} | {CheckResult.SourceLabel(r.Source)} |\n";

        /// <summary>按来源分类的统计小节</summary>
        private static string SourceBreakdown(List<CheckResult> results)
        {
            var sb = new StringBuilder("\n### 按来源分类\n");
            foreach (var g in results.GroupBy(r => CheckResult.SourceLabel(r.Source)).OrderByDescending(g => g.Count()))
            {
                var valid = g.Count(r => r.Status == "现行");
                var obsolete = g.Count(r => r.Status == "作废" || r.Status == "废止");
                var replaced = g.Count(r => r.Status == "被代替" || r.Status == "被替代");
                var notFound = g.Count(r => r.Status == "未找到");
                var parts = new List<string>();
                if (valid > 0) parts.Add($"现行 {valid}");
                if (obsolete > 0) parts.Add($"作废 {obsolete}");
                if (replaced > 0) parts.Add($"被替代 {replaced}");
                if (notFound > 0) parts.Add($"未找到 {notFound}");
                sb.Append($"- **{g.Key}**: {g.Count()} 项");
                if (parts.Count > 0) sb.Append($"（{string.Join("，", parts)}）");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>导出前弹出筛选对话框，返回过滤后的结果（用户取消返回 null）</summary>
        private List<CheckResult> FilterResultsForExport()
        {
            try
            {
                var dlg = new ExportFilterWindow(_lastResults) { Owner = this };
                return dlg.ShowDialog() == true ? dlg.Filter(_lastResults) : null;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"筛选对话框异常: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return _lastResults;   // 出错时导出全部，避免中断流程
            }
        }

        private void BtnExportWord_Click(object sender, RoutedEventArgs e)
        {
            if (_lastResults.Count == 0)
            {
                MessageBox.Show("没有检查结果可导出。请先执行规范检查。",
                    "无数据", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var filtered = FilterResultsForExport();
            if (filtered == null) return;   // 用户取消筛选

            var dlg = new SaveFileDialog { Filter = "Word 文档|*.docx", FileName = "规范检查报告" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var fileName = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "批量文件";
                    ExportService.ExportWord(dlg.FileName, fileName, filtered);
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

            var filtered = FilterResultsForExport();
            if (filtered == null) return;   // 用户取消筛选

            var dlg = new SaveFileDialog { Filter = "Excel 表格|*.xlsx", FileName = "规范检查报告" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var fileName = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "批量文件";
                    ExportService.ExportExcel(dlg.FileName, fileName, filtered);
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

        // ═════════════ AI 按钮（工具栏 + 右下角悬浮球） ═════════════
        private void BtnAi_Click(object sender, RoutedEventArgs e) => OpenAiWithContext();



        /// <summary>打开 AI 助手并携带当前 OCR/检查上下文</summary>
        private void OpenAiWithContext()
        {
            ShowAiWindow();

            // 设置上下文（内容未变时 SetContext 幂等，不会重复总结/发送）
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
 _pendingCadFilePath = null;
 _aiWindow?.Close();
 _queryWindow?.Close();
 _checker?.Dispose();
 _cadReBakeCts?.Cancel();
 Services.CadWpfRenderer.ClearModelCache();
 try { _previewSvc?.Close(); } catch { }
 // 清理OCR临时图片
 try { if (!string.IsNullOrEmpty(_currentImageForOcr) && File.Exists(_currentImageForOcr)) File.Delete(_currentImageForOcr); } catch { }
 base.OnClosed(e);
 }
 }
}
