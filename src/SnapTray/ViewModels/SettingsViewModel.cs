using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SnapTray.Models;

namespace SnapTray.ViewModels;

public sealed record MainKeyOption(uint VirtualKey, string Name);

/// <summary>编辑独立副本；点击“应用”之前不会修改当前运行中的设置。</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private bool _startWithWindows;
    private bool _alt;
    private bool _control;
    private bool _shift;
    private string _saveDirectory;
    private MainKeyOption _selectedKey;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<MainKeyOption> MainKeys { get; }

    public SettingsViewModel(AppSettings current)
    {
        AppSettings copy = current.Clone();
        _startWithWindows = copy.StartWithWindows;
        _saveDirectory = copy.SaveDirectory;
        _alt = (copy.Hotkey.Modifiers & HotkeyGesture.Alt) != 0;
        _control = (copy.Hotkey.Modifiers & HotkeyGesture.Control) != 0;
        _shift = (copy.Hotkey.Modifiers & HotkeyGesture.Shift) != 0;
        IEnumerable<uint> keys = Enum.GetValues<Key>()
            .Select(key => (uint)KeyInterop.VirtualKeyFromKey(key))
            .Where(HotkeyGesture.IsMainKey)
            .Append(copy.Hotkey.VirtualKey)
            .Distinct()
            .OrderBy(key => key);
        MainKeys = new ObservableCollection<MainKeyOption>(keys.Select(key => new MainKeyOption(key, HotkeyGesture.GetKeyName(key))));
        _selectedKey = MainKeys.First(key => key.VirtualKey == copy.Hotkey.VirtualKey);
    }

    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public string SaveDirectory { get => _saveDirectory; set => Set(ref _saveDirectory, value); }
    public bool UseAlt { get => _alt; set { if (Set(ref _alt, value)) Notify(nameof(ShortcutLabel)); } }
    public bool UseControl { get => _control; set { if (Set(ref _control, value)) Notify(nameof(ShortcutLabel)); } }
    public bool UseShift { get => _shift; set { if (Set(ref _shift, value)) Notify(nameof(ShortcutLabel)); } }

    public MainKeyOption SelectedKey
    {
        get => _selectedKey;
        set { if (value is not null && Set(ref _selectedKey, value)) Notify(nameof(ShortcutLabel)); }
    }

    public string ShortcutLabel => CreateGesture().ToString();

    public void RecordKey(uint virtualKey, ModifierKeys modifiers)
    {
        if (!HotkeyGesture.IsMainKey(virtualKey)) return;
        var option = MainKeys.FirstOrDefault(key => key.VirtualKey == virtualKey);
        if (option is null)
        {
            option = new MainKeyOption(virtualKey, HotkeyGesture.GetKeyName(virtualKey));
            MainKeys.Add(option);
        }
        SelectedKey = option;
        // 只按主键时保留勾选项；按组合键时同步识别 Alt/Ctrl/Shift。
        ModifierKeys supported = modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift);
        if (supported != ModifierKeys.None)
        {
            UseAlt = (supported & ModifierKeys.Alt) != 0;
            UseControl = (supported & ModifierKeys.Control) != 0;
            UseShift = (supported & ModifierKeys.Shift) != 0;
        }
    }

    public void ResetHotkey()
    {
        UseAlt = true;
        UseControl = false;
        UseShift = false;
        SelectedKey = MainKeys.First(key => key.VirtualKey == HotkeyGesture.Default.VirtualKey);
    }

    public bool TryBuildSettings(out AppSettings? settings, out string? error)
    {
        settings = null;
        HotkeyGesture gesture = CreateGesture();
        if (!gesture.IsValid(out error)) return false;
        try
        {
            string directory = Environment.ExpandEnvironmentVariables(SaveDirectory.Trim());
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            {
                error = "请选择或输入完整的保存目录，例如 C:\\Users\\你的用户名\\Pictures。";
                return false;
            }
            directory = Path.GetFullPath(directory);
            settings = new AppSettings
            {
                StartWithWindows = StartWithWindows,
                SaveDirectory = directory,
                Hotkey = gesture
            };
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"保存目录无效：{ex.Message}";
            return false;
        }
    }

    private HotkeyGesture CreateGesture() => new(
        (UseAlt ? HotkeyGesture.Alt : 0) | (UseControl ? HotkeyGesture.Control : 0) | (UseShift ? HotkeyGesture.Shift : 0),
        SelectedKey.VirtualKey);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
