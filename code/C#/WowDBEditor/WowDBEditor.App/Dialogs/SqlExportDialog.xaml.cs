using System.Windows;
using WowDBEditor.Core.MySql;

namespace WowDBEditor.App.Dialogs;

public partial class SqlExportDialog : Window
{
    public SqlDumpOptions Options { get; private set; } = SqlDumpOptions.Idempotent();

    public SqlExportDialog() => InitializeComponent();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Options = RebuildRadio.IsChecked == true ? SqlDumpOptions.Rebuild() : SqlDumpOptions.Idempotent();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
