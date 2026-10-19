using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SnapTray.Models;

namespace SnapTray.Views;

public partial class PreviewWindow : Window
{
    private CaptureResult? _result;
    private readonly Func<CaptureResult, bool, Window, Task<bool>> _perform;
    private bool _working;

    public PreviewWindow(CaptureResult result, Func<CaptureResult, bool, Window, Task<bool>> perform)
    {
        InitializeComponent();
        _result = result;
        _perform = perform;
        Screenshot.Source = result.Image;
        Details.Text = $"{result.Image.PixelWidth} × {result.Image.PixelHeight} 像素 · 可继续复制、保存或置顶；关闭即丢弃本次截图。";
        Closed += (_, _) => { Screenshot.Source = null; _result = null; };
        Closing += (_, e) => { if (_working) e.Cancel = true; };
    }

    private async Task RunAsync(CaptureAction action, bool quickSave = false)
    {
        if (_working || _result is null) return;
        _working = true;
        Actions.IsEnabled = false;
        bool completed = false;
        try { completed = await _perform(new CaptureResult(action, _result.Image, _result.Bounds), quickSave, this); }
        finally { _working = false; Actions.IsEnabled = true; }
        if (completed) Close();
    }

    private async void OnCopy(object sender, RoutedEventArgs e) => await RunAsync(CaptureAction.Copy);
    private async void OnSave(object sender, RoutedEventArgs e) => await RunAsync(CaptureAction.Save);
    private async void OnQuickSave(object sender, RoutedEventArgs e) => await RunAsync(CaptureAction.Save, true);
    private async void OnPin(object sender, RoutedEventArgs e) => await RunAsync(CaptureAction.Pin);
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
