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
        MessageBox.Show(Current.MainWindow, e.Exception.Message, "发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}