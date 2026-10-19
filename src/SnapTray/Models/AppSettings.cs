using System;
using System.IO;

namespace SnapTray.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public string SaveDirectory { get; set; } = GetDefaultSaveDirectory();
    public HotkeyGesture Hotkey { get; set; } = HotkeyGesture.Default;

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        SaveDirectory = SaveDirectory,
        Hotkey = Hotkey with { }
    };

    public static string GetDefaultSaveDirectory()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
            pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        return Path.Combine(pictures, "轻截");
    }
}
