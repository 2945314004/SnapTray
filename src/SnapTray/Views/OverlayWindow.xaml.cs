using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapTray.Helpers;
using SnapTray.Models;
using SnapTray.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingPoint = System.Drawing.Point;

namespace SnapTray.Views;

public partial class OverlayWindow : Window
{
    private readonly CaptureSession _session;
    private DrawingRectangle _selection;
    private bool _toolbarVisible;
    private bool _dragCaptured;
    private bool _pressPending;
    private Point _pressPosition;
    private DrawingPoint _pressScreen;
    private WindowTarget? _hoverTarget;
    private bool _closingFromSession;
    private bool _placing;
    private IntPtr _handle;

    public DrawingRectangle MonitorBounds { get; }

    public OverlayWindow(CaptureSession session, DesktopSnapshot snapshot, DrawingRectangle monitorBounds)
    {
        _session = session;
        MonitorBounds = monitorBounds;
        InitializeComponent();
        DrawingRectangle local = ScreenGeometry.ToBitmapRectangle(monitorBounds, snapshot.Bounds);
        var monitorImage = new CroppedBitmap(snapshot.Image, new Int32Rect(local.X, local.Y, local.Width, local.Height));
        monitorImage.Freeze();
        DesktopImage.Source = monitorImage;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        DpiChanged += OnDpiChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        PlaceOnMonitor();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceOnMonitor();
        Redraw();
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        // WPF 接收 WM_DPICHANGED 后重排控件；待本轮布局完成，再校正到屏幕精确物理边界。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closingFromSession) return;
            PlaceOnMonitor();
            Redraw();
        }));
    }

    private void PlaceOnMonitor()
    {
        if (_handle == IntPtr.Zero || _placing || _closingFromSession) return;
        _placing = true;
        try { NativeCapture.PlaceOverlay(_handle, MonitorBounds); }
        finally { _placing = false; }
    }

    internal void UpdateSelection(DrawingRectangle selection, bool showToolbar)
    {
        _selection = selection;
        _toolbarVisible = showToolbar;
        // 直接清字段，避免 ClearHover 再触发一次重绘。
        if (showToolbar) _hoverTarget = null;
        Redraw();
    }

    private void Redraw()
    {
        double width = Surface.ActualWidth;
        double height = Surface.ActualHeight;
        if (width <= 0 || height <= 0) return;

        // 一块屏幕一个 HWND。物理选区先与本屏相交，再按实际客户区 DIP 宽高映射。
        // 因此两块 100%/150% 的屏幕各用自己的比例，跨屏选区也不会累积缩放偏差。
        DrawingRectangle intersection = DrawingRectangle.Intersect(_selection, MonitorBounds);
        Rect local = ToLocal(intersection);
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(new Rect(0, 0, width, height)));
        bool hasIntersection = intersection.Width > 0 && intersection.Height > 0;
        if (hasIntersection) mask.Children.Add(new RectangleGeometry(local));
        mask.Freeze();
        Shade.Data = mask;
        SelectionBorder.Visibility = hasIntersection ? Visibility.Visible : Visibility.Collapsed;
        SizeBadge.Visibility = hasIntersection ? Visibility.Visible : Visibility.Collapsed;
        if (hasIntersection)
        {
            // 向内缩半个边框，保证贴着屏幕边缘时仍能看到轮廓。
            SelectionBorder.Width = Math.Max(0, local.Width - 1.5);
            SelectionBorder.Height = Math.Max(0, local.Height - 1.5);
            Canvas.SetLeft(SelectionBorder, local.X + 0.75);
            Canvas.SetTop(SelectionBorder, local.Y + 0.75);
            SizeText.Text = $"{_selection.Width} × {_selection.Height} 像素";
            SizeBadge.Measure(new Size(width, height));
            Canvas.SetLeft(SizeBadge, Math.Clamp(local.X, 0, Math.Max(0, width - SizeBadge.DesiredSize.Width)));
            Canvas.SetTop(SizeBadge, local.Y >= SizeBadge.DesiredSize.Height + 8 ? local.Y - SizeBadge.DesiredSize.Height - 8 : Math.Min(height - SizeBadge.DesiredSize.Height, local.Y + 8));
        }

        DrawWindowHint(hasIntersection, width, height);

        bool hasSelection = _selection.Width > 0 && _selection.Height > 0;
        HintBar.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        HintBar.MaxWidth = Math.Max(1, width - 24);
        HintBar.Measure(new Size(HintBar.MaxWidth, height));
        Canvas.SetLeft(HintBar, Math.Max(0, (width - HintBar.DesiredSize.Width) / 2));
        Canvas.SetTop(HintBar, Math.Min(24, Math.Max(0, height - HintBar.DesiredSize.Height)));

        ActionBar.Visibility = _toolbarVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_toolbarVisible)
        {
            ActionBar.MaxWidth = Math.Max(1, width - 16);
            ActionBar.Measure(new Size(ActionBar.MaxWidth, height));
            double toolbarWidth = ActionBar.DesiredSize.Width;
            double toolbarHeight = ActionBar.DesiredSize.Height;
            double x = hasIntersection ? local.Right - toolbarWidth : (width - toolbarWidth) / 2;
            double y = hasIntersection ? local.Bottom + 12 : (height - toolbarHeight) / 2;
            if (y + toolbarHeight > height - 8)
                y = hasIntersection && local.Top >= toolbarHeight + 20 ? local.Top - toolbarHeight - 12 : height - toolbarHeight - 12;
            Canvas.SetLeft(ActionBar, Math.Clamp(x, 8, Math.Max(8, width - toolbarWidth - 8)));
            Canvas.SetTop(ActionBar, Math.Clamp(y, 8, Math.Max(8, height - toolbarHeight - 8)));
        }
    }

    /// <summary>无选区时高亮光标下的窗口，单击即可整窗截取。</summary>
    private void DrawWindowHint(bool hasIntersection, double width, double height)
    {
        DrawingRectangle hint = !hasIntersection && _hoverTarget is { } target
            ? DrawingRectangle.Intersect(target.Bounds, MonitorBounds)
            : DrawingRectangle.Empty;
        if (hint.Width <= 0 || hint.Height <= 0)
        {
            WindowHintBorder.Visibility = Visibility.Collapsed;
            WindowHintBadge.Visibility = Visibility.Collapsed;
            return;
        }

        Rect hintLocal = ToLocal(hint);
        WindowHintBorder.Visibility = Visibility.Visible;
        WindowHintBorder.Width = Math.Max(0, hintLocal.Width - 1.5);
        WindowHintBorder.Height = Math.Max(0, hintLocal.Height - 1.5);
        Canvas.SetLeft(WindowHintBorder, hintLocal.X + 0.75);
        Canvas.SetTop(WindowHintBorder, hintLocal.Y + 0.75);

        WindowHintText.Text = $"{_hoverTarget!.Title} · {_hoverTarget.Bounds.Width} × {_hoverTarget.Bounds.Height}";
        WindowHintBadge.Measure(new Size(width, height));
        double badgeWidth = WindowHintBadge.DesiredSize.Width;
        double badgeHeight = WindowHintBadge.DesiredSize.Height;
        // 优先放在窗口上方；贴到屏幕顶部时改放到窗口内部，避免被裁掉。
        double top = hintLocal.Y >= badgeHeight + 6 ? hintLocal.Y - badgeHeight - 6 : hintLocal.Y + 6;
        Canvas.SetLeft(WindowHintBadge, Math.Clamp(hintLocal.X, 0, Math.Max(0, width - badgeWidth)));
        Canvas.SetTop(WindowHintBadge, Math.Clamp(top, 0, Math.Max(0, height - badgeHeight)));
        WindowHintBadge.Visibility = Visibility.Visible;
    }

    private Rect ToLocal(DrawingRectangle rectangle) => new(
        (rectangle.X - MonitorBounds.X) * Surface.ActualWidth / MonitorBounds.Width,
        (rectangle.Y - MonitorBounds.Y) * Surface.ActualHeight / MonitorBounds.Height,
        Math.Max(0, rectangle.Width) * Surface.ActualWidth / MonitorBounds.Width,
        Math.Max(0, rectangle.Height) * Surface.ActualHeight / MonitorBounds.Height);

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void OnSelectionDown(object sender, MouseButtonEventArgs e)
    {
        // 透明 Canvas 的空白处不命中；工具条内部（包括按钮间距）不能开始新的选区。
        if (HintBar.IsMouseOver || ActionBar.IsMouseOver) return;
        Activate();
        Focus();
        if (!Mouse.Capture(this, CaptureMode.Element)) return;
        _dragCaptured = true;
        // 先不建选区：单击要能截取整窗，只有拖动超过系统阈值才转为区域截图。
        _pressPending = true;
        _pressPosition = e.GetPosition(this);
        _pressScreen = NativeCapture.CursorPosition();
        e.Handled = true;
    }

    private void OnSelectionMove(object sender, MouseEventArgs e)
    {
        if (!_dragCaptured)
        {
            UpdateHover();
            return;
        }
        if (_pressPending)
        {
            System.Windows.Point current = e.GetPosition(this);
            if (Math.Abs(current.X - _pressPosition.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(current.Y - _pressPosition.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _pressPending = false;
            ClearHover();
            _session.BeginSelection(_pressScreen);
        }
        _session.MoveSelection(NativeCapture.CursorPosition());
    }

    private void OnSelectionUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragCaptured) return;
        bool wasClick = _pressPending;
        _pressPending = false;
        if (wasClick)
        {
            // 单击：选中光标下的窗口；点在桌面上则保持无选区。
            // 热键触发后立刻按下（鼠标没移动过）时还没有 hover 结果，这里补一次查询。
            WindowTarget? target = _hoverTarget
                ?? WindowLocator.FromPoint(NativeCapture.CursorPosition(), (uint)Environment.ProcessId);
            ReleaseDrag();
            if (target is not null) _session.SelectWindow(target.Bounds);
            else Redraw();
        }
        else
        {
            // FinishSelection 先清理会话拖拽状态，释放鼠标产生的 LostMouseCapture 才不会误取消。
            _session.FinishSelection(NativeCapture.CursorPosition());
            ReleaseDrag();
        }
        e.Handled = true;
    }

    private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _session.Cancel();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragCaptured) ClearHover();
    }

    private void UpdateHover()
    {
        if (HintBar.IsMouseOver || ActionBar.IsMouseOver) { ClearHover(); return; }
        if (_selection.Width > 0 && _selection.Height > 0) { ClearHover(); return; }

        DrawingPoint cursor = NativeCapture.CursorPosition();
        // 缓存命中时省掉一次全窗口枚举：鼠标仍停在上次命中的窗口上就不必重新查。
        if (_hoverTarget is { } cached && cached.Bounds.Contains(cursor) && WindowLocator.IsWindow(cached.Handle))
            return;

        WindowTarget? target = WindowLocator.FromPoint(cursor, (uint)Environment.ProcessId);
        if (target == _hoverTarget) return;
        _hoverTarget = target;
        Redraw();
    }

    private void ClearHover()
    {
        if (_hoverTarget is null) return;
        _hoverTarget = null;
        Redraw();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragCaptured) return;
        _dragCaptured = false;
        _pressPending = false;
        _session.LostDragCapture();
    }

    internal void ReleaseDrag()
    {
        _dragCaptured = false;
        _pressPending = false;
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _session.Cancel(); e.Handled = true; }
        else if (e.Key == Key.F) { _session.SelectFullScreen((Keyboard.Modifiers & ModifierKeys.Control) != 0); e.Handled = true; }
        else if (e.Key == Key.Enter) { _session.Complete(CaptureAction.Copy); e.Handled = true; }
    }

    private void OnCurrentScreen(object sender, RoutedEventArgs e) => _session.SelectFullScreen(false);
    private void OnAllScreens(object sender, RoutedEventArgs e) => _session.SelectFullScreen(true);
    private void OnCopy(object sender, RoutedEventArgs e) => _session.Complete(CaptureAction.Copy);
    private void OnSave(object sender, RoutedEventArgs e) => _session.Complete(CaptureAction.Save);
    private void OnPin(object sender, RoutedEventArgs e) => _session.Complete(CaptureAction.Pin);
    private void OnCancel(object sender, RoutedEventArgs e) => _session.Cancel();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingFromSession) return;
        e.Cancel = true;
        // 避免在 Closing 处理器里递归 Close 当前窗口。
        Dispatcher.BeginInvoke(new Action(_session.Cancel));
    }

    internal void CloseFromSession()
    {
        _closingFromSession = true;
        _hoverTarget = null;
        ReleaseDrag();
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnLoaded;
        DpiChanged -= OnDpiChanged;
        DesktopImage.Source = null;
        Shade.Data = null;
        Close();
    }
}
