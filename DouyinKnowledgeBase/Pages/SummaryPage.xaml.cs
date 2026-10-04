using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DouyinKnowledgeBase.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DouyinKnowledgeBase.Pages;

/// <summary>
/// 文稿总结页：选择本地转写稿，调用大模型生成结构化知识文章，并可保存为 Markdown。
/// </summary>
public sealed partial class SummaryPage : Page
{
    private readonly SettingsService _settingsService = new();
    private readonly SummaryService _summaryService = new(new LlmClient());

    private StorageFile? _sourceFile;
    private string? _sourceText;

    public SummaryPage()
    {
        InitializeComponent();
    }

    private static nint GetWindowHandle()
    {
        return WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
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

    private async void PickFileButton_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetWindowHandle());
        picker.FileTypeFilter.Add(".txt");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        _sourceFile = file;
        _sourceText = await FileIO.ReadTextAsync(file);
        FileNameText.Text = $"{file.Name}（{_sourceText.Length} 字）";
        SaveButton.IsEnabled = false;
        ShowStatus("已载入转写稿，可以开始生成。", InfoBarSeverity.Success);
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceFile is null || string.IsNullOrWhiteSpace(_sourceText))
        {
            ShowStatus("请先选择转写稿文件。", InfoBarSeverity.Warning);
            return;
        }

        LlmSettings settings = await _settingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            ShowStatus("请先在“设置”中填写接入点地址和模型名称。", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        ShowStatus("正在调用大模型生成文章，请稍候……", InfoBarSeverity.Informational);
        try
        {
            string article = await _summaryService.SummarizeAsync(settings, _sourceText);
            ResultBox.Text = article;
            SaveButton.IsEnabled = true;
            ShowStatus("文章生成完成，可编辑后保存。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"生成失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultBox.Text))
        {
            return;
        }

        FileSavePicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetWindowHandle());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.DefaultFileExtension = ".md";
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        string baseName = _sourceFile is not null ? _sourceFile.DisplayName : "知识文章";
        picker.SuggestedFileName = baseName + "-知识文章";

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await FileIO.WriteTextAsync(file, ResultBox.Text);
        ShowStatus($"已保存：{file.Path}", InfoBarSeverity.Success);
    }

    private void SetBusy(bool busy)
    {
        GenerateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PickFileButton.IsEnabled = !busy;
        GenerateButton.IsEnabled = !busy;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfo.Severity = severity;
        StatusInfo.Title = message;
        StatusInfo.IsOpen = true;
    }
}