using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SnapTray.Models;

public enum AnnotationTool { None, Rectangle, Ellipse, Arrow, Pen, Mosaic, Text }

/// <summary>
/// 标注的坐标一律是「选区内的物理像素」，原点在选区左上角。
/// 这样屏幕预览按显示器缩放比缩放、导出时按 1:1 渲染，两处共用同一份数据。
/// </summary>
public abstract class Annotation
{
    protected Annotation(Color color, double thickness)
    {
        Color = color;
        Thickness = thickness;
    }

    public Color Color { get; }
    public double Thickness { get; }

    public abstract void Draw(DrawingContext context, AnnotationSurface surface);
}

/// <summary>导出的底图像素，供马赛克取样用（Bgra32，自上而下）。</summary>
public sealed class AnnotationSurface
{
    private readonly Dictionary<int, Color> _mosaicCache = new();

    public AnnotationSurface(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;

    public static AnnotationSurface FromBitmap(System.Windows.Media.Imaging.BitmapSource image)
    {
        var converted = image.Format == PixelFormats.Bgra32
            ? image
            : new System.Windows.Media.Imaging.FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new AnnotationSurface(converted.PixelWidth, converted.PixelHeight, pixels);
    }

    /// <summary>取某一格的平均色调（按格中心取样，够用且足够快）。</summary>
    public Color SampleCell(int cellX, int cellY, int cellSize)
    {
        int key = cellY * 100000 + cellX;
        if (_mosaicCache.TryGetValue(key, out Color cached)) return cached;
        int x = Math.Clamp(cellX * cellSize + cellSize / 2, 0, Width - 1);
        int y = Math.Clamp(cellY * cellSize + cellSize / 2, 0, Height - 1);
        int offset = y * Stride + x * 4;
        Color color = Color.FromRgb(Pixels[offset + 2], Pixels[offset + 1], Pixels[offset]);
        _mosaicCache[key] = color;
        return color;
    }
}

public sealed class RectangleAnnotation : Annotation
{
    public RectangleAnnotation(Rect bounds, Color color, double thickness) : base(color, thickness) => Bounds = bounds;
    public Rect Bounds { get; }

    public override void Draw(DrawingContext context, AnnotationSurface surface)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        // WPF 的描边本来就居中在几何线上，所以直接用用户拖出来的矩形，
        // 线宽的一半会自然落在框外、一半落在框内（再 Inflate 会让整条线整体外移）。
        var pen = new Pen(new SolidColorBrush(Color), Thickness) { LineJoin = PenLineJoin.Miter };
        pen.Freeze();
        context.DrawRectangle(null, pen, Bounds);
    }
}

public sealed class EllipseAnnotation : Annotation
{
    public EllipseAnnotation(Rect bounds, Color color, double thickness) : base(color, thickness) => Bounds = bounds;
    public Rect Bounds { get; }

    public override void Draw(DrawingContext context, AnnotationSurface surface)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var pen = new Pen(new SolidColorBrush(Color), Thickness);
        pen.Freeze();
        context.DrawEllipse(null, pen, new Point(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2),
            Bounds.Width / 2, Bounds.Height / 2);
    }
}

public sealed class ArrowAnnotation : Annotation
{
    public ArrowAnnotation(Point start, Point end, Color color, double thickness) : base(color, thickness)
    {
        Start = start;
        End = end;
    }

    public Point Start { get; }
    public Point End { get; }

    public override void Draw(DrawingContext context, AnnotationSurface surface)
    {
        double length = (End - Start).Length;
        if (length < 2) return;
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat };
        pen.Freeze();

        // 箭头长度随线宽走，短箭头也不会出现头部比线还细的怪样子。
        double head = Math.Min(length * 0.5, Math.Max(14, Thickness * 4.5));
        double angle = Math.Atan2(End.Y - Start.Y, End.X - Start.X);
        double spread = 0.42;
        Point baseCenter = new(End.X - Math.Cos(angle) * head, End.Y - Math.Sin(angle) * head);
        Point left = new(baseCenter.X - Math.Sin(angle) * head * Math.Sin(spread),
            baseCenter.Y + Math.Cos(angle) * head * Math.Sin(spread));
        Point right = new(baseCenter.X + Math.Sin(angle) * head * Math.Sin(spread),
            baseCenter.Y - Math.Cos(angle) * head * Math.Sin(spread));

        // 线只画到箭头根部，避免线头从箭尖戳出来。
        context.DrawLine(pen, Start, baseCenter);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(End, isFilled: true, isClosed: true);
            ctx.LineTo(left, isStroked: false, isSmoothJoin: false);
            ctx.LineTo(right, isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        context.DrawGeometry(brush, null, geometry);
    }
}

public sealed class PenAnnotation : Annotation
{
    public PenAnnotation(IReadOnlyList<Point> points, Color color, double thickness) : base(color, thickness)
        => Points = points;

    public IReadOnlyList<Point> Points { get; }

    public override void Draw(DrawingContext context, AnnotationSurface surface)
    {
        if (Points.Count < 2) return;
        var pen = new Pen(new SolidColorBrush(Color), Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(Points[0], isFilled: false, isClosed: false);
            for (int i = 1; i < Points.Count; i++) ctx.LineTo(Points[i], isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }
}

public sealed class MosaicAnnotation : Annotation
{
    private const int CellSize = 10;

    public MosaicAnnotation(IReadOnlyList<Point> points, double size, Color color, double thickness)
        : base(color, thickness)
    {
        Points = points;
        Size = size;
    }

    public IReadOnlyList<Point> Points { get; }
    /// <summary>笔刷直径（物理像素）。</summary>
    public double Size { get; }

    public override void Draw(DrawingContext context, AnnotationSurface surface)
    {
        if (surface.Width <= 0 || surface.Height <= 0) return;
        double radius = Math.Max(CellSize, Size) / 2;
        var painted = new HashSet<int>();
        foreach (Point point in Points)
        {
            int minX = (int)Math.Floor((point.X - radius) / CellSize);
            int maxX = (int)Math.Floor((point.X + radius) / CellSize);
            int minY = (int)Math.Floor((point.Y - radius) / CellSize);
            int maxY = (int)Math.Floor((point.Y + radius) / CellSize);
            for (int cy = minY; cy <= maxY; cy++)
            for (int cx = minX; cx <= maxX; cx++)
            {
                if (cx < 0 || cy < 0) continue;
                if (cx * CellSize >= surface.Width || cy * CellSize >= surface.Height) continue;
                if (!painted.Add(cy * 100000 + cx)) continue;
                var brush = new SolidColorBrush(surface.SampleCell(cx, cy, CellSize));
                brush.Freeze();
                context.DrawRectangle(brush, null,
                    new Rect(cx * CellSize, cy * CellSize, CellSize, CellSize));
            }
        }
    }
}

public sealed class TextAnnotation : Annotation
{
    public TextAnnotation(Point origin, string text, Color color, double fontSize) : base(color, 0)
    {
        Origin = origin;
        Text = text;
        FontSize = fontSize;
    }

    public Point Origin { get; }
    public string Text { get; }
    public double FontSize { get; }

    public override void Draw(DrawingContext context, AnnotationSurface surface)
    {
        if (string.IsNullOrEmpty(Text)) return;
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        var formatted = new FormattedText(Text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), FontSize, brush, 1.0);
        // 深色背景上自动描一圈浅边，保证文字在任意截图内容上都读得清。
        var halo = new GeometryDrawing(null,
            new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), Math.Max(1, FontSize / 12)),
            formatted.BuildGeometry(Origin));
        halo.Freeze();
        context.DrawGeometry(null, halo.Pen, halo.Geometry);
        context.DrawText(formatted, Origin);
    }
}
