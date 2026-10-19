using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace SnapTray.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SnapTray";

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

    public static string GetStartupCommand()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定程序路径，请通过 SnapTray.exe 启动后重试。");
        // 所有路径都加双引号，避免空格让 Windows 把程序路径误拆成参数。
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assembly = Assembly.GetEntryAssembly()?.Location ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assembly))
                throw new InvalidOperationException("无法确定程序集路径，请通过 SnapTray.exe 启动后重试。");
            return $"\"{executable}\" \"{assembly}\"";
        }
        return $"\"{executable}\"";
    }
}
