namespace DouyinKnowledgeBase.Services;

/// <summary>知识条目的处理状态。</summary>
public enum KnowledgeStatus
{
    /// <summary>已转写，尚未生成 AI 文章。</summary>
    Transcribed,

    /// <summary>已生成 AI 文章。</summary>
    Summarized,
}

/// <summary>
/// 知识库中的一个条目：一条抖音视频对应的本地产物（转写稿 / 视频 / 音频 / AI 文章）。
/// </summary>
public sealed record KnowledgeItem
{
    /// <summary>稳定标识（slug + 短哈希），同时用作文章文件名。</summary>
    public required string Id { get; init; }

    /// <summary>清洗后的展示标题（已去掉 #标签）。</summary>
    public required string Title { get; init; }

    /// <summary>抖音原标题，含话题标签。</summary>
    public required string SourceTitle { get; init; }

    /// <summary>从原标题解析出的话题标签。</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>转写稿路径；可能为空（仅有文章时）。</summary>
    public string TranscriptPath { get; init; } = "";

    public string? VideoPath { get; init; }

    public string? AudioPath { get; init; }

    /// <summary>时长（秒）；0 表示未知。</summary>
    public double DurationSec { get; init; }

    /// <summary>对应文章标识；为空表示尚未生成文章。</summary>
    public string? ArticleId { get; init; }

    /// <summary>文章摘要（一句话摘要），用于列表摘要行。</summary>
    public string Summary { get; init; } = "";

    public KnowledgeStatus Status { get; init; }

    /// <summary>最近活动时间，用于列表排序。</summary>
    public DateTime Timestamp { get; init; }

    public bool HasArticle => !string.IsNullOrEmpty(ArticleId);

    public bool HasVideo => !string.IsNullOrEmpty(VideoPath);

    public string DurationLabel =>
        DurationSec <= 0 ? "" : $"{(int)DurationSec / 60:00}:{(int)DurationSec % 60:00}";
}

/// <summary>
/// 文章元数据，与正文 &lt;id&gt;.md 同目录同名（&lt;id&gt;.json）。
/// 字段保持可缺省，便于向后兼容地读取历史文件。
/// </summary>
public sealed record ArticleMeta
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>文章摘要（Markdown 里的一句话摘要）。</summary>
    public string Summary { get; init; } = "";

    /// <summary>来源抖音原标题，用于与转写稿关联。</summary>
    public string SourceTitle { get; init; } = "";

    public string TranscriptPath { get; init; } = "";

    public string[] Tags { get; init; } = [];

    /// <summary>生成文章所用模型。</summary>
    public string Model { get; init; } = "";

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}
