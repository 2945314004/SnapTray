using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SnapTray.Helpers;

internal static class NativeCapture
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect bounds, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    public static Point CursorPosition()
    {
        if (!GetCursorPos(out NativePoint point))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取鼠标位置。");
        // app.manifest 的 PerMonitorV2 使此 UI 线程接收真实像素坐标，不受 DPI 虚拟化影响。
        return new Point(point.X, point.Y);
    }

    public static IReadOnlyList<Rectangle> GetMonitors()
    {
        var result = new List<Rectangle>();
        MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref NativeRect bounds, IntPtr data) =>
        {
            var rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            if (rectangle.Width > 0 && rectangle.Height > 0 && !result.Contains(rectangle)) result.Add(rectangle);
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举显示器。");
        return result.AsReadOnly();
    }

    public static void PlaceOverlay(IntPtr handle, Rectangle bounds)
    {
        // Window.Left/Top/Width/Height 是 DIP，绝不能直接填入跨屏像素值。
        // 用 Win32 一次性按目标屏幕的物理矩形定位，WM_DPICHANGED 交由 WPF 正常更新内容比例。
        if (!SetWindowPos(handle, HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法定位截图遮罩。");
    }
}
