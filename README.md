# 抖音知识库（DouyinKnowledgeBase / DouKB）

一个 Windows 桌面应用：把抖音上 AI 博主的知识类视频保存到本地，用本地语音转写模型转成文字稿，再用 AI 总结成有结构的知识科普文章，沉淀为个人知识库。

## 功能规划

- **知识库首页**：片格列表浏览已保存的视频与文章，支持搜索（标题/标签/正文）、状态筛选、标签行
- **快速获取**：粘贴抖音分享文案，纵向步骤轴展示解析/下载/音频/转写/文稿五阶段进度
- **阅读视图**：Markdown 渲染 AI 总结文章，左侧 9:16 片格预览，支持目录/导出/复制/回看原视频
- **语音转写**：调用本地语音转写模型（FunASR，离线运行），把视频音轨转成文字稿
- **AI 总结**：把文字稿交给大模型，生成结构化知识科普文章
- **环境准备向导**：首次使用时检测设备（NVIDIA GPU / CPU），按需从国内源下载转写与下载组件、语音模型

## 技术栈

- WinUI 3（Windows App SDK）
- C# / .NET 10
- Python：Playwright（无头浏览器下载抖音视频）、curl_cffi（HTTP 下载）、ffmpeg（音频提取）、FunASR（本地语音转写）

## 开发环境

- Windows 10 1809（build 17763）及以上
- Visual Studio 2026（含 WinUI / Windows App SDK 工作负载）或 .NET 10 SDK
- 已启用开发者模式
- Python 环境（推荐 Anaconda），已安装 `funasr`、支持 CUDA 的 `torch`、`playwright`、`curl_cffi`
- 已安装 Playwright 浏览器内核：`python -m playwright install chromium`
- `ffmpeg` 已加入 PATH（用于提取音频）

## 构建与运行

```powershell
cd DouyinKnowledgeBase
dotnet build -c Debug -p:Platform=x64
dotnet run -p:Platform=x64
```

也可以直接双击仓库根目录的 `启动应用.bat`。

## 环境准备向导（首次使用）

应用内置「环境准备」向导（主页 / 设置页入口），用于一键准备本地运行环境：

1. 自动检测 NVIDIA 显卡并弹窗让用户选择 **GPU 加速** 或 **CPU** 转写
2. 按选择安装 PyTorch、FunASR 依赖、Playwright 浏览器内核
3. 从 **ModelScope 国内源** 下载语音模型（Paraformer-large + VAD + 标点）
4. 可指定已有的 **离线模型目录**，已就绪的项会自动跳过

命令行等价用法：

```powershell
python tools\env_setup.py --check                          # 自检并输出 @@ENV@@ 报告
python tools\env_setup.py --install --device cpu|gpu       # 按需安装（@@SETUP@@ 进度）
```

CPU 版本内置并随安装包分发，无需联网即可安装；GPU 版本按需下载 CUDA 版 PyTorch。

## 打包与安装

打包脚本会生成自包含的 `packaging\payload`（应用 + 内置 CPU Python 运行时 + ffmpeg + 工具脚本），
并可用 Inno Setup 6 编译为每用户安装器（安装向导中可自选安装位置）。

```powershell
powershell -ExecutionPolicy Bypass -File packaging\build.ps1                 # 生成 payload
powershell -ExecutionPolicy Bypass -File packaging\build.ps1 -MakeInstaller  # 生成安装器
```

详见 `packaging\README.md`。安装包为「瘦客户端」模式：转写与下载所需的 Python 依赖（CPU 版）
已内置，语音模型在首次运行时按设备从国内源下载，也可手动指定离线模型目录。

## 使用流程

1. 打开应用 → 左侧导航「快速获取」→ 粘贴抖音分享文案，点击开始
2. 步骤轴实时展示解析/下载/音频/转写/文稿进度，完成后自动出现在「知识库」列表
3. 大多数视频无需登录即可下载；若遇到需登录或受限的视频，到「设置」获取 Cookie，在应用内打开抖音登录一次，导出 `data\cookies.txt` 后再试
4. 在「知识库」点击条目进入「阅读视图」，可生成/导出/复制 AI 总结文章
5. 在「设置」中配置大模型接入点（OpenAI 兼容），即可生成结构化文章

## 目录结构

```text
DouyinKnowledgeBase/            # WinUI 3 应用
├── App.xaml / App.xaml.cs      # 应用入口与生命周期
├── MainWindow.xaml            # 主窗口（NavigationView 导航外壳）
├── Pages/
│   ├── HomePage.xaml           # 知识库首页（片格列表/搜索/标签/处理中）
│   ├── CapturePage.xaml        # 快速获取（粘贴文案→五阶段步骤轴）
│   ├── ReadingPage.xaml        # 阅读视图（Markdown 渲染+目录+导出）
│   ├── CookiePage.xaml         # 应用内登录抖音并导出 Cookie
│   ├── SetupPage.xaml           # 环境准备向导（设备检测 / 按需安装 / 自检）
│   └── SettingsPage.xaml       # 大模型接入点 + 管线配置
├── Services/                   # 配置持久化、LLM 客户端、管线编排、知识索引
├── Themes/                     # 设计令牌（明暗双套）与控件样式
├── Package.appxmanifest        # 打包清单（runFullTrust）
└── Assets/                     # 应用图标与资源

tools/                          # Python 工具脚本
packaging/                      # 打包脚本 + Inno Setup 安装器脚本（payload/out 不入库）
design/                         # UI 设计稿（HTML 原型 + 截图）
tests/                         # Python 回归测试
data/                           # 运行时数据（不入库：videos/audio/cookies）
```

## 工具脚本

- `tools/process_douyin.py`：管线编排。输入分享链接 → 下载视频 → ffmpeg 提取 16kHz 单声道音频 → FunASR 转写，并以 `@@PROG@@` 前缀的 JSON 行上报进度
- `tools/douyin_download.py`：抖音视频下载。用无头 Chromium 打开视频页，截获官方接口返回的 `play_addr` 直链（含音轨）后下载；必要时回退 yt-dlp
- `tools/transcribe_funasr.py`：本地 FunASR 转写（Paraformer-large + VAD + 标点，GPU 加速）
- `tools/env_setup.py`：环境检测与按需安装（torch / 依赖 / Playwright 浏览器 / 语音模型）
- `tools/asr_server.py` / `tools/asr_client.py`：常驻转写服务与客户端（加载一次模型，多次复用）
- `tools/export_cookies.py`：导出浏览器抖音 Cookie（备用，需管理员运行）

## Roadmap

- [x] 视频链接解析与下载（无头浏览器直取直链，免签名）
- [x] 本地语音转写（FunASR Paraformer-large，GPU 加速）
- [x] AI 文章总结（OpenAI 兼容接入点，可配置）
- [x] 粘贴链接 → 下载 → 转写 一键流程
- [x] 应用内登录抖音获取 Cookie（免去管理员导出）
- [x] 常驻转写服务（模型只加载一次，显著加快连续转写）
- [x] 环境准备向导（设备检测 / 国内源按需下载 / 离线模型目录）
- [x] 打包脚本与 Inno Setup 安装器（内置 CPU 运行时，自选安装位置）
- [x] 知识库浏览与搜索（片格列表/全文搜索/标签/状态筛选）
- [x] 阅读视图（Markdown 渲染/目录/导出/回看原视频）
- [x] 暗色主题支持（设计令牌明暗双套）
- [x] 浏览器预打包（安装包内置 Chromium 内核，开箱即用）

## 更新记录

### 2026-10-05

- **UI 全面重设计**：NavigationView 导航外壳（知识库/快速获取/环境准备/设置四节），导航轨底部显示本地转写环境状态；新增设计令牌系统（色板/字阶/间距/圆角，明暗双套主题）
- **知识库首页**：片格列表、搜索（标题/标签/正文全文）、状态筛选（全部/已总结/待总结）、标签行、处理中 livebar、空状态
- **快速获取页**：粘贴文案 → 纵向步骤轴（解析/下载/音频/转写/文稿五节点状态机）+ 进度条 + 最近完成列表
- **阅读视图**：左侧 9:16 片格栏 + Markdown 渲染 + 目录 + 生成/导出/复制/回看原视频
- **数据底座**：KnowledgeIndex（扫描 data/ 合并知识条目）、ArticleStore（总结落盘 .md+.json）、PipelineBus（跨页进度总线）
- **设置/环境准备页卡片化**：分组卡片布局，去掉冗余返回按钮
- **修复：中文路径下下载必失败**：curl_cffi（libcurl）在 Windows 上用 ANSI fopen 打开 CA 证书，安装路径含中文时报 curl: (77) 导致短链解析失败、回退 yt-dlp 也拿不到正确 URL。现在自动把 cacert.pem 复制到 %TEMP% 并设 CURL_CA_BUNDLE
- **浏览器预打包**：build.ps1 新增预打包步骤（chromium-headless-shell 直接进安装包，npmmirror 镜像优先），playwright 锁定 1.63.0 防版本失配；离线部署无需再手动装浏览器
- **转写进度估算**：按 WAV 字节推算音频时长 × 设备 RTF 系数（GPU 0.08 / CPU 0.5）上报百分比，超时转不确定态，失败带出最近 8 行输出
- **新增回归测试**：tests/test_regressions.py（6 用例，覆盖下载流程/短链优先走浏览器等）