using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTray.Helpers;

namespace SnapTray.Services;

/// <summary>
/// 滚动截图：逐屏抓取 → 用行特征找出两帧之间的滚动量 → 只拼新增的那些行。
/// 只依赖像素比对，不注入任何钩子，所以对浏览器、记事本、PDF 阅读器都通用；
/// 带吸顶/悬浮层的页面可能在接缝处留下重复条带，这是纯像素方案固有限制。
/// </summary>
public static class ScrollingCaptureService
{
    private const int MaxFrames = 40;
    private const int MaxHeight = 20000;
    private const int ScrollNotches = 3;
    private const int SettleMilliseconds = 380;

    public static async Task<BitmapSource> CaptureAsync(ScreenCaptureService capture, System.Drawing.Rectangle region,
        IntPtr targetWindow, Action<string>? progress, CancellationToken token)
    {
        int width = region.Width;
        int height = region.Height;
        byte[]? stitched = null;
        byte[]? previous = null;
        int totalHeight = 0;

        for (int frame = 0; frame < MaxFrames; frame++)
        {
            token.ThrowIfCancellationRequested();
            if (NativeInput.EscapePressed()) break;

            BitmapSource shot = await CaptureSettledAsync(capture, region, token).ConfigureAwait(true);
            byte[] current = ToPixels(shot, out int w, out int h);
            if (w != width || h != height)
                throw new InvalidOperationException("抓取到的帧尺寸不一致，滚动截图已中止。");

            if (frame == 0)
            {
                stitched = (byte[])current.Clone();
                totalHeight = height;
                previous = current;
                ScrollOnce(region, targetWindow);
                progress?.Invoke("滚动截图中 · 按 Esc 结束");
                await Task.Delay(SettleMilliseconds, token).ConfigureAwait(true);
                continue;
            }

            int shift = FindScrollOffset(previous!, current, width, height);
            if (shift <= 0) break; // 已经到底，或这一屏根本没滚动

            byte[] grown = new byte[(totalHeight + shift) * width * 4];
            Buffer.BlockCopy(stitched!, 0, grown, 0, totalHeight * width * 4);
            // 只要新露出来的那几行（最后 shift 行），重叠部分已经在前一帧里了。
            Buffer.BlockCopy(current, (height - shift) * width * 4, grown, totalHeight * width * 4, shift * width * 4);
            stitched = grown;
            totalHeight += shift;
            previous = current;
            progress?.Invoke($"滚动截图中 · 已拼接 {totalHeight} 像素 · 按 Esc 结束");

            if (totalHeight >= MaxHeight) break;
            ScrollOnce(region, targetWindow);
            await Task.Delay(SettleMilliseconds, token).ConfigureAwait(true);
        }

        if (stitched is null) throw new InvalidOperationException("滚动截图没有抓到任何画面。");
        BitmapSource image = BitmapSource.Create(width, totalHeight, 96, 96, PixelFormats.Bgr32, null,
            stitched, width * 4);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 滚轮消息是按"焦点窗口"投递的，不是光标下的窗口——遮罩隐藏后焦点可能落在别处，
    /// 所以每次滚动前先把目标窗口抢回前台，再滚。
    /// </summary>
    private static void ScrollOnce(System.Drawing.Rectangle region, IntPtr targetWindow)
    {
        if (targetWindow != IntPtr.Zero) NativeCapture.ForceForeground(targetWindow);
        NativeInput.ParkCursor(region);
        NativeInput.ScrollDown(ScrollNotches);
    }

    /// <summary>
    /// 滚动后页面往往还在做动画/惰性渲染，直接抓会抓到半帧（拼接处出现撕裂条带）。
    /// 连抓两次，内容一致才认为是稳定帧，最多等约 0.5 秒。
    /// </summary>
    private static async Task<BitmapSource> CaptureSettledAsync(ScreenCaptureService capture,
        System.Drawing.Rectangle region, CancellationToken token)
    {
        BitmapSource previous = capture.CaptureRegion(region);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(110, token).ConfigureAwait(true);
            BitmapSource current = capture.CaptureRegion(region);
            if (ContentMatches(previous, current)) return current;
            previous = current;
            await Task.Delay(180, token).ConfigureAwait(true);
        }
        return previous;
    }

    private static bool ContentMatches(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        byte[] left = ToPixels(a, out int width, out int height);
        byte[] right = ToPixels(b, out _, out _);
        const int step = 97;   // 抽样步长：不逐像素比，够快也够敏感
        int difference = 0, samples = 0;
        for (int i = 0; i < left.Length; i += step)
        {
            difference += Math.Abs(left[i] - right[i]);
            samples++;
        }
        return samples > 0 && difference / samples <= 2;
    }

    private static byte[] ToPixels(BitmapSource image, out int width, out int height)
    {
        BitmapSource source = image.Format == PixelFormats.Bgr32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        width = source.PixelWidth;
        height = source.PixelHeight;
        byte[] pixels = new byte[width * 4 * height];
        source.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>在两帧之间找滚动位移：把上一帧的 [shift, h) 行与当前帧的 [0, h-shift) 行逐行比对。</summary>
    private static int FindScrollOffset(byte[] previous, byte[] current, int width, int height)
    {
        const int sampleStep = 16;      // 每 16 个像素取一个通道值
        const int checkRows = 60;       // 参与比对的样例行数
        int samples = Math.Max(1, width / sampleStep);
        byte[] prevSignature = RowSignature(previous, width, height, samples, sampleStep);
        byte[] curSignature = RowSignature(current, width, height, samples, sampleStep);

        int maxShift = height - checkRows - 1;
        if (maxShift <= 1) return 0;
        int bestShift = 0;
        long bestScore = long.MaxValue;
        for (int shift = 1; shift <= maxShift; shift++)
        {
            long score = 0;
            for (int row = 0; row < checkRows; row++)
            {
                int a = (shift + row) * samples;
                int b = row * samples;
                for (int k = 0; k < samples; k++) score += Math.Abs(prevSignature[a + k] - curSignature[b + k]);
            }
            if (score < bestScore)
            {
                bestScore = score;
                bestShift = shift;
                if (score == 0) break;
            }
        }
        long average = bestScore / (long)(checkRows * samples);
        // 平均通道差超过阈值说明没找到真正对应的内容（比如到底了、或页面是一条色带）。
        return average <= 8 ? bestShift : 0;
    }

    private static byte[] RowSignature(byte[] pixels, int width, int height, int samples, int sampleStep)
    {
        byte[] signature = new byte[height * samples];
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            int baseIndex = y * rowBytes;
            int offset = y * samples;
            for (int k = 0; k < samples; k++)
            {
                int x = k * sampleStep;
                signature[offset + k] = pixels[baseIndex + x * 4]; // 蓝色通道足够区分行
            }
        }
        return signature;
    }
}
