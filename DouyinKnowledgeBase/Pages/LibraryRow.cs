using Microsoft.UI.Xaml;

namespace DouyinKnowledgeBase.Pages;

/// <summary>知识库列表的一行：把 KnowledgeItem 整理为可直接绑定的展示字段。</summary>
public sealed record LibraryRow
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>摘要：优先用文章的一句话摘要，否则取转写稿开头。</summary>
    public string Excerpt { get; init; } = "";

    public string DateLabel { get; init; } = "";

    public string StatusLabel { get; init; } = "";

    /// <summary>时长（mm:ss）；未知时为空串。</summary>
    public string DurationLabel { get; init; } = "";

    public string TranscriptPath { get; init; } = "";

    public string ArticleId { get; init; } = "";

    public bool HasArticle { get; init; }

    public bool HasVideo { get; init; }

    /// <summary>片格里的播放标记：只有本地存有视频时才显示。</summary>
    public Visibility PlayVisibility => HasVideo ? Visibility.Visible : Visibility.Collapsed;
}
