using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using SnapTray.Helpers;
using SnapTray.Models;

namespace SnapTray.Views;

/// <summary>
/// 标注层。坐标是选区内的物理像素，通过 <see cref="Scale"/> 换算到 DIP 显示；
/// 跨屏选区时用 <see cref="OffsetX"/>/<see cref="OffsetY"/> 把画布裁到本显示器上。
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private readonly List<Annotation> _items = new();
    private Annotation? _preview;

    public IReadOnlyList<Annotation> Items => _items;
    public AnnotationSurface? Surface { get; set; }
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    public AnnotationCanvas()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public bool HasItems => _items.Count > 0;

    public void Add(Annotation annotation)
    {
        _items.Add(annotation);
        InvalidateVisual();
    }

    public void SetPreview(Annotation? annotation)
    {
        _preview = annotation;
        InvalidateVisual();
    }

    public void Undo()
    {
        if (_items.Count == 0) return;
        _items.RemoveAt(_items.Count - 1);
        InvalidateVisual();
    }

    public void Reset()
    {
        _items.Clear();
        _preview = null;
        Surface = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Surface is null || Scale <= 0) return;
        if (_items.Count == 0 && _preview is null) return;

        drawingContext.PushTransform(new ScaleTransform(Scale, Scale));
        drawingContext.PushTransform(new TranslateTransform(-OffsetX, -OffsetY));
        AnnotationRenderer.Draw(drawingContext, _items, Surface);
        _preview?.Draw(drawingContext, Surface);
        drawingContext.Pop();
        drawingContext.Pop();
    }

    /// <summary>画布 DIP 坐标 → 选区内的物理像素坐标。</summary>
    public Point ToSurfacePoint(Point canvasPoint) => new(
        canvasPoint.X / Scale + OffsetX,
        canvasPoint.Y / Scale + OffsetY);
}
