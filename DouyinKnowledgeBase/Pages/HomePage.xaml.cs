using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DouyinKnowledgeBase.Services;

namespace DouyinKnowledgeBase.Pages;

/// <summary>
/// 知识库首页：列出本地已沉淀的转写稿与文章，支持全文搜索、状态与标签筛选。
/// </summary>
public sealed partial class HomePage : Page
{
    private const int ExcerptLength = 88;
    private const int MaxTagButtons = 12;

    private readonly KnowledgeIndex _index = new();
    private readonly PipelineSettingsService _settingsService = new();

    private IReadOnlyList<KnowledgeItem> _all = [];
    private KnowledgeStatus? _status;
    private string? _tag;

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PipelineBus.Progress += OnPipelineProgress;
        PipelineBus.Finished += OnPipelineFinished;
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        PipelineBus.Progress -= OnPipelineProgress;
        PipelineBus.Finished -= OnPipelineFinished;
    }

    private void OnPipelineProgress(object? sender, PipelineProgress progress)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyPipelineProgress(progress);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => ApplyPipelineProgress(progress));
        }
    }

    private void ApplyPipelineProgress(PipelineProgress progress)
    {
        LiveBar.Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(PipelineBus.CurrentTitle))
        {
            LiveTitle.Text = PipelineBus.CurrentTitle;
        }

        string label = progress.Stage switch
        {
            "parse" => "正在解析链接",
            "download" => "正在下载视频",
            "audio" => "正在提取音频",
            "transcribe" => "正在转写",
            _ => "正在处理",
        };

        if (progress.Percent >= 0)
        {
            LiveBarProgress.IsIndeterminate = false;
            LiveBarProgress.Value = Math.Clamp(progress.Percent, 0, 100);
            LiveState.Text = progress.Percent >= 100 ? label : $"{label} {progress.Percent:F0}%";
        }
        else
        {
            LiveBarProgress.IsIndeterminate = true;
            LiveState.Text = label;
        }
    }

    private void OnPipelineFinished(object? sender, PipelineResultNotice notice)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            LiveBar.Visibility = Visibility.Collapsed;
            _ = ReloadAsync();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                LiveBar.Visibility = Visibility.Collapsed;
                _ = ReloadAsync();
            });
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            PipelineSettings settings = await _settingsService.LoadAsync();
            _all = await _index.LoadAsync(settings);
        }
        catch (Exception ex)
        {
            _all = [];
            StatsText.Text = $"读取本地知识库失败：{ex.Message}";
        }

        RenderTagRow();
        ApplyFilter();
    }

    private void RenderTagRow()
    {
        IReadOnlyList<(string Tag, int Count)> counts = KnowledgeIndex.TagCounts(_all);
        List<string> labels = counts
            .Take(MaxTagButtons)
            .Select(pair => pair.Count > 1 ? $"{pair.Tag}（{pair.Count}）" : pair.Tag)
            .ToList();

        TagList.ItemsSource = labels;
        TagList.Visibility = labels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        string? search = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();

        // 先按标题/标签/状态过滤，再对未命中的做正文检索（全文搜索）。
        IReadOnlyList<KnowledgeItem> byMeta = KnowledgeIndex.Query(_all, search, _status, _tag);

        List<KnowledgeItem> matched = new(byMeta);
        if (search is not null)
        {
            HashSet<KnowledgeItem> seen = new(byMeta);
            foreach (KnowledgeItem item in _all)
            {
                if (!seen.Contains(item) && MatchesFilter(item) && KnowledgeIndex.MatchesBody(item, search))
                {
                    matched.Add(item);
                }
            }

            matched.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        }

        List<LibraryRow> rows = matched.Select(ToRow).ToList();
        ItemList.ItemsSource = rows;

        TotalText.Text = $"共 {matched.Count} 条";
        if (_all.Count > 0)
        {
            StatsText.Text = BuildStats();
        }

        bool empty = rows.Count == 0;
        ItemList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0
            ? "还没有内容。到「快速获取」粘贴一个抖音链接，转写完成后就会出现在这里。"
            : "没有匹配的内容，换个关键词或筛选条件试试。";
    }

    private bool MatchesFilter(KnowledgeItem item)
    {
        if (_status is not null && item.Status != _status)
        {
            return false;
        }

        if (_tag is not null && !item.Tags.Contains(_tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private string BuildStats()
    {
        int total = _all.Count;
        int summarized = _all.Count(item => item.Status == KnowledgeStatus.Summarized);
        DateTime now = DateTime.Now;
        int thisMonth = _all.Count(item => item.Timestamp.Year == now.Year && item.Timestamp.Month == now.Month);
        return $"共 {total} 条内容，已总结 {summarized} 篇，待总结 {total - summarized} 篇，本月新增 {thisMonth} 条";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void StatusTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked)
        {
            return;
        }

        _status = (clicked.Tag as string) switch
        {
            "Summarized" => KnowledgeStatus.Summarized,
            "Transcribed" => KnowledgeStatus.Transcribed,
            _ => null,
        };

        Style normal = (Style)Application.Current.Resources["DouKBTabButtonStyle"];
        Style selected = (Style)Application.Current.Resources["DouKBTabButtonSelectedStyle"];
        foreach (Button tab in new[] { TabAll, TabSummarized, TabTranscribed })
        {
            tab.Style = ReferenceEquals(tab, clicked) ? selected : normal;
        }

        ApplyFilter();
    }

    private void TagButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked || clicked.Content is not string label)
        {
            return;
        }

        // 标签文案形如 “话题（3）”，括号内是计数，需要剥掉。
        string tag = StripCount(label);

        _tag = string.Equals(_tag, tag, StringComparison.Ordinal) ? null : tag;
        RefreshTagStyles();
        ApplyFilter();
    }

    private void RefreshTagStyles()
    {
        Style normal = (Style)Application.Current.Resources["DouKBTagButtonStyle"];
        Style selected = (Style)Application.Current.Resources["DouKBTagButtonSelectedStyle"];

        if (TagList.ItemsPanelRoot is null)
        {
            return;
        }

        foreach (Button button in TagList.ItemsPanelRoot.Children.OfType<Button>())
        {
            if (button.Content is string label)
            {
                string tag = StripCount(label);
                button.Style = string.Equals(_tag, tag, StringComparison.Ordinal) ? selected : normal;
            }
        }
    }

    private static string StripCount(string label)
    {
        int paren = label.IndexOf('（');
        return paren > 0 ? label[..paren] : label;
    }

    private void ItemList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LibraryRow row || string.IsNullOrEmpty(row.TranscriptPath))
        {
            return;
        }

        NavigationService.Current.Navigate(AppPage.Reading, row.TranscriptPath);
    }

    private static LibraryRow ToRow(KnowledgeItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Excerpt = BuildExcerpt(item),
        DateLabel = FormatDate(item.Timestamp),
        StatusLabel = item.HasArticle ? "已总结" : "待总结",
        DurationLabel = item.DurationLabel,
        TranscriptPath = item.TranscriptPath,
        ArticleId = item.ArticleId ?? "",
        HasArticle = item.HasArticle,
        HasVideo = item.HasVideo,
    };

    /// <summary>摘要优先用文章的一句话摘要，否则取转写稿开头。</summary>
    private static string BuildExcerpt(KnowledgeItem item)
    {
        string text = item.Summary;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = KnowledgeIndex.ReadTranscript(item);
        }

        text = WhitespaceRegex().Replace(text, " ").Trim();
        if (text.Length == 0)
        {
            return "尚无摘要";
        }

        return text.Length <= ExcerptLength ? text : text[..ExcerptLength].TrimEnd() + "…";
    }

    private static string FormatDate(DateTime time)
    {
        DateTime today = DateTime.Today;
        if (time.Date == today)
        {
            return $"今天 {time:HH:mm}";
        }

        if (time.Date == today.AddDays(-1))
        {
            return $"昨天 {time:HH:mm}";
        }

        return time.Year == today.Year
            ? $"{time.Month} 月 {time.Day} 日"
            : $"{time.Year} 年 {time.Month} 月 {time.Day} 日";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
