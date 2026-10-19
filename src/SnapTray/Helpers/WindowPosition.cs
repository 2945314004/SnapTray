using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace SnapTray.Helpers;

internal static class WindowPosition
{
    public static void PlaceImageWindow(Window window, Rectangle anchor, int pixelWidth, int pixelHeight)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Rectangle work = Forms.Screen.FromRectangle(anchor).WorkingArea;
        // 先将 HWND 放到目标显示器，再读取该窗口的 DPI，不能用主显示器比例代替。
        SetWindowPos(handle, IntPtr.Zero, work.Left, work.Top, 0, 0, 0x0015);
        double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        int border = (int)Math.Ceiling(2 * scale);
        double ratio = Math.Min(1.0, Math.Min(
            Math.Max(1, work.Width - 40 - border) / (double)pixelWidth,
            Math.Max(1, work.Height - 40 - border) / (double)pixelHeight));
        int width = Math.Max(1, (int)Math.Ceiling(pixelWidth * ratio)) + border;
        int height = Math.Max(1, (int)Math.Ceiling(pixelHeight * ratio)) + border;
        int left = Math.Clamp(anchor.Left, work.Left, Math.Max(work.Left, work.Right - width));
        int top = Math.Clamp(anchor.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        window.Width = width / scale;
        window.Height = height / scale;
        SetWindowPos(handle, new IntPtr(-1), left, top, width, height, 0x0010);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
