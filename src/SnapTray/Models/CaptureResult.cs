using System.Collections.Generic;
using System.Drawing;
using System.Windows.Media.Imaging;

namespace SnapTray.Models;

public enum CaptureAction
{
    Copy,
    Save,
    Pin,
    /// <summary>不直接执行动作，交给预览窗口让用户决定（滚动截图用它）。</summary>
    Preview
}

/// <summary>Bounds 使用虚拟桌面物理像素；Image 只有选区，不包含操作栏和遮罩。</summary>
public sealed record CaptureResult(CaptureAction Action, BitmapSource Image, Rectangle Bounds);

public sealed record DesktopSnapshot(BitmapSource Image, Rectangle Bounds, IReadOnlyList<Rectangle> Monitors);
