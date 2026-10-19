using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SnapTray.Services;

public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _drawingIcon;
    private bool _disposed;

    public event EventHandler? CaptureRequested;
    public event EventHandler? FullScreenCaptureRequested;
    public event EventHandler? OpenSaveFolderRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconManager(Dispatcher dispatcher)
    {
        _drawingIcon = CreateIcon();
        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        _menu.Items.Add("区域截图", null, (_, _) => OnUi(dispatcher, CaptureRequested));
        _menu.Items.Add("全屏截图", null, (_, _) => OnUi(dispatcher, FullScreenCaptureRequested));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("打开保存目录", null, (_, _) => OnUi(dispatcher, OpenSaveFolderRequested));
        _menu.Items.Add("设置…", null, (_, _) => OnUi(dispatcher, SettingsRequested));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => OnUi(dispatcher, ExitRequested));
        _icon = new Forms.NotifyIcon
        {
            Icon = _drawingIcon,
            Text = "轻截 · 双击截图，右键设置",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OnUi(dispatcher, CaptureRequested);
    }

    /// <summary>托盘回调发生在 WinForms 消息循环上下文，统一切回 WPF 的 UI 线程再执行。</summary>
    private void OnUi(Dispatcher dispatcher, EventHandler? handler)
    {
        if (handler is null) return;
        dispatcher.BeginInvoke(new Action(() => handler(this, EventArgs.Empty)));
    }

    public void Notify(string title, string message, bool error = false)
    {
        if (_disposed) return;
        _icon.ShowBalloonTip(3500, title, message, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(37, 99, 235));
            graphics.FillEllipse(background, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLines(pen, new[] { new Point(13, 8), new Point(8, 8), new Point(8, 13) });
            graphics.DrawLines(pen, new[] { new Point(19, 8), new Point(24, 8), new Point(24, 13) });
            graphics.DrawLines(pen, new[] { new Point(8, 19), new Point(8, 24), new Point(13, 24) });
            graphics.DrawLines(pen, new[] { new Point(19, 24), new Point(24, 24), new Point(24, 19) });
        }
        IntPtr nativeIcon = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(nativeIcon);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(nativeIcon); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false; // 退出时主动移除图标，避免通知区残留。
        _icon.Dispose();
        _menu.Dispose();
        _drawingIcon.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
