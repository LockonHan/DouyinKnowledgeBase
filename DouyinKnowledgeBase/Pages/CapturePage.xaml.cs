using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using DouyinKnowledgeBase.Services;

namespace DouyinKnowledgeBase.Pages;

/// <summary>
/// 快速获取页：粘贴抖音分享文案，下载视频并本地转写。
/// 处理过程以纵向步骤轴呈现（解析 → 下载 → 音频 → 转写 → 文稿）。
/// </summary>
public sealed partial class CapturePage : Page
{
    private readonly PipelineSettingsService _pipelineSettingsService = new();
    private readonly VideoPipelineService _pipelineService = new();
    private readonly KnowledgeIndex _index = new();

    private bool _running;
    private PipelineSettings _settings = new();

    /// <summary>步骤在轴上的顺序，用于把阶段映射为“已完成/进行中/待处理”。</summary>
    private static readonly string[] StageOrder = ["parse", "download", "audio", "transcribe", "done"];

    public CapturePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (RecentPanel.Visibility == Visibility.Visible)
        {
            return;
        }

        try
        {
            _settings = await _pipelineSettingsService.LoadAsync();
        }
        catch (Exception)
        {
            // 配置读取失败时保留默认值，管线启动时会给出明确报错。
        }

        await LoadRecentAsync();
    }

    // ==================== 最近完成 ====================

    private async Task LoadRecentAsync()
    {
        try
        {
            IReadOnlyList<KnowledgeItem> items = await _index.LoadAsync(_settings);
            List<RecentRow> rows = items.Take(5).Select(item => new RecentRow
            {
                Title = item.Title,
                Sub = item.HasArticle ? "已转写并生成文章" : "已转写，待生成文章",
                When = FormatWhen(item.Timestamp),
                TranscriptPath = item.TranscriptPath,
            }).ToList();

            RecentList.ItemsSource = rows;
            RecentPanel.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
            RecentPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatWhen(DateTime time)
    {
        DateTime today = DateTime.Today;
        return time.Date == today
            ? $"今天 {time:HH:mm}"
            : time.Date == today.AddDays(-1)
                ? $"昨天 {time:HH:mm}"
                : time.Year == today.Year
                    ? $"{time.Month} 月 {time.Day} 日"
                    : $"{time.Year} 年 {time.Month} 月 {time.Day} 日";
    }

    private void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentRow row && !string.IsNullOrEmpty(row.TranscriptPath))
        {
            NavigationService.Current.Navigate(AppPage.Reading, row.TranscriptPath);
        }
    }

    // ==================== 输入与启动 ====================

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        ShareBox.Text = "";
    }

    private void CookieButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationService.Current.Navigate(AppPage.Cookie);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        string share = ShareBox.Text?.Trim() ?? "";
        if (share.Length == 0)
        {
            ShareBox.Focus(FocusState.Programmatic);
            return;
        }

        PipelineSettings settings = await _pipelineSettingsService.LoadAsync();

        _running = true;
        StartButton.IsEnabled = false;
        ClearButton.IsEnabled = false;
        ResetPipeline();
        PipePanel.Visibility = Visibility.Visible;
        SetStep("parse", "now", "正在从分享文案中提取链接…", -1);

        var progress = new Progress<PipelineProgress>(OnProgress);

        try
        {
            VideoPipelineResult result = await _pipelineService.ProcessAsync(share, settings, progress, CancellationToken.None);

            PipeTitle.Text = string.IsNullOrWhiteSpace(result.Title) ? "" : result.Title;
            SetStep("done", "done", "转写稿已存入本地", 100);
            PipelineBus.Finish(success: true, result.Title);
            await Task.Delay(600);

            // 完成后直接进入阅读视图生成文章。
            NavigationService.Current.Navigate(AppPage.Reading, result.TranscriptPath);
        }
        catch (Exception ex)
        {
            SetStep(CurrentStage, "error", ex.Message, -1);
            ErrorText.Text = "失败：" + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            PipelineBus.Finish(success: false, PipeTitle.Text);
        }
        finally
        {
            _running = false;
            StartButton.IsEnabled = true;
            ClearButton.IsEnabled = true;
            _ = LoadRecentAsync();
        }
    }

    // ==================== 步骤轴状态机 ====================

    /// <summary>当前进行到的阶段（用于失败时定位错误步骤）。</summary>
    private string CurrentStage { get; set; } = "parse";

    private void ResetPipeline()
    {
        PipeTitle.Text = "";
        ErrorText.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
        CurrentStage = "parse";

        foreach (string stage in StageOrder)
        {
            SetDot(stage, pending: true);
            SetName(stage, pending: true);
            SetDetail(stage, "");
        }

        DownloadStats.Text = "";
        DownloadStats.Visibility = Visibility.Collapsed;
        BarDownload.Visibility = Visibility.Collapsed;
        BarTranscribe.Visibility = Visibility.Collapsed;
        BarDownload.IsIndeterminate = false;
        BarTranscribe.IsIndeterminate = false;
    }

    private void OnProgress(PipelineProgress progress)
    {
        PipelineBus.Publish(progress);

        switch (progress.Stage)
        {
            case "parse":
                CurrentStage = "parse";
                if (progress.Percent >= 100)
                {
                    MarkDone("parse", string.IsNullOrWhiteSpace(progress.Message) ? "链接已识别" : progress.Message);
                }
                else
                {
                    SetStep("parse", "now", progress.Message, -1);
                }
                break;

            case "download":
                CurrentStage = "download";
                MarkDone("parse", "链接已识别");
                if (progress.Message.Contains("MB/s", StringComparison.OrdinalIgnoreCase)
                    || progress.Message.Contains("KB/s", StringComparison.OrdinalIgnoreCase)
                    || progress.Message.Contains("剩余", StringComparison.Ordinal))
                {
                    DownloadStats.Text = progress.Message;
                    DownloadStats.Visibility = Visibility.Visible;
                }
                if (progress.Percent >= 100)
                {
                    MarkDone("download", progress.Message);
                }
                else
                {
                    SetStep("download", "now", progress.Message, progress.Percent);
                }
                break;

            case "audio":
                CurrentStage = "audio";
                MarkDone("download", "已存入本地");
                if (progress.Percent >= 100)
                {
                    MarkDone("audio", progress.Message);
                }
                else
                {
                    SetStep("audio", "now", progress.Message, -1);
                }
                break;

            case "transcribe":
                CurrentStage = "transcribe";
                MarkDone("audio", "16 kHz 单声道 WAV");
                if (progress.Percent >= 100)
                {
                    MarkDone("transcribe", progress.Message);
                }
                else
                {
                    SetStep("transcribe", "now", progress.Message, progress.Percent);
                }
                break;

            case "done":
                CurrentStage = "done";
                MarkDone("transcribe", "转写完成");
                SetStep("done", "now", "正在生成文稿…", -1);
                break;

            case "error":
                SetStep(CurrentStage, "error", progress.Message, -1);
                ErrorText.Text = "失败：" + progress.Message;
                ErrorText.Visibility = Visibility.Visible;
                break;
        }
    }

    /// <summary>把某个步骤标记为完成，并补一条说明。</summary>
    private void MarkDone(string stage, string? detail)
    {
        SetDot(stage, pending: false);
        SetName(stage, pending: false, now: false);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            SetDetail(stage, detail);
        }
    }

    /// <summary>把某个步骤置为“进行中”，并更新说明与进度条。</summary>
    private void SetStep(string stage, string state, string? detail, double percent)
    {
        bool now = state == "now";
        bool error = state == "error";
        SetDot(stage, pending: false, now, error);
        SetName(stage, pending: false, now, error);

        if (!string.IsNullOrWhiteSpace(detail))
        {
            SetDetail(stage, detail, now || error);
        }

        ProgressBar? bar = stage switch
        {
            "download" => BarDownload,
            "transcribe" => BarTranscribe,
            _ => null,
        };

        if (bar is not null)
        {
            if (percent >= 0)
            {
                bar.Visibility = Visibility.Visible;
                bar.IsIndeterminate = false;
                bar.Value = Math.Clamp(percent, 0, 100);
            }
            else if (now)
            {
                bar.Visibility = Visibility.Visible;
                bar.IsIndeterminate = true;
            }
        }
    }

    private void SetDot(string stage, bool pending, bool now = false, bool error = false)
    {
        Ellipse? dot = stage switch
        {
            "parse" => DotParse,
            "download" => DotDownload,
            "audio" => DotAudio,
            "transcribe" => DotTranscribe,
            "done" => DotDone,
            _ => null,
        };

        if (dot is null)
        {
            return;
        }

        if (pending)
        {
            dot.Fill = (Brush)Application.Current.Resources["DouKBPaperBrush"];
            dot.Stroke = (Brush)Application.Current.Resources["DouKBInk3Brush"];
        }
        else if (error)
        {
            dot.Fill = (Brush)Application.Current.Resources["DouKBInkBrush"];
            dot.Stroke = (Brush)Application.Current.Resources["DouKBInkBrush"];
        }
        else if (now)
        {
            dot.Fill = (Brush)Application.Current.Resources["DouKBLiveBrush"];
            dot.Stroke = (Brush)Application.Current.Resources["DouKBLiveBrush"];
        }
        else
        {
            dot.Fill = (Brush)Application.Current.Resources["DouKBInkBrush"];
            dot.Stroke = (Brush)Application.Current.Resources["DouKBInkBrush"];
        }
    }

    private void SetName(string stage, bool pending, bool now = false, bool error = false)
    {
        TextBlock? name = stage switch
        {
            "parse" => NameParse,
            "download" => NameDownload,
            "audio" => NameAudio,
            "transcribe" => NameTranscribe,
            "done" => NameDone,
            _ => null,
        };

        if (name is null)
        {
            return;
        }

        name.Foreground = pending
            ? (Brush)Application.Current.Resources["DouKBInk3Brush"]
            : (Brush)Application.Current.Resources["DouKBInkBrush"];
        name.FontWeight = now ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void SetDetail(string stage, string text, bool live = false)
    {
        TextBlock? detail = stage switch
        {
            "parse" => DetailParse,
            "download" => DetailDownload,
            "audio" => DetailAudio,
            "transcribe" => DetailTranscribe,
            "done" => DetailDone,
            _ => null,
        };

        if (detail is null)
        {
            return;
        }

        detail.Text = text;
        detail.Foreground = live
            ? (Brush)Application.Current.Resources["DouKBLiveBrush"]
            : (Brush)Application.Current.Resources["DouKBInk3Brush"];
    }
}
