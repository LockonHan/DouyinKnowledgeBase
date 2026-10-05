namespace DouyinKnowledgeBase.Pages;

/// <summary>「最近完成」列表的一行：快速获取页底部展示最近处理过的条目。</summary>
public sealed record RecentRow
{
    public required string Title { get; init; }

    public required string Sub { get; init; }

    public required string When { get; init; }

    public string TranscriptPath { get; init; } = "";
}
