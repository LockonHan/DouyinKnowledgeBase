using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DouyinKnowledgeBase.Services;

namespace DouyinKnowledgeBase.Pages;

/// <summary>自检结果条目。</summary>
public sealed record SetupCheckItem(string Icon, string Title, string Detail);

/// <summary>
/// 环境准备向导：检测设备 → 让用户选择 GPU / CPU → 按需下载运行时与模型 → 自检。
/// </summary>
public sealed partial class SetupPage : Page
{
    private readonly PipelineSettingsService _pipelineSettingsService = new();
    private readonly EnvironmentService _environmentService = new();
    private readonly StringBuilder _log = new();

    private PipelineSettings _pipeline = new();
    private NvidiaInfo _nvidia = new();
    private string _python = "";
    private string _browsersDir = "";
    private string _ffmpeg = "";

    private bool _initialized;
    private bool _running;
    private CancellationTokenSource? _cts;
    private bool _suppressDeviceChanged;
    private bool _deviceUiReady;

    public SetupPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        _pipeline = await _pipelineSettingsService.LoadAsync();
        _nvidia = await Task.Run(EnvironmentService.DetectNvidia);

        _python = RepoLocator.SetupTargetPython(_pipeline.PythonPath);
        _browsersDir = RepoLocator.SetupBrowsersDir();
        _ffmpeg = RepoLocator.BundledFfmpeg() ?? "";

        GpuText.Text = _nvidia.Present
            ? $"检测到 NVIDIA 显卡：{_nvidia.Summary}"
            : "未检测到 NVIDIA 显卡，将使用 CPU 转写（速度稍慢，但兼容性最好）。";

        // 首次运行是否需要弹窗询问：以加载时的状态为准（SelectionChanged 可能异步触发）。
        bool deviceAlreadyChosen = _pipeline.DeviceChosen;

        _suppressDeviceChanged = true;
        bool wantGpu = _pipeline.Device.StartsWith("cuda", StringComparison.OrdinalIgnoreCase);
        DeviceChoice.SelectedIndex = _nvidia.Present && wantGpu ? 0 : 1;
        _suppressDeviceChanged = false;

        ModelsDirBox.Text = ResolveModelsDir(_pipeline.ModelsDir);
        PythonText.Text = "Python：" + (string.IsNullOrWhiteSpace(_python) ? "未找到可用解释器" : _python);
        BrowsersText.Text = "浏览器目录：" + (string.IsNullOrWhiteSpace(_browsersDir)
            ? "使用 Playwright 默认位置（%LOCALAPPDATA%\\ms-playwright）"
            : _browsersDir);
        FfmpegText.Text = "ffmpeg：" + (string.IsNullOrWhiteSpace(_ffmpeg)
            ? "未内置，将使用系统 PATH 中的 ffmpeg"
            : _ffmpeg);

        await RefreshCheckAsync();

        if (_nvidia.Present && !deviceAlreadyChosen)
        {
            await AskDeviceAsync();
        }

        // 到这里为止的设备变更都视为“初始化”，不触发保存。
        _deviceUiReady = true;

        if (EnvironmentService.IsInstalling)
        {
            // 安装进程挂在被切换走的旧页面上，这里重新暴露“停止”入口。
            StepText.Text = "环境准备正在后台继续进行（切换页面不会中断下载）。";
            StopButton.IsEnabled = true;
        }
    }

    /// <summary>选定模型缓存目录：优先复用已有模型的目录，其次安装目录，最后默认缓存。</summary>
    private static string ResolveModelsDir(string configured)
    {
        string effective = RepoLocator.EffectiveModelsCache(configured);
        if (RepoLocator.HasAllModels(effective))
        {
            return effective;
        }

        // 尚未下载模型：优先放到安装目录（内置），其次用户指定目录，最后默认缓存。
        string? runtime = RepoLocator.RuntimeRoot();
        if (runtime is not null)
        {
            return Path.Combine(runtime, "models");
        }

        return string.IsNullOrWhiteSpace(effective) ? RepoLocator.DefaultModelscopeCache() : effective;
    }

    private async Task AskDeviceAsync()
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "检测到 NVIDIA 显卡",
            PrimaryButtonText = "使用 GPU 加速",
            SecondaryButtonText = "使用 CPU",
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text =
                    $"检测到 {_nvidia.Summary}。\n\n" +
                    "使用 GPU 加速：转写速度更快，需要额外下载约 2.5 GB 的 CUDA 版 PyTorch。\n" +
                    "使用 CPU：仅需约 200 MB，速度稍慢，兼容性最好。\n\n" +
                    "之后可以随时在“设置 → 转写设备”中修改。",
            },
        };

        ContentDialogResult result = await dialog.ShowAsync();
        bool useGpu = result == ContentDialogResult.Primary;

        _suppressDeviceChanged = true;
        DeviceChoice.SelectedIndex = useGpu ? 0 : 1;
        _suppressDeviceChanged = false;

        _pipeline.Device = useGpu ? "cuda:0" : "cpu";
        _pipeline.DeviceChosen = true;
        await _pipelineSettingsService.SaveAsync(_pipeline);
    }

    private async void DeviceChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceChanged || !_deviceUiReady)
        {
            return;
        }

        _pipeline.Device = DeviceChoice.SelectedIndex == 0 ? "cuda:0" : "cpu";
        _pipeline.DeviceChosen = true;
        try
        {
            await _pipelineSettingsService.SaveAsync(_pipeline);
        }
        catch (Exception ex)
        {
            AppendLog("保存设备选择失败：" + ex.Message);
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Windows.Storage.Pickers.FolderPicker picker = new();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            if (App.MainWindow is null)
            {
                throw new InvalidOperationException("主窗口尚未就绪");
            }

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                ModelsDirBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            ShowStatus("无法打开目录选择器：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCheckAsync();
    }

    private async Task RefreshCheckAsync()
    {
        if (string.IsNullOrWhiteSpace(_python) || !File.Exists(_python))
        {
            CheckList.ItemsSource = new[]
            {
                new SetupCheckItem("❌", "Python 解释器", "未找到可用的 Python 运行环境，无法准备环境。"),
            };
            ShowStatus("未找到可用的 Python 解释器，请重新安装应用。", InfoBarSeverity.Error);
            return;
        }

        RefreshButton.IsEnabled = false;
        StepText.Text = "正在自检…";
        try
        {
            EnvironmentReport? report = await _environmentService.CheckAsync(
                BuildRequest(), CancellationToken.None, AppendLog);
            ApplyReport(report);
        }
        catch (Exception ex)
        {
            ShowStatus("自检失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            StepText.Text = "";
        }
    }

    private void ApplyReport(EnvironmentReport? report)
    {
        if (report is null)
        {
            CheckList.ItemsSource = new[]
            {
                new SetupCheckItem("❌", "自检", "未能获取自检结果，请查看安装日志。"),
            };
            return;
        }

        string torchVersion = PackageOf(report, "torch");
        string funasrVersion = PackageOf(report, "funasr");
        string sentencepiece = PackageOf(report, "sentencepiece");
        string playwright = PackageOf(report, "playwright");
        string curlCffi = PackageOf(report, "curl_cffi");
        bool wantsGpu = report.RequestedDevice.StartsWith("cuda", StringComparison.OrdinalIgnoreCase)
                       || (string.IsNullOrWhiteSpace(report.RequestedDevice) && DeviceChoice.SelectedIndex == 0);
        bool torchReady = !string.IsNullOrWhiteSpace(torchVersion) && (!wantsGpu || report.TorchCuda);
        string torchDetail = string.IsNullOrWhiteSpace(torchVersion)
            ? "未安装"
            : wantsGpu && !report.TorchCuda
                ? $"{torchVersion}｜GPU 不可用，当前为 CPU 模式"
                : $"{torchVersion}｜{(report.TorchCuda ? "CUDA 可用" : "CPU 模式")}";

        List<SetupCheckItem> items = new()
        {
            Item(!string.IsNullOrWhiteSpace(report.PythonVersion),
                "Python 运行时",
                $"{report.PythonPath}（{report.PythonVersion}）"),
            Item(torchReady,
                "PyTorch（转写推理）",
                torchDetail),
            Item(!string.IsNullOrWhiteSpace(funasrVersion),
                "FunASR 与依赖",
                $"funasr {funasrVersion}｜playwright {playwright}｜curl_cffi {curlCffi}｜sentencepiece {sentencepiece}"),
            Item(report.BrowserReady,
                "Playwright 浏览器（下载视频）",
                string.IsNullOrWhiteSpace(report.BrowsersDir) ? "使用 Playwright 默认位置" : report.BrowsersDir),
            Item(!string.IsNullOrWhiteSpace(report.Ffmpeg),
                "ffmpeg（提取音频）",
                string.IsNullOrWhiteSpace(report.Ffmpeg) ? "未找到 ffmpeg" : report.Ffmpeg),
            Item(report.Models.Values.All(ok => ok) && report.Models.Count > 0,
                "语音模型（Paraformer-large + VAD + 标点）",
                BuildModelDetail(report)),
        };

        CheckList.ItemsSource = items;

        if (report.Ready)
        {
            ShowStatus($"环境已就绪，转写设备：{report.Device}。", InfoBarSeverity.Success);
        }
        else
        {
            ShowStatus("环境尚未就绪：" + string.Join("；", report.Issues), InfoBarSeverity.Warning);
        }
    }

    private static string BuildModelDetail(EnvironmentReport report)
    {
        string missing = string.Join("、",
            report.Models.Where(kv => !kv.Value).Select(kv => EnvironmentReport.ModelLabel(kv.Key)));
        string location = string.IsNullOrWhiteSpace(report.ModelsDir) ? "默认缓存目录" : report.ModelsDir;
        return string.IsNullOrWhiteSpace(missing)
            ? $"已就绪｜{location}"
            : $"缺少：{missing}｜{location}";
    }

    private static string PackageOf(EnvironmentReport report, string name)
        => report.Packages.TryGetValue(name, out string? version) ? version ?? "" : "";

    private static SetupCheckItem Item(bool ok, string title, string detail)
        => new(ok ? "✅" : "❌", title, detail);

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_python) || !File.Exists(_python))
        {
            ShowStatus("未找到可用的 Python 解释器，无法准备环境。", InfoBarSeverity.Error);
            return;
        }

        bool useGpu = DeviceChoice.SelectedIndex == 0;
        _pipeline.PythonPath = _python;
        _pipeline.Device = useGpu ? "cuda:0" : "cpu";
        _pipeline.DeviceChosen = true;
        _pipeline.ModelsDir = ModelsDirBox.Text?.Trim() ?? "";

        try
        {
            await _pipelineSettingsService.SaveAsync(_pipeline);
        }
        catch (Exception ex)
        {
            ShowStatus("保存配置失败：" + ex.Message, InfoBarSeverity.Warning);
        }

        _running = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        RefreshButton.IsEnabled = false;
        SetupProgressBar.Value = 0;
        TransferText.Text = "";
        TransferText.Visibility = Visibility.Collapsed;
        StepText.Text = "正在准备环境…";

        var progress = new Progress<SetupProgress>(OnSetupProgress);

        try
        {
            EnvironmentReport? report = await _environmentService.InstallAsync(
                BuildRequest(), progress, _cts.Token, AppendLog);
            ApplyReport(report);

            if (report is null)
            {
                StepText.Text = "环境准备未返回结果，请查看安装日志。";
            }
            else if (useGpu && !report.TorchCuda)
            {
                StepText.Text = "环境准备完成，但 CUDA 不可用，已回落为 CPU 转写：" +
                                string.Join("；", report.Issues);
                ShowStatus(StepText.Text, InfoBarSeverity.Warning);
            }
            else if (!report.Ready)
            {
                StepText.Text = "环境准备完成，但仍有未就绪项：" + string.Join("；", report.Issues);
            }
            else
            {
                // 环境就绪后按配置启动常驻转写服务，后续转写无需重复加载模型。
                // 放在后台线程：设备切换时需要先停掉旧服务，等待期间不能阻塞 UI。
                PipelineSettings pipeline = _pipeline;
                _ = Task.Run(() => App.AsrServer.Start(pipeline));
                StepText.Text = $"环境已就绪，无需再次准备。转写设备：{report.Device}。";
            }
        }
        catch (OperationCanceledException)
        {
            StepText.Text = "已停止环境准备。已下载的内容会保留，可稍后再点「开始准备环境」继续。";
            ShowStatus(StepText.Text, InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            StepText.Text = "环境准备失败：" + ex.Message;
            ShowStatus("环境准备失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _running = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            RefreshButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (EnvironmentService.IsInstalling)
        {
            StepText.Text = "正在停止环境准备…";
            StopButton.IsEnabled = false;
            EnvironmentService.KillActiveInstall();
            return;
        }

        if (_cts is null || _cts.IsCancellationRequested)
        {
            return;
        }

        StepText.Text = "正在停止环境准备…";
        StopButton.IsEnabled = false;
        _cts.Cancel();
    }

    private void OnSetupProgress(SetupProgress progress)
    {
        if (progress.Percent >= 0)
        {
            SetupProgressBar.IsIndeterminate = false;
            SetupProgressBar.Value = Math.Clamp(progress.Percent, 0, 100);
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            StepText.Text = progress.Message;
            if (progress.Message.Contains("MB/s", StringComparison.OrdinalIgnoreCase)
                || progress.Message.Contains("KB/s", StringComparison.OrdinalIgnoreCase)
                || progress.Message.Contains("剩余", StringComparison.Ordinal))
            {
                TransferText.Text = progress.Message;
                TransferText.Visibility = Visibility.Visible;
            }
        }
    }

    private SetupRequest BuildRequest()
    {
        return new SetupRequest
        {
            PythonPath = _python,
            UseGpu = DeviceChoice.SelectedIndex == 0,
            ModelsDir = ModelsDirBox.Text?.Trim() ?? "",
            BrowsersDir = _browsersDir,
            Ffmpeg = _ffmpeg,
        };
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (_log.Length > 200_000)
        {
            _log.Remove(0, _log.Length - 100_000);
        }

        _log.AppendLine(line);
        LogBox.Text = _log.ToString();
        LogBox.SelectionStart = LogBox.Text.Length;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusInfo.Severity = severity;
        StatusInfo.Title = message;
        StatusInfo.IsOpen = true;
    }
}