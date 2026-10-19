using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using SnapTray.Models;

namespace SnapTray.Services;

/// <summary>必须在 WPF UI 线程创建、修改和释放；无需主窗口也能接收 WM_HOTKEY。</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly HwndSource _source;
    private int _activeId;
    private bool _disposed;

    public event EventHandler? Pressed;
    public HotkeyGesture? CurrentGesture { get; private set; }

    public HotkeyManager()
    {
        // HWND_MESSAGE (-3) 创建消息专用窗口：不显示、无任务栏按钮，但具有真实 HWND。
        _source = new HwndSource(new HwndSourceParameters("SnapTray.HotkeyHost")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
            Width = 0,
            Height = 0
        });
        _source.AddHook(WndProc);
    }

    public bool TryRegister(HotkeyGesture gesture, out string? error)
    {
        _source.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(gesture);
        if (!gesture.IsValid(out error)) return false;
        if (CurrentGesture == gesture) return true;

        // 用另一个 ID 先注册新组合，再注销旧组合。冲突时旧热键始终保持有效。
        // MOD_NOREPEAT 防止用户长按主键导致连续创建截图会话。
        int candidateId = _activeId == 1 ? 2 : 1;
        if (!RegisterHotKey(_source.Handle, candidateId, gesture.Modifiers | ModNoRepeat, gesture.VirtualKey))
        {
            int code = Marshal.GetLastWin32Error();
            error = code == 1409
                ? $"快捷键 {gesture} 已被其他程序占用。请在设置中更换组合，或关闭占用它的程序。"
                : $"无法注册快捷键 {gesture}（Windows 错误 {code}：{new Win32Exception(code).Message}）。"
                  + "该组合可能被系统保留，请在设置中更换组合。F12 由 Windows 调试器保留。";
            return false;
        }
        if (_activeId != 0 && !UnregisterHotKey(_source.Handle, _activeId))
        {
            int code = Marshal.GetLastWin32Error();
            UnregisterHotKey(_source.Handle, candidateId);
            error = $"注销旧快捷键失败（Windows 错误 {code}），未应用新设置，请重试。";
            return false;
        }
        _activeId = candidateId;
        CurrentGesture = gesture;
        error = null;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == _activeId && !_disposed)
        {
            // 同时核对键码，过滤快速更改设置后消息队列中仍可能存在的旧事件。
            long value = lParam.ToInt64();
            uint modifiers = (uint)(value & 0xFFFF) & ~ModNoRepeat;
            uint key = (uint)((value >> 16) & 0xFFFF);
            if (CurrentGesture is { } current && current.Modifiers == modifiers && current.VirtualKey == key)
            {
                handled = true;
                Pressed?.Invoke(this, EventArgs.Empty);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _source.Dispatcher.VerifyAccess();
        _disposed = true;
        if (_activeId != 0) UnregisterHotKey(_source.Handle, _activeId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        CurrentGesture = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
