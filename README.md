# 抖音知识库（DouyinKnowledgeBase / DouKB）

一个 Windows 桌面应用：把抖音上 AI 博主的知识类视频保存到本地，用本地语音转写模型转成文字稿，再用 AI 总结成有结构的知识科普文章，沉淀为个人知识库。

## 功能规划

- **视频下载**：粘贴抖音分享链接，自动解析并下载视频到本地
- **语音转写**：调用本地语音转写模型（FunASR，离线运行），把视频音轨转成文字稿
- **AI 总结**：把文字稿交给大模型，生成结构化知识科普文章
- **知识库管理**：按主题/博主归档文章，支持搜索与浏览（规划中）

## 技术栈

- WinUI 3（Windows App SDK）
- C# / .NET 10
- Python：yt-dlp（下载）、ffmpeg（音频提取）、FunASR（本地语音转写）

## 开发环境

- Windows 10 1809（build 17763）及以上
- Visual Studio 2026（含 WinUI / Windows App SDK 工作负载）或 .NET 10 SDK
- 已启用开发者模式
- Python 环境（推荐 Anaconda），已安装 `funasr` 与支持 CUDA 的 `torch`
- `yt-dlp`、`ffmpeg` 已加入 PATH

## 构建与运行

```powershell
cd DouyinKnowledgeBase
dotnet build -c Debug -p:Platform=x64
dotnet run -p:Platform=x64
```

也可以直接双击仓库根目录的 `启动应用.bat`。

## 使用流程

1. 首次使用前，以**管理员身份**运行 `python tools\export_cookies.py`，导出浏览器（Edge）中的抖音 Cookie 到 `data\cookies.txt`
2. 打开应用 → “粘贴链接，下载并转写” → 粘贴抖音分享文案，点击“开始下载并转写”
3. 完成后可点击“用这篇稿去 AI 总结”，或回到主页“我已有转写稿，直接 AI 总结”
4. 在“设置”中配置大模型接入点（OpenAI 兼容），即可生成结构化文章

## 目录结构

```text
DouyinKnowledgeBase/            # WinUI 3 应用
├── App.xaml / App.xaml.cs      # 应用入口与生命周期
├── MainWindow.xaml             # 主窗口（标题栏 + 页面容器）
├── MainPage.xaml               # 主页入口
├── Pages/
│   ├── DownloadPage.xaml       # 粘贴链接 → 下载并转写
│   ├── SummaryPage.xaml        # 转写稿 → AI 结构化文章
│   └── SettingsPage.xaml       # 大模型接入点 + 管线配置
├── Services/                   # 配置持久化、LLM 客户端、管线编排
├── Package.appxmanifest        # 打包清单（runFullTrust）
└── Assets/                     # 应用图标与资源

tools/                          # Python 工具脚本
data/                           # 运行时数据（不入库：videos/audio/cookies）
```

## 工具脚本

- `tools/process_douyin.py`：管线编排。输入分享链接 → yt-dlp 下载 → ffmpeg 提取 16kHz 单声道音频 → FunASR 转写，并以 `@@PROG@@` 前缀的 JSON 行上报进度
- `tools/transcribe_funasr.py`：本地 FunASR 转写（Paraformer-large + VAD + 标点，GPU 加速）
- `tools/export_cookies.py`：导出浏览器抖音 Cookie（需管理员运行）

## Roadmap

- [x] 视频链接解析与下载（yt-dlp + 浏览器 cookie）
- [x] 本地语音转写（FunASR Paraformer-large，GPU 加速）
- [x] AI 文章总结（OpenAI 兼容接入点，可配置）
- [x] 粘贴链接 → 下载 → 转写 一键流程
- [ ] 知识库浏览与搜索
- [ ] 应用内登录抖音获取 Cookie（免去管理员导出）