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

    /// <summary>转写设备：auto / cpu / cuda:0。</summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = "auto";

    /// <summary>是否启用常驻转写加速（模型常驻内存，后续转写免加载）。</summary>
    [JsonPropertyName("asrResident")]
    public bool AsrResident { get; set; } = true;

    /// <summary>离线模型目录（可选）；留空则使用内置/默认缓存目录。</summary>
    [JsonPropertyName("modelsDir")]
    public string ModelsDir { get; set; } = "";
}