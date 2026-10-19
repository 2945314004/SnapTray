using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SnapTray.Models;
using SnapTray.Services;
using SnapTray.Views;

namespace SnapTray;

public partial class App : Application
{
    private const string CaptureSignalName = @"Local\SnapTray.Capture";
    private const string FullScreenSignalName = @"Local\SnapTray.FullScreen";
    private const string SettingsSignalName = @"Local\SnapTray.Settings";

    private readonly SettingsService _settingsService = new();
    private readonly StartupService _startupService = new();
    private readonly ScreenCaptureService _captureService = new();
    private readonly ImageFileService _fileService = new();
    private readonly HashSet<PinWindow> _pins = new();
    private readonly HashSet<PreviewWindow> _previews = new();
    private AppSettings _settings = new();
    private HotkeyManager? _hotkeys;
    private TrayIconManager? _tray;
    private SettingsWindow? _settingsWindow;
    private CaptureSession? _session;
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private bool _working;
    private bool _quitting;
    private EventWaitHandle? _captureSignal;
    private EventWaitHandle? _fullScreenSignal;
    private EventWaitHandle? _settingsSignal;
    private Thread? _signalThread;
    private volatile bool _signalStop;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool wantCapture = HasArgument(e.Args, "--capture");
        bool wantFullScreen = HasArgument(e.Args, "--fullscreen");
        bool wantSettings = HasArgument(e.Args, "--settings");
        // 同一登录会话仅保留一个托盘进程，避免第二次启动抢占自己的热键。
        _instanceMutex = new Mutex(true, @"Local\SnapTray.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            // 已经有一个常驻实例在跑：把这次的意图转交给它，而不是白启动一个进程。
            if ((wantCapture || wantFullScreen || wantSettings) && SignalExistingInstance(wantFullScreen, wantSettings))
            {
                Shutdown();
                return;
            }
            MessageBox.Show("轻截已经在运行。请双击通知区图标截图，或右键打开设置。", "轻截");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            _settings = _settingsService.Load();
            // 以注册表的实际状态展示开机自启，不在启动时擅自写入 Run 键。
            _settings.StartWithWindows = _startupService.IsEnabled();
            StartSignalListener();
            _tray = new TrayIconManager(Dispatcher);
            _tray.CaptureRequested += (_, _) => BeginCapture();
            _tray.FullScreenCaptureRequested += (_, _) => BeginCapture(true);
            _tray.OpenSaveFolderRequested += (_, _) => OpenSaveFolder();
            _tray.SettingsRequested += (_, _) => OpenSettings();
            _tray.ExitRequested += (_, _) => Quit();
            CreateHotkeyManager();
            if (!_hotkeys!.TryRegister(_settings.Hotkey, out string? error))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show($"{error}\n仍可双击托盘图标截图，请在设置中更换快捷键。", "轻截 · 快捷键注册失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    OpenSettings();
                }));
            }
            else if (wantSettings)
                Dispatcher.BeginInvoke(new Action(OpenSettings));
            else if (wantFullScreen)
                Dispatcher.BeginInvoke(new Action(() => BeginCapture(true)));
            else if (wantCapture)
                Dispatcher.BeginInvoke(new Action(() => BeginCapture()));
            else if (!_settingsService.Existed)
                // 仅首次运行提示一次；日常开机启动静默驻留托盘，不弹气泡。
                _tray.Notify("轻截已就绪", $"按 {_settings.Hotkey} 截图，或双击托盘图标。右键可打开设置。");

            if (_settingsService.LastLoadWarning is { Length: > 0 } warning)
                _tray.Notify("设置已恢复", warning, true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"轻截启动失败：\n{ex.Message}", "轻截", MessageBoxButton.OK, MessageBoxImage.Error);
            Quit();
        }
    }

    private static bool HasArgument(string[] args, string name)
        => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把本次启动的意图发给已存在的实例。命名事件只用于单次唤醒，自动重置。
    /// 目标进程尚未创建事件（冷启动瞬间）时返回 false，让调用方走提示分支。
    /// </summary>
    private static bool SignalExistingInstance(bool fullScreen, bool settings)
    {
        string name = settings ? SettingsSignalName : fullScreen ? FullScreenSignalName : CaptureSignalName;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(name, out EventWaitHandle? handle))
            {
                using (handle) { handle.Set(); }
                return true;
            }
            Thread.Sleep(120);
        }
        return false;
    }

    private void StartSignalListener()
    {
        _captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureSignalName);
        _fullScreenSignal = new EventWaitHandle(false, EventResetMode.AutoReset, FullScreenSignalName);
        _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsSignalName);
        _signalThread = new Thread(SignalLoop) { IsBackground = true, Name = "SnapTray.Signal" };
        _signalThread.Start();
    }

    /// <summary>在后台线程阻塞等待，收到信号后切回 UI 线程执行，不能在等待线程里直接建窗口。</summary>
    private void SignalLoop()
    {
        WaitHandle[] handles = { _captureSignal!, _fullScreenSignal!, _settingsSignal! };
        while (!_signalStop)
        {
            int index;
            try { index = WaitHandle.WaitAny(handles); }
            catch (ObjectDisposedException) { return; }
            catch (AbandonedMutexException) { continue; }
            if (_signalStop || _quitting) return;
            if (index == WaitHandle.WaitTimeout) continue;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_quitting) return;
                if (index == 0) BeginCapture();
                else if (index == 1) BeginCapture(true);
                else OpenSettings();
            }));
        }
    }

    private void StopSignalListener()
    {
        _signalStop = true;
        // 唤醒阻塞中的 WaitAny，让后台线程退出后再释放句柄，避免 ObjectDisposedException 竞态。
        _captureSignal?.Set();
        _fullScreenSignal?.Set();
        _settingsSignal?.Set();
        _signalThread?.Join(400);
        _captureSignal?.Dispose();
        _fullScreenSignal?.Dispose();
        _settingsSignal?.Dispose();
        _captureSignal = _fullScreenSignal = _settingsSignal = null;
    }

    private void CreateHotkeyManager()
    {
        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += (_, _) =>
        {
            if (_settingsWindow?.IsRecordingHotkey == true) return;
            // 设置窗口在前台时不抢它的按键，否则遮罩会盖住用户正在编辑的设置。
            if (_settingsWindow is { IsActive: true }) return;
            // 不在 WM_HOTKEY 窗口过程里进行截图/创建窗口，以免嵌套消息处理。
            Dispatcher.BeginInvoke(new Action(() => BeginCapture()));
        };
    }

    private async void BeginCapture(bool fullScreen = false)
    {
        if (_quitting || _working || _session is not null) return;
        _working = true;
        try
        {
            // 等通知区右键菜单完成关闭，避免把菜单拍进截图。
            await Task.Delay(150);
            if (_quitting) return;
            var session = new CaptureSession(_captureService);
            _session = session;
            session.Completed += result => OnCaptureCompleted(session, result);
            session.Cancelled += (_, _) => EndSession(session);
            session.Start(fullScreen);
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            ShowError("无法开始截图，请确认桌面处于可交互状态后重试。", ex);
        }
        finally { _working = false; }
    }

    private void OpenSaveFolder()
    {
        try
        {
            string directory = _settings.SaveDirectory;
            Directory.CreateDirectory(directory);
            // explorer.exe 由 ShellExecute 打开，不用拼接 cmd 参数。
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowError("无法打开保存目录。", ex);
        }
    }

    private void EndSession(CaptureSession session)
    {
        if (ReferenceEquals(_session, session)) _session = null;
        session.Dispose();
    }

    private async void OnCaptureCompleted(CaptureSession session, CaptureResult result)
    {
        EndSession(session);
        if (_quitting) return;
        _working = true;
        try
        {
            if (!await PerformResultAsync(result, false, null) && !_quitting)
                ShowPreview(result);
        }
        finally { _working = false; }
    }

    private async Task<bool> PerformResultAsync(CaptureResult result, bool quickSave, Window? owner)
    {
        try
        {
            switch (result.Action)
            {
                case CaptureAction.Copy:
                    await ClipboardService.CopyAsync(result.Image);
                    _tray?.Notify("截图已复制", "可以粘贴到聊天窗口、文档或画图工具中。");
                    return true;
                case CaptureAction.Save:
                    string? saved = quickSave
                        ? _fileService.SaveToDefault(result.Image, _settings.SaveDirectory)
                        : _fileService.SaveWithDialog(result.Image, _settings.SaveDirectory, owner);
                    if (saved is null) return false;
                    _tray?.Notify("截图已保存", saved);
                    return true;
                case CaptureAction.Pin:
                    var pin = new PinWindow(result.Image, result.Bounds);
                    _pins.Add(pin);
                    pin.Closed += (_, _) => _pins.Remove(pin);
                    pin.SaveRequested += (window, image) =>
                    {
                        try
                        {
                            string? path = _fileService.SaveWithDialog(image, _settings.SaveDirectory, window);
                            if (path is not null) _tray?.Notify("截图已保存", path);
                        }
                        catch (Exception ex) { ShowError("无法保存截图。", ex, window); }
                    };
                    pin.Show();
                    return true;
                default: throw new ArgumentOutOfRangeException(nameof(result));
            }
        }
        catch (Exception ex)
        {
            ShowError("操作未完成，截图会保留以便重试。", ex, owner);
            return false;
        }
    }

    private void ShowPreview(CaptureResult result)
    {
        var preview = new PreviewWindow(result, async (capture, quick, owner) =>
        {
            if (_working || _session is not null) return false;
            _working = true;
            try { return await PerformResultAsync(capture, quick, owner); }
            finally { _working = false; }
        });
        _previews.Add(preview);
        preview.Closed += (_, _) => _previews.Remove(preview);
        preview.Show();
    }

    private void OpenSettings()
    {
        if (_quitting || _session is not null || _working) return;
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings.Clone(), ApplySettings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private string? ApplySettings(AppSettings candidate)
    {
        if (_hotkeys is null) return "热键服务尚未启动，请重新启动程序。";
        HotkeyGesture? oldHotkey = _hotkeys.CurrentGesture;
        bool oldStartup = _startupService.IsEnabled();
        bool hotkeyChanged = false;
        bool startupChanged = false;
        try
        {
            if (string.IsNullOrWhiteSpace(candidate.SaveDirectory) || !Path.IsPathFullyQualified(candidate.SaveDirectory))
                return "请选择有效的绝对保存目录。";
            Directory.CreateDirectory(candidate.SaveDirectory);
            if (!_hotkeys.TryRegister(candidate.Hotkey, out string? error)) return error;
            hotkeyChanged = true;
            if (candidate.StartWithWindows != oldStartup)
            {
                _startupService.SetEnabled(candidate.StartWithWindows);
                startupChanged = true;
            }
            _settingsService.Save(candidate);
            _settings = candidate.Clone();
            return null;
        }
        catch (Exception ex)
        {
            // JSON 或注册表失败时回滚已经应用的设置，避免界面、配置和实际热键不一致。
            var rollbackErrors = new List<string>();
            if (startupChanged)
            {
                try { _startupService.SetEnabled(oldStartup); }
                catch (Exception rollback) { rollbackErrors.Add("自启状态回滚失败：" + rollback.Message); }
            }
            if (hotkeyChanged)
            {
                if (oldHotkey is not null)
                {
                    if (!_hotkeys.TryRegister(oldHotkey, out string? rollback)) rollbackErrors.Add(rollback ?? "热键回滚失败");
                }
                else
                {
                    _hotkeys.Dispose();
                    CreateHotkeyManager();
                }
            }
            return "设置未保存：" + ex.Message + (rollbackErrors.Count == 0 ? "" : "\n" + string.Join("\n", rollbackErrors));
        }
    }

    private static void ShowError(string message, Exception ex, Window? owner = null)
    {
        if (owner is null) MessageBox.Show($"{message}\n{ex.Message}", "轻截", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(owner, $"{message}\n{ex.Message}", "轻截", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 清掉遮罩后报告错误，防止异常将用户困在置顶遮罩内。
        e.Handled = true;
        _session?.Dispose();
        _session = null;
        _working = false;
        ShowError("发生未预期的错误，请重试或重新启动轻截。", e.Exception);
    }

    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _session?.Dispose();
        _session = null;
        foreach (var pin in _pins.ToArray()) pin.Close();
        foreach (var preview in _previews.ToArray()) preview.Close();
        _settingsWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopSignalListener();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _session?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
