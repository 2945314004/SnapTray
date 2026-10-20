using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTray.Helpers;
using SnapTray.Models;

namespace SnapTray.Services;

public sealed class ScreenCaptureService
{
    public DesktopSnapshot CaptureDesktop()
    {
        var monitors = NativeCapture.GetMonitors();
        Rectangle bounds = ScreenGeometry.GetDesktopBounds(monitors);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Black);
            // 必须在显示所有遮罩前抓取一次；之后所有预览/导出均使用这个冻结快照。
            // 源坐标允许为负，目标位图坐标固定为 (0,0)。多屏之间没有显示器的空隙为黑色。
            // CopyPixelOperation 不是可任意组合的 Flags；组合 CaptureBlt 会被此 API 的参数校验拒绝。
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size,
                CopyPixelOperation.SourceCopy);
        }

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            // BitmapSource.Create 会复制像素；不需要持有 GDI Bitmap/HBITMAP，避免重复截图泄漏句柄。
            BitmapSource image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32,
                null, data.Scan0, checked(data.Stride * data.Height), data.Stride);
            image.Freeze();
            return new DesktopSnapshot(image, bounds, monitors);
        }
        finally { bitmap.UnlockBits(data); }
    }

    public BitmapSource Crop(DesktopSnapshot snapshot, Rectangle physicalBounds)
    {
        Rectangle relative = ScreenGeometry.ToBitmapRectangle(physicalBounds, snapshot.Bounds);
        var crop = new CroppedBitmap(snapshot.Image, new Int32Rect(relative.X, relative.Y, relative.Width, relative.Height));
        return CopyToStandalone(crop, relative.Width, relative.Height);
    }

    /// <summary>重新抓取屏幕上的指定区域（滚动截图要一帧一帧地取，不能用冻结快照）。</summary>
    public BitmapSource CaptureRegion(Rectangle physicalBounds)
    {
        if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(physicalBounds), "截图区域必须至少为 1 × 1 像素。");
        using var bitmap = new Bitmap(physicalBounds.Width, physicalBounds.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(physicalBounds.Location, System.Drawing.Point.Empty, physicalBounds.Size,
                CopyPixelOperation.SourceCopy);
        }
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            BitmapSource image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32,
                null, data.Scan0, checked(data.Stride * data.Height), data.Stride);
            image.Freeze();
            return image;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static BitmapSource CopyToStandalone(BitmapSource source, int width, int height)
    {
        // CroppedBitmap 直接交付会一直引用整张桌面图；复制后只保留选区像素，贴图越多也不会保留多个整屏快照。
        int stride = checked((width * source.Format.BitsPerPixel + 7) / 8);
        byte[] pixels = new byte[checked(stride * height)];
        source.CopyPixels(pixels, stride, 0);
        BitmapSource image = BitmapSource.Create(width, height, 96, 96,
            source.Format, source.Palette, pixels, stride);
        image.Freeze();
        return image;
    }
}
