using System;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;

namespace LDAssistant
{
    public partial class App : Application
    {
        static App()
        {
            // SHX 大字体字形按 GB2312 内码存储，需要代码页 936；
            // .NET Core 默认不带代码页，必须显式注册，否则 Encoding.GetEncoding(936) 抛异常。
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
            catch { }
        }

        /// <summary>--open 命令行参数指定的启动文件路径</summary>
        public static string StartupOpenPath;

        /// <summary>--selfshot <秒>：启动 N 秒后应用自截图（调试用，绕过外部屏幕捕获）</summary>
        public static int StartupSelfShotSecs;

        /// <summary>--cadzoom <倍数>：CAD 打开后预设缩放（调试/验证文字渲染用）</summary>
        public static double StartupCadZoom;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局异常处理 — 防止子线程异常导致进程静默退出
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // 调试通道：LDAssistant.exe --export-cad <dwg路径> [输出png]
            // 用真实渲染代码将CAD导出为PNG，用于定位空白/重叠根因（不影响正常GUI逻辑）
            if (e.Args != null && e.Args.Length >= 2 && e.Args[0] == "--export-cad")
            {
                ExportCadDebug(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : @"D:\ZCODE\cad_export.png");
                Shutdown(0);
                return;
            }

            // 调试通道：LDAssistant.exe --export-docx <docx路径> <输出目录>
            // 走真实的 DOCX→HTML→WebView2 链路，把分页后的每一页截成 PNG，
            // 用于离线验证表格分页与缩略图一致性（不弹主窗口）
            if (e.Args != null && e.Args.Length >= 2 && e.Args[0] == "--export-docx")
            {
                _ = ExportDocxDebugAsync(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : @"D:\ZCODE\docx_debug");
                return;
            }

            // 启动后自动打开文件：LDAssistant.exe --open <文件路径>
            // 命令行打开文件（也是定位加载卡死问题的稳定复现通道）
            if (e.Args != null && e.Args.Length >= 2 && e.Args[0] == "--open")
            {
                StartupOpenPath = e.Args[1];
            }

            // 调试通道：LDAssistant.exe --selfshot <秒> —— 启动 N 秒后自截图（WPF窗口+WebView2）
            // 位置不限（可与 --open 同时使用），扫描全部参数
            if (e.Args != null)
            {
                for (int i = 0; i + 1 < e.Args.Length; i++)
                {
                    if (e.Args[i] == "--selfshot")
                    {
                        int.TryParse(e.Args[i + 1], out var secs);
                        StartupSelfShotSecs = secs;
                    }
                    else if (e.Args[i] == "--cadzoom")
                    {
                        double.TryParse(e.Args[i + 1], out var z);
                        if (z > 0) StartupCadZoom = z;
                    }
                }
            }

            base.OnStartup(e);
        }

        /// <summary>
        /// 离线渲染 DOCX：复用生产的 DocxToHtmlConverter + WebView2 + CDP 截图。
        /// 输出 page_1.png … page_N.png 与 out.html，便于直接核对分页效果。
        /// </summary>
        private async System.Threading.Tasks.Task ExportDocxDebugAsync(string docxPath, string outDir)
        {
            string log = System.IO.Path.Combine(outDir, "log.txt");
            try
            {
                System.IO.Directory.CreateDirectory(outDir);
                var html = LDAssistant.Services.DocxToHtmlConverter.Convert(docxPath);
                var htmlPath = System.IO.Path.Combine(outDir, "out.html");
                System.IO.File.WriteAllText(htmlPath, html, System.Text.Encoding.UTF8);

                // 隐藏窗口承载 WebView2（不显示给用户）
                var win = new Window
                {
                    Width = 1000,
                    Height = 800,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Left = -5000,
                    Top = -5000
                };
                var wv = new Microsoft.Web.WebView2.Wpf.WebView2();
                win.Content = wv;
                win.Show();

                await wv.EnsureCoreWebView2Async();
                var navDone = new System.Threading.Tasks.TaskCompletionSource<bool>();
                wv.CoreWebView2.NavigationCompleted += (s, ev) => navDone.TrySetResult(ev.IsSuccess);
                wv.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                await navDone.Task;

                // 等分页脚本打标记
                bool paged = false;
                for (int i = 0; i < 80; i++)
                {
                    var v = await wv.CoreWebView2.ExecuteScriptAsync("document.body.getAttribute('data-paged')");
                    if (!string.IsNullOrEmpty(v) && v.Contains("1")) { paged = true; break; }
                    await System.Threading.Tasks.Task.Delay(100);
                }

                var rectsJson = await wv.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify(Array.prototype.map.call(document.querySelectorAll('.page')," +
                    "function(p){var r=p.getBoundingClientRect();" +
                    "return{x:r.left+window.scrollX,y:r.top+window.scrollY,w:r.width,h:r.height," +
                    "ov:(p.scrollHeight>p.clientHeight+5)};}))");
                var rj = rectsJson;
                if (rj.StartsWith("\"")) rj = System.Text.Json.JsonSerializer.Deserialize<string>(rj);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"FILE={System.IO.Path.GetFileName(docxPath)} PAGED={paged}");

                using var jd = System.Text.Json.JsonDocument.Parse(rj);
                int idx = 0, overflowCount = 0;
                foreach (var el in jd.RootElement.EnumerateArray())
                {
                    idx++;
                    double x = el.GetProperty("x").GetDouble();
                    double y = el.GetProperty("y").GetDouble();
                    double w = el.GetProperty("w").GetDouble();
                    double h = el.GetProperty("h").GetDouble();
                    bool ov = el.GetProperty("ov").GetBoolean();
                    if (ov) overflowCount++;
                    sb.AppendLine($"page{idx}: {w:F0}x{h:F0} at({x:F0},{y:F0}) overflow={ov}");

                    var args = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        format = "png",
                        captureBeyondViewport = true,
                        clip = new { x, y, width = w, height = h, scale = 1.0 }
                    });
                    var shot = await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args);
                    using var sd = System.Text.Json.JsonDocument.Parse(shot);
                    var b64 = sd.RootElement.GetProperty("data").GetString();
                    System.IO.File.WriteAllBytes(
                        System.IO.Path.Combine(outDir, $"page_{idx}.png"), Convert.FromBase64String(b64));
                }
                sb.AppendLine($"TOTAL_PAGES={idx} OVERFLOW_PAGES={overflowCount}");
                System.IO.File.WriteAllText(log, sb.ToString());
                win.Close();
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(outDir);
                    System.IO.File.WriteAllText(log, $"ERR {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
                }
                catch { }
            }
            Shutdown(0);
        }

        private void ExportCadDebug(string dwgPath, string outPng)
        {
            try
            {
                var svc = new LDAssistant.Services.FilePreviewService();
                bool opened = svc.Open(dwgPath);
                var info = svc.DebugCadInfo();
                // 与界面预览完全一致的渲染路径（SkiaSharp + 暗色背景）
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var bmp = svc.RenderCadSkia(0, 5000, true);
                sw.Stop();
                if (bmp != null)
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var fs = System.IO.File.Create(outPng)) enc.Save(fs);
                    System.IO.File.AppendAllText(@"D:\ZCODE\cad_export_log.txt",
                        $"EXPORTED {bmp.PixelWidth}x{bmp.PixelHeight} in {sw.ElapsedMilliseconds}ms OPENED={opened} {info} FILE={System.IO.Path.GetFileName(dwgPath)}\n");
                }
                else
                {
                    System.IO.File.WriteAllText(@"D:\ZCODE\cad_export_log.txt",
                        $"RENDER_NULL OPENED={opened} {info}\n");
                    // 原始加载诊断：绕过 LoadCadDocument，直接用相同 API+InvariantCulture 加载
                    try
                    {
                        var prev = System.Globalization.CultureInfo.CurrentCulture;
                        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                        using var r = new ACadSharp.IO.DwgReader(dwgPath);
                        var d = r.Read();
                        System.Globalization.CultureInfo.CurrentCulture = prev;
                        int n = d?.ModelSpace?.Entities?.Count ?? -1;
                        System.IO.File.AppendAllText(@"D:\ZCODE\cad_export_log.txt", $"RAW_OK entities={n}\n");
                    }
                    catch (Exception ex)
                    {
                        System.IO.File.AppendAllText(@"D:\ZCODE\cad_export_log.txt",
                            $"RAW_ERR {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
                    }
                }
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(@"D:\ZCODE\cad_export_log.txt",
                    $"ERR {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                System.IO.File.AppendAllText(@"D:\ZCODE\_gui_test\crash.log",
                    $"[{DateTime.Now:HH:mm:ss}] UI {e.Exception.ToString()}\n\n");
            }
            catch { }
            e.Handled = true;
            System.Windows.MessageBox.Show(
                $"程序遇到错误但已拦截：\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\n{e.Exception.StackTrace?[..Math.Min(500, e.Exception.StackTrace.Length)]}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            try
            {
                System.IO.File.AppendAllText(@"D:\ZCODE\_gui_test\crash.log",
                    $"[{DateTime.Now:HH:mm:ss}] FATAL {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n\n");
            }
            catch { }
            try
            {
                System.Windows.MessageBox.Show(
                    $"程序遇到严重错误：\n\n{ex?.GetType().Name}: {ex?.Message}",
                    "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }

        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
        }
    }
}
