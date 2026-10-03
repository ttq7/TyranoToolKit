using System;
using System.Windows;

namespace TyranoToolKit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                MessageBox.Show(FormatException(ex), "未处理异常", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(FormatException(args.Exception), "未处理异常", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    private static string FormatException(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        var current = ex;
        while (current != null)
        {
            sb.AppendLine($"[{current.GetType().Name}] {current.Message}");
            if (current.StackTrace != null)
                sb.AppendLine(current.StackTrace[..Math.Min(2000, current.StackTrace.Length)]);
            current = current.InnerException;
            if (current != null) sb.AppendLine("\n--- Inner Exception ---");
        }
        return sb.ToString();
    }
}
