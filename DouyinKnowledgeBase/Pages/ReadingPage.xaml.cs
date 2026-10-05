using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using DouyinKnowledgeBase.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DouyinKnowledgeBase.Pages;

/// <summary>
/// 阅读视图：展示一条知识条目的 AI 文章（Markdown 渲染），
/// 左侧为视频片格与回看入口，右侧为文章结构目录。
/// 支持从转写稿路径或文章标识进入；尚未生成文章时可在此一键生成。
/// </summary>
public sealed partial class ReadingPage : Page
{
    private readonly SettingsService _settingsService = new();
    private readonly SummaryService _summaryService = new(new LlmClient());
    private readonly PipelineSettingsService _pipelineSettingsService = new();

    private KnowledgeItem? _item;
    private ArticleStore? _store;
    private string _markdown = "";
    private bool _generating;

    /// <summary>用户是否停留在正文底部附近；用于流式生成时的智能滚动跟随。</summary>
    private bool _autoScroll = true;

    public ReadingPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        PipelineSettings settings = await _pipelineSettingsService.LoadAsync();
        _store = new ArticleStore(settings);

        // 参数既可以是转写稿路径（来自快速获取/首页），也可以是文章标识（来自知识库）。
        string? transcriptPath = null;
        string? articleId = null;
        if (e.Parameter is string param && param.Length > 0)
        {
            if (File.Exists(param))
            {
                transcriptPath = param;
            }
            else
            {
                articleId = param;
            }
        }

        await LoadAsync(settings, transcriptPath, articleId);
    }

    private async Task LoadAsync(PipelineSettings settings, string? transcriptPath, string? articleId)
    {
        try
        {
            IReadOnlyList<KnowledgeItem> items = await new KnowledgeIndex().LoadAsync(settings);

            _item = MatchItem(items, transcriptPath, articleId);
            if (_item is null)
            {
                ShowStatus("没有找到对应的知识条目，它可能已被移动或删除。", InfoBarSeverity.Warning);
                return;
            }

            ApplyItem(_item);
        }
        catch (Exception ex)
        {
            ShowStatus("读取知识条目失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private static KnowledgeItem? MatchItem(
        IReadOnlyList<KnowledgeItem> items,
        string? transcriptPath,
        string? articleId)
    {
        if (!string.IsNullOrEmpty(transcriptPath))
        {
            KnowledgeItem? byPath = items.FirstOrDefault(item =>
                string.Equals(item.TranscriptPath, transcriptPath, StringComparison.OrdinalIgnoreCase));
            if (byPath is not null)
            {
                return byPath;
            }
        }

        if (!string.IsNullOrEmpty(articleId))
        {
            KnowledgeItem? byId = items.FirstOrDefault(item => item.ArticleId == articleId);
            if (byId is not null)
            {
                return byId;
            }
        }

        // 兜底：按文件名（不含扩展名）匹配，覆盖路径大小写或目录移动的情况。
        string? stem = transcriptPath is null
            ? null
            : Path.GetFileNameWithoutExtension(transcriptPath);
        return items.FirstOrDefault(item =>
            !string.IsNullOrEmpty(item.TranscriptPath)
            && Path.GetFileNameWithoutExtension(item.TranscriptPath) == stem);
    }

    private async void ApplyItem(KnowledgeItem item)
    {
        TitleText.Text = item.Title;
        CrumbText.Text = item.Tags.Length > 0
            ? $"知识库　/　{string.Join("　·　", item.Tags)}"
            : "知识库";
        MetaSource.Text = "来源：抖音视频";
        MetaDuration.Text = item.DurationSec > 0 ? $"时长 {item.DurationLabel}" : "";
        MetaDate.Text = $"转写于 {item.Timestamp.Month} 月 {item.Timestamp.Day} 日";
        FrameDur.Text = item.DurationLabel;
        SourceTitle.Text = item.Title;
        WatchButton.IsEnabled = item.HasVideo;

        string markdown = "";
        if (_store is not null && !string.IsNullOrEmpty(item.ArticleId))
        {
            markdown = await _store.ReadMarkdownAsync(item.ArticleId) ?? "";
        }

        _markdown = markdown;
        Article.Text = markdown;
        TocList.ItemsSource = ExtractToc(markdown);

        bool hasArticle = markdown.Length > 0;
        EmptyPanel.Visibility = hasArticle ? Visibility.Collapsed : Visibility.Visible;
        Article.Visibility = hasArticle ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = hasArticle;
        CopyButton.IsEnabled = hasArticle;
        GenerateButton.IsEnabled = !hasArticle && !string.IsNullOrEmpty(item.TranscriptPath);
    }

    /// <summary>从 Markdown 提取二级标题作为目录。</summary>
    private static IReadOnlyList<string> ExtractToc(string markdown)
    {
        List<string> entries = new();
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                entries.Add(line[3..].Trim());
            }
        }

        return entries;
    }

    // ==================== 操作 ====================

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationService.Current.GoBack();
    }

    private void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_item?.VideoPath is not null && File.Exists(_item.VideoPath))
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{_item.VideoPath}\"");
            }
            catch (Exception ex)
            {
                ShowStatus("无法打开视频：" + ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private void TranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_item is null || string.IsNullOrEmpty(_item.TranscriptPath))
        {
            return;
        }

        try
        {
            if (File.Exists(_item.TranscriptPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_item.TranscriptPath}\"");
            }
        }
        catch (Exception ex)
        {
            ShowStatus("无法打开转写稿：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_generating || _item is null || _store is null)
        {
            return;
        }

        string transcript = KnowledgeIndex.ReadTranscript(_item);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            ShowStatus("转写稿为空或不可读，无法生成文章。", InfoBarSeverity.Warning);
            return;
        }

        LlmSettings settings = await _settingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            ShowStatus("请先在「设置」中填写接入点地址和模型名称。", InfoBarSeverity.Warning);
            return;
        }

        _generating = true;
        GenerateButton.IsEnabled = false;
        BusyRing.Visibility = Visibility.Visible;
        GenerateStateText.Visibility = Visibility.Visible;
        GenerateStateText.Text = "正在生成文章…";
        ShowStatus("正在调用大模型生成文章，请稍候……", InfoBarSeverity.Informational);

        // 流式打字机：正文区实时增长（时间节流，每 150ms 刷新一次快照）。
        string previousMarkdown = _markdown;
        StringBuilder streamedArticle = new();
        long lastFlushTicks = 0;
        bool articleAreaShown = false;
        try
        {
            string article = await _summaryService.SummarizeStreamAsync(settings, transcript, delta =>
            {
                streamedArticle.Append(delta);

                // 首块内容到达即切到正文显示，让用户立刻看到文字增长。
                long now = Environment.TickCount64;
                if (now - lastFlushTicks >= 150)
                {
                    lastFlushTicks = now;
                    string snapshot = streamedArticle.ToString();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!articleAreaShown)
                        {
                            articleAreaShown = true;
                            EmptyPanel.Visibility = Visibility.Collapsed;
                            Article.Visibility = Visibility.Visible;
                        }

                        Article.Text = snapshot;
                        if (_autoScroll)
                        {
                            ArticleScroll.ChangeView(null, ArticleScroll.ScrollableHeight, null);
                        }
                    });
                }
            });

            ArticleMeta meta = await _store.SaveAsync(
                _item.SourceTitle, _item.TranscriptPath, article, settings.Model);

            _markdown = article;
            Article.Text = article;
            TocList.ItemsSource = ExtractToc(article);
            EmptyPanel.Visibility = Visibility.Collapsed;
            Article.Visibility = Visibility.Visible;
            ExportButton.IsEnabled = true;
            CopyButton.IsEnabled = true;
            ShowStatus("文章已生成并保存到本地知识库。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus("生成失败：" + ex.Message, InfoBarSeverity.Error);
            GenerateButton.IsEnabled = true;

            // 失败恢复：有旧文章则还原，无则回到空态引导。
            if (!string.IsNullOrEmpty(previousMarkdown))
            {
                Article.Text = previousMarkdown;
            }
            else
            {
                Article.Text = "";
                Article.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _generating = false;
            BusyRing.Visibility = Visibility.Collapsed;
            GenerateStateText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>滚动跟随：用户在底部附近时自动跟随；主动上翻则停止跟随，回到底部后恢复。</summary>
    private void ArticleScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ArticleScroll.ScrollableHeight <= 0)
        {
            return;
        }

        double distanceToBottom = ArticleScroll.ScrollableHeight - ArticleScroll.VerticalOffset;
        _autoScroll = distanceToBottom <= 120;
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_markdown))
        {
            return;
        }

        FileSavePicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetWindowHandle());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.DefaultFileExtension = ".md";
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        picker.SuggestedFileName = MakeFileName(_item?.Title ?? "知识文章");

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await FileIO.WriteTextAsync(file, _markdown);
        ShowStatus($"已导出：{file.Path}", InfoBarSeverity.Success);
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_markdown))
        {
            return;
        }

        DataPackage package = new();
        package.SetText(_markdown);
        Clipboard.SetContent(package);
        ShowStatus("已复制全文到剪贴板。", InfoBarSeverity.Success);
        await Task.CompletedTask;
    }

    private async void ObsidianExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || _item is null)
        {
            ShowStatus("当前没有可导出的知识条目。", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_markdown))
        {
            ShowStatus("当前文章为空，请先点击「生成知识文章」。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            ArticleMeta? meta = await _store.ReadMetaAsync(_item.ArticleId ?? _item.Id);
            if (meta is null)
            {
                ShowStatus("未找到文章元数据，无法导出到 Obsidian。", InfoBarSeverity.Warning);
                return;
            }

            ObsidianExporter exporter = new();
            ObsidianExportResult result = await exporter.ExportAsync(meta, _markdown);
            if (result.Success)
            {
                string suffix = result.Opened ? "（已在 Obsidian 中打开）" : "";
                ShowStatus($"已导出到 Obsidian：{result.FilePath}{suffix}", InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus($"导出失败：{result.Error}", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("导出失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private static nint GetWindowHandle()
    {
        return WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
    }

    private static string MakeFileName(string title)
    {
        StringBuilder builder = new();
        foreach (char ch in title)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= 40)
            {
                break;
            }
        }

        return builder.ToString().Trim('-');
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfo.Severity = severity;
        StatusInfo.Title = message;
        StatusInfo.IsOpen = true;
    }
}
