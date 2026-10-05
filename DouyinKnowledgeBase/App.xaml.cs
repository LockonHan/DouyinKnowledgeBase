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

        // 记录未处理异常，便于用户反馈与排查（写入本地数据目录 crash.log）。
        UnhandledException += (_, e) => WriteCrashLog(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => WriteCrashLog(e.Exception);
    }

    /// <summary>把未处理异常追加写入 crash.log。</summary>
    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            string path = Path.Combine(AppPaths.LocalDataDir, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {exception}\n\n");
        }
        catch (Exception)
        {
            // 记录失败不影响应用。
        }
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
            if (!settings.AsrResident || !RepoLocator.ModelsReady(settings.ModelsDir))
            {
                // 环境尚未就绪（缺依赖或语音模型）时不启动，交由「环境准备」向导引导下载。
                return;
            }

            // 这里会启动/探测子进程（可能耗时数秒）。必须放到后台线程执行：
            // 在 UI 线程上同步等待会让窗口停止响应，Windows 会判定 AppHang 并强制关闭应用。
            await Task.Run(() =>
            {
                string python = RepoLocator.EffectivePython(settings.PythonPath);
                if (string.IsNullOrWhiteSpace(python))
                {
                    return;
                }

                settings.PythonPath = python;
                AsrServer.Start(settings);
            });
        }
        catch (Exception)
        {
            // 启动失败不影响主流程；转写时会自动回落到一次性模式。
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // 关闭应用时结束仍在运行的安装/下载进程，避免其成为孤儿进程继续占用带宽。
        EnvironmentService.KillActiveInstall();

        // 停止常驻服务需要等待子进程退出（可能数秒）。不能在 UI 线程上等待，
        // 否则关闭窗口时会因为“无响应”被 Windows 记为 AppHang。常驻服务本身带
        // --parent-pid 守卫，即便这里来不及收尾，宿主退出后它也会自行结束。
        Task.Run(() =>
        {
            try
            {
                AsrServer.Stop();
            }
            catch (Exception)
            {
                // 忽略：进程可能已经退出。
            }
        });
    }
}