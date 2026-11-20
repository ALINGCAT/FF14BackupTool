using System;
using System.Windows;
using System.Windows.Threading;
using FF14BackupTool.Core;

namespace FF14BackupTool
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                AppLog.Error("Unhandled exception", args.ExceptionObject as Exception);
            };
            ThemeManager.ApplySavedTheme();
            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error("Dispatcher exception", e.Exception);
            MessageBox.Show(
                "程序遇到一个未处理的错误：\n\n" + (e.Exception == null ? "未知错误" : e.Exception.Message) +
                "\n\n日志位置：%LOCALAPPDATA%\\FF14BackupTool\\logs",
                "FF14 备份工具",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
