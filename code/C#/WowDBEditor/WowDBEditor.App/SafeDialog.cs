using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WowDBEditor.App;

/// <summary>
/// 文件对话框的统一入口。Windows Shell 的 SetFolder 只接受能被 SHCreateItemFromParsingName 解析的路径，
/// 像 "G:/dir/sub"（正斜杠）、已拔掉的盘、超长路径都会抛 ArgumentException 并让整个“打开”命令失效。
/// 这里先规整路径，失败时退回系统默认目录重试，再失败才提示，保证按钮永远可用。
/// </summary>
internal static class SafeDialog
{
    public static bool Show(FileDialog dialog, Window owner, string initialDirectory)
    {
        ApplyInitialDirectory(dialog, initialDirectory);
        try
        {
            return dialog.ShowDialog(owner) == true;
        }
        catch (ArgumentException)
        {
            // 记住的目录不可用：清掉再弹一次（退回“文档/上次位置”）
            dialog.InitialDirectory = null;
            try
            {
                return dialog.ShowDialog(owner) == true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "文件对话框无法打开：" + ex.Message, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
    }

    private static void ApplyInitialDirectory(FileDialog dialog, string initialDirectory)
    {
        dialog.InitialDirectory = null;
        if (string.IsNullOrWhiteSpace(initialDirectory))
            return;

        try
        {
            // 关键：把 "G:/a/b" 规整成 "G:\a\b"，Shell 才认
            var normalized = Path.GetFullPath(initialDirectory);
            if (Directory.Exists(normalized))
                dialog.InitialDirectory = normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or System.Security.SecurityException)
        {
            // 路径本身非法，直接不设初始目录
        }
    }

    /// <summary>把记住的目录规整成标准形式，避免下次再踩同一个坑。</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var full = Path.GetFullPath(path);
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
