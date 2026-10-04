# 打包与发布（Windows）

面向 Windows 的**瘦客户端**打包方案：安装包只内置「应用 + Python 运行时 + CPU 版 PyTorch + ffmpeg」，
语音模型与 Playwright 浏览器在用户**首次运行**时由应用内的「环境准备」向导按机器情况下载。

## 产物结构

```text
<安装目录>\
├── app\                     # 应用本体（dotnet publish 自包含，无需预装 Windows App SDK）
│   └── DouyinKnowledgeBase.exe
├── tools\                   # Python 工具脚本（下载 / 转写 / 常驻服务 / 环境准备）
├── runtime\
│   ├── python\              # 内置 CPython 3.11 + CPU 版 PyTorch + FunASR 工具链
│   ├── bin\ffmpeg.exe       # 内置 ffmpeg（提取 16kHz 单声道音频）
│   ├── models\              # 语音模型（首次运行时下载，约 2 GB）
│   └── browsers\            # Playwright Chromium（首次运行时下载，约 150 MB）
├── data\                    # 视频 / 音频 / 转写稿 / cookies.txt
├── 使用说明.txt
└── 离线部署说明.md          # 离线部署（拷贝模型目录 + 指定离线路径）步骤
```

`RepoLocator` 会从 `app\` 逐级向上查找 `tools\process_douyin.py`，因此安装目录整体可移动。

## 构建

前置条件：

- .NET 10 SDK（或 Visual Studio 2026，含 WinUI 工作负载）
- PowerShell 5.1+
- 可选：Inno Setup 6（用于生成 `.exe` 安装器）
- 网络：构建阶段会从国内镜像下载 Python 运行时与 pip 包

```powershell
# 完整构建（应用 + Python 运行时 + CPU torch + ffmpeg）
powershell -ExecutionPolicy Bypass -File packaging\build.ps1

# 复用本机 ffmpeg，并顺带编译安装器
powershell -ExecutionPolicy Bypass -File packaging\build.ps1 -FfmpegPath D:\tools\ffmpeg.exe -MakeInstaller

# 只更新应用（保留已有的 Python 运行时与 ffmpeg，迭代最快）
powershell -ExecutionPolicy Bypass -File packaging\build.ps1 -SkipPython -SkipPip
```

常用参数：

| 参数 | 说明 |
| --- | --- |
| `-SkipApp` | 不重新发布应用 |
| `-SkipPython` | 不重新下载内置 Python 运行时 |
| `-SkipPip` | 不安装/更新内置依赖 |
| `-SkipFfmpeg` | 不内置 ffmpeg |
| `-FfmpegPath` | 指定本机 ffmpeg.exe（优先于下载） |
| `-PythonVersion` | 内置 Python 版本，默认 `3.11.9` |
| `-TorchVersion` | 内置 PyTorch 版本，默认 `2.6.0` |
| `-PipIndex` | pip 镜像，默认清华 TUNA |
| `-MakeInstaller` | 调用 Inno Setup 编译安装器 |
| `-MakeZip` | 额外生成便携版 zip |

## 安装器

`packaging\DouyinKnowledgeBase.iss`（Inno Setup 6）：

- `PrivilegesRequired=lowest` → 每用户安装，**不需要管理员权限**
- `DefaultDirName={localappdata}\Programs\DouyinKnowledgeBase`，且 `DisableDirPage=no`
  → 安装向导中可**自选安装位置**
- 卸载时保留 `runtime\models\` 与 `data\`（模型与用户数据体积大，避免误删）

编译：

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" packaging\DouyinKnowledgeBase.iss
# 输出：packaging\out\DouyinKnowledgeBase-Setup-<版本>-x64.exe
```

## 首次运行流程（用户视角）

1. 启动应用 → 主页「环境准备」
2. 应用检测 NVIDIA 显卡；检测到则**弹窗询问**「GPU 加速 / 仅 CPU」
3. 点击「开始准备环境」，向导按需执行：
   - PyTorch：CPU 版已内置；选 GPU 时从国内镜像下载 CUDA 版（约 2.5 GB）
   - 依赖：`funasr` / `modelscope` / `playwright` / `curl_cffi` / `sentencepiece==0.1.99`
   - 浏览器：Playwright Chromium（约 150 MB）
   - 模型：Paraformer-large + VAD + 标点（约 2 GB，ModelScope 国内源，实测 ~37 MB/s）
4. 自检通过后即可「粘贴链接 → 下载 → 转写 → AI 总结」

「环境准备」可重复执行，已就绪的项目会自动跳过；也可在页面中**手动指定离线模型目录**，
若其中已包含所需模型则直接复用。

## 离线部署

目标电脑不能联网时，可先在联网机器上准备好语音模型与 Playwright 浏览器，拷贝到目标机复用。
完整步骤（目录结构、指定离线路径、浏览器拷贝、常见问题）见 [离线部署说明.md](离线部署说明.md)。

## 关键约束

- **`sentencepiece` 必须锁 `0.1.99`**：`0.2.x` 在 Windows 上加载 BPE 词表会原生崩溃（0xC0000005）。
  Paraformer 使用 `tokens.json` 不受影响，但环境准备脚本仍会统一降到 `0.1.99`。
- **CPU 与 GPU 共用同一套模型**（Paraformer-large + VAD + 标点），只差 PyTorch 运行时：
  CPU ≈ 200 MB / cu124 ≈ 2.5 GB，因此 GPU 版留到首次运行按需下载。
- **模型缓存布局**：`MODELSCOPE_CACHE` 指向缓存根目录，模型位于其下 `models\iic\<仓库名>`。
  手动指定目录时会自动兼容「指向 `models` 子目录」的写法。
- 下载源均为国内地址：ModelScope（模型）、清华 TUNA / 阿里云（pip）、华为云 / npmmirror（Python）、
  SJTU / 阿里云 / 官方（CUDA 版 PyTorch，三级回退）。
- GPU 版安装失败时会**自动回退**到 CPU 版，保证应用始终可用。

## 体积与耗时参考

| 项目 | 体积 | 说明 |
| --- | --- | --- |
| 应用（自包含） | ~150 MB | 含 Windows App SDK 运行时 |
| 内置 Python + CPU torch + 依赖 | ~1.2 GB | 安装包主要体积 |
| ffmpeg | ~80 MB | 单个静态可执行文件 |
| 模型（首次运行下载） | ~2.0 GB | 不进入安装包 |
| Playwright Chromium（首次运行下载） | ~150 MB | 不进入安装包 |

安装包（LZMA2 压缩）预计 **~600 MB ~ 1 GB**；首次运行下载模型约需 1~3 分钟（视网速）。

## 端到端验证清单

1. 干净机器安装 → 安装向导可选目录、无 UAC 提示
2. 首次启动 → 「环境准备」弹出 GPU/CPU 选择
3. 选择 CPU → 全流程自检通过 → 粘贴抖音链接完成「下载 + 转写」
4. 选择 GPU（有 NVIDIA）→ 下载 cu124 → 自检显示 CUDA 可用
5. 指定离线模型目录 → 自检直接复用，不重复下载
6. 卸载 → `runtime\models` 与 `data` 保留