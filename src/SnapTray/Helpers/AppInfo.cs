using System;
using System.Reflection;

namespace SnapTray.Helpers;

internal static class AppInfo
{
    /// <summary>形如 "1.1.0"，来自 csproj 的 &lt;Version&gt;（AssemblyInformationalVersion）。</summary>
    public static string Version { get; } = ResolveVersion();

    public static string Name => "轻截 SnapTray";

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(AppInfo).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // 某些构建会在末尾追加 "+<commit>"，展示时去掉。
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational.Trim();
        }
        Version? version = assembly.GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
