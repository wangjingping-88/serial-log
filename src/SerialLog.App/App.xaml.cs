using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using SerialLog.App.Diagnostics;
using SerialLog.Core.Configuration;

namespace SerialLog.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        try
        {
            ApplicationDataPaths.MigrateLegacyWorkspaceIfNeeded();
        }
        catch (Exception exception)
        {
            CrashLogWriter.Write("启动时迁移旧版工作区", exception);
            MessageBox.Show(
                $"无法在工具目录创建数据文件夹，程序不能安全启动。\n\n{exception.Message}\n\n" +
                $"请确认目录可写：{ApplicationDataPaths.DataDirectory}",
                "Serial Log 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogWriter.Write("WPF UI 未处理异常", e.Exception);
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ??
            new InvalidOperationException($"非 Exception 未处理错误：{e.ExceptionObject}");
        CrashLogWriter.Write("AppDomain 未处理异常", exception);
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLogWriter.Write("未观察到的后台任务异常", e.Exception);
        e.SetObserved();
    }
}

