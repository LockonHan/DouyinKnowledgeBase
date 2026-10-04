using Microsoft.UI.Xaml;
using DouyinKnowledgeBase.Services;

namespace DouyinKnowledgeBase;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>主窗口实例，供文件选择器等需要窗口句柄的组件使用。</summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>常驻转写服务管理器（模型常驻，避免每次转写重复加载）。</summary>
    public static AsrServerManager AsrServer { get; } = new();

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindow = _window;
        _window.Closed += OnWindowClosed;
        _window.Activate();

        _ = StartResidentServerAsync();
    }

    /// <summary>按配置在后台启动常驻转写服务（未开启时不做任何事）。</summary>
    private static async Task StartResidentServerAsync()
    {
        try
        {
            PipelineSettings settings = await new PipelineSettingsService().LoadAsync();
            AsrServer.Start(settings);
        }
        catch (Exception)
        {
            // 启动失败不影响主流程；转写时会自动回落到一次性模式。
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        AsrServer.Stop();
    }
}