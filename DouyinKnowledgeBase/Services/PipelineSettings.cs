using System.Text.Json.Serialization;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 抖音下载与本地转写管线配置。
/// </summary>
public sealed class PipelineSettings
{
    /// <summary>带 FunASR 的 Python 解释器路径。</summary>
    [JsonPropertyName("pythonPath")]
    public string PythonPath { get; set; } = "";

    /// <summary>数据目录（存放 videos / audio / transcripts / cookies.txt）。</summary>
    [JsonPropertyName("dataDir")]
    public string DataDir { get; set; } = "";

    /// <summary>转写设备，例如 cuda:0 或 cpu。</summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = "cuda:0";
}