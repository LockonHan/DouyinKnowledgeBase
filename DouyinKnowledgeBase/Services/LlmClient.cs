using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// OpenAI 兼容的大模型客户端，用于后续的文稿总结与问答。
/// </summary>
public sealed class LlmClient
{
    private readonly HttpClient _http;

    public LlmClient()
    {
        // 不在实例级限制总超时：单次请求超时由 ChatAsync 按 settings.TimeoutSeconds 动态控制，
        // 以适配长文稿生成（固定 60 秒曾导致长视频转写生成文章被中断）。
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// 发送一次普通对话，返回助手回复内容。超时按 settings.TimeoutSeconds 动态控制；
    /// 超时抛出 TimeoutException（含友好提示），调用方主动取消则原样抛出。
    /// </summary>
    public async Task<string> ChatAsync(
        LlmSettings settings,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        double timeoutSeconds = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 300;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        string url = settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new
        {
            model = settings.Model,
            temperature = settings.Temperature,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        }

        request.Content = JsonContent.Create(payload, options: JsonDefaults.CaseInsensitive);

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, timeoutCts.Token);
            string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP {(int)response.StatusCode}: {Truncate(body, 400)}");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out JsonElement message) &&
                message.TryGetProperty("content", out JsonElement content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? "";
            }

            throw new Exception("响应中未找到 choices[0].message.content，请确认接入点兼容 OpenAI 格式。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消，原样抛出，不误报为超时。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 链接令牌因超时触发（CancelAfter），给出可操作的友好提示。
            throw new TimeoutException(
                $"生成超时：模型在 {timeoutSeconds:0} 秒内未完成响应。可在「设置 → 模型设置」调大「请求超时（秒）」后重试。");
        }
    }

    /// <summary>发送一条极简消息，用于“测试连接”。</summary>
    public async Task<string> TestAsync(LlmSettings settings)
    {
        return await ChatAsync(settings, "You are a helpful assistant.", "请只回复两个字：OK");
    }

    private static string Truncate(string text, int max)
    {
        return text.Length <= max ? text : text[..max] + "…";
    }
}