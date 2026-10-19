using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SnapTray.Helpers;
using SnapTray.Models;
using SnapTray.Views;
using Point = System.Drawing.Point;

namespace SnapTray.Services;

/// <summary>一个会话拥有所有显示器的遮罩；一张快照、一份物理选区、一次完成/取消通知。</summary>
public sealed class CaptureSession : IDisposable
{
    private readonly ScreenCaptureService _service;
    private readonly Dispatcher _dispatcher;
    private readonly List<OverlayWindow> _windows = new();
    private DesktopSnapshot? _snapshot;
    private Point _start;
    private Rectangle _selection;
    private bool _dragging;
    private bool _started;
    private bool _ended;
    private bool _displayEventsSubscribed;
    private OverlayWindow? _toolbarWindow;

    public event Action<CaptureResult>? Completed;
    public event EventHandler? Cancelled;

    public CaptureSession(ScreenCaptureService service)
    {
        _service = service;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Start(bool fullScreen = false)
    {
        _dispatcher.VerifyAccess();
        if (_started || _ended) throw new InvalidOperationException("截图会话不能重复启动。");
        _started = true;
        try
        {
            _snapshot = _service.CaptureDesktop();
            Point cursor = NativeCapture.CursorPosition();
            Rectangle active = ScreenGeometry.FindNearestMonitor(cursor, _snapshot.Monitors);
            foreach (Rectangle monitor in _snapshot.Monitors)
            {
                var overlay = new OverlayWindow(this, _snapshot, monitor);
                _windows.Add(overlay);
            }
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            _displayEventsSubscribed = true;
            foreach (OverlayWindow overlay in _windows) overlay.Show();
            OverlayWindow activeWindow = _windows.Find(w => w.MonitorBounds == active) ?? _windows[0];
            activeWindow.Activate();
            activeWindow.Focus();
            // 全屏模式要等遮罩完成首轮布局（ActualWidth 有效）后再选中，否则 Redraw 会被跳过。
            if (fullScreen)
                _dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!_ended) SelectFullScreen(false);
                }));
        }
        catch
        {
            End();
            throw;
        }
    }

    internal void BeginSelection(Point point)
    {
        if (_ended || _snapshot is null) return;
        _start = point;
        _selection = Rectangle.Empty;
        _dragging = true;
        _toolbarWindow = null;
        Refresh();
    }

    internal void MoveSelection(Point point)
    {
        if (!_dragging || _ended || _snapshot is null) return;
        _selection = ScreenGeometry.SelectionFromPoints(_start, point, _snapshot.Bounds);
        Refresh();
    }

    internal void FinishSelection(Point point)
    {
        if (!_dragging || _ended || _snapshot is null) return;
        MoveSelection(point);
        _dragging = false;
        if (_selection.Width > 0 && _selection.Height > 0)
            _toolbarWindow = FindWindow(point);
        Refresh();
        _toolbarWindow?.Activate();
    }

    internal void LostDragCapture()
    {
        if (!_dragging || _ended) return;
        // 系统弹窗或其他程序抢走捕获时，不能留下永远处于拖拽状态的遮罩。
        Cancel();
    }

    internal void SelectFullScreen(bool allMonitors)
    {
        if (_ended || _snapshot is null) return;
        Point cursor = NativeCapture.CursorPosition();
        _dragging = false;
        foreach (OverlayWindow window in _windows) window.ReleaseDrag();
        _selection = allMonitors ? _snapshot.Bounds : ScreenGeometry.FindNearestMonitor(cursor, _snapshot.Monitors);
        _toolbarWindow = FindWindow(cursor);
        Refresh();
        _toolbarWindow?.Activate();
    }

    /// <summary>单击遮罩选中的整窗矩形（物理像素，已由调用方保证与桌面相交）。</summary>
    internal void SelectWindow(Rectangle windowBounds)
    {
        if (_ended || _snapshot is null) return;
        Rectangle clipped = Rectangle.Intersect(windowBounds, _snapshot.Bounds);
        if (clipped.Width <= 0 || clipped.Height <= 0) return;
        _dragging = false;
        foreach (OverlayWindow window in _windows) window.ReleaseDrag();
        _selection = clipped;
        // 选区中心可能落在别的显示器上（跨屏窗口），操作栏要出现在覆盖中心的那一块。
        _toolbarWindow = FindWindow(new Point(clipped.Left + clipped.Width / 2, clipped.Top + clipped.Height / 2));
        Refresh();
        _toolbarWindow?.Activate();
    }

    internal void Complete(CaptureAction action)
    {
        if (_ended || _snapshot is null || _selection.Width <= 0 || _selection.Height <= 0) return;
        CaptureResult result;
        try { result = new CaptureResult(action, _service.Crop(_snapshot, _selection), _selection); }
        catch (Exception ex)
        {
            MessageBox.Show(_toolbarWindow ?? _windows[0], "无法生成截图：" + ex.Message,
                "轻截", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        End();
        Completed?.Invoke(result);
    }

    public void Cancel()
    {
        _dispatcher.VerifyAccess();
        if (_ended) return;
        End();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private OverlayWindow FindWindow(Point point)
    {
        Rectangle bounds = ScreenGeometry.FindNearestMonitor(point, _snapshot!.Monitors);
        return _windows.Find(w => w.MonitorBounds == bounds) ?? _windows[0];
    }

    private void Refresh()
    {
        foreach (OverlayWindow overlay in _windows)
            overlay.UpdateSelection(_selection, !_dragging && ReferenceEquals(overlay, _toolbarWindow));
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(new Action(Cancel));
    }

    private void End()
    {
        if (_ended) return;
        _ended = true;
        _dragging = false;
        if (_displayEventsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            _displayEventsSubscribed = false;
        }
        foreach (OverlayWindow window in _windows)
            window.CloseFromSession();
        _windows.Clear();
        _toolbarWindow = null;
        _snapshot = null;
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        End();
        Completed = null;
        Cancelled = null;
        GC.SuppressFinalize(this);
    }
}
