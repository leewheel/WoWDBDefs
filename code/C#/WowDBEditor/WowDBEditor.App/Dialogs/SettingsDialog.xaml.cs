using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WowDBEditor.App.Dialogs;

public partial class SettingsDialog : Window
{
    public string DefinitionsDirectory { get; private set; }

    public SettingsDialog(string currentDirectory)
    {
        InitializeComponent();
        DirectoryBox.Text = currentDirectory ?? "";
        UpdateStatus();
        DirectoryBox.TextChanged += (_, _) => UpdateStatus();
    }

    private void UpdateStatus()
    {
        var dir = DirectoryBox.Text.Trim();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            StatusText.Text = "目录不存在。";
            return;
        }
        var count = Directory.GetFiles(dir, "*.dbd").Length;
        StatusText.Text = count > 0 ? $"找到 {count} 个 .dbd 定义文件。" : "该目录下没有 .dbd 定义文件。";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 definitions 目录" };
        if (Directory.Exists(DirectoryBox.Text.Trim()))
            dialog.InitialDirectory = DirectoryBox.Text.Trim();
        if (dialog.ShowDialog(this) == true)
            DirectoryBox.Text = dialog.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var dir = DirectoryBox.Text.Trim();
        if (!Directory.Exists(dir))
        {
            MessageBox.Show(this, "目录不存在，请重新选择。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DefinitionsDirectory = Path.GetFullPath(dir);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}