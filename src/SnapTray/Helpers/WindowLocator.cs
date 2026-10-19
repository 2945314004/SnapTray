using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace SnapTray.Helpers;

/// <summary>光标下的顶层窗口。Bounds 为物理像素，已使用 DWM 可见边框而非含阴影的窗口矩形。</summary>
internal sealed record WindowTarget(IntPtr Handle, Rectangle Bounds, string Title);

/// <summary>
/// 按 Z 序枚举顶层窗口，找到光标下第一个真正可见的窗口。
/// 不能使用 WindowFromPoint：截图遮罩本身是覆盖全屏的置顶窗口，会永远命中自己。
/// </summary>
internal static class WindowLocator
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>
    /// 返回光标下的窗口；桌面壳窗口（Progman / WorkerW）视作空白，返回 null。
    /// 必须在 PerMonitorV2 感知的 UI 线程调用，才能拿到物理像素坐标。
    /// </summary>
    public static WindowTarget? FromPoint(Point point, uint excludedProcessId)
    {
        WindowTarget? found = null;
        // 委托在本次调用期间由参数引用保持存活，不会被 GC 回收。
        EnumWindows((hwnd, _) =>
        {
            if (found is not null) return false;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            // 跳过本进程窗口（截图遮罩），否则永远只能选中自己。
            if (GetWindowThreadProcessId(hwnd, out uint processId) == 0) return true;
            if (processId == excludedProcessId) return true;
            if (!TryGetBounds(hwnd, out Rectangle bounds) || !bounds.Contains(point)) return true;
            if (IsCloaked(hwnd)) return true;
            string className = ReadClassName(hwnd);
            // 桌面与壁纸宿主铺满整屏且位于 Z 序底部，点空白处不应选中它们。
            if (className is "Progman" or "WorkerW") return true;
            found = new WindowTarget(hwnd, bounds, ReadTitle(hwnd, className));
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool TryGetBounds(IntPtr hwnd, out Rectangle bounds)
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS 给出去掉不可见阴影后的真实可见矩形；
        // GetWindowRect 在 Win10 之后包含重定向到 DWM 的阴影区域，直接用会多截一圈黑边。
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out NativeRect rect,
                Marshal.SizeOf<NativeRect>()) != 0
            && !GetWindowRect(hwnd, out rect))
        {
            bounds = Rectangle.Empty;
            return false;
        }
        bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        // UWP 应用会保留大量 DWM 托管（cloaked）的隐藏窗口，它们仍有正常矩形，
        // 不排除就会截到一块透明或过期的内容。
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        int length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private static string ReadTitle(IntPtr hwnd, string className)
    {
        var buffer = new StringBuilder(512);
        int length = GetWindowText(hwnd, buffer, buffer.Capacity);
        string title = length > 0 ? buffer.ToString(0, length).Trim() : string.Empty;
        if (title.Length == 0) title = className.Length > 0 ? className : "窗口";
        return title.Length > 40 ? title[..40] + "…" : title;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
