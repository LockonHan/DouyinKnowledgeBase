using Microsoft.UI.Xaml.Controls;
using DouyinKnowledgeBase.Pages;

namespace DouyinKnowledgeBase.Services;

/// <summary>应用内的页面标识。</summary>
public enum AppPage
{
    /// <summary>知识库（应用首页）。</summary>
    Library,

    /// <summary>快速获取（下载并转写）。</summary>
    Capture,

    /// <summary>阅读视图；参数为转写稿路径或文章标识。</summary>
    Reading,

    /// <summary>环境准备。</summary>
    Setup,

    /// <summary>设置。</summary>
    Settings,

    /// <summary>Cookie 获取（次级页面，从快速获取进入）。</summary>
    Cookie,
}

/// <summary>
/// 统一的页面导航：集中维护路由表、参数传递与导航节。
/// 次级页面（阅读视图、Cookie）会归属到某个导航节，使左侧栏保持高亮。
/// </summary>
public sealed class NavigationService
{
    private static readonly Dictionary<AppPage, Type> Routes = new()
    {
        [AppPage.Library] = typeof(HomePage),
        [AppPage.Capture] = typeof(CapturePage),
        [AppPage.Reading] = typeof(ReadingPage),
        [AppPage.Setup] = typeof(SetupPage),
        [AppPage.Settings] = typeof(SettingsPage),
        [AppPage.Cookie] = typeof(CookiePage),
    };

    /// <summary>次级页面 → 所属导航节。未列出的页面自成节。</summary>
    private static readonly Dictionary<AppPage, AppPage> Sections = new()
    {
        [AppPage.Reading] = AppPage.Library,
        [AppPage.Cookie] = AppPage.Capture,
    };

    public static NavigationService Current { get; } = new();

    private Frame? _frame;

    /// <summary>当前页面。</summary>
    public AppPage CurrentPage { get; private set; } = AppPage.Library;

    /// <summary>当前应高亮的导航节。</summary>
    public AppPage CurrentSection => SectionOf(CurrentPage);

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    /// <summary>导航完成后触发，参数为进入的页面。</summary>
    public event EventHandler<AppPage>? Navigated;

    /// <summary>取页面所属的导航节。</summary>
    public static AppPage SectionOf(AppPage page) =>
        Sections.TryGetValue(page, out AppPage section) ? section : page;

    /// <summary>绑定承载页面的 Frame（应用启动时调用一次）。</summary>
    public void Initialize(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += (_, e) =>
        {
            if (e.SourcePageType is not null && TryResolvePage(e.SourcePageType, out AppPage page))
            {
                CurrentPage = page;
                Navigated?.Invoke(this, page);
            }
        };
    }

    /// <summary>导航到指定页面；parameter 传给目标页的 OnNavigatedTo。</summary>
    public bool Navigate(AppPage page, object? parameter = null)
    {
        if (_frame is null || !Routes.TryGetValue(page, out Type? target))
        {
            return false;
        }

        // 已在目标页且没有新参数时不再导航，避免历史堆叠。
        if (parameter is null && _frame.CurrentSourcePageType == target)
        {
            return true;
        }

        bool navigated = _frame.Navigate(target, parameter);
        if (navigated)
        {
            CurrentPage = page;
        }

        return navigated;
    }

    /// <summary>返回上一页；没有历史时回到知识库。</summary>
    public void GoBack()
    {
        if (_frame is null)
        {
            return;
        }

        if (_frame.CanGoBack)
        {
            _frame.GoBack();
        }
        else
        {
            Navigate(AppPage.Library);
        }
    }

    private static bool TryResolvePage(Type pageType, out AppPage page)
    {
        foreach (KeyValuePair<AppPage, Type> pair in Routes)
        {
            if (pair.Value == pageType)
            {
                page = pair.Key;
                return true;
            }
        }

        page = AppPage.Library;
        return false;
    }
}
