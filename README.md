# 抖音知识库（DouyinKnowledgeBase / DouKB）

一个 Windows 桌面应用：把抖音上 AI 博主的知识类视频保存到本地，用本地语音转写模型转成文字稿，再由 AI 总结成有结构的知识科普文章，沉淀为个人知识库。

## 功能规划

- **视频收藏与本地保存**：粘贴抖音分享链接，解析并下载视频到本地
- **语音转写**：调用本地语音转写模型（离线运行），把视频音轨转成文字稿
- **AI 总结**：把文字稿交给 AI，生成结构化知识科普文章
- **知识库管理**：按主题/博主归档文章，支持搜索与浏览

## 技术栈

- WinUI 3（Windows App SDK）
- C# / .NET 10
- 本地语音转写与 AI 总结能力将在后续版本接入

## 开发环境

- Windows 10 1809（build 17763）及以上
- Visual Studio 2026（含 WinUI / Windows App SDK 工作负载）或 .NET 10 SDK
- 已启用开发者模式

## 构建与运行

```powershell
cd DouyinKnowledgeBase
dotnet build -c Debug -p:Platform=x64
dotnet run -p:Platform=x64
```

## 目录结构

```text
DouyinKnowledgeBase/
├── App.xaml / App.xaml.cs      # 应用入口与生命周期
├── MainWindow.xaml             # 主窗口（标题栏 + 页面容器）
├── MainPage.xaml               # 主页面（后续在此实现核心界面）
├── Package.appxmanifest        # 打包清单
└── Assets/                     # 应用图标与资源
```

## Roadmap

- [ ] 视频链接解析与下载
- [ ] 本地语音转写
- [ ] AI 文章总结
- [ ] 知识库浏览与搜索