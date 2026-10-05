using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DouyinKnowledgeBase.Services;

namespace DouyinKnowledgeBase.Pages;

/// <summary>
/// 大模型接入点与视频管线配置页。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly SettingsService _settingsService = new();
    private readonly PipelineSettingsService _pipelineSettingsService = new();
    private readonly ObsidianSettingsService _obsidianSettingsService = new();
    private readonly LlmClient _llmClient = new();
    private readonly AsrServerManager _asrServer = App.AsrServer;

    /// <summary>加载配置回填控件时抑制即时保存，避免把默认值反写回配置。</summary>
    private bool _suppressObsidianSave;

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
        TimeoutBox.Text = settings.TimeoutSeconds.ToString("0");
        TestButton.IsEnabled = true;

        PipelineSettings pipeline = await _pipelineSettingsService.LoadAsync();
        PythonPathBox.Text = pipeline.PythonPath;
        DataDirBox.Text = pipeline.DataDir;
        ModelsDirBox.Text = pipeline.ModelsDir;
        ResidentSwitch.IsOn = pipeline.AsrResident;
        SelectDevice(pipeline.Device);

        ObsidianSettings obsidian = await _obsidianSettingsService.LoadAsync();
        _suppressObsidianSave = true;
        VaultNameBox.Text = obsidian.VaultName;
        VaultPathBox.Text = obsidian.VaultPath;
        SubfolderBox.Text = obsidian.Subfolder;
        FrontmatterSwitch.IsOn = obsidian.AddFrontmatter;
        OpenAfterExportSwitch.IsOn = obsidian.OpenAfterExport;
        _suppressObsidianSave = false;
    }

    private async void DetectVaultButton_Click(object sender, RoutedEventArgs e)
    {
        DetectVaultButton.IsEnabled = false;
        DetectStatusText.Text = "正在检测……";
        try
        {
            if (!await ObsidianCli.IsAvailableAsync())
            {
                DetectStatusText.Text = "未检测到 Obsidian CLI：请确保 Obsidian 正在运行，并在「设置 → 关于 → 高级」开启「命令行界面」。";
                return;
            }

            IReadOnlyList<ObsidianCli.ObsidianVaultInfo> vaults = await ObsidianCli.ListVaultsAsync();
            if (vaults.Count == 0)
            {
                DetectStatusText.Text = "未发现 vault（Obsidian 中可能还没打开过任何 vault）。";
                return;
            }

            ObsidianCli.ObsidianVaultInfo first = vaults[0];
            VaultNameBox.Text = first.Name;
            VaultPathBox.Text = first.Path;
            DetectStatusText.Text = vaults.Count == 1
                ? $"已自动填入：{first.Name}"
                : $"已自动填入第一个 vault（共 {vaults.Count} 个，可手动修改）：{first.Name}";
        }
        catch (Exception ex)
        {
            DetectStatusText.Text = "检测失败：" + ex.Message;
        }
        finally
        {
            DetectVaultButton.IsEnabled = true;
        }
    }

    /// <summary>Obsidian 区块改动即时保存：开关切换、文本框失焦均触发。</summary>
    private async void ObsidianSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressObsidianSave)
        {
            return;
        }

        try
        {
            await _obsidianSettingsService.SaveAsync(ReadObsidianSettings());
        }
        catch (Exception)
        {
            // 即时保存失败不打断用户操作，值仍可后续通过主保存重试。
        }
    }

    private void SelectDevice(string device)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            DeviceBox.SelectedIndex = 0;
            return;
        }

        foreach (object item in DeviceBox.Items)
        {
            if (item is ComboBoxItem combo && string.Equals(combo.Content?.ToString(), device, StringComparison.OrdinalIgnoreCase))
            {
                DeviceBox.SelectedItem = combo;
                return;
            }
        }

        DeviceBox.SelectedIndex = 0;
    }

    private void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationService.Current.Navigate(AppPage.Setup);
    }

    private void TemperatureSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        TemperatureText.Text = Math.Round(TemperatureSlider.Value, 1).ToString("0.0");
    }

    private LlmSettings ReadSettings()
    {
        double timeout = double.TryParse(TimeoutBox.Text.Trim(), out double parsed) && parsed > 0
            ? parsed
            : 300;

        return new LlmSettings
        {
            BaseUrl = BaseUrlBox.Text.Trim(),
            ApiKey = ApiKeyBox.Password.Trim(),
            Model = ModelBox.Text.Trim(),
            Temperature = Math.Round(TemperatureSlider.Value, 1),
            TimeoutSeconds = timeout,
        };
    }

    private ObsidianSettings ReadObsidianSettings()
    {
        return new ObsidianSettings
        {
            VaultName = VaultNameBox.Text.Trim(),
            VaultPath = VaultPathBox.Text.Trim(),
            Subfolder = SubfolderBox.Text.Trim(),
            AddFrontmatter = FrontmatterSwitch.IsOn,
            OpenAfterExport = OpenAfterExportSwitch.IsOn,
        };
    }

    private PipelineSettings ReadPipelineSettings()
    {
        string device = (DeviceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "auto";
        return new PipelineSettings
        {
            PythonPath = PythonPathBox.Text.Trim(),
            DataDir = DataDirBox.Text.Trim(),
            Device = device,
            ModelsDir = ModelsDirBox.Text.Trim(),
            AsrResident = ResidentSwitch.IsOn,
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

        if (settings.TimeoutSeconds < 30 || settings.TimeoutSeconds > 1800)
        {
            message = "请求超时应在 30~1800 秒之间（长视频文稿建议 300 秒以上）";
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

        PipelineSettings pipeline = ReadPipelineSettings();
        if (string.IsNullOrWhiteSpace(pipeline.PythonPath))
        {
            ShowStatus("请填写 Python 解释器路径（用于本地转写）。", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(pipeline.DataDir))
        {
            ShowStatus("请填写数据目录。", InfoBarSeverity.Warning);
            return;
        }

        ObsidianSettings obsidian = ReadObsidianSettings();

        await _settingsService.SaveAsync(settings);
        await _pipelineSettingsService.SaveAsync(pipeline);
        await _obsidianSettingsService.SaveAsync(obsidian);

        // 按常驻开关启停转写服务。
        if (pipeline.AsrResident)
        {
            _asrServer.Start(pipeline);
            ShowStatus("配置已保存；常驻转写服务正在后台加载模型。", InfoBarSeverity.Success);
            return;
        }

        _asrServer.Stop();
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
        ShowStatus("正在测试连接……", InfoBarSeverity.Informational);
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