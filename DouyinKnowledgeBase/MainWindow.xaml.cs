using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DouyinKnowledgeBase.Services;

namespace DouyinKnowledgeBase;

/// <summary>
/// 应用外壳：左侧导航轨（NavigationView）+ 内容 Frame。
/// 具体页面逻辑放在 Pages/ 下，窗口只负责导航与外壳状态。
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        NavigationService.Current.Initialize(RootFrame);
        NavigationService.Current.Navigated += OnNavigated;

        LoadRailFooter();

        // 默认进入知识库；SelectSection 会触发 SelectionChanged 完成首次导航。
        SelectSection(AppPage.Library);
        if (RootFrame.Content is null)
        {
            NavigationService.Current.Navigate(AppPage.Library);
        }
    }

    private void Shell_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item
            && item.Tag is string tag
            && Enum.TryParse(tag, out AppPage page))
        {
            NavigationService.Current.Navigate(page);
        }
    }

    private void OnNavigated(object? sender, AppPage page) =>
        SelectSection(NavigationService.SectionOf(page));

    /// <summary>把导航轨的选中态同步到当前页面所属的导航节。</summary>
    private void SelectSection(AppPage section)
    {
        NavigationViewItem? target = FindItem(section);
        if (target is not null && !ReferenceEquals(Shell.SelectedItem, target))
        {
            Shell.SelectedItem = target;
        }
    }

    private NavigationViewItem? FindItem(AppPage page)
    {
        foreach (object entry in Shell.MenuItems)
        {
            if (entry is NavigationViewItem item
                && item.Tag is string tag
                && Enum.TryParse(tag, out AppPage itemPage)
                && itemPage == page)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>导航轨底部显示本地转写环境状态。</summary>
    private async void LoadRailFooter()
    {
        try
        {
            PipelineSettings settings = await new PipelineSettingsService().LoadAsync();
            bool ready = RepoLocator.ModelsReady(settings.ModelsDir);
            RailFooterText.Text = ready
                ? $"本地转写已就绪\nFunASR · {DeviceLabel(settings.Device)}"
                : "环境未就绪\n请先完成环境准备";
        }
        catch (Exception)
        {
            RailFooterText.Text = "";
        }
    }

    private static string DeviceLabel(string device) => device switch
    {
        "cuda" or "cuda:0" => "GPU",
        "cpu" => "CPU",
        _ => "自动选择",
    };
}
