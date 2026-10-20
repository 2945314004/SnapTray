using System;
using System.IO;
using Microsoft.Win32;

namespace SnapTray.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SnapTray";
    private const string AppHostName = "SnapTray.exe";
    /// <summary>登录拉起时附加的参数：程序据此抑制弹窗，只留托盘气泡。</summary>
    public const string StartupArgument = "--startup";

    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public void SetEnabled(bool enabled)
    {
        // 仅当前用户 HKCU，不需要管理员权限。不要在程序启动时擅自重写用户的 Run 键。
        if (!enabled)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        string command = GetStartupCommand();
        using RegistryKey keyToWrite = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动注册表项。");
        keyToWrite.SetValue(ValueName, command, RegistryValueKind.String);
    }

    /// <summary>
    /// 已启用自启、但注册表里记录的命令与当前可执行文件不一致时改写。
    /// 典型场景：用户把程序目录改名或搬家后，旧路径失效；或旧版本写入过 dotnet.exe 形式（会闪控制台）。
    /// 只修复"已启用"的状态，不会因为用户关掉了自启就替他打开。
    /// </summary>
    public bool RepairRegistrationIfNeeded()
    {
        using RegistryKey? readKey = Registry.CurrentUser.OpenSubKey(RunKey);
        if (readKey?.GetValue(ValueName) is not string current || string.IsNullOrWhiteSpace(current)) return false;
        string expected;
        try { expected = GetStartupCommand(); }
        catch (InvalidOperationException) { return false; }
        if (string.Equals(current.Trim(), expected, StringComparison.OrdinalIgnoreCase)) return false;
        SetEnabled(true);
        return true;
    }

    /// <summary>
    /// 生成注册表 Run 值。必须是 GUI 子系统的 exe 直接启动，
    /// 绝不能拼 cmd /c 或 dotnet.exe —— 那两种方式在登录时会弹出控制台黑框。
    /// 末尾的 --startup 让程序知道这是登录拉起，从而抑制一切弹窗、只留托盘气泡。
    /// </summary>
    public static string GetStartupCommand()
    {
        // 双引号包住整个路径，避免空格让 Windows 把路径拆成多个参数。
        string? appHost = Path.Combine(AppContext.BaseDirectory, AppHostName);
        if (File.Exists(appHost)) return $"\"{appHost}\" {StartupArgument}";

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定程序路径，请通过 SnapTray.exe 启动后重试。");
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // 通过 `dotnet SnapTray.dll` 启动且找不到同目录的 apphost：
            // 写 dotnet.exe 会在登录时弹一个控制台窗口，这里直接拒绝并提示换用 exe 启动。
            throw new InvalidOperationException(
                "当前是通过 dotnet 命令启动的，无法写入静默的开机启动项。请直接运行 SnapTray.exe 后再开启开机自启。");
        }
        return $"\"{executable}\" {StartupArgument}";
    }
}
