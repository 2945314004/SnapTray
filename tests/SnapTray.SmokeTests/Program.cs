using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTray.Helpers;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using SnapTray.Models;
using SnapTray.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace SnapTray.SmokeTests;

/// <summary>
/// 无第三方测试框架的 Windows 集成测试。STA 是 WPF / HWND 的前提。
/// 不读取真实用户配置，不改 Run 注册表、剪贴板，不模拟任何键盘输入。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Run("Settings: missing file uses defaults", SettingsMissingFile);
        Run("Settings: Unicode path and hotkey round trip, replacement save", SettingsRoundTrip);
        Run("Settings: corrupt JSON falls back and preserves original", SettingsCorruptJson);
        Run("Settings: invalid values recover with a warning", SettingsInvalidFields);
        Run("Capture: negative desktop origin and exact pixel crop", CropNegativeCoordinates);
        Run("Capture: selection spanning differently positioned monitors", CropAcrossMonitors);
        Run("Capture: full desktop retains original dimensions and pixels", CropFullDesktop);
        Run("Geometry: drag direction, clamping and 1px minimum", SelectionGeometry);
        Run("Geometry: bitmap rectangle rejects non-intersecting region", BitmapRectangleGuards);
        Run("Geometry: nearest monitor for gap and negative coordinates", NearestMonitor);
        Run("Hotkey: gesture validation and display names", HotkeyGestureRules);
        Run("Annotation: rectangle stroke paints the border only", AnnotationRectangle);
        Run("Annotation: mosaic replaces cells with sampled colours", AnnotationMosaic);
        Run("Annotation: empty list returns the original bitmap", AnnotationPassthrough);
        Run("OCR: recognises text rendered into a bitmap", OcrRecognisesRenderedText);
        Run("Hotkey: conflict preserves old binding; switching and disposal release it", HotkeyLifecycle);

        if (args.Contains("--capture", StringComparer.OrdinalIgnoreCase))
            Run("Capture: live desktop matches physical monitor bounds", LiveDesktopCapture);
        else
        {
            _skipped++;
            Console.WriteLine("SKIP Live desktop capture (pass --capture to enable; no screen image is saved).");
        }

        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed, {_skipped} skipped.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (SkipException ex)
        {
            _skipped++;
            Console.WriteLine($"SKIP {name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {ex}");
        }
    }

    private static void SettingsMissingFile()
    {
        WithTemporaryDirectory(directory =>
        {
            var service = new SettingsService(directory);
            var settings = service.Load();
            Equal(HotkeyGesture.Default, settings.Hotkey, "Default shortcut");
            Equal(false, settings.StartWithWindows, "Default startup flag");
            Require(Path.IsPathFullyQualified(settings.SaveDirectory), "Default directory must be absolute.");
            Require(service.LastLoadWarning is null, "A missing first-run file is not a warning.");
            Require(!File.Exists(service.SettingsPath), "Loading defaults must not write settings.");
        });
    }

    private static void SettingsRoundTrip()
    {
        WithTemporaryDirectory(directory =>
        {
            var service = new SettingsService(directory);
            string saveDirectory = Path.Combine(directory, "图片 文件夹");
            var original = new AppSettings
            {
                StartWithWindows = true,
                SaveDirectory = saveDirectory,
                Hotkey = new HotkeyGesture(HotkeyGesture.Control | HotkeyGesture.Shift, 0x78)
            };
            service.Save(original);
            var loaded = new SettingsService(directory).Load();
            Equal(original.StartWithWindows, loaded.StartWithWindows, "Startup flag round trip");
            Equal(original.SaveDirectory, loaded.SaveDirectory, "Unicode directory round trip");
            Equal(original.Hotkey, loaded.Hotkey, "Shortcut round trip");
            Require(!Directory.Exists(saveDirectory), "Persisting settings must not create the image directory.");

            var edited = loaded.Clone();
            edited.StartWithWindows = false;
            edited.Hotkey = HotkeyGesture.Default;
            Equal(original.Hotkey, loaded.Hotkey, "Editing a clone must not change the source.");
            service.Save(edited);
            var replaced = service.Load();
            Equal(false, replaced.StartWithWindows, "Second save must replace first value");
            Equal(HotkeyGesture.Default, replaced.Hotkey, "Second save must replace first shortcut");
            Require(service.LastLoadWarning is null, "Valid saved data must load without a warning.");
            Require(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Successful saves must clean their temp files.");
        });
    }

    private static void SettingsCorruptJson()
    {
        WithTemporaryDirectory(directory =>
        {
            var service = new SettingsService(directory);
            const string corrupt = "{\"Hotkey\": [ incomplete";
            File.WriteAllText(service.SettingsPath, corrupt);
            var loaded = service.Load();
            Equal(HotkeyGesture.Default, loaded.Hotkey, "Corrupt JSON fallback shortcut");
            Require(!string.IsNullOrWhiteSpace(service.LastLoadWarning), "Corrupt JSON must produce an actionable warning.");
            Equal(corrupt, File.ReadAllText(service.SettingsPath), "Loading must preserve the corrupt source");
            string[] backups = Directory.GetFiles(directory, "settings.corrupt-*.json");
            Equal(1, backups.Length, "Corrupt source backup count");
            Equal(corrupt, File.ReadAllText(backups[0]), "Backup must preserve original bytes");
            service.Save(loaded);
            var recovered = service.Load();
            Equal(HotkeyGesture.Default, recovered.Hotkey, "Recovery settings must be readable");
            Require(service.LastLoadWarning is null, "Warning must clear after successful recovery.");
        });
    }

    private static void SettingsInvalidFields()
    {
        WithTemporaryDirectory(directory =>
        {
            var service = new SettingsService(directory);
            File.WriteAllText(service.SettingsPath,
                "{\"SaveDirectory\":\"relative-path\",\"Hotkey\":{\"Modifiers\":0,\"VirtualKey\":16}}");
            var loaded = service.Load();
            Equal(HotkeyGesture.Default, loaded.Hotkey, "Invalid shortcut must use default");
            Require(Path.IsPathFullyQualified(loaded.SaveDirectory), "Invalid directory must use an absolute default.");
            Require(!string.IsNullOrWhiteSpace(service.LastLoadWarning), "Invalid fields must produce a warning.");
            File.WriteAllText(service.SettingsPath, "{\"SaveDirectory\":null,\"Hotkey\":null}");
            loaded = service.Load();
            Equal(HotkeyGesture.Default, loaded.Hotkey, "Null shortcut must use default");
            Require(Path.IsPathFullyQualified(loaded.SaveDirectory), "Null directory must use an absolute default.");
        });
    }

    private static void CropNegativeCoordinates()
    {
        var snapshot = SyntheticDesktop();
        var result = new ScreenCaptureService().Crop(snapshot, new DrawingRectangle(-7, -2, 3, 3));
        AssertCrop(snapshot.Image, result, 1, 1, 3, 3);
    }

    private static void CropAcrossMonitors()
    {
        var snapshot = SyntheticDesktop();
        // 左屏 x=-8..-1，右屏 x=0..7。此区域横跨 x=0，且 y 原点不是 0。
        var result = new ScreenCaptureService().Crop(snapshot, new DrawingRectangle(-2, -1, 6, 4));
        AssertCrop(snapshot.Image, result, 6, 2, 6, 4);
    }

    private static void CropFullDesktop()
    {
        var snapshot = SyntheticDesktop();
        var result = new ScreenCaptureService().Crop(snapshot, snapshot.Bounds);
        AssertCrop(snapshot.Image, result, 0, 0, snapshot.Image.PixelWidth, snapshot.Image.PixelHeight);
    }

    private static DesktopSnapshot SyntheticDesktop()
    {
        const int width = 16;
        const int height = 10;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(x * 11);       // B
            pixels[offset + 1] = (byte)(y * 17);   // G
            pixels[offset + 2] = (byte)(x + y);    // R
            pixels[offset + 3] = 255;
        }
        // 非 96 DPI 的源位图使测试同时捕捉把 DIP 当物理像素进行裁剪的错误。
        var image = BitmapSource.Create(width, height, 144, 144, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return new DesktopSnapshot(image, new DrawingRectangle(-8, -3, width, height),
            new[] { new DrawingRectangle(-8, -3, 8, 10), new DrawingRectangle(0, -1, 8, 8) });
    }

    private static void AssertCrop(BitmapSource original, BitmapSource actual, int x, int y, int width, int height)
    {
        Equal(width, actual.PixelWidth, "Crop pixel width");
        Equal(height, actual.PixelHeight, "Crop pixel height");
        BitmapSource converted = actual.Format == PixelFormats.Bgra32 ? actual
            : new FormatConvertedBitmap(actual, PixelFormats.Bgra32, null, 0);
        byte[] source = new byte[original.PixelWidth * original.PixelHeight * 4];
        byte[] destination = new byte[width * height * 4];
        original.CopyPixels(source, original.PixelWidth * 4, 0);
        converted.CopyPixels(destination, width * 4, 0);
        for (int row = 0; row < height; row++)
        for (int col = 0; col < width; col++)
        for (int channel = 0; channel < 4; channel++)
            Equal(source[((row + y) * original.PixelWidth + col + x) * 4 + channel],
                destination[(row * width + col) * 4 + channel], $"Pixel ({col},{row}) channel {channel}");
    }

    private static void SelectionGeometry()
    {
        var desktop = new DrawingRectangle(-1920, 0, 3840, 1080);
        // 从右下往左上拖：矩形必须是规范化的 Left/Top/Right/Bottom。
        Equal(new DrawingRectangle(10, 20, 30, 40),
            ScreenGeometry.SelectionFromPoints(new System.Drawing.Point(40, 60), new System.Drawing.Point(10, 20), desktop),
            "Reverse drag");
        // 拖出桌面外要被裁剪，不能生成负坐标。
        Equal(new DrawingRectangle(-1920, 0, 1920, 1080),
            ScreenGeometry.SelectionFromPoints(new System.Drawing.Point(-5000, -5000), new System.Drawing.Point(0, 5000), desktop),
            "Clamped drag");
        // 只点一下（起点等于终点）得到零面积矩形，调用方据此判断“没有选区”。
        DrawingRectangle click = ScreenGeometry.SelectionFromPoints(
            new System.Drawing.Point(5, 5), new System.Drawing.Point(5, 5), desktop);
        Require(click.Width == 0 && click.Height == 0, "Click without drag must produce an empty selection.");
    }

    private static void BitmapRectangleGuards()
    {
        var desktop = new DrawingRectangle(-100, -50, 200, 100);
        Equal(new DrawingRectangle(0, 0, 200, 100),
            ScreenGeometry.ToBitmapRectangle(desktop, desktop), "Full desktop maps to origin");
        Equal(new DrawingRectangle(195, 90, 5, 10),
            ScreenGeometry.ToBitmapRectangle(new DrawingRectangle(95, 40, 10, 10), desktop),
            "Region hanging over the desktop edge is clipped to the bitmap");
        bool threw = false;
        try { ScreenGeometry.ToBitmapRectangle(new DrawingRectangle(500, 500, 10, 10), desktop); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Require(threw, "Non-intersecting region must throw instead of producing an empty bitmap.");
    }

    private static void NearestMonitor()
    {
        var monitors = new[]
        {
            new DrawingRectangle(-1920, -200, 1920, 1280), // 左侧副屏，原点为负
            new DrawingRectangle(0, 0, 1920, 1080),        // 主屏
        };
        Equal(monitors[0], ScreenGeometry.FindNearestMonitor(new System.Drawing.Point(-500, 400), monitors), "Left monitor");
        Equal(monitors[1], ScreenGeometry.FindNearestMonitor(new System.Drawing.Point(100, 100), monitors), "Right monitor");
        // 落在两屏之间的空隙（x=1930 已超出主屏），仍应选最近的而不是抛异常。
        Equal(monitors[1], ScreenGeometry.FindNearestMonitor(new System.Drawing.Point(2000, 500), monitors), "Gap point");
        bool threw = false;
        try { ScreenGeometry.GetDesktopBounds(Array.Empty<DrawingRectangle>()); }
        catch (InvalidOperationException) { threw = true; }
        Require(threw, "Empty monitor list must throw.");
    }

    private static void HotkeyGestureRules()
    {
        Require(!new HotkeyGesture(0, 0xC0).IsValid(out _), "Modifier-free shortcut must be rejected.");
        Require(!new HotkeyGesture(HotkeyGesture.Alt, 0x12).IsValid(out _), "Alt+Alt must be rejected.");
        Require(!new HotkeyGesture(HotkeyGesture.Windows | HotkeyGesture.Alt, 0xC0).IsValid(out _),
            "Windows key is not a supported modifier and must be rejected.");
        Require(new HotkeyGesture(HotkeyGesture.Alt, 0xC0).IsValid(out _), "Default Alt+` must be valid.");
        Equal("Alt + `（反引号）", HotkeyGesture.Default.ToString(), "Default shortcut label");
        Equal("Ctrl + Alt + F5", new HotkeyGesture(HotkeyGesture.Control | HotkeyGesture.Alt, 0x74).ToString(),
            "Combined shortcut label");
        Equal("小键盘 3", HotkeyGesture.GetKeyName(0x63), "Numpad name");
        Equal("F12", HotkeyGesture.GetKeyName(0x7B), "Function key name");
    }

    private static BitmapSource SolidBitmap(int width, int height, System.Windows.Media.Color color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static byte[] ToBgra(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        byte[] buffer = new byte[stride * image.PixelHeight];
        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        source.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    private static void AnnotationRectangle()
    {
        BitmapSource source = SolidBitmap(100, 100, System.Windows.Media.Colors.White);
        var items = new List<Annotation>
        {
            new RectangleAnnotation(new WpfRect(20, 20, 40, 40), System.Windows.Media.Colors.Red, 4)
        };
        BitmapSource composed = AnnotationRenderer.Compose(source, items);
        byte[] pixels = ToBgra(composed);
        Equal(100, composed.PixelWidth, "Compose must keep the pixel width");
        Equal(100, composed.PixelHeight, "Compose must keep the pixel height");

        int border = (40 * 100 + 20) * 4;   // 左边框上的一点 (20,40)
        Require(pixels[border + 2] > 180 && pixels[border + 1] < 90,
            $"Rectangle border should be painted red, got B={pixels[border]} G={pixels[border + 1]} R={pixels[border + 2]}");

        int inside = (40 * 100 + 40) * 4;   // 框内部 (40,40) 不应被填充
        Require(pixels[inside] > 230 && pixels[inside + 1] > 230 && pixels[inside + 2] > 230,
            "Rectangle must not fill its interior");
    }

    private static void AnnotationMosaic()
    {
        // 竖条纹底图：每 10px 一列不同颜色，马赛克后同一格内应变成纯色。
        const int width = 100, height = 100;
        byte[] source = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            source[offset] = (byte)(x * 2);
            source[offset + 1] = (byte)(x * 3 % 255);
            source[offset + 2] = (byte)(x * 5 % 255);
            source[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, source, width * 4);
        bitmap.Freeze();

        var items = new List<Annotation>
        {
            new MosaicAnnotation(new[] { new WpfPoint(50, 50) }, 41, System.Windows.Media.Colors.Black, 0)
        };
        byte[] composed = ToBgra(AnnotationRenderer.Compose(bitmap, items));

        int a = (45 * width + 45) * 4;
        int b = (48 * width + 48) * 4;
        Require(composed[a] == composed[b] && composed[a + 1] == composed[b + 1] && composed[a + 2] == composed[b + 2],
            "Pixels inside one mosaic cell must share the sampled colour");

        int outside = (95 * width + 95) * 4;
        Require(composed[outside] == source[outside] && composed[outside + 2] == source[outside + 2],
            "Pixels outside the mosaic brush must be untouched");

        // 纯色底图上取样必须还原成同一个颜色：能抓出行/列映射错位（stride 算错会取到别的行）。
        BitmapSource green = SolidBitmap(120, 120, System.Windows.Media.Color.FromRgb(0, 128, 0));
        var solidMosaic = new List<Annotation>
        {
            new MosaicAnnotation(new[] { new WpfPoint(60, 60) }, 30, System.Windows.Media.Colors.Black, 0)
        };
        byte[] solid = ToBgra(AnnotationRenderer.Compose(green, solidMosaic));
        int center = (60 * 120 + 60) * 4;
        Require(solid[center + 1] > 120 && solid[center] < 12 && solid[center + 2] < 12,
            $"Mosaic on a green area must keep the green tone, got B={solid[center]} G={solid[center + 1]} R={solid[center + 2]}");
        int corner = (2 * 120 + 2) * 4;
        Require(solid[corner + 1] > 120 && solid[corner] < 12,
            "Mosaic must not paint outside its brush radius");
    }

    private static void AnnotationPassthrough()
    {
        BitmapSource source = SolidBitmap(20, 20, System.Windows.Media.Colors.White);
        BitmapSource composed = AnnotationRenderer.Compose(source, Array.Empty<Annotation>());
        Require(ReferenceEquals(source, composed), "Compose without annotations should not copy pixels");
    }

    private static void OcrRecognisesRenderedText()
    {
        if (!OcrService.IsAvailable)
            throw new SkipException(OcrService.UnavailableReason ?? "系统没有可用的 OCR 引擎");

        // 先用 WPF 把文字画进位图，再交给 OCR，形成"栅格化 → 识别"的闭环，不依赖真实屏幕内容。
        const string expected = "SnapTray 123";
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new WpfRect(0, 0, 460, 100));
            var text = new FormattedText(expected, System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 44, Brushes.Black, 1.0);
            context.DrawText(text, new WpfPoint(20, 22));
        }
        var bitmap = new RenderTargetBitmap(460, 100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        string result = OcrService.RecognizeAsync(bitmap).GetAwaiter().GetResult();
        Require(result.Contains("123"), $"OCR 结果里应出现 123，实际得到：{result.Replace('\n', ' ')}");
    }

    private static void HotkeyLifecycle()
    {
        // 不发送 WM_HOTKEY 或真实按键。仅注册、检测冲突、注销真实的 HWND 全局热键。
        using var first = new HotkeyManager();
        using var second = new HotkeyManager();
        using var observer = new HotkeyManager();
        var candidates = new uint[] { 0x78, 0x79, 0x7A, 0x75, 0x76, 0x77 };
        var gestures = candidates.Select(key => new HotkeyGesture(
            HotkeyGesture.Control | HotkeyGesture.Alt | HotkeyGesture.Shift, key)).ToArray();
        HotkeyGesture? firstGesture = null;
        HotkeyGesture? secondGesture = null;
        var unavailable = new List<string>();
        foreach (var gesture in gestures)
        {
            var manager = firstGesture is null ? first : second;
            if (!manager.TryRegister(gesture, out string? error))
            {
                unavailable.Add($"{gesture}: {error}");
                continue;
            }
            if (firstGesture is null) firstGesture = gesture;
            else { secondGesture = gesture; break; }
        }
        if (firstGesture is null || secondGesture is null)
            throw new SkipException("Could not reserve two temporary Ctrl+Alt+Shift shortcuts. "
                + string.Join(" | ", unavailable));

        Require(!second.TryRegister(firstGesture, out string? conflict), "Duplicate shortcut registration must fail.");
        Require(!string.IsNullOrWhiteSpace(conflict), "Conflict must provide a user-facing error.");
        Equal(secondGesture, second.CurrentGesture, "Conflict must preserve previous binding");
        Require(!observer.TryRegister(secondGesture, out _), "Previous shortcut must still be reserved after conflict.");
        Require(second.TryRegister(secondGesture, out _), "Registering the same current shortcut should be idempotent.");

        first.Dispose();
        Require(second.TryRegister(firstGesture, out string? switchError), "Disposed first shortcut must be reusable: " + switchError);
        Equal(firstGesture, second.CurrentGesture, "Successful rebind must change the current shortcut");
        Require(observer.TryRegister(secondGesture, out string? releaseError), "Successful rebind must release old shortcut: " + releaseError);
        second.Dispose();
        Require(observer.TryRegister(firstGesture, out string? disposeError), "Dispose must release final shortcut: " + disposeError);
    }

    private static void LiveDesktopCapture()
    {
        if (!Environment.UserInteractive)
            throw new SkipException("Interactive Windows desktop is unavailable.");
        var snapshot = new ScreenCaptureService().CaptureDesktop();
        DrawingRectangle expected = System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds)
            .Aggregate(DrawingRectangle.Union);
        Equal(expected, snapshot.Bounds, "Snapshot physical virtual-desktop bounds");
        Equal(expected.Width, snapshot.Image.PixelWidth, "Live capture width");
        Equal(expected.Height, snapshot.Image.PixelHeight, "Live capture height");
        Equal(System.Windows.Forms.Screen.AllScreens.Length, snapshot.Monitors.Count, "Live capture monitor count");
        // 屏幕像素仅短暂存在内存中；不编码、保存或打印屏幕内容。
    }

    private static void WithTemporaryDirectory(Action<string> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "SnapTray.SmokeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally
        {
            // 只清理由本次测试创建并持有完整路径的唯一目录。
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'.");
    }

    private sealed class SkipException(string message) : Exception(message);
}
