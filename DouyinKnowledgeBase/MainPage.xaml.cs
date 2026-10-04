using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DouyinKnowledgeBase.Pages;

namespace DouyinKnowledgeBase;

/// <summary>
/// 应用主页面：后续在此展示已收藏的视频列表与处理状态。
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SettingsPage));
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(DownloadPage));
    }

    private void SummaryButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SummaryPage));
    }
}