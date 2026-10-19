using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using SnapTray.Models;

namespace SnapTray.Services;

/// <summary>只负责 JSON；热键和注册表的事务协调由 App 的设置应用回调完成。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _directory;
    public string SettingsPath => Path.Combine(_directory, "settings.json");
    public string? LastLoadWarning { get; private set; }
    /// <summary>本次 Load 是否读到了已存在的配置文件；用于区分首次运行与日常启动。</summary>
    public bool Existed { get; private set; }

    public SettingsService(string? settingsDirectory = null)
    {
        _directory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapTray");
    }

    public AppSettings Load()
    {
        LastLoadWarning = null;
        Existed = File.Exists(SettingsPath);
        if (!Existed) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? throw new JsonException("设置文件内容为空。");
            var warnings = new List<string>();
            if (settings.Hotkey is null || !settings.Hotkey.IsValid(out _))
            {
                settings.Hotkey = HotkeyGesture.Default;
                warnings.Add("快捷键配置无效，已使用默认 Alt + `。");
            }
            if (string.IsNullOrWhiteSpace(settings.SaveDirectory)
                || !Path.IsPathFullyQualified(settings.SaveDirectory))
            {
                settings.SaveDirectory = AppSettings.GetDefaultSaveDirectory();
                warnings.Add("保存目录配置无效，已使用图片文件夹下的“轻截”目录。");
            }
            else
            {
                // 验证路径语法，不在加载配置时创建用户指定的目录。
                settings.SaveDirectory = Path.GetFullPath(settings.SaveDirectory);
            }
            if (warnings.Count > 0) LastLoadWarning = string.Join(Environment.NewLine, warnings);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            string backupNote = "原设置文件保留在原处。";
            if (ex is JsonException or ArgumentException or NotSupportedException)
            {
                // 损坏的 JSON 先备份；用户随后保存新设置时仍可找回原始内容。
                string backup = Path.Combine(_directory,
                    $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                try
                {
                    File.Copy(SettingsPath, backup);
                    backupNote = $"原文件已备份到：{backup}";
                }
                catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
                {
                    backupNote = "无法备份原文件，请在保存新设置之前自行备份 settings.json。";
                }
            }
            LastLoadWarning = $"读取设置失败，本次使用默认设置。{Environment.NewLine}{ex.Message}"
                + $"{Environment.NewLine}{backupNote}";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Hotkey is null || !settings.Hotkey.IsValid(out _))
            throw new ArgumentException("快捷键配置无效。", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.SaveDirectory)
            || !Path.IsPathFullyQualified(settings.SaveDirectory))
            throw new ArgumentException("保存目录必须是完整的绝对路径。", nameof(settings));
        var copy = settings.Clone();
        copy.SaveDirectory = Path.GetFullPath(copy.SaveDirectory);
        Directory.CreateDirectory(_directory);
        string temporary = Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            // 临时文件与目标位于同一目录，写完并刷盘后再原子替换，避免中断留下半截 JSON。
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy, JsonOptions));
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, null);
            else File.Move(temporary, SettingsPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* 不覆盖最初的保存错误；残留临时文件不会被加载。 */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
