using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

    /// <summary>
    /// 流式对话（SSE，stream: true）。逐块调用 onDelta 回调；
    /// 采用「空闲超时」：只要持续收到内容就重置计时，长时间生成不会被误杀。
    /// 兼容各家 OpenAI 兼容接口的 data: {...} / data: [DONE] 格式，无法解析的块静默跳过。
    /// </summary>
    public async Task<string> ChatStreamAsync(
        LlmSettings settings,
        string systemPrompt,
        string userMessage,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        double timeoutSeconds = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 300;
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        string url = settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new
        {
            model = settings.Model,
            temperature = settings.Temperature,
            stream = true,
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
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, idleCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(idleCts.Token);
                throw new Exception($"HTTP {(int)response.StatusCode}: {Truncate(errorBody, 400)}");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(idleCts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            StringBuilder full = new();
            // 服务端若忽略 stream 参数直接返回普通 JSON（无 data: 前缀），收集原始行以便回退解析。
            StringBuilder fallback = new();
            string? line;
            while ((line = await reader.ReadLineAsync(idleCts.Token)) is not null)
            {
                // 收到任何数据（含心跳/空块）都重置空闲超时。
                idleCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    fallback.AppendLine(line);
                    continue;
                }

                string data = line["data:".Length..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                if (!TryReadStreamContent(data, out string? piece) || piece is null)
                {
                    continue;
                }

                full.Append(piece);
                onDelta?.Invoke(piece);
            }

            if (full.Length > 0)
            {
                return full.ToString();
            }

            // 回退：整段响应不是 SSE，尝试当普通 OpenAI JSON 解析。
            if (fallback.Length > 0)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(fallback.ToString());
                    if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) &&
                        choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("message", out JsonElement message) &&
                        message.TryGetProperty("content", out JsonElement content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        return content.GetString() ?? "";
                    }
                }
                catch (JsonException)
                {
                    // 既不是 SSE 也不是标准 JSON，交给调用方按空结果处理。
                }
            }

            return "";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消，原样抛出。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 空闲超时触发：长时间未收到任何内容。
            throw new TimeoutException(
                $"生成超时：模型在 {timeoutSeconds:0} 秒内未输出内容。可在「设置 → 模型设置」调大「请求超时（秒）」后重试。");
        }
    }

    /// <summary>解析一条 SSE data 块中的增量文本；兼容 delta / message 两种结构；无法解析返回 false。</summary>
    private static bool TryReadStreamContent(string data, out string? piece)
    {
        piece = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                choices.GetArrayLength() == 0)
            {
                return false;
            }

            JsonElement choice = choices[0];
            JsonElement segment = choice.TryGetProperty("delta", out JsonElement delta)
                ? delta
                : choice.TryGetProperty("message", out JsonElement message) ? message : default;
            if (segment.ValueKind != JsonValueKind.Object ||
                !segment.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            piece = content.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
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