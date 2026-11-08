using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace WowDBEditor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(Current.MainWindow, Describe(e.Exception), "发生错误",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>把包装异常（XAML 加载等）背后的真实原因一并展示出来，否则只有一句没用的外壳消息。</summary>
    private static string Describe(Exception exception)
    {
        var lines = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            lines.Add(current.GetType().Name + ": " + current.Message);
            if (lines.Count >= 4)
                break;
        }
        return string.Join("\n\n", lines);
    }
}