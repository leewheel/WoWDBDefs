using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WowDBEditor.App;

/// <summary>
/// 把窗口尺寸限制在「当前显示器工作区」内。WPF 的 Window.Width 是 DIP，
/// 而 Win32 返回的是物理像素，必须按窗口实际 DPI 折算，否则高分屏上窗口会比屏幕还大。
/// </summary>
internal static class ScreenFit
{
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    public static void Apply(Window window, double preferredWidth, double preferredHeight,
        double minWidth, double minHeight)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                return;

            var dpi = VisualTreeHelper.GetDpi(window);
            var workWidth = (info.rcWork.Right - info.rcWork.Left) / dpi.DpiScaleX;
            var workHeight = (info.rcWork.Bottom - info.rcWork.Top) / dpi.DpiScaleY;
            if (workWidth < 200 || workHeight < 200)
                return;

            window.Width = Math.Max(Math.Min(minWidth, workWidth), Math.Min(preferredWidth, workWidth - 16));
            window.Height = Math.Max(Math.Min(minHeight, workHeight), Math.Min(preferredHeight, workHeight - 16));

            window.Left = info.rcWork.Left / dpi.DpiScaleX + (workWidth - window.Width) / 2;
            window.Top = info.rcWork.Top / dpi.DpiScaleY + (workHeight - window.Height) / 2;
        }
        catch
        {
            // 取不到显示器信息时保持默认尺寸
        }
    }
}
