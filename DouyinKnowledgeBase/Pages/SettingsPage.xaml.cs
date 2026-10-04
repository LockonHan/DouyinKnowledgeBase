using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DouyinKnowledgeBase.Services;

namespace DouyinKnowledgeBase.Pages;

/// <summary>
/// 大模型接入点配置页。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly SettingsService _settingsService = new();
    private readonly LlmClient _llmClient = new();

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LlmSettings settings = await _settingsService.LoadAsync();
        BaseUrlBox.Text = settings.BaseUrl;
        ApiKeyBox.Password = settings.ApiKey;
        ModelBox.Text = settings.Model;
        TemperatureSlider.Value = settings.Temperature;
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

    private void TemperatureSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        TemperatureText.Text = Math.Round(TemperatureSlider.Value, 1).ToString("0.0");
    }

    private LlmSettings ReadSettings()
    {
        return new LlmSettings
        {
            BaseUrl = BaseUrlBox.Text.Trim(),
            ApiKey = ApiKeyBox.Password.Trim(),
            Model = ModelBox.Text.Trim(),
            Temperature = Math.Round(TemperatureSlider.Value, 1),
        };
    }

    private bool TryValidate(LlmSettings settings, out string message)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            message = "请填写接入点地址";
            return false;
        }

        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out Uri? _))
        {
            message = "接入点地址格式不正确，应以 http(s):// 开头";
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            message = "请填写模型名称";
            return false;
        }

        message = "";
        return true;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        LlmSettings settings = ReadSettings();
        if (!TryValidate(settings, out string error))
        {
            ShowStatus(error, InfoBarSeverity.Warning);
            return;
        }

        await _settingsService.SaveAsync(settings);
        ShowStatus("配置已保存", InfoBarSeverity.Success);
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        LlmSettings settings = ReadSettings();
        if (!TryValidate(settings, out string error))
        {
            ShowStatus(error, InfoBarSeverity.Warning);
            return;
        }

        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ShowStatus("正在测试连接…", InfoBarSeverity.Informational);
        try
        {
            string reply = await _llmClient.TestAsync(settings);
            string preview = reply.Length > 200 ? reply[..200] + "…" : reply;
            ShowStatus($"连接成功，模型回复：{preview}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"连接失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            TestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfo.Severity = severity;
        StatusInfo.Title = message;
        StatusInfo.IsOpen = true;
    }
}