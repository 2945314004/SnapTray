using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SnapTray.Helpers;
using SnapTray.Models;
using SnapTray.ViewModels;

namespace SnapTray.Views;

public partial class SettingsWindow : Window
{
    private readonly Func<AppSettings, string?> _applySettings;
    private readonly SettingsViewModel _viewModel;

    /// <summary>App 在此状态下忽略全局截图事件，避免录入当前快捷键时弹出截图遮罩。</summary>
    public bool IsRecordingHotkey => HotkeyRecorder.IsKeyboardFocusWithin || MainKeyBox.IsKeyboardFocusWithin;

    public SettingsWindow(AppSettings current, Func<AppSettings, string?> applySettings)
    {
        _applySettings = applySettings ?? throw new ArgumentNullException(nameof(applySettings));
        _viewModel = new SettingsViewModel(current);
        InitializeComponent();
        DataContext = _viewModel;
        VersionText.Text = $"v{AppInfo.Version}";
        Loaded += (_, _) => FitToWorkArea();
    }

    /// <summary>固定窗口高度在小屏或高缩放下会超出工作区，打开时先收进可用区域。</summary>
    private void FitToWorkArea()
    {
        double available = SystemParameters.WorkArea.Height - 32;
        if (available <= 0) return;
        MaxHeight = available;
        if (Height > available) Height = Math.Max(MinHeight, available);
    }

    private void RecordHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        // 保留无修饰 Tab 的焦点导航；Alt/Ctrl/Shift + Tab 仍可录入。
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
        e.Handled = true;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)
            return;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0 || key is Key.LWin or Key.RWin)
        {
            ShowStatus("请选择 Alt、Ctrl、Shift 修饰键，不使用 Windows 键。", false);
            return;
        }
        _viewModel.RecordKey((uint)KeyInterop.VirtualKeyFromKey(key), Keyboard.Modifiers);
        ShowStatus("已录入，请点击“应用设置”使其生效。", true);
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetHotkey();
        ShowStatus("已选择默认快捷键，请点击“应用设置”。", true);
    }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        // .NET 8 自带的 WPF 文件夹选择器，ShowDialog(this) 为它设置正确的父窗口。
        var dialog = new OpenFolderDialog { Title = "选择截图默认保存目录", Multiselect = false };
        if (Directory.Exists(_viewModel.SaveDirectory)) dialog.InitialDirectory = _viewModel.SaveDirectory;
        if (dialog.ShowDialog(this) == true) _viewModel.SaveDirectory = dialog.FolderName;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryBuildSettings(out AppSettings? settings, out string? error))
        {
            ShowStatus(error ?? "请检查设置。", false);
            return;
        }
        try
        {
            // App 负责协调注册热键、修改 Run 键与写入 JSON；失败则返回可读错误。
            error = _applySettings(settings!);
            if (error is not null)
            {
                ShowStatus(error, false);
                return;
            }
            _viewModel.SaveDirectory = settings!.SaveDirectory;
            ShowStatus("设置已保存并立即生效。", true);
        }
        catch (Exception ex)
        {
            ShowStatus($"应用设置失败：{ex.Message}", false);
        }
    }

    private void ShowStatus(string message, bool success)
    {
        StatusText.Foreground = new SolidColorBrush(success ? Color.FromRgb(22, 128, 86) : Color.FromRgb(185, 28, 28));
        StatusText.Text = message;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
