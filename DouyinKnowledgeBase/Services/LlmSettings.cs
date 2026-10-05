using System.Text.Json.Serialization;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 大模型接入点配置（OpenAI 兼容接口）。
/// </summary>
public sealed class LlmSettings
{
    /// <summary>接口基础地址，例如 https://api.openai.com/v1</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    /// <summary>API Key（仅保存在本机应用数据目录）。</summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";

    /// <summary>模型名称，例如 deepseek-chat / qwen-plus / gpt-4o-mini。</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>采样温度，范围 0~2，默认 0.7。</summary>
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.7;

    /// <summary>单次请求超时（秒），默认 300；长视频转写生成文章可能需要数分钟。</summary>
    [JsonPropertyName("timeoutSeconds")]
    public double TimeoutSeconds { get; set; } = 300;
}