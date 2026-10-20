using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTray.Models;

namespace SnapTray.Helpers;

/// <summary>
/// 标注的唯一绘制实现：屏幕预览（<see cref="Views.AnnotationCanvas"/>）和导出合成都走这里，
/// 因此"屏幕上看到的"和"保存下来的"必然一致。
/// </summary>
public static class AnnotationRenderer
{
    public static void Draw(DrawingContext context, IReadOnlyList<Annotation> items, AnnotationSurface surface)
    {
        foreach (Annotation item in items) item.Draw(context, surface);
    }

    /// <summary>把标注烧进裁剪后的位图。没有标注时直接返回原图，避免无谓的像素拷贝。</summary>
    public static BitmapSource Compose(BitmapSource crop, IReadOnlyList<Annotation> items)
    {
        if (items.Count == 0) return crop;
        var surface = AnnotationSurface.FromBitmap(crop);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(crop, new Rect(0, 0, crop.PixelWidth, crop.PixelHeight));
            Draw(context, items, surface);
        }
        // 裁剪图的 DPI 固定 96（ScreenCaptureService 里创建时就是 96），
        // 所以这里按 96 渲染即 1:1 物理像素，不会因为屏幕缩放而变糊。
        var target = new RenderTargetBitmap(crop.PixelWidth, crop.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
