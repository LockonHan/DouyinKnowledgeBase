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
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// 发送一次普通对话，返回助手回复内容。
    /// </summary>
    public async Task<string> ChatAsync(LlmSettings settings, string systemPrompt, string userMessage)
    {
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
        using HttpResponseMessage response = await _http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
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