using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using DouyinKnowledgeBase.Services;
using Windows.Storage;

namespace DouyinKnowledgeBase.Pages;

/// <summary>
/// 应用内获取抖音 Cookie：用 WebView2 打开抖音，让页面写入 ttwid / msToken 等，
/// 再导出为 Netscape 格式的 cookies.txt 供下载使用（无需管理员权限）。
/// </summary>
public sealed partial class CookiePage : Page
{
    private const string DouyinHome = "https://www.douyin.com/";

    private readonly PipelineSettingsService _pipelineSettingsService = new();

    private bool _initialized;
    private bool _busy;

    public CookiePage()
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
        try
        {
            string userDataFolder = Path.Combine(AppPaths.LocalDataDir, "WebView2");
            Directory.CreateDirectory(userDataFolder);
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
            await Web.EnsureCoreWebView2Async(environment);
            Web.Source = new Uri(DouyinHome);
            StatusText.Text = "已打开抖音。需要时请先登录，然后点击“提取 Cookie 并保存”。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"WebView2 初始化失败：{ex.Message}";
        }
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

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is null)
        {
            StatusText.Text = "浏览器尚未就绪，请稍候。";
            return;
        }

        Web.CoreWebView2.Navigate(DouyinHome);
        StatusText.Text = "正在重新打开抖音…";
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (Web.CoreWebView2 is null)
        {
            StatusText.Text = "浏览器尚未就绪，请稍候。";
            return;
        }

        _busy = true;
        ExtractButton.IsEnabled = false;
        StatusText.Text = "正在读取 Cookie…";

        try
        {
            PipelineSettings settings = await _pipelineSettingsService.LoadAsync();
            Directory.CreateDirectory(settings.DataDir);
            string cookiePath = Path.Combine(settings.DataDir, "cookies.txt");

            string[] uris =
            {
                "https://www.douyin.com/",
                "https://douyin.com/",
                "https://v.douyin.com/",
                "https://www.iesdouyin.com/",
            };

            Dictionary<string, CoreWebView2Cookie> cookies = new(StringComparer.Ordinal);
            foreach (string uri in uris)
            {
                IReadOnlyList<CoreWebView2Cookie> list = await Web.CoreWebView2.CookieManager.GetCookiesAsync(uri);
                foreach (CoreWebView2Cookie cookie in list)
                {
                    cookies[cookie.Domain + "|" + cookie.Name] = cookie;
                }
            }

            string content = ToNetscape(cookies.Values);
            await File.WriteAllTextAsync(cookiePath, content, new UTF8Encoding(false));

            int count = cookies.Count;
            bool hasMsToken = cookies.Values.Any(c => string.Equals(c.Name, "msToken", StringComparison.Ordinal));
            bool hasTtwid = cookies.Values.Any(c => string.Equals(c.Name, "ttwid", StringComparison.Ordinal));

            StatusText.Text = $"已保存 {count} 个 Cookie 到 {cookiePath}（ttwid：{(hasTtwid ? "有" : "无")}，msToken：{(hasMsToken ? "有" : "无")}）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"提取失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            ExtractButton.IsEnabled = true;
        }
    }

    private static string ToNetscape(IEnumerable<CoreWebView2Cookie> cookies)
    {
        StringBuilder sb = new();
        sb.Append("# Netscape HTTP Cookie File\n");
        foreach (CoreWebView2Cookie cookie in cookies)
        {
            string domain = cookie.Domain ?? "";
            bool includeSub = domain.StartsWith('.');
            // WebView2 的 Expires 为 Unix 时间戳数值；会话 Cookie 为 <0 或 0。
            long expires = 0;
            double raw = cookie.Expires;
            if (raw > 1e11)
            {
                expires = (long)(raw / 1000);   // 毫秒时间戳
            }
            else if (raw > 0)
            {
                expires = (long)raw;            // 秒时间戳
            }

            sb.Append(domain).Append('\t')
              .Append(includeSub ? "TRUE" : "FALSE").Append('\t')
              .Append(string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path).Append('\t')
              .Append(cookie.IsSecure ? "TRUE" : "FALSE").Append('\t')
              .Append(expires).Append('\t')
              .Append(cookie.Name).Append('\t')
              .Append(cookie.Value ?? "").Append('\n');
        }

        return sb.ToString();
    }
}