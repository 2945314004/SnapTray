using System;
using System.Windows;
using SnapTray.Services;

namespace SnapTray.Views;

public partial class TextResultWindow : Window
{
    private readonly bool _translate;
    private string _translated = string.Empty;

    public TextResultWindow(string text, bool translate)
    {
        InitializeComponent();
        SourceText.Text = text;
        _translate = translate;
        Title = translate ? "轻截 · 文字翻译" : "轻截 · 文字识别";
        if (translate)
        {
            DirectionText.Visibility = Visibility.Visible;
            TranslatedText.Visibility = Visibility.Visible;
            CopyTranslatedButton.Visibility = Visibility.Visible;
            DirectionText.Text = "翻译中…";
            StatusText.Text = "正在调用翻译服务，需要联网。";
            Loaded += async (_, _) => await TranslateAsync();
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "没有识别到文字，可以换个区域再试。";
        }
        else
        {
            StatusText.Text = $"共 {text.Length} 个字符，可直接复制。";
        }
    }

    private async System.Threading.Tasks.Task TranslateAsync()
    {
        try
        {
            TranslationResult result = await TranslationService.TranslateAsync(SourceText.Text);
            _translated = result.Text;
            TranslatedText.Text = result.Text;
            DirectionText.Text = $"翻译（{result.Direction} · {result.Provider}）";
            StatusText.Text = "翻译完成。";
        }
        catch (Exception ex)
        {
            DirectionText.Text = "翻译失败";
            TranslatedText.Text = ex.Message;
            StatusText.Text = "原文仍可复制；翻译需要能访问翻译服务的网络。";
        }
    }

    private void OnCopySource(object sender, RoutedEventArgs e) => CopyToClipboard(SourceText.Text, "原文已复制");

    private void OnCopyTranslated(object sender, RoutedEventArgs e) =>
        CopyToClipboard(_translated, string.IsNullOrEmpty(_translated) ? "还没有译文" : "译文已复制");

    private void CopyToClipboard(string text, string message)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = message;
            return;
        }
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = message;
        }
        catch (Exception ex)
        {
            StatusText.Text = "剪贴板被其他程序占用，请重试：" + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
