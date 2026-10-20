using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SnapTray.Services;

/// <summary>离线文字识别，使用系统自带的 Windows.Media.Ocr（不需要联网、不需要 API Key）。</summary>
public static class OcrService
{
    private static string? _unavailable;

    public static bool IsAvailable => Engine() is not null;

    /// <summary>不可用时的原因（缺语言包时会返回可读提示）。</summary>
    public static string? UnavailableReason
    {
        get
        {
            if (Engine() is not null) return null;
            return _unavailable ?? "系统未安装任何 OCR 识别语言包。请在 设置 → 时间和语言 → 语言 中添加中文或英文的「光学字符识别」组件。";
        }
    }

    private static OcrEngine? Engine()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"))
                ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        }
        catch (Exception ex)
        {
            _unavailable = "无法初始化系统 OCR：" + ex.Message;
            return null;
        }
    }

    public static async Task<string> RecognizeAsync(BitmapSource image)
    {
        OcrEngine? engine = Engine() ?? throw new InvalidOperationException(UnavailableReason);
        if (image.PixelWidth < 8 || image.PixelHeight < 8)
            throw new InvalidOperationException("选区太小，无法识别文字。");

        BitmapSource source = Prepare(image, engine);
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        encoder.Save(stream);
        stream.Position = 0;

        using var randomAccess = stream.AsRandomAccessStream();
        Windows.Graphics.Imaging.BitmapDecoder decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccess);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync();
        OcrResult result = await engine.RecognizeAsync(bitmap);

        // 按行拼接：逐词拼接会把中文断成一句一格。
        var builder = new StringBuilder();
        foreach (OcrLine line in result.Lines)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(string.Join(string.Empty, line.Words.Select(word => word.Text)));
        }
        return builder.ToString().Trim();
    }

    /// <summary>小图先放大再识别：系统 OCR 对小于 ~30px 的字几乎识别不出来。</summary>
    private static BitmapSource Prepare(BitmapSource image, OcrEngine engine)
    {
        double scale = 1;
        if (image.PixelWidth < 600 && image.PixelWidth > 0)
            scale = Math.Min(3.0, 600.0 / image.PixelWidth);
        int max = (int)OcrEngine.MaxImageDimension;
        double limit = Math.Min(max / (double)Math.Max(1, image.PixelWidth), max / (double)Math.Max(1, image.PixelHeight));
        if (scale > limit) scale = limit;
        if (scale <= 1.01) return image;

        var transformed = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        BitmapSource frozen = transformed;
        if (frozen.CanFreeze) frozen.Freeze();
        return frozen;
    }
}
