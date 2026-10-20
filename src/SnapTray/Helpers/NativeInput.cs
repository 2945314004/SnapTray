using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTray.Helpers;

/// <summary>注入鼠标滚轮与按键状态查询，滚动截图专用。</summary>
internal static class NativeInput
{
    private const uint MouseEventWheel = 0x0800;
    private const int WheelDelta = 120;
    private const int VkEscape = 0x1B;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint flags, uint dx, uint dy, int data, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public static void MoveCursor(Point point) => SetCursorPos(point.X, point.Y);

    /// <summary>向下滚动，notches 为滚轮格数（1 格 = 120）。</summary>
    public static void ScrollDown(int notches)
    {
        for (int i = 0; i < notches; i++) mouse_event(MouseEventWheel, 0, 0, -WheelDelta, IntPtr.Zero);
    }

    public static bool EscapePressed() => (GetAsyncKeyState(VkEscape) & 0x8000) != 0;

    /// <summary>把光标放到区域中心，并确认它能落在目标窗口上。</summary>
    public static void ParkCursor(Rectangle region)
    {
        int x = region.Left + region.Width / 2;
        int y = region.Top + Math.Min(region.Height / 2, Math.Max(10, region.Height / 4));
        MoveCursor(new Point(x, y));
        Application.DoEvents();
    }
}
