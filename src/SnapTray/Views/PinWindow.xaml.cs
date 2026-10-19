using System;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapTray.Helpers;
using SnapTray.Services;

namespace SnapTray.Views;

public partial class PinWindow : Window
{
    private readonly Rectangle _anchor;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private BitmapSource? _image;

    public event Action<PinWindow, BitmapSource>? SaveRequested;

    public PinWindow(BitmapSource image, Rectangle physicalBounds)
    {
        InitializeComponent();
        _image = image;
        _anchor = physicalBounds;
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        Screenshot.Source = image;
        Loaded += (_, _) => WindowPosition.PlaceImageWindow(this, _anchor, _pixelWidth, _pixelHeight);
        DpiChanged += OnDpiChanged;
        Closed += (_, _) => { Screenshot.Source = null; _image = null; };
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2)
        {
            // 双击还原为 1:1 像素尺寸，并回到截图时的位置。
            e.Handled = true;
            WindowPosition.PlaceImageWindow(this, _anchor, _pixelWidth, _pixelHeight);
            return;
        }
        try { DragMove(); } // 无边框 WPF 窗口通过原生移动循环拖拽，可跨显示器移动。
        catch (InvalidOperationException) { /* 快速松开鼠标时可能已不处于按下状态。 */ }
    }

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        e.Handled = true;
        double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        double width = Math.Clamp(ActualWidth * factor, 48, 8000);
        double height = Math.Clamp(ActualHeight * factor, 48, 8000);
        // 以指针位置为锚点缩放，避免画面在缩放时从光标下滑走。
        System.Windows.Point origin = e.GetPosition(this);
        double ratioX = origin.X / ActualWidth;
        double ratioY = origin.Y / ActualHeight;
        double left = Left + (ActualWidth - width) * ratioX;
        double top = Top + (ActualHeight - height) * ratioY;
        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        // WPF 只会保持 DIP 尺寸，贴图拖到另一块不同缩放的屏幕上物理大小会变。
        // 等本轮 DPI 布局结束，再按新旧比例换算回原来的物理尺寸。
        double ratio = e.OldDpi.DpiScaleX / e.NewDpi.DpiScaleX;
        if (Math.Abs(ratio - 1) < 0.001) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded) return;
            Width = Math.Max(1, ActualWidth * ratio);
            Height = Math.Max(1, ActualHeight * ratio);
        }));
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_image is null) return;
        try { await ClipboardService.CopyAsync(_image); }
        catch (Exception ex) { MessageBox.Show(this, $"暂时无法写入剪贴板，请重试。\n{ex.Message}", "轻截"); }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_image is not null) SaveRequested?.Invoke(this, _image);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        { e.Handled = true; OnCopyClick(this, e); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        { e.Handled = true; OnSaveClick(this, e); }
    }
}
