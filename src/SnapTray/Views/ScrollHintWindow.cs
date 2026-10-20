using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SnapTray.Views;

/// <summary>滚动截图期间的一行提示。不抢焦点，所以用户仍可以按 Esc 中止。</summary>
internal sealed class ScrollHintWindow : Window
{
    private readonly TextBlock _text;

    public ScrollHintWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        _text = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 14,
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x17, 0x20, 0x33)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 12, 18, 12),
            Child = _text
        };
    }

    public void SetMessage(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new System.Action(() => SetMessage(message)));
            return;
        }
        _text.Text = message;
        // 内容变长时窗口会重新量尺寸，保持居中不跳。
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = 48;
    }
}
