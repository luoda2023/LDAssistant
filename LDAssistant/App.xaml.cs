using System;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;

namespace LDAssistant
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局异常处理 — 防止子线程异常导致进程静默退出
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
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
