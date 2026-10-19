using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SnapTray.Services;

public sealed class ImageFileService
{
    public string? SaveWithDialog(BitmapSource image, string defaultDirectory, Window? owner = null)
    {
        Directory.CreateDirectory(defaultDirectory);
        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = defaultDirectory,
            FileName = NewName(),
            CheckPathExists = true
        };
        // 不更改目录、直接按保存，就写入设置中的默认目录。
        // 对话框的取消保持取消语义，交给调用方保留截图预览。
        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true) return null;
        SavePng(image, dialog.FileName, overwrite: true);
        return dialog.FileName;
    }

    public string SaveToDefault(BitmapSource image, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, NewName());
        // 毫秒时间戳通常已唯一；遇到同名使用随机后缀，不覆盖已有截图。
        if (File.Exists(path))
            path = Path.Combine(directory, $"截图_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
        SavePng(image, path, overwrite: false);
        return path;
    }

    public static void SavePng(BitmapSource image, string path, bool overwrite)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                encoder.Save(stream);
            // 先写同目录临时文件，编码失败不会破坏原图。
            File.Move(temporary, fullPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string NewName() => $"截图_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
}
