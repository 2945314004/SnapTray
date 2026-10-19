using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace SnapTray.Models;

/// <summary>Modifiers 使用 RegisterHotKey 的位标志；VirtualKey 是 Win32 虚拟键码。</summary>
public sealed record HotkeyGesture(uint Modifiers = 0x0001, uint VirtualKey = 0xC0)
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    /// <summary>MOD_WIN。仅用于识别并拒绝：Win 组合会被系统优先占用，注册通常失败。</summary>
    public const uint Windows = 0x0008;
    public static HotkeyGesture Default { get; } = new();

    public bool IsValid(out string? error)
    {
        if (Modifiers == 0 || (Modifiers & ~(Alt | Control | Shift)) != 0)
        {
            error = "请至少选择 Alt、Ctrl、Shift 中的一个修饰键。";
            return false;
        }
        if (!IsMainKey(VirtualKey))
        {
            error = "请选择一个主键，不能只使用 Ctrl、Alt、Shift 或 Windows 键。";
            return false;
        }
        error = null;
        return true;
    }

    public static bool IsMainKey(uint key) => key is >= 0x08 and <= 0xFE
        && key is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5);

    public static string GetKeyName(uint virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x60 and <= 0x69)
            return $"小键盘 {virtualKey - 0x60}";
        if (virtualKey is >= 0x70 and <= 0x87)
            return $"F{virtualKey - 0x70 + 1}";
        return virtualKey switch
        {
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause",
            0x14 => "Caps Lock", 0x1B => "Esc", 0x20 => "Space", 0x21 => "Page Up",
            0x22 => "Page Down", 0x23 => "End", 0x24 => "Home", 0x25 => "←",
            0x26 => "↑", 0x27 => "→", 0x28 => "↓", 0x2C => "Print Screen",
            0x2D => "Insert", 0x2E => "Delete", 0x5D => "菜单键", 0x90 => "Num Lock",
            0x91 => "Scroll Lock", 0xBA => ";", 0xBB => "=", 0xBC => ",",
            0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`（反引号）",
            0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            _ => KeyInterop.KeyFromVirtualKey((int)virtualKey) is var key && key != Key.None
                ? key.ToString() : $"VK 0x{virtualKey:X2}"
        };
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        parts.Add(GetKeyName(VirtualKey));
        return string.Join(" + ", parts);
    }
}
