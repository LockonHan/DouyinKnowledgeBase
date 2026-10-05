#Requires -Version 5.1
<#
.SYNOPSIS
    打包「抖音知识库」Windows 客户端（瘦客户端模式）。

.DESCRIPTION
    生成 packaging\payload（安装器/便携版的内容），包含：
      app\              应用自身（dotnet publish 自包含，Windows App SDK 自带运行时）
      tools\            Python 工具脚本（下载 / 转写 / 环境准备）
      runtime\python\   内置 CPython + CPU 版 PyTorch + FunASR 工具链
      runtime\bin\      ffmpeg.exe
      runtime\models\   （空）首次运行时按设备下载模型
      runtime\browsers\ Playwright 无头内核（构建时预打包，版本与内置 playwright 严格匹配）

    随后可用 packaging\DouyinKnowledgeBase.iss 编译安装器（需自行安装 Inno Setup 6）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\build.ps1

.EXAMPLE
    # 复用本机已有 ffmpeg，并顺带编译安装器
    powershell -ExecutionPolicy Bypass -File packaging\build.ps1 -FfmpegPath D:\tools\ffmpeg.exe -MakeInstaller
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$OutDir = '',
    [string]$PythonVersion = '3.11.9',
    [string]$TorchVersion = '2.6.0',
    [string]$PlaywrightVersion = '1.63.0',
    [string]$FfmpegPath = '',
    [string]$PipIndex = 'https://pypi.tuna.tsinghua.edu.cn/simple',
    [switch]$SkipApp,
    [switch]$SkipPython,
    [switch]$SkipPip,
    [switch]$SkipFfmpeg,
    [switch]$SkipBrowser,
    [switch]$MakeInstaller,
    [switch]$MakeZip
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$PackagingDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $PackagingDir
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $PackagingDir 'payload' }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$DefaultOutDir = [System.IO.Path]::GetFullPath((Join-Path $PackagingDir 'payload'))

function Write-Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Info([string]$Message) { Write-Host "    $Message" }
function Write-Note([string]$Message) { Write-Host "    ! $Message" -ForegroundColor Yellow }

function Remove-Directory([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        [System.IO.Directory]::Delete($Path, $true)
    }
}

function Get-RemoteFile {
    param(
        [string[]]$Urls,
        [string]$Destination,
        [string]$Label
    )

    if (Test-Path -LiteralPath $Destination) {
        Write-Info "$Label 已存在，跳过下载"
        return
    }

    $parent = Split-Path -Parent $Destination
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

    foreach ($url in $Urls) {
        $temp = "$Destination.part"
        try {
            Write-Info "下载 $Label：$url"
            $client = New-Object System.Net.WebClient
            try { $client.DownloadFile($url, $temp) } finally { $client.Dispose() }
            Move-Item -LiteralPath $temp -Destination $Destination -Force
            return
        }
        catch {
            Write-Note "下载失败（$($_.Exception.Message)），尝试下一个来源…"
            if (Test-Path -LiteralPath $temp) { [System.IO.File]::Delete($temp) }
        }
    }

    throw "无法下载 $Label。请检查网络，或手动放置到：$Destination"
}

Write-Host "抖音知识库 打包脚本" -ForegroundColor Green
Write-Info "仓库目录：$RepoRoot"
Write-Info "输出目录：$OutDir"

# ---------------------------------------------------------------------------
# 1. 发布应用
# ---------------------------------------------------------------------------
if ($SkipApp) {
    Write-Step "跳过应用发布（-SkipApp）"
}
else {
    Write-Step "构建应用（dotnet build $Configuration/$Platform，自包含）"
    $appOut = Join-Path $OutDir 'app'
    Remove-Directory $appOut

    $project = Join-Path $RepoRoot 'DouyinKnowledgeBase\DouyinKnowledgeBase.csproj'
    if (-not (Test-Path -LiteralPath $project)) { throw "未找到项目文件：$project" }

    # 必须用 build 而不是 publish：publish 会漏掉应用自身的 <App>.pri 与 Assets\，
    # 导致打包后的 exe 启动即崩溃（Microsoft.UI.Xaml.dll，0xc000027b）。
    & dotnet build $project -c $Configuration -p:Platform=$Platform -r win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（退出码 $LASTEXITCODE）" }

    $builtExe = Get-ChildItem -LiteralPath (Join-Path $RepoRoot "DouyinKnowledgeBase\bin\$Platform\$Configuration") `
        -Recurse -Filter 'DouyinKnowledgeBase.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like '*win-x64*' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $builtExe) { throw "未找到构建输出（DouyinKnowledgeBase.exe）" }
    if (-not (Test-Path -LiteralPath (Join-Path $builtExe.DirectoryName 'DouyinKnowledgeBase.pri'))) {
        throw "构建输出缺少 DouyinKnowledgeBase.pri，无法打包。"
    }

    New-Item -ItemType Directory -Force -Path $appOut | Out-Null
    Copy-Item -Path (Join-Path $builtExe.DirectoryName '*') -Destination $appOut -Recurse -Force
    Get-ChildItem -LiteralPath $appOut -Filter '*.pdb' -File -Recurse | ForEach-Object { [System.IO.File]::Delete($_.FullName) }

    $exe = Join-Path $appOut 'DouyinKnowledgeBase.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "构建结果缺少 $exe" }
    Write-Info "应用已构建：$exe"
}

# ---------------------------------------------------------------------------
# 2. 复制 Python 工具脚本
# ---------------------------------------------------------------------------
Write-Step "复制 tools\*.py"
$toolsOut = Join-Path $OutDir 'tools'
New-Item -ItemType Directory -Force -Path $toolsOut | Out-Null
Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'tools') -Filter '*.py' -File |
    Copy-Item -Destination $toolsOut -Force
Write-Info "已复制 $((Get-ChildItem -LiteralPath $toolsOut -Filter '*.py' -File).Count) 个脚本"

# ---------------------------------------------------------------------------
# 3. 内置 ffmpeg
# ---------------------------------------------------------------------------
$ffDest = Join-Path $OutDir 'runtime\bin\ffmpeg.exe'
if ($SkipFfmpeg) {
    Write-Step "跳过 ffmpeg（-SkipFfmpeg）"
}
elseif (Test-Path -LiteralPath $ffDest) {
    Write-Step "ffmpeg 已内置，跳过"
}
else {
    Write-Step "准备内置 ffmpeg"
    $source = $FfmpegPath
    if ([string]::IsNullOrWhiteSpace($source)) {
        $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
        if ($cmd) { $source = $cmd.Source }
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ffDest) | Out-Null

    if ($source -and (Test-Path -LiteralPath $source)) {
        Copy-Item -LiteralPath $source -Destination $ffDest -Force
        Write-Info "使用本机 ffmpeg：$source"
    }
    else {
        $archive = Join-Path $env:TEMP 'doukb-ffmpeg.zip'
        Get-RemoteFile -Urls @(
            'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip',
            'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip'
        ) -Destination $archive -Label 'ffmpeg'
        $extract = Join-Path $env:TEMP ('doukb-ffmpeg-' + [guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
        $found = Get-ChildItem -LiteralPath $extract -Recurse -Filter 'ffmpeg.exe' -File | Select-Object -First 1
        if (-not $found) { throw "下载的 ffmpeg 压缩包中未找到 ffmpeg.exe" }
        Copy-Item -LiteralPath $found.FullName -Destination $ffDest -Force
        Remove-Directory $extract
        Write-Info "已内置 ffmpeg（来自官方构建）"
    }
}

# ---------------------------------------------------------------------------
# 4. 内置 Python 运行时
# ---------------------------------------------------------------------------
$pyDir = Join-Path $OutDir 'runtime\python'
$pyExe = Join-Path $pyDir 'python.exe'

if (-not $SkipPython) {
    if (Test-Path -LiteralPath $pyExe) {
        Write-Step "内置 Python 已存在，跳过运行时下载"
    }
    else {
        Write-Step "准备内置 Python $PythonVersion（嵌入式发行版）"
        $archive = Join-Path $env:TEMP "python-$PythonVersion-embed-amd64.zip"
        Get-RemoteFile -Urls @(
            "https://mirrors.huaweicloud.com/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip",
            "https://registry.npmmirror.com/-/binary/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip",
            "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
        ) -Destination $archive -Label "Python $PythonVersion"
        New-Item -ItemType Directory -Force -Path $pyDir | Out-Null
        Expand-Archive -LiteralPath $archive -DestinationPath $pyDir -Force
        Write-Info "已解压到 $pyDir"
    }

    # 启用 site-packages，否则 pip 安装的包无法被导入。
    $pth = Get-ChildItem -LiteralPath $pyDir -Filter 'python*._pth' -File | Select-Object -First 1
    if ($pth) {
        $text = Get-Content -LiteralPath $pth.FullName -Raw
        $text = $text -replace '(?m)^\s*#\s*import site', 'import site'
        if ($text -notmatch '(?m)^\s*Lib\\site-packages\s*$') {
            $text = $text.TrimEnd() + "`r`nLib\site-packages`r`n"
        }
        [System.IO.File]::WriteAllText($pth.FullName, $text, (New-Object System.Text.ASCIIEncoding))
        Write-Info "已启用 site-packages：$($pth.Name)"
    }

    $pipDir = Join-Path $pyDir 'Lib\site-packages\pip'
    if (-not (Test-Path -LiteralPath $pipDir)) {
        Write-Step "为内置 Python 安装 pip"
        $getPip = Join-Path $env:TEMP 'doukb-get-pip.py'
        Get-RemoteFile -Urls @(
            'https://bootstrap.pypa.io/get-pip.py',
            'https://mirrors.aliyun.com/pypi/get-pip.py'
        ) -Destination $getPip -Label 'get-pip.py'
        & $pyExe $getPip --no-warn-script-location
        if ($LASTEXITCODE -ne 0) { throw "get-pip.py 执行失败（退出码 $LASTEXITCODE）" }
    }

    & $pyExe -c "import sys; print('内置 Python:', sys.version.split()[0], sys.executable)"
    if ($LASTEXITCODE -ne 0) { throw "内置 Python 无法运行" }
}

# ---------------------------------------------------------------------------
# 4b. 内置 VC++ 运行库（app-local 部署，免管理员安装 vc_redist）
# ---------------------------------------------------------------------------
# torch / onnxruntime 等原生扩展依赖 MSVCP140.dll、VCRUNTIME140*.dll。
# 微软允许把这组 CRT DLL 随应用本地部署（app-local），安装包因此无需管理员权限。
if (Test-Path -LiteralPath $pyDir) {
    Write-Step "内置 VC++ 运行库（app-local）"
    $crtNames = @(
        'concrt140.dll',
        'msvcp140.dll',
        'msvcp140_1.dll',
        'msvcp140_2.dll',
        'msvcp140_atomic_wait.dll',
        'msvcp140_codecvt_ids.dll',
        'vcruntime140.dll',
        'vcruntime140_1.dll'
    )

    # 依次尝试：VS 再分发包目录（首选）→ 系统目录（同为可再分发二进制）。
    $crtDirs = @()
    foreach ($crtRoot in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio2\BuildTools\VC\Redist\MSVC'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio2\BuildTools\VC\Redist\MSVC')
    )) {
        if (Test-Path -LiteralPath $crtRoot) {
            $crtDirs += Get-ChildItem -LiteralPath $crtRoot -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } |
                Where-Object { Test-Path -LiteralPath $_ }
        }
    }
    $system32 = Join-Path $env:SystemRoot 'System32'
    $crtDirs += $system32

    $crtCopied = 0
    foreach ($name in $crtNames) {
        $src = $null
        foreach ($dir in $crtDirs) {
            $candidate = Join-Path $dir $name
            if (Test-Path -LiteralPath $candidate) { $src = $candidate; break }
        }
        if ($src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $pyDir $name) -Force
            $crtCopied++
        }
        else {
            Write-Note "未找到 $name（目标机器缺 VC++ 运行库时，torch 可能加载失败）"
        }
    }
    Write-Info "已内置 $crtCopied/$($crtNames.Count) 个 VC++ 运行库 DLL"

    # torch 通过 add_dll_directory 加载 torch\lib，再放一份以确保依赖解析成功。
    $torchLib = Join-Path $pyDir 'Lib\site-packages\torch\lib'
    if (Test-Path -LiteralPath $torchLib) {
        foreach ($name in $crtNames) {
            $dst = Join-Path $pyDir $name
            if (Test-Path -LiteralPath $dst) {
                Copy-Item -LiteralPath $dst -Destination (Join-Path $torchLib $name) -Force
            }
        }
        Write-Info "已同步 CRT 到 torch\lib"
    }
}

# ---------------------------------------------------------------------------
# 4c. 内置 WebView2 引导程序（应用内登录抖音需要 WebView2 运行时）
# ---------------------------------------------------------------------------
$wv2Dir = Join-Path $OutDir 'runtime\bin\webview2'
$wv2Exe = Join-Path $wv2Dir 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path -LiteralPath $wv2Exe)) {
    Write-Step "下载 WebView2 引导程序"
    New-Item -ItemType Directory -Force -Path $wv2Dir | Out-Null
    try {
        Get-RemoteFile -Urls @(
            'https://go.microsoft.com/fwlink/p/?LinkId=2124703',
            'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/MicrosoftEdgeWebview2Setup.exe'
        ) -Destination $wv2Exe -Label 'WebView2 引导程序'
        Write-Info "已内置 WebView2 引导程序"
    }
    catch {
        Write-Note "WebView2 引导程序下载失败（可稍后手动重试）；应用其余功能不受影响。"
    }
}
else {
    Write-Info "WebView2 引导程序已存在，跳过下载"
}

# ---------------------------------------------------------------------------
# 5. 安装依赖（CPU 版 PyTorch 直接内置）
# ---------------------------------------------------------------------------
if ($SkipPip) {
    Write-Step "跳过依赖安装（-SkipPip）"
}
else {
    Write-Step "安装内置依赖（CPU 版 PyTorch + FunASR 工具链）"
    Write-Info "pip 源：$PipIndex"
    $common = @('-m', 'pip', 'install', '--no-input', '--disable-pip-version-check',
                '--retries', '5', '--timeout', '60', '-i', $PipIndex)

    & $pyExe @common '--upgrade' 'pip'
    if ($LASTEXITCODE -ne 0) { Write-Note "pip 升级失败，继续使用当前版本" }

    & $pyExe @common "torch==$TorchVersion" "torchaudio==$TorchVersion"
    if ($LASTEXITCODE -ne 0) { throw "PyTorch（CPU）安装失败" }

    & $pyExe @common 'funasr' 'modelscope' "playwright==$PlaywrightVersion" 'curl_cffi' 'sentencepiece==0.1.99'
    if ($LASTEXITCODE -ne 0) { throw "FunASR 工具链安装失败" }

    Write-Step "验证内置运行时"
    & $pyExe -c "import torch, funasr, modelscope, playwright, curl_cffi, sentencepiece; print('torch', torch.__version__); print('cuda', torch.cuda.is_available()); print('funasr', funasr.__version__); print('playwright', playwright.__version__)"
    if ($LASTEXITCODE -ne 0) { throw "内置运行时自检失败" }
    Write-Info "CPU 版 PyTorch 已内置；GPU 用户首次运行时由环境准备向导按需下载 CUDA 版"
}

# ---------------------------------------------------------------------------
# 5b. 预打包 Playwright 无头内核（与内置 playwright 版本严格匹配）
# ---------------------------------------------------------------------------
# 浏览器内核版本由 playwright 包版本决定，两者必须一致；这里用刚装好依赖的内置
# Python 下载，保证版本匹配。官方源国内常超时，npmmirror 镜像优先。
if ($SkipBrowser) {
    Write-Step "跳过浏览器预打包（-SkipBrowser）"
}
elseif (-not (Test-Path -LiteralPath $pyExe)) {
    Write-Note "内置 Python 不存在，跳过浏览器预打包；首次运行时由环境准备向导下载"
}
else {
    $browsersDir = Join-Path $OutDir 'runtime\browsers'
    $headlessMarker = Get-ChildItem -LiteralPath $browsersDir -Directory -Filter 'chromium_headless_shell-*' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($headlessMarker) {
        Write-Step "Playwright 无头内核已预打包（$($headlessMarker.Name)），跳过"
    }
    else {
        Write-Step "预打包 Playwright 无头内核（约 270 MB）"
        New-Item -ItemType Directory -Force -Path $browsersDir | Out-Null
        $env:PLAYWRIGHT_BROWSERS_PATH = $browsersDir
        $hosts = @(
            'https://cdn.npmmirror.com/binaries/playwright',
            'https://registry.npmmirror.com/-/binary/playwright',
            ''
        )
        $installed = $false
        foreach ($dlHost in $hosts) {
            if ($dlHost) {
                Write-Info "下载源：$dlHost"
                $env:PLAYWRIGHT_DOWNLOAD_HOST = $dlHost
            }
            else {
                Write-Info '下载源：Playwright 官方'
                Remove-Item Env:\PLAYWRIGHT_DOWNLOAD_HOST -ErrorAction SilentlyContinue
            }
            & $pyExe -m playwright install chromium-headless-shell
            if ($LASTEXITCODE -eq 0) { $installed = $true; break }
            Write-Note "该源下载失败，尝试下一个…"
        }
        Remove-Item Env:\PLAYWRIGHT_BROWSERS_PATH -ErrorAction SilentlyContinue
        Remove-Item Env:\PLAYWRIGHT_DOWNLOAD_HOST -ErrorAction SilentlyContinue
        if (-not $installed) {
            throw "Playwright 无头内核下载失败。请检查网络后重试，或加 -SkipBrowser 跳过（首次运行时由环境准备向导下载）"
        }

        # 启动探测：确保预打包的内核真的能跑起来。
        $env:PLAYWRIGHT_BROWSERS_PATH = $browsersDir
        & $pyExe -c "from playwright.sync_api import sync_playwright; p = sync_playwright().start(); b = p.chromium.launch(); print('LAUNCH_OK', b.version); b.close(); p.stop()"
        Remove-Item Env:\PLAYWRIGHT_BROWSERS_PATH -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -ne 0) { throw "预打包的浏览器内核启动探测失败" }
        Write-Info "已预打包 Playwright 无头内核到 runtime\browsers（启动探测通过）"
    }
}

# ---------------------------------------------------------------------------
# 6. 目录骨架与说明
# ---------------------------------------------------------------------------
Write-Step "创建目录骨架"
foreach ($dir in @('runtime\models', 'runtime\browsers', 'runtime\bin', 'data', 'tools')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $OutDir $dir) | Out-Null
}

$readme = @"
抖音知识库 —— 使用说明
====================================

1. 双击 app\DouyinKnowledgeBase.exe 启动应用。
2. 首次使用请点击主页的「环境准备」：
   - 检测到 NVIDIA 显卡时，会询问使用 GPU 加速还是仅 CPU；
   - 按需下载 PyTorch（CPU 版已内置，GPU 版约 2.5 GB）与语音模型（约 2 GB）；
   - Playwright 无头内核（约 270 MB）已随包预打包，无需下载；
   - 下载源为国内镜像（ModelScope / 清华 TUNA / 华为云 / npmmirror），支持断点与重试。
3. 若本机已有模型或需要离线部署，可在「环境准备」里手动指定模型目录，
   应用会直接复用其中的模型；详细步骤见同目录下的《离线部署说明.md》。
4. 在「设置」中配置大模型接入点（OpenAI 兼容），即可把转写稿总结成知识文章。

目录说明
--------
app\                 应用本体
tools\               Python 工具脚本
runtime\python\      内置 Python（含 CPU 版 PyTorch 与 FunASR 依赖）
runtime\bin\         内置 ffmpeg
runtime\models\      语音模型（首次运行时下载）
runtime\browsers\    Playwright 无头内核（已预打包）
data\                下载的视频、音频与转写稿

卸载不会删除 data\ 与 runtime\models\ 中的内容，如需彻底清理请手动删除。
"@
[System.IO.File]::WriteAllText((Join-Path $OutDir '使用说明.txt'), $readme, (New-Object System.Text.UTF8Encoding($false)))

# 离线部署指南（随包分发）
$offlineDoc = Join-Path $PackagingDir '离线部署说明.md'
if (Test-Path -LiteralPath $offlineDoc) {
    Copy-Item -LiteralPath $offlineDoc -Destination (Join-Path $OutDir '离线部署说明.md') -Force
    Write-Info '已内置 离线部署说明.md'
}

# ---------------------------------------------------------------------------
# 7. 汇总
# ---------------------------------------------------------------------------
$size = (Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Step "打包完成"
Write-Info ("输出目录：{0}（{1:N2} GB）" -f $OutDir, ($size / 1GB))

if ($MakeInstaller) {
    Write-Step "编译安装器（Inno Setup）"
    if ($OutDir -ne $DefaultOutDir) {
        Write-Note "-MakeInstaller 仅支持默认输出目录 $DefaultOutDir，已跳过。"
    }
    else {
        $iscc = @(
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

        if (-not $iscc) {
            $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
            if ($cmd) { $iscc = $cmd.Source }
        }

        if (-not $iscc) {
            Write-Note "未找到 Inno Setup（ISCC.exe）。请安装 Inno Setup 6 后重试，或手动编译："
            Write-Note "  ISCC.exe `"$PackagingDir\DouyinKnowledgeBase.iss`""
        }
        else {
            & $iscc (Join-Path $PackagingDir 'DouyinKnowledgeBase.iss')
            if ($LASTEXITCODE -ne 0) { throw "安装器编译失败（退出码 $LASTEXITCODE）" }
            Write-Info "安装器输出：$(Join-Path $PackagingDir 'out')"
        }
    }
}

if ($MakeZip) {
    Write-Step "生成便携版压缩包"
    $zipDir = Join-Path $PackagingDir 'out'
    New-Item -ItemType Directory -Force -Path $zipDir | Out-Null
    $zipPath = Join-Path $zipDir "DouyinKnowledgeBase-portable-x64.zip"
    if (Test-Path -LiteralPath $zipPath) { [System.IO.File]::Delete($zipPath) }
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Info "便携版：$zipPath"
}

Write-Host "`n下一步：" -ForegroundColor Green
Write-Info "1) 直接运行：$OutDir\app\DouyinKnowledgeBase.exe"
Write-Info "2) 制作安装器：安装 Inno Setup 6 后执行 build.ps1 -MakeInstaller"