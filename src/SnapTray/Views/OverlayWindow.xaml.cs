using System;
using System.Collections.Generic;
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
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

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

    private AnnotationTool _tool = AnnotationTool.None;
    private Annotation? _draft;
    private bool _drawing;
    private List<Point>? _strokePoints;
    private Point _drawStart;
    private Color _color = Color.FromRgb(0xF5, 0x22, 0x2D);
    // 笔头粗细档位：细 / 中 / 粗。画笔按线宽换算，马赛克按笔刷直径换算。
    private static readonly double[] PenWidths = { 2, 4, 8 };
    private static readonly double[] MosaicSizes = { 14, 26, 44 };
    private int _sizeLevel = 1;
    private TextBox? _textEditor;
    private Point _textAnchor;
    private AnnotationSurface? _surface;

    public DrawingRectangle MonitorBounds { get; }

    internal IReadOnlyList<Annotation> Annotations => AnnotationLayer.Items;

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
        UpdateColorVisuals();
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

    internal void UpdateSelection(DrawingRectangle selection, bool showToolbar, AnnotationSurface? surface)
    {
        bool changed = selection != _selection;
        _selection = selection;
        _toolbarVisible = showToolbar;
        if (showToolbar) _hoverTarget = null;
        if (changed)
        {
            // 选区变了，之前画在旧选区上的标注全部作废（坐标系已经不同了）。
            CommitTextInput();
            AnnotationLayer.Reset();
            _draft = null;
            _drawing = false;
        }
        _surface = surface;
        Redraw();
    }

    private bool HasSelection => _selection.Width > 0 && _selection.Height > 0;

    /// <summary>标注线宽按显示器缩放换算，保证 150% 屏上看起来和 100% 屏一样粗。</summary>
    private double StrokeThickness => PenWidths[_sizeLevel] / Math.Max(0.2, AnnotationLayer.Scale);

    /// <summary>马赛克笔刷直径（物理像素），同样随显示器缩放。</summary>
    private double MosaicBrushSize => MosaicSizes[_sizeLevel] / Math.Max(0.2, AnnotationLayer.Scale);

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

        PositionAnnotationLayer(hasIntersection, intersection, local);
        DrawWindowHint(hasIntersection, width, height);

        bool hasSelection = HasSelection;
        HintBar.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        HintBar.MaxWidth = Math.Max(1, width - 24);
        HintBar.Measure(new Size(HintBar.MaxWidth, height));
        Canvas.SetLeft(HintBar, Math.Max(0, (width - HintBar.DesiredSize.Width) / 2));
        Canvas.SetTop(HintBar, Math.Min(24, Math.Max(0, height - HintBar.DesiredSize.Height)));

        ActionBar.Visibility = _toolbarVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_toolbarVisible) PositionActionBar(hasIntersection, local, width, height);
    }

    /// <summary>标注画布只覆盖本屏与选区的交集，坐标系仍是整个选区，靠 Offset 平移过来。</summary>
    private void PositionAnnotationLayer(bool hasIntersection, DrawingRectangle intersection, Rect local)
    {
        AnnotationLayer.Surface = _surface;
        if (!hasIntersection || _surface is null)
        {
            AnnotationLayer.Visibility = Visibility.Collapsed;
            return;
        }
        double scale = Surface.ActualWidth / MonitorBounds.Width;
        if (scale <= 0) { AnnotationLayer.Visibility = Visibility.Collapsed; return; }
        AnnotationLayer.Visibility = Visibility.Visible;
        AnnotationLayer.Scale = scale;
        AnnotationLayer.OffsetX = intersection.X - _selection.X;
        AnnotationLayer.OffsetY = intersection.Y - _selection.Y;
        AnnotationLayer.Width = local.Width;
        AnnotationLayer.Height = local.Height;
        Canvas.SetLeft(AnnotationLayer, local.X);
        Canvas.SetTop(AnnotationLayer, local.Y);
        AnnotationLayer.InvalidateVisual();
    }

    private void PositionActionBar(bool hasIntersection, Rect local, double width, double height)
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
        NativeCapture.ForceForeground(_handle);

        if (_tool != AnnotationTool.None)
        {
            if (!HasSelection || !TrySurfacePoint(e.GetPosition(AnnotationLayer), out Point anchor)) return;
            e.Handled = true;
            if (_tool == AnnotationTool.Text)
            {
                BeginTextInput(e.GetPosition(ControlsLayer), anchor);
                return;
            }
            if (!Mouse.Capture(this, CaptureMode.Element)) return;
            _drawing = true;
            _drawStart = anchor;
            _strokePoints = _tool is AnnotationTool.Pen or AnnotationTool.Mosaic
                ? new List<Point> { anchor }
                : null;
            _draft = BuildStroke(_strokePoints);
            AnnotationLayer.SetPreview(_draft);
            return;
        }

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
        if (_drawing)
        {
            if (TrySurfacePoint(e.GetPosition(AnnotationLayer), out Point point)) UpdateDraft(point);
            return;
        }
        if (!_dragCaptured)
        {
            UpdateHover();
            return;
        }
        if (_pressPending)
        {
            Point current = e.GetPosition(this);
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
        if (_drawing)
        {
            FinishDrawing();
            e.Handled = true;
            return;
        }
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
        if (_tool != AnnotationTool.None) { SetTool(AnnotationTool.None); return; }
        _session.Cancel();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragCaptured && !_drawing) ClearHover();
    }

    private void UpdateHover()
    {
        if (HintBar.IsMouseOver || ActionBar.IsMouseOver) { ClearHover(); return; }
        if (HasSelection) { ClearHover(); return; }

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
        if (_drawing) { FinishDrawing(); return; }
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

    internal void PrepareForScrolling()
    {
        CancelTextInput();
        _drawing = false;
        _draft = null;
        ReleaseDrag();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 中文输入法激活时字母键报告为 ImeProcessed，真实键在 ImeProcessedKey；
        // Alt 组合报告为 System，真实键在 SystemKey。不归一化的话 R/E/A/M 全都收不到。
        Key key = e.Key == Key.System ? e.SystemKey
            : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
            : e.Key;

        if (_textEditor is not null && _textEditor.IsKeyboardFocusWithin)
        {
            if (key == Key.Escape) { CancelTextInput(); e.Handled = true; }
            else if (key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                CommitTextInput();
                e.Handled = true;
            }
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (key == Key.Escape)
        {
            if (_tool != AnnotationTool.None) { SetTool(AnnotationTool.None); e.Handled = true; return; }
            _session.Cancel();
            e.Handled = true;
            return;
        }
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            switch (key)
            {
                case Key.Z: AnnotationLayer.Undo(); e.Handled = true; return;
                case Key.S: _session.Complete(CaptureAction.Save); e.Handled = true; return;
                case Key.P: _session.Complete(CaptureAction.Pin); e.Handled = true; return;
                case Key.E: _ = RunTextAsync(false); e.Handled = true; return;
                case Key.T: _ = RunTextAsync(true); e.Handled = true; return;
                case Key.L: _ = _session.StartScrollingAsync(this); e.Handled = true; return;
            }
        }
        if (modifiers == ModifierKeys.None && HasSelection)
        {
            AnnotationTool? shortcut = key switch
            {
                Key.R => AnnotationTool.Rectangle,
                Key.E => AnnotationTool.Ellipse,
                Key.A => AnnotationTool.Arrow,
                Key.B => AnnotationTool.Pen,
                Key.M => AnnotationTool.Mosaic,
                Key.T => AnnotationTool.Text,
                _ => null
            };
            if (shortcut is not null)
            {
                SetTool(_tool == shortcut ? AnnotationTool.None : shortcut.Value);
                e.Handled = true;
                return;
            }
        }
        if (key == Key.F) { _session.SelectFullScreen((modifiers & ModifierKeys.Control) != 0); e.Handled = true; }
        else if (key == Key.Enter) { _session.Complete(CaptureAction.Copy); e.Handled = true; }
    }

    private void OnCurrentScreen(object sender, RoutedEventArgs e) => _session.SelectFullScreen(false);
    private void OnAllScreens(object sender, RoutedEventArgs e) => _session.SelectFullScreen(true);
    private void OnCopy(object sender, RoutedEventArgs e) => _session.Complete(CaptureAction.Copy);
    private void OnSave(object sender, RoutedEventArgs e) => _session.Complete(CaptureAction.Save);
    private void OnPin(object sender, RoutedEventArgs e) => _session.Complete(CaptureAction.Pin);
    private void OnCancel(object sender, RoutedEventArgs e) => _session.Cancel();

    private void OnRectangleTool(object sender, RoutedEventArgs e) => SetTool(AnnotationTool.Rectangle);
    private void OnEllipseTool(object sender, RoutedEventArgs e) => SetTool(AnnotationTool.Ellipse);
    private void OnArrowTool(object sender, RoutedEventArgs e) => SetTool(AnnotationTool.Arrow);
    private void OnPenTool(object sender, RoutedEventArgs e) => SetTool(AnnotationTool.Pen);
    private void OnMosaicTool(object sender, RoutedEventArgs e) => SetTool(AnnotationTool.Mosaic);
    private void OnTextTool(object sender, RoutedEventArgs e) => SetTool(AnnotationTool.Text);
    private void OnUndoClick(object sender, RoutedEventArgs e) => AnnotationLayer.Undo();

    private void OnColorRed(object sender, MouseButtonEventArgs e) => SetColor(Color.FromRgb(0xF5, 0x22, 0x2D));
    private void OnColorOrange(object sender, MouseButtonEventArgs e) => SetColor(Color.FromRgb(0xFA, 0x8C, 0x16));
    private void OnColorBlue(object sender, MouseButtonEventArgs e) => SetColor(Color.FromRgb(0x16, 0x77, 0xFF));
    private void OnColorGreen(object sender, MouseButtonEventArgs e) => SetColor(Color.FromRgb(0x22, 0xC5, 0x5E));

    private void SetColor(Color color)
    {
        _color = color;
        UpdateColorVisuals();
    }

    private void UpdateColorVisuals()
    {
        (Border Swatch, Color Value)[] swatches =
        {
            (ColorRed, Color.FromRgb(0xF5, 0x22, 0x2D)),
            (ColorOrange, Color.FromRgb(0xFA, 0x8C, 0x16)),
            (ColorBlue, Color.FromRgb(0x16, 0x77, 0xFF)),
            (ColorGreen, Color.FromRgb(0x22, 0xC5, 0x5E))
        };
        foreach ((Border swatch, Color value) in swatches)
            swatch.BorderBrush = value == _color ? Brushes.White : Brushes.Transparent;
    }

    private void SetTool(AnnotationTool tool)
    {
        CommitTextInput();
        _tool = tool;
        _draft = null;
        _drawing = false;
        _strokePoints = null;
        AnnotationLayer.SetPreview(null);
        ReleaseDrag();
        UpdateToolVisuals();
        UpdateBrushPanel();
        Cursor = tool switch
        {
            AnnotationTool.None => Cursors.Cross,
            AnnotationTool.Text => Cursors.IBeam,
            _ => Cursors.Pen
        };
    }

    private void UpdateToolVisuals()
    {
        var active = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        var idle = new SolidColorBrush(Color.FromRgb(0x26, 0x34, 0x4A));
        var idleBorder = new SolidColorBrush(Color.FromRgb(0x3E, 0x51, 0x70));
        (Button Button, AnnotationTool Tool)[] tools =
        {
            (RectangleTool, AnnotationTool.Rectangle),
            (EllipseTool, AnnotationTool.Ellipse),
            (ArrowTool, AnnotationTool.Arrow),
            (PenTool, AnnotationTool.Pen),
            (MosaicTool, AnnotationTool.Mosaic),
            (TextTool, AnnotationTool.Text)
        };
        foreach ((Button button, AnnotationTool tool) in tools)
        {
            bool selected = _tool == tool;
            button.Background = selected ? active : idle;
            button.BorderBrush = selected ? Brushes.White : idleBorder;
        }
    }

    /// <summary>粗细只对画笔和马赛克有意义，其它工具选中时整块隐藏，避免误以为能调。</summary>
    private void UpdateBrushPanel()
    {
        bool visible = _tool is AnnotationTool.Pen or AnnotationTool.Mosaic;
        BrushSizePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SizeSeparator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        Border[] swatches = { SizeSmall, SizeMedium, SizeLarge };
        for (int i = 0; i < swatches.Length; i++)
        {
            bool active = i == _sizeLevel;
            swatches[i].BorderBrush = active ? Brushes.White : Brushes.Transparent;
            swatches[i].Background = new SolidColorBrush(active
                ? Color.FromRgb(0x25, 0x63, 0xEB)
                : Color.FromRgb(0x26, 0x34, 0x4A));
        }
    }

    private void SetSizeLevel(int level)
    {
        _sizeLevel = Math.Clamp(level, 0, PenWidths.Length - 1);
        UpdateBrushPanel();
    }

    private void OnSizeSmall(object sender, MouseButtonEventArgs e) => SetSizeLevel(0);
    private void OnSizeMedium(object sender, MouseButtonEventArgs e) => SetSizeLevel(1);
    private void OnSizeLarge(object sender, MouseButtonEventArgs e) => SetSizeLevel(2);

    private bool TrySurfacePoint(Point dipPoint, out Point surface)
    {
        surface = AnnotationLayer.ToSurfacePoint(dipPoint);
        return HasSelection
               && surface.X >= -4 && surface.Y >= -4
               && surface.X <= _selection.Width + 4 && surface.Y <= _selection.Height + 4;
    }

    private Annotation? BuildStroke(List<Point>? points)
    {
        if (points is null) return null;
        return _tool switch
        {
            AnnotationTool.Pen => new PenAnnotation(points, _color, StrokeThickness),
            AnnotationTool.Mosaic => new MosaicAnnotation(points, MosaicBrushSize, _color, 0),
            _ => null
        };
    }

    private void UpdateDraft(Point point)
    {
        switch (_tool)
        {
            case AnnotationTool.Rectangle:
                _draft = new RectangleAnnotation(Normalize(_drawStart, point), _color, StrokeThickness);
                break;
            case AnnotationTool.Ellipse:
                _draft = new EllipseAnnotation(Normalize(_drawStart, point), _color, StrokeThickness);
                break;
            case AnnotationTool.Arrow:
                _draft = new ArrowAnnotation(_drawStart, point, _color, StrokeThickness);
                break;
            case AnnotationTool.Pen:
            case AnnotationTool.Mosaic:
                if (_strokePoints is null) return;
                Point last = _strokePoints[^1];
                if ((point - last).Length < 1.5) return;
                _strokePoints.Add(point);
                _draft = BuildStroke(_strokePoints);
                break;
        }
        AnnotationLayer.SetPreview(_draft);
    }

    private void FinishDrawing()
    {
        _drawing = false;
        if (_draft is not null && IsMeaningful(_draft)) AnnotationLayer.Add(_draft);
        _draft = null;
        _strokePoints = null;
        AnnotationLayer.SetPreview(null);
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
    }

    private static bool IsMeaningful(Annotation annotation) => annotation switch
    {
        RectangleAnnotation rectangle => rectangle.Bounds.Width > 3 && rectangle.Bounds.Height > 3,
        EllipseAnnotation ellipse => ellipse.Bounds.Width > 3 && ellipse.Bounds.Height > 3,
        ArrowAnnotation arrow => (arrow.End - arrow.Start).Length > 6,
        PenAnnotation pen => pen.Points.Count > 1,
        MosaicAnnotation mosaic => mosaic.Points.Count > 0,
        _ => true
    };

    private static Rect Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void BeginTextInput(Point dipPoint, Point surfacePoint)
    {
        CommitTextInput();
        _textAnchor = surfacePoint;
        _textEditor = new TextBox
        {
            MinWidth = 150,
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 14,
            AcceptsReturn = true,
            Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
            Foreground = new SolidColorBrush(_color),
            BorderBrush = new SolidColorBrush(_color),
            BorderThickness = new Thickness(2),
            ToolTip = "输入文字，Enter 确认，Shift+Enter 换行，Esc 取消"
        };
        Canvas.SetLeft(_textEditor, Math.Max(0, dipPoint.X));
        Canvas.SetTop(_textEditor, Math.Max(0, dipPoint.Y));
        ControlsLayer.Children.Add(_textEditor);
        _textEditor.LostKeyboardFocus += (_, _) => CommitTextInput();
        _textEditor.Focus();
    }

    private void CommitTextInput()
    {
        TextBox? editor = _textEditor;
        if (editor is null) return;
        _textEditor = null;
        ControlsLayer.Children.Remove(editor);
        string text = editor.Text.Trim();
        // 字号按物理像素给出：屏幕上 20 DIP 的字，导出后仍然是这个视觉比例。
        if (text.Length > 0)
            AnnotationLayer.Add(new TextAnnotation(_textAnchor, text, _color,
                20 / Math.Max(0.2, AnnotationLayer.Scale)));
    }

    private void CancelTextInput()
    {
        TextBox? editor = _textEditor;
        if (editor is null) return;
        _textEditor = null;
        ControlsLayer.Children.Remove(editor);
    }

    private async void OnOcrClick(object sender, RoutedEventArgs e) => await RunTextAsync(false);

    private async void OnTranslateClick(object sender, RoutedEventArgs e) => await RunTextAsync(true);

    private async System.Threading.Tasks.Task RunTextAsync(bool translate)
    {
        BitmapSource? image = _session.SelectionImage;
        if (image is null) return;
        if (!OcrService.IsAvailable)
        {
            MessageBox.Show(this, OcrService.UnavailableReason, "轻截 · 文字识别",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // OCR 用未经标注的原始像素：箭头和马赛克会干扰识别。
        _session.Cancel();
        try
        {
            string text = await OcrService.RecognizeAsync(image);
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("这个区域没有识别到文字，换一块再试。", "轻截 · 文字识别",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            new TextResultWindow(text, translate).Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("文字识别失败：" + ex.Message, "轻截 · 文字识别",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnScrollClick(object sender, RoutedEventArgs e) => await _session.StartScrollingAsync(this);

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
        CancelTextInput();
        ReleaseDrag();
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnLoaded;
        DpiChanged -= OnDpiChanged;
        DesktopImage.Source = null;
        Shade.Data = null;
        Close();
    }
}
