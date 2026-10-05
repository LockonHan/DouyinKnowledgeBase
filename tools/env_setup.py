"""环境准备：检测并按需安装「抖音知识库」所需的本地运行时。

用法（二选一）：
    python env_setup.py --check [--python <解释器>] [--models-dir <目录>] [--ffmpeg <路径>]
    python env_setup.py --install --device cpu|gpu [--python <解释器>]
        [--models-dir <目录>] [--browsers-dir <目录>] [--skip-torch] [--skip-models]
        [--skip-browser] [--skip-deps]

输出（stdout，每行一条，供宿主程序解析）：
    @@ENV@@   {"python": "...", "models": {...}, ...}          自检报告（--check）
    @@SETUP@@ {"step": "torch", "percent": 42, "message": "..."} 安装进度（--install）

安装内容：
    1. PyTorch（CPU 或 CUDA 版，国内镜像优先，失败自动降级/回落 CPU）
    2. funasr / modelscope / playwright / curl_cffi / sentencepiece==0.1.99
    3. Playwright Chromium（下载到 --browsers-dir，可离线复用）
    4. FunASR 模型：Paraformer-large + VAD + 标点（ModelScope 国内源）

    --models-dir 即 ModelScope 缓存根目录，模型最终位于
    <models-dir>/models/iic/<模型名>。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time

PREFIX_SETUP = "@@SETUP@@"
PREFIX_ENV = "@@ENV@@"
PIP_PROGRESS_RE = re.compile(
    r"(\d+(?:\.\d+)?)\s*/\s*(\d+(?:\.\d+)?)\s*([KMG]?B)\s+"
    r"(\d+(?:\.\d+)?)\s*([KMG]?B)/s\s+(\d+:\d{2}(?::\d{2})?)",
    re.IGNORECASE,
)
PIP_RAW_RE = re.compile(r"Progress\s+(\d+)\s+of\s+(\d+)", re.IGNORECASE)

MODEL_IDS = {
    "asr": "iic/speech_paraformer-large-vad-punc_asr_nat-zh-cn-16k-common-vocab8404-pytorch",
    "vad": "iic/speech_fsmn_vad_zh-cn-16k-common-pytorch",
    "punc": "iic/punc_ct-transformer_cn-en-common-vocab471067-large",
}
MODEL_LABELS = {
    "asr": "语音识别模型 Paraformer-large（约 870 MB）",
    "vad": "人声切分模型 FSMN-VAD（约 4 MB）",
    "punc": "标点恢复模型 CT-Transformer（约 1.1 GB）",
}

# 依赖包：sentencepiece 必须锁 0.1.99，0.2.x 在 Windows 上加载 BPE 词表会原生崩溃。
# playwright 必须锁版本：浏览器内核版本由它决定，升级会导致预打包的内核失配。
PLAYWRIGHT_VERSION = "1.63.0"
BASE_PACKAGES = ["funasr", "modelscope", f"playwright=={PLAYWRIGHT_VERSION}", "curl_cffi"]
SENTENCEPIECE = "sentencepiece==0.1.99"
TORCH_VERSION = "2.6.0"
TORCH_CUDA_TAG = "cu124"

# 国内源优先；每一项是 pip 的参数列表片段。
PYPI_MIRRORS = [
    ["-i", "https://pypi.tuna.tsinghua.edu.cn/simple"],
    ["-i", "https://mirrors.aliyun.com/pypi/simple/"],
    ["-i", "https://pypi.org/simple"],
]
# CUDA 版 PyTorch 走国内镜像；SJTU 的 pytorch-wheels 页面已不再直接提供文件链接，故移除。
# 顺序按国内实测速度排列：阿里云 CDN 最快，官方源仅作最后兜底。
TORCH_CUDA_MIRRORS = [
    ["-f", "https://mirrors.aliyun.com/pytorch-wheels/cu124/",
     "-i", "https://pypi.tuna.tsinghua.edu.cn/simple"],
    ["-f", "https://mirrors.aliyun.com/pytorch-wheels/cu124/",
     "-i", "https://mirrors.aliyun.com/pypi/simple/"],
    ["-i", "https://download.pytorch.org/whl/cu124"],
]

# 各安装步骤在总进度里的区间（start, end）。
STEP_RANGES = {
    "torch": (0, 40),
    "deps": (40, 58),
    "browser": (58, 66),
    "models": (66, 97),
    "verify": (97, 100),
}

def emit(step: str, percent: float, message: str, level: str = "info", **extra) -> None:
    payload = {"step": step, "percent": round(float(percent), 1), "message": message, "level": level}
    payload.update(extra)
    print(PREFIX_SETUP + json.dumps(payload, ensure_ascii=False), flush=True)


def emit_report(report: dict) -> None:
    print(PREFIX_ENV + json.dumps(report, ensure_ascii=False), flush=True)


def run(cmd, env=None, timeout=None):
    """运行命令并返回 (returncode, 合并后的输出)。"""
    try:
        proc = subprocess.run(
            cmd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=timeout, text=True, encoding="utf-8", errors="replace",
        )
        return proc.returncode, proc.stdout or ""
    except subprocess.TimeoutExpired as exc:
        return -9, (exc.output or "") if isinstance(exc.output, str) else ""
    except OSError as exc:
        return -1, str(exc)


def python_of(exe: str, code: str, env=None, timeout=120):
    """在目标解释器里执行一段代码，返回 (returncode, 输出)。"""
    return run([exe, "-c", code], env=env, timeout=timeout)


def pkg_version(exe: str, name: str, env=None) -> str:
    code = (
        "import importlib.metadata as m\n"
        "try:\n"
        "    print(m.version('" + name + "'))\n"
        "except Exception:\n"
        "    print('')\n"
    )
    rc, out = python_of(exe, code, env=env, timeout=60)
    return out.strip().splitlines()[-1] if out.strip() else ""


def find_ffmpeg(hint: str | None) -> str:
    if hint and os.path.isfile(hint):
        return hint
    found = shutil.which("ffmpeg")
    return found or ""


def nvidia_info() -> dict:
    """检测 NVIDIA 显卡（nvidia-smi），不依赖 torch。"""
    smi = shutil.which("nvidia-smi")
    if not smi:
        system32 = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "nvidia-smi.exe")
        smi = system32 if os.path.isfile(system32) else ""
    if not smi:
        return {"present": False, "name": "", "driver": ""}
    rc, out = run([smi, "--query-gpu=name,driver_version", "--format=csv,noheader"], timeout=30)
    if rc != 0 or not out.strip():
        return {"present": False, "name": "", "driver": ""}
    first = out.strip().splitlines()[0]
    parts = [p.strip() for p in first.split(",")]
    return {"present": True, "name": parts[0] if parts else "", "driver": parts[1] if len(parts) > 1 else ""}


def model_root(models_dir: str) -> str:
    return os.path.join(models_dir, "models", "iic") if models_dir else ""


WEIGHT_MARKERS = ("model.pt", "model.pth", "model.pb", "model.bin")


def _cache_roots(models_dir: str) -> list:
    """缓存根候选；用户可能直接指向了根下的 models 目录。"""
    if not models_dir:
        return []
    roots = [models_dir]
    norm = os.path.normpath(models_dir)
    if os.path.basename(norm).lower() == "models":
        roots.append(os.path.dirname(norm))
    return roots


def model_candidates(models_dir: str, name: str) -> list:
    """某模型在所有已知布局下的候选目录。

    旧版 ModelScope：<cache>/models/iic/<name>、<cache>/iic/<name>
    新版 ModelScope（HF 风格）：<cache>/models/iic--<name>/snapshots/<revision>
    """
    candidates = []
    for root in _cache_roots(models_dir):
        candidates.append(os.path.join(root, "models", "iic", name))
        candidates.append(os.path.join(root, "iic", name))
        for parent in (os.path.join(root, "models", "iic--" + name),
                       os.path.join(root, "iic--" + name)):
            candidates.append(parent)
            snapshots = os.path.join(parent, "snapshots")
            if os.path.isdir(snapshots):
                try:
                    for rev in sorted(os.listdir(snapshots)):
                        candidates.append(os.path.join(snapshots, rev))
                except OSError:
                    pass
    return candidates


def dir_has_weights(path: str) -> bool:
    """目录内存在已完成的权重文件才算就绪（排除 *.incomplete 等中断残留）。"""
    try:
        entries = os.listdir(path)
    except OSError:
        return False
    for entry in entries:
        low = entry.lower()
        if low.endswith(".incomplete") or low.endswith(".tmp") or low.endswith(".part"):
            continue
        if entry in WEIGHT_MARKERS or low.endswith(".safetensors"):
            return True
    return False


def find_model_dir(models_dir: str, name: str):
    """返回已就绪的模型目录；找不到返回 None。"""
    for candidate in model_candidates(models_dir, name):
        if dir_has_weights(candidate):
            return candidate
    return None


def installed_models(models_dir: str) -> dict:
    """返回 {模型键: 是否已就绪}；兼容新旧两种 ModelScope 缓存布局。"""
    result = {}
    for key, model_id in MODEL_IDS.items():
        name = model_id.split("/")[-1]
        result[key] = find_model_dir(models_dir, name) is not None
    return result


def chromium_present(exe: str, env) -> bool:
    """真实启动一次无头浏览器来判定可用性。

    仅检查可执行文件是否存在会漏判：Playwright 1.49+ 的无头模式默认使用独立的
    chromium-headless-shell，只装完整版 Chromium 时依然无法启动。
    """
    code = (
        "from playwright.sync_api import sync_playwright\n"
        "with sync_playwright() as p:\n"
        "    print('EXE:' + p.chromium.executable_path)\n"
        "    b = p.chromium.launch()\n"
        "    print('LAUNCH_OK:' + b.version)\n"
        "    b.close()\n"
    )
    rc, out = python_of(exe, code, env=env, timeout=240)
    if rc != 0:
        return False
    return "LAUNCH_OK:" in (out or "")


def default_models_cache() -> str:
    """未显式指定时的默认 ModelScope 缓存根目录（模型位于其下 models/iic/）。"""
    env_dir = os.environ.get("MODELSCOPE_CACHE")
    if env_dir:
        return env_dir
    return os.path.expanduser(os.path.join("~", ".cache", "modelscope", "hub"))


def build_report(args) -> dict:
    exe = args.python or sys.executable
    env = setup_env(args)
    models_dir = args.models_dir or default_models_cache()
    report = {
        "pythonPath": exe,
        "pythonVersion": "",
        "packages": {},
        "torchCuda": False,
        "nvidia": nvidia_info(),
        "ffmpeg": find_ffmpeg(args.ffmpeg),
        "browsersDir": env.get("PLAYWRIGHT_BROWSERS_PATH", ""),
        "browserReady": False,
        "modelsDir": models_dir,
        "models": {},
        "requestedDevice": "cuda:0" if args.device == "gpu" else "cpu",
        "device": "",
        "ready": False,
        "issues": [],
    }

    rc, out = python_of(exe, "import sys; print(sys.version.split()[0])", env=env, timeout=60)
    report["pythonVersion"] = out.strip().splitlines()[-1] if rc == 0 and out.strip() else ""

    for name in ["funasr", "modelscope", "playwright", "curl_cffi", "torch", "sentencepiece"]:
        report["packages"][name] = pkg_version(exe, name, env=env)

    code = (
        "try:\n"
        "    import torch\n"
        "    print('1' if torch.cuda.is_available() else '0')\n"
        "except Exception:\n"
        "    print('0')\n"
    )
    rc, out = python_of(exe, code, env=env, timeout=180)
    report["torchCuda"] = rc == 0 and out.strip().endswith("1")

    report["browserReady"] = chromium_present(exe, env)
    report["models"] = installed_models(models_dir)

    report["device"] = "cuda:0" if report["torchCuda"] else "cpu"

    if not report["pythonVersion"]:
        report["issues"].append("Python 解释器不可用")
    if not report["packages"].get("funasr"):
        report["issues"].append("缺少 funasr")
    if not report["packages"].get("torch"):
        report["issues"].append("缺少 torch")
    if args.device == "gpu" and not report["torchCuda"]:
        report["issues"].append("CUDA 不可用（当前 PyTorch 为 CPU 版或驱动不可用）")
    if not report["ffmpeg"]:
        report["issues"].append("未找到 ffmpeg")
    if not report["browserReady"]:
        report["issues"].append("Playwright 浏览器未安装")
    missing = [MODEL_LABELS[k] for k, ok in report["models"].items() if not ok]
    if missing:
        report["issues"].append("缺少模型：" + "、".join(missing))

    report["ready"] = not report["issues"]
    return report


def setup_env(args) -> dict:
    env = dict(os.environ)
    env["PYTHONIOENCODING"] = "utf-8"
    env["PYTHONUTF8"] = "1"
    if args.models_dir:
        env["MODELSCOPE_CACHE"] = os.path.abspath(args.models_dir)
    if getattr(args, "browsers_dir", ""):
        env["PLAYWRIGHT_BROWSERS_PATH"] = os.path.abspath(args.browsers_dir)
    return env


class _Heartbeat:
    """在耗时的 pip 步骤里定期上报“已用时”，让界面保持活动。"""

    def __init__(self, step, start, end, label):
        self.step, self.start, self.end, self.label = step, start, end, label
        self._stop = threading.Event()
        self._thread = None

    def __enter__(self):
        def loop():
            t0 = time.time()
            while not self._stop.wait(5):
                emit(self.step, self.start, f"{self.label}（已用时 {int(time.time() - t0)} 秒）")
        self._thread = threading.Thread(target=loop, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        return False


def _fmt_speed(bps: float) -> str:
    if bps >= 1048576:
        return "%.1f MB/s" % (bps / 1048576.0)
    if bps >= 1024:
        return "%.0f KB/s" % (bps / 1024.0)
    return "%.0f B/s" % bps


def _fmt_eta(seconds: float) -> str:
    seconds = max(seconds, 0.0)
    if seconds < 60:
        return "%d 秒" % int(seconds)
    if seconds < 3600:
        return "%d 分 %d 秒" % (int(seconds // 60), int(seconds % 60))
    return "%d 时 %d 分" % (int(seconds // 3600), int(seconds % 3600 // 60))


class _DirGrowth:
    """按目录体积增长估算下载速度与剩余时间。

    用于自身不汇报进度的下载器（Playwright 的浏览器下载）。
    """

    def __init__(self, step, start, label, watch_dir, total_hint=0):
        self.step, self.start, self.label = step, start, label
        self.watch_dir, self.total_hint = watch_dir, total_hint
        self._stop = threading.Event()
        self._thread = None

    def _size(self):
        total = 0
        for root, _dirs, files in os.walk(self.watch_dir):
            for name in files:
                try:
                    total += os.path.getsize(os.path.join(root, name))
                except OSError:
                    pass
        return total

    def __enter__(self):
        def loop():
            t0 = time.time()
            base = float(self._size())
            while not self._stop.wait(2):
                elapsed = max(time.time() - t0, 0.001)
                done = max(self._size() - base, 0)
                speed = done / elapsed
                if speed <= 0:
                    emit(self.step, self.start, f"{self.label}（已用时 {int(elapsed)} 秒）")
                    continue
                text = f"{self.label}：已下载 {done / 1048576.0:.1f} MB"
                if self.total_hint > 0 and done < self.total_hint:
                    eta = (self.total_hint - done) / speed
                    text += f"｜{_fmt_speed(speed)}｜剩余约 {_fmt_eta(eta)}"
                else:
                    text += f"｜{_fmt_speed(speed)}"
                emit(self.step, self.start, text)
        self._thread = threading.Thread(target=loop, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        return False


def pip_install(exe, packages, index_args, env, step, start, end, label, extra_args=None):
    """执行一次 pip install，成功返回 True。"""
    cmd = [exe, "-m", "pip", "install", "--no-input", "--disable-pip-version-check",
           "--progress-bar", "raw", "--retries", "5", "--timeout", "60"]
    cmd += list(extra_args or [])
    cmd += list(packages) + list(index_args)
    emit(step, start, f"{label}：正在下载并安装…")
    print("$ " + " ".join(cmd), flush=True)
    try:
        proc = subprocess.Popen(cmd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                bufsize=0)
    except OSError as exc:
        emit(step, start, f"{label}：无法启动 pip（{exc}）", level="error")
        return False

    state = {"started": time.monotonic(), "done": 0, "label": label}

    def handle_pip_line(line):
        line = line.strip()
        if not line:
            return
        match = PIP_PROGRESS_RE.search(line)
        if match:
            done = float(match.group(1))
            total = float(match.group(2))
            file_percent = min(done / total, 1.0) if total > 0 else 0.0
            overall = start + (end - start) * file_percent
            message = (f"{label}：{file_percent * 100:.0f}%｜"
                       f"{match.group(4)} {match.group(5)}/s｜剩余 {match.group(6)}")
            emit(step, overall, message)
            return
        raw = PIP_RAW_RE.search(line)
        if raw:
            done = int(raw.group(1))
            total = int(raw.group(2))
            if done <= 0:
                return
            if done < state["done"]:  # 新文件，重置计时
                state["started"] = time.monotonic()
            state["done"] = done
            if total > 0:
                elapsed = max(time.monotonic() - state["started"], 0.001)
                speed = done / elapsed
                file_percent = min(done / total, 1.0)
                eta = (total - done) / speed if speed > 0 else 0.0
                emit(step, start + (end - start) * file_percent,
                     f"{label}：{file_percent * 100:.0f}%｜{_fmt_speed(speed)}｜剩余 {_fmt_eta(eta)}")
            return
        emit(step, start, line[:220])

    try:
        buffer = ""
        while True:
            chunk = proc.stdout.read(4096) if proc.stdout else b""
            if not chunk:
                break
            buffer += chunk.decode("utf-8", "replace")
            parts = re.split(r"[\r\n]+", buffer)
            buffer = parts.pop()
            for line in parts:
                handle_pip_line(line)
        if buffer:
            handle_pip_line(buffer)
        proc.wait()
    except Exception as exc:  # 用户中断等
        proc.kill()
        emit(step, start, f"{label}：安装中断（{exc}）", level="error")
        return False

    if proc.returncode == 0:
        emit(step, end, f"{label}：完成")
        return True
    emit(step, start, f"{label}：失败（pip 退出码 {proc.returncode}）", level="warn")
    return False


def install_torch(args, exe, env) -> bool:
    start, end = STEP_RANGES["torch"]
    want_gpu = args.device == "gpu"
    target = f"torch=={TORCH_VERSION}+{TORCH_CUDA_TAG}" if want_gpu else f"torch=={TORCH_VERSION}"

    current = pkg_version(exe, "torch", env=env)
    if current:
        has_cuda = current.endswith("+" + TORCH_CUDA_TAG)
        if (want_gpu and has_cuda) or (not want_gpu and not has_cuda):
            emit("torch", end, f"PyTorch 已安装（{current}），跳过")
            return True
        emit("torch", start + 2, f"已安装 PyTorch {current}，与所选设备不匹配，准备更换…")
    else:
        emit("torch", start + 1, "未检测到 PyTorch，开始安装…")

    ok = _torch_install_attempts(args, exe, env, target, want_gpu, start, end)
    if ok:
        ensure_torchaudio(args, exe, env)
    return ok


def ensure_torchaudio(args, exe, env) -> None:
    """尽量补上 torchaudio（个别 FunASR 前端会用到）；失败不影响主流程。"""
    if pkg_version(exe, "torchaudio", env=env):
        return
    start, end = STEP_RANGES["torch"]
    want_gpu = args.device == "gpu"
    name = f"torchaudio=={TORCH_VERSION}+{TORCH_CUDA_TAG}" if want_gpu else f"torchaudio=={TORCH_VERSION}"
    candidates = TORCH_CUDA_MIRRORS if want_gpu else PYPI_MIRRORS
    emit("torch", max(end - 3, start), "正在补充 torchaudio…")
    for index_args in candidates:
        if pip_install(exe, [name], index_args, env, "torch", max(end - 3, start), end, "安装 torchaudio"):
            return
    emit("torch", end, "torchaudio 安装失败（不影响转写）。", level="warn")


def _torch_install_attempts(args, exe, env, target, want_gpu, start, end) -> bool:
    label = "安装 GPU 版 PyTorch（CUDA，约 2.5 GB）" if want_gpu else "安装 CPU 版 PyTorch（约 200 MB）"
    candidates = TORCH_CUDA_MIRRORS if want_gpu else PYPI_MIRRORS
    with _Heartbeat("torch", start + 2, end - 2, label):
        for index_args in candidates:
            source = " ".join(index_args)
            if pip_install(exe, [target], index_args, env, "torch", start + 2, end - 2, label):
                return True
            emit("torch", start + 2, f"该下载源不可用（{source}），尝试下一个…", level="warn")

    if want_gpu:
        emit("torch", start + 5, "GPU 版安装失败，自动回退到 CPU 版（转写仍可用，速度稍慢）。", level="warn")
        with _Heartbeat("torch", start + 5, end - 2, "安装 CPU 版 PyTorch"):
            for index_args in PYPI_MIRRORS:
                if pip_install(exe, [f"torch=={TORCH_VERSION}"], index_args, env,
                               "torch", start + 5, end - 2, "安装 CPU 版 PyTorch"):
                    return True
    emit("torch", end, "PyTorch 安装失败：所有下载源均不可用，请检查网络后重试。", level="error")
    return False


def install_deps(args, exe, env) -> bool:
    start, end = STEP_RANGES["deps"]
    missing = [name for name in BASE_PACKAGES if not pkg_version(exe, name, env=env)]
    need_sp = pkg_version(exe, "sentencepiece", env=env) != "0.1.99"
    # playwright 版本与预打包的浏览器内核严格绑定，版本不符时必须重装到锁定版。
    need_pw = pkg_version(exe, "playwright", env=env) != PLAYWRIGHT_VERSION

    if not missing and not need_sp and not need_pw:
        emit("deps", end, "依赖包已齐全，跳过")
        return True

    packages = list(missing) + ([SENTENCEPIECE] if need_sp else [])
    if need_pw and f"playwright=={PLAYWRIGHT_VERSION}" not in packages:
        packages.append(f"playwright=={PLAYWRIGHT_VERSION}")
    emit("deps", start + 1, "正在安装依赖：" + "、".join(packages))
    with _Heartbeat("deps", start + 2, end - 2, "安装依赖包"):
        for index_args in PYPI_MIRRORS:
            if pip_install(exe, packages, index_args, env, "deps", start + 2, end - 2, "安装依赖包"):
                return True
    emit("deps", end, "依赖安装失败，请检查网络后重试。", level="error")
    return False


# Playwright 1.63 起 Chromium 走 CFT 布局，官方 cdn.playwright.dev 在国内常超时。
# 依次尝试国内镜像，最后回退官方源。
BROWSER_DOWNLOAD_HOSTS = [
    "https://cdn.npmmirror.com/binaries/playwright",
    "https://registry.npmmirror.com/-/binary/playwright",
    "",
]

# 应用只用无头模式下载视频，因此优先只装无头内核（约 270 MB，省掉 430 MB 完整版）；
# 若启动探测失败，再兜底安装完整版 + 无头内核。
BROWSER_TARGET_SETS = [
    ["chromium-headless-shell"],
    ["chromium", "chromium-headless-shell"],
]


def _install_browser_targets(exe, env, host, targets, start, end) -> bool:
    attempt = dict(env)
    attempt["PLAYWRIGHT_DOWNLOAD_CONNECTION_TIMEOUT"] = "60000"
    if host:
        attempt["PLAYWRIGHT_DOWNLOAD_HOST"] = host
    for target in targets:
        code = (
            "import subprocess,sys\n"
            "sys.exit(subprocess.call([sys.executable,'-m','playwright','install',%r]))\n" % target
        )
        hint = (430 if target == "chromium" else 270) * 1048576
        local_appdata = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~")
        watch_dir = attempt.get("PLAYWRIGHT_BROWSERS_PATH") or os.path.join(local_appdata, "ms-playwright")
        with _DirGrowth("browser", start + 2, "下载 Playwright " + target, watch_dir, hint):
            rc, out = python_of(exe, code, env=attempt, timeout=3600)
        if rc != 0:
            return False
    return True


def install_browser(args, exe, env) -> bool:
    start, end = STEP_RANGES["browser"]
    if chromium_present(exe, env):
        emit("browser", end, "Playwright 浏览器已就绪（启动探测通过），跳过")
        return True

    hosts = []
    explicit = (os.environ.get("PLAYWRIGHT_DOWNLOAD_HOST") or "").strip()
    for host in ([explicit] if explicit else []) + BROWSER_DOWNLOAD_HOSTS:
        if host not in hosts:
            hosts.append(host)

    # 首个源先试「仅无头内核」，其余情况一律装完整版。
    attempts = []
    for index, host in enumerate(hosts):
        if index == 0:
            attempts.append((host, BROWSER_TARGET_SETS[0]))
        attempts.append((host, BROWSER_TARGET_SETS[-1]))

    for host, targets in attempts:
        label = host or "Playwright 官方源"
        emit("browser", start + 1, "正在下载 Playwright 浏览器（%s：%s）…" % (label, "+".join(targets)))
        if not _install_browser_targets(exe, env, host, targets, start, end):
            emit("browser", start + 2, "%s 下载失败，改用下一个源…" % label, level="warn")
            continue
        if chromium_present(exe, env):
            emit("browser", end, "Playwright 浏览器下载完成（启动探测通过）")
            return True
        emit("browser", start + 2, "%s 下载完成但启动探测失败，改用完整版重试…" % label, level="warn")

    emit("browser", end, "Playwright 浏览器安装失败：所有下载源均未成功。", level="error")
    return False


def install_models(args, exe, env) -> bool:
    start, end = STEP_RANGES["models"]
    models_dir = os.path.abspath(args.models_dir) if args.models_dir else ""
    if not models_dir:
        emit("models", end, "未指定模型目录，跳过模型下载。", level="warn")
        return True

    os.makedirs(models_dir, exist_ok=True)
    have = installed_models(models_dir)
    pending = [key for key in ("asr", "vad", "punc") if not have.get(key)]
    if not pending:
        emit("models", end, "模型已齐全，跳过")
        return True

    total = len(pending)
    span = (end - start) / total
    for i, key in enumerate(pending):
        lo = start + span * i
        hi = start + span * (i + 1)
        label = MODEL_LABELS[key]
        emit("models", lo, f"正在下载{label}（{i + 1}/{total}）…")
        if download_one_model(exe, env, MODEL_IDS[key], models_dir, lo, hi, label):
            emit("models", hi, f"{label} 下载完成")
        else:
            emit("models", hi, f"{label} 下载失败", level="error")
            return False
    return True


def download_one_model(exe, env, model_id, models_dir, lo, hi, label) -> bool:
    """用 modelscope.snapshot_download 下载单个模型，并按文件进度上报。"""
    code = """
import json, os, sys, threading, time
from modelscope.hub.snapshot_download import snapshot_download
from modelscope.hub.callback import ProgressCallback

state = {"sizes": {}, "done": {}, "lock": threading.Lock(), "last": -1.0, "started": time.monotonic()}
lo, hi = float(sys.argv[2]), float(sys.argv[3])
label = sys.argv[4]

def report(force=False):
    with state["lock"]:
        total = sum(state["sizes"].values())
        done = sum(state["done"].values())
    if total <= 0:
        return
    pct = lo + (hi - lo) * min(done / total, 0.99)
    if force or pct - state["last"] >= 1.0:
        state["last"] = pct
        elapsed = max(time.monotonic() - state["started"], 0.001)
        speed = done / elapsed
        eta = (total - done) / speed if total > done and speed > 0 else 0.0
        speed_text = "%.1f MB/s" % (speed / 1048576.0) if speed >= 1048576 else "%.0f KB/s" % (speed / 1024.0)
        eta_text = "%.0f 秒" % eta if eta < 60 else "%d 分 %d 秒" % (int(eta // 60), int(eta % 60))
        print("@@SETUP@@" + json.dumps(
            {"step": "models", "percent": round(pct, 1),
             "message": label + "：%.0f%%（%.1f/%.1f MB｜%s｜剩余约 %s）" % (
                 min(done / total, 1) * 100, done / 1048576.0, total / 1048576.0,
                 speed_text, eta_text),
             "level": "info"}, ensure_ascii=False), flush=True)

class Cb(ProgressCallback):
    def __init__(self, filename, file_size):
        super().__init__(filename, file_size)
        with state["lock"]:
            state["sizes"][filename] = int(file_size or 0)
            state["done"].setdefault(filename, 0)
        report()

    def update(self, size):
        with state["lock"]:
            state["done"][self.filename] = max(int(size or 0), state["done"].get(self.filename, 0))
        report()

    def end(self):
        with state["lock"]:
            state["done"][self.filename] = state["sizes"].get(self.filename, 0)
        report()

path = snapshot_download(sys.argv[1], progress_callbacks=[Cb])
print("MODEL_PATH:" + path)
"""
    rc, out = _run_long([exe, "-c", code, model_id, str(lo), str(hi), label, models_dir], env)
    ok = "MODEL_PATH:" in (out or "")
    if not ok:
        # 进度回调签名不兼容等情况下，退化为无进度的下载重试。
        emit("models", lo, f"{label}：改用兼容模式重试…", level="warn")
        fallback = (
            "import sys\n"
            "from modelscope.hub.snapshot_download import snapshot_download\n"
            "p = snapshot_download(sys.argv[1])\n"
            "print('MODEL_PATH:' + p)\n"
        )
        rc, out = run([exe, "-c", fallback, model_id], env=env, timeout=None)
        ok = "MODEL_PATH:" in (out or "")
    return ok


def _run_long(cmd, env):
    """运行长任务，实时转发 @@SETUP@@ 行。"""
    try:
        proc = subprocess.Popen(cmd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, encoding="utf-8", errors="replace", bufsize=1)
    except OSError as exc:
        return -1, str(exc)
    collected = []
    for line in proc.stdout or []:
        line = line.rstrip("\n")
        collected.append(line)
        if line.startswith(PREFIX_SETUP):
            print(line, flush=True)
    proc.wait()
    return proc.returncode, "\n".join(collected)


def run_install(args) -> int:
    exe = args.python or sys.executable
    env = setup_env(args)

    if not os.path.isfile(exe):
        emit("verify", 0, f"Python 解释器不存在：{exe}", level="error")
        return 2

    initial = build_report(args)
    if initial["ready"]:
        emit("verify", STEP_RANGES["verify"][1], "环境已通过自检，无需再次准备。")
        emit_report(initial)
        emit("done", 100, "环境已通过自检，无需再次准备。", ready=True)
        return 0

    ok = True
    emit("torch", 0, "开始环境准备…")
    if args.skip_torch:
        emit("torch", STEP_RANGES["torch"][1], "已跳过 PyTorch 安装")
    else:
        ok = install_torch(args, exe, env) and ok

    if args.skip_deps:
        emit("deps", STEP_RANGES["deps"][1], "已跳过依赖安装")
    else:
        ok = install_deps(args, exe, env) and ok

    if args.skip_browser:
        emit("browser", STEP_RANGES["browser"][1], "已跳过浏览器安装")
    else:
        ok = install_browser(args, exe, env) and ok

    if args.skip_models:
        emit("models", STEP_RANGES["models"][1], "已跳过模型下载")
    else:
        ok = install_models(args, exe, env) and ok

    start, end = STEP_RANGES["verify"]
    emit("verify", start, "正在自检…")
    report = build_report(args)
    report["ok"] = ok and report["ready"]
    emit("verify", end, "环境已就绪" if report["ready"] else "自检发现问题：" + "、".join(report["issues"]),
         level="info" if report["ready"] else "warn")
    emit_report(report)
    emit("done", 100, "环境准备完成" if report["ready"] else "环境准备结束（仍有未完成项）",
         level="info" if report["ready"] else "warn", ready=report["ready"])
    return 0 if report["ready"] else 1


def main() -> int:
    ap = argparse.ArgumentParser(description="抖音知识库环境检测与安装")
    ap.add_argument("--check", action="store_true", help="只检测，输出 @@ENV@@ 报告")
    ap.add_argument("--install", action="store_true", help="按需安装并输出进度")
    ap.add_argument("--python", default=None, help="目标 Python 解释器（默认当前解释器）")
    ap.add_argument("--device", default="cpu", choices=["cpu", "gpu"], help="PyTorch 版本选择")
    ap.add_argument("--models-dir", default="", help="ModelScope 缓存根目录")
    ap.add_argument("--browsers-dir", default="", help="Playwright 浏览器存放目录")
    ap.add_argument("--ffmpeg", default="", help="ffmpeg 可执行文件路径（提示用）")
    ap.add_argument("--skip-torch", action="store_true")
    ap.add_argument("--skip-deps", action="store_true")
    ap.add_argument("--skip-browser", action="store_true")
    ap.add_argument("--skip-models", action="store_true")
    args = ap.parse_args()

    if args.install:
        return run_install(args)

    emit_report(build_report(args))
    return 0


if __name__ == "__main__":
    sys.exit(main())