using System;
using System.Collections.Generic;
using System.Drawing;

namespace SnapTray.Helpers;

/// <summary>纯物理像素几何。右、下边界为排他边界，与 Bitmap 裁剪约定一致。</summary>
public static class ScreenGeometry
{
    public static Rectangle SelectionFromPoints(Point start, Point end, Rectangle desktop)
    {
        int x1 = Math.Clamp(start.X, desktop.Left, desktop.Right);
        int y1 = Math.Clamp(start.Y, desktop.Top, desktop.Bottom);
        int x2 = Math.Clamp(end.X, desktop.Left, desktop.Right);
        int y2 = Math.Clamp(end.Y, desktop.Top, desktop.Bottom);
        return Rectangle.FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    public static Rectangle ToBitmapRectangle(Rectangle physicalBounds, Rectangle desktop)
    {
        Rectangle clipped = Rectangle.Intersect(physicalBounds, desktop);
        if (clipped.Width <= 0 || clipped.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(physicalBounds), "截图区域必须与桌面相交且至少为 1 × 1 像素。");
        // 副屏可位于主屏左边/上边；减去虚拟桌面原点后才是位图内非负坐标。
        return new Rectangle(clipped.X - desktop.X, clipped.Y - desktop.Y, clipped.Width, clipped.Height);
    }

    public static Rectangle GetDesktopBounds(IReadOnlyList<Rectangle> monitors)
    {
        if (monitors.Count == 0)
            throw new InvalidOperationException("未检测到可用显示器。");
        Rectangle result = monitors[0];
        for (int i = 1; i < monitors.Count; i++)
            result = Rectangle.Union(result, monitors[i]);
        return result;
    }

    public static Rectangle FindNearestMonitor(Point point, IReadOnlyList<Rectangle> monitors)
    {
        if (monitors.Count == 0)
            throw new InvalidOperationException("未检测到可用显示器。");
        Rectangle nearest = monitors[0];
        double minDistance = double.MaxValue;
        foreach (Rectangle monitor in monitors)
        {
            if (monitor.Contains(point)) return monitor;
            double dx = point.X - Math.Clamp(point.X, monitor.Left, monitor.Right - 1);
            double dy = point.Y - Math.Clamp(point.Y, monitor.Top, monitor.Bottom - 1);
            double distance = dx * dx + dy * dy;
            if (distance < minDistance) { nearest = monitor; minDistance = distance; }
        }
        return nearest;
    }
}
