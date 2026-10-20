using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTray.Services;

public sealed record TranslationResult(string Text, string Provider, string Direction);

/// <summary>
/// 免 Key 的翻译：按顺序尝试多个公开接口，谁通用谁的结果。
/// 网络不通时如实抛出失败原因，不退化成"假装成功"。
/// </summary>
public static class TranslationService
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // 默认走系统代理（HttpClient.DefaultProxy 读取 Windows 的 Internet 设置）。
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SnapTray/1.1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return client;
    }

    public static async Task<TranslationResult> TranslateAsync(string text, CancellationToken token = default)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) throw new InvalidOperationException("没有可翻译的文字。");
        if (trimmed.Length > 4000) trimmed = trimmed[..4000];

        bool chinese = LooksChinese(trimmed);
        string from = chinese ? "zh-CN" : "en";
        string to = chinese ? "en" : "zh-CN";
        string direction = chinese ? "中文 → English" : "English → 中文";

        var failures = new List<string>();
        foreach ((string name, Func<string, string, string, CancellationToken, Task<string>> provider) in Providers())
        {
            try
            {
                string translated = await provider(from, to, trimmed, token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(translated))
                    return new TranslationResult(translated.Trim(), name, direction);
                failures.Add($"{name}：返回空结果");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                or FormatException or InvalidOperationException)
            {
                failures.Add($"{name}：{Short(ex)}");
            }
        }
        throw new InvalidOperationException(
            "翻译服务都不可用（需要联网）：" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<(string Name, Func<string, string, string, CancellationToken, Task<string>> Run)> Providers()
    {
        yield return ("有道", YoudaoAsync);
        yield return ("MyMemory", MyMemoryAsync);
        yield return ("Google", GoogleAsync);
    }

    private static async Task<string> YoudaoAsync(string from, string to, string text, CancellationToken token)
    {
        string url = "https://fanyi.youdao.com/translate?&doctype=json&type=AUTO&i=" + Uri.EscapeDataString(text);
        string body = await Client.GetStringAsync(url, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errorCode", out JsonElement code) && code.GetInt32() != 0)
            throw new HttpRequestException("errorCode " + code.GetInt32());
        var parts = new StringBuilder();
        foreach (JsonElement line in document.RootElement.GetProperty("translateResult").EnumerateArray())
        foreach (JsonElement item in line.EnumerateArray())
            parts.Append(item.GetProperty("tgt").GetString());
        return parts.ToString();
    }

    private static async Task<string> MyMemoryAsync(string from, string to, string text, CancellationToken token)
    {
        string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={from}|{to}";
        string body = await Client.GetStringAsync(url, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        string? translated = document.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString();
        if (translated is null) throw new HttpRequestException("响应缺少 translatedText");
        // 接口超限时会回一段提示语，当失败处理，别把提示当译文显示。
        if (translated.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("免费额度已用完");
        return translated;
    }

    private static async Task<string> GoogleAsync(string from, string to, string text, CancellationToken token)
    {
        string url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t"
                     + $"&sl={from}&tl={to}&q={Uri.EscapeDataString(text)}";
        string body = await Client.GetStringAsync(url, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        var parts = new StringBuilder();
        foreach (JsonElement segment in document.RootElement[0].EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0) continue;
            string? piece = segment[0].GetString();
            if (piece is not null) parts.Append(piece);
        }
        return parts.ToString();
    }

    private static bool LooksChinese(string text)
    {
        int cjk = 0, letters = 0;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            else if (char.IsLetter(c)) letters++;
        }
        return cjk > 0 && cjk * 3 >= cjk + letters;
    }

    private static string Short(Exception ex)
    {
        string message = ex is TaskCanceledException ? "请求超时" : ex.Message;
        return message.Length > 80 ? message[..80] + "…" : message;
    }
}
