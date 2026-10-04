using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DouyinKnowledgeBase.Services;

namespace DouyinKnowledgeBase.Pages;

/// <summary>
/// 下载并转写页：粘贴抖音分享链接，自动下载视频、提取音频并本地转写。
/// </summary>
public sealed partial class DownloadPage : Page
{
    private readonly PipelineSettingsService _pipelineSettingsService = new();
    private readonly VideoPipelineService _pipelineService = new();

    private string? _transcriptPath;
    private bool _running;

    public DownloadPage()
    {
        InitializeComponent();
        Progress.Minimum = 0;
        Progress.Maximum = 100;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(MainPage));
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        ShareBox.Text = "";
        ResetResult();
        StatusText.Text = "";
    }

    private void ResetResult()
    {
        _transcriptPath = null;
        ResultTitleText.Visibility = Visibility.Collapsed;
        TranscriptBox.Visibility = Visibility.Collapsed;
        ResultActions.Visibility = Visibility.Collapsed;
        TranscriptBox.Text = "";
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
            StatusText.Text = "请先粘贴抖音分享文案或链接。";
            return;
        }

        PipelineSettings settings = await _pipelineSettingsService.LoadAsync();

        ResetResult();
        _running = true;
        StartButton.IsEnabled = false;
        ClearButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        Progress.Value = 0;
        StatusText.Text = "准备中…";

        var progress = new Progress<PipelineProgress>(OnProgress);

        try
        {
            VideoPipelineResult result = await _pipelineService.ProcessAsync(share, settings, progress, CancellationToken.None);
            _transcriptPath = result.TranscriptPath;

            ResultTitleText.Text = string.IsNullOrWhiteSpace(result.Title)
                ? "转写完成"
                : $"《{result.Title}》";
            ResultTitleText.Visibility = Visibility.Visible;

            TranscriptBox.Text = await File.ReadAllTextAsync(result.TranscriptPath);
            TranscriptBox.Visibility = Visibility.Visible;
            ResultActions.Visibility = Visibility.Visible;

            StatusText.Text = $"已完成。转写稿：{result.TranscriptPath}";
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"失败：{ex.Message}";
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
        }
        finally
        {
            _running = false;
            StartButton.IsEnabled = true;
            ClearButton.IsEnabled = true;
        }
    }

    private void OnProgress(PipelineProgress progress)
    {
        string stageName = progress.Stage switch
        {
            "parse" => "解析链接",
            "download" => "下载视频",
            "audio" => "提取音频",
            "transcribe" => "语音转写",
            "done" => "完成",
            "error" => "出错",
            _ => "处理中",
        };

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            StatusText.Text = $"[{stageName}] {progress.Message}";
        }

        if (progress.Stage == "download" && progress.Percent >= 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(progress.Percent, 0, 100);
        }
        else if (progress.Stage == "download" || progress.Stage == "transcribe")
        {
            Progress.IsIndeterminate = true;
        }
        else if (progress.Percent >= 0 && progress.Stage != "done")
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(progress.Percent, 0, 100);
        }
    }

    private void CookieButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(CookiePage));
    }

    private void SummarizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_transcriptPath) && File.Exists(_transcriptPath))
        {
            Frame.Navigate(typeof(SummaryPage), _transcriptPath);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_transcriptPath) || !File.Exists(_transcriptPath))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_transcriptPath}\"");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"无法打开文件夹：{ex.Message}";
        }
    }
}