using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SnapTray.Services;

public static class ClipboardService
{
    public static async Task CopyAsync(BitmapSource image)
    {
        // 必须在 WPF 的 STA 线程执行。剪贴板被其它程序短暂占用时异步重试，
        // 不在 UI 线程 Thread.Sleep，也不把剪贴板操作移入 Task.Run。
        Application.Current.Dispatcher.VerifyAccess();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return;
            }
            catch (ExternalException) when (attempt < 5)
            {
                await Task.Delay(70 * (attempt + 1));
            }
        }
    }
}
