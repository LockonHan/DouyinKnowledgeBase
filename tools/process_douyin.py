"""抖音分享链接 -> 下载视频 -> 提取音频 -> 本地 FunASR 转写。

用法:
    python process_douyin.py --share "分享文案或链接" --data-dir <数据目录>
        [--cookies <cookies.txt>] [--device cuda:0]
        [--ytdlp <yt-dlp 可执行文件>] [--ffmpeg <ffmpeg 可执行文件>]

下载默认走无头浏览器方案（tools/douyin_download.py），用于绕过抖音网页接口的
签名校验；若浏览器方案失败且本机存在 yt-dlp，则回退到 yt-dlp。

进度通过 stdout 上以 @@PROG@@ 为前缀的 JSON 行输出，供宿主程序解析：
    @@PROG@@ {"stage": "download", "percent": 42, "message": "..."}
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys

import douyin_download

PREFIX = "@@PROG@@"
PERCENT_RE = re.compile(r"\[download\]\s+(\d+(?:\.\d+)?)%")


def emit(**fields):
    payload = {"stage": "info", "percent": -1, "message": ""}
    payload.update(fields)
    print(PREFIX + json.dumps(payload, ensure_ascii=False), flush=True)


def fail(message):
    emit(stage="error", message=message)
    sys.exit(1)


def which(name, override):
    if override:
        return override
    found = shutil.which(name)
    if not found:
        fail(f"未找到 {name}，请安装并加入 PATH，或在设置中指定路径。")
    return found


def stream(cmd):
    """运行子进程并逐行回传输出。首行 line 为 None，便于调用方先取到 proc。"""
    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    yield proc, None
    for line in proc.stdout:
        yield proc, line.rstrip("\r\n")
    proc.wait()


def download_ytdlp(ytdlp, url, out_template, cookies, videos_dir):
    cmd = [
        ytdlp, "--no-playlist", "--newline", "--no-warnings",
        "--no-simulate", "--print", "after_move:filepath",
        "-f", "bv*+ba/b", "--merge-output-format", "mp4",
        "-o", out_template,
    ]
    if cookies and os.path.isfile(cookies):
        # yt-dlp 会把 cookie jar 回写到 --cookies 指定的文件，这里复制一份临时副本，
        # 避免覆盖用户导出的 cookies.txt。
        tmp_cookies = os.path.join(os.path.dirname(os.path.abspath(cookies)), "cookies.ytdlp.tmp")
        shutil.copyfile(cookies, tmp_cookies)
        cmd += ["--cookies", tmp_cookies]
    cmd.append(url)

    emit(stage="download", percent=0, message="正在用 yt-dlp 解析视频地址…")
    printed_path = None
    for proc, line in stream(cmd):
        if not line:
            continue
        m = PERCENT_RE.search(line)
        if m:
            emit(stage="download", percent=float(m.group(1)), message=line[:160])
        elif line:
            if os.path.isabs(line) and os.path.isfile(line):
                printed_path = line
            emit(stage="download", message=line[:200])

    if proc.returncode != 0:
        fail(f"下载失败（yt-dlp 退出码 {proc.returncode}）。")

    if printed_path and os.path.isfile(printed_path):
        return printed_path, None

    if not os.path.isdir(videos_dir):
        fail("下载目录不存在。")
    files = [os.path.join(videos_dir, f) for f in os.listdir(videos_dir)]
    media = [f for f in files if os.path.splitext(f)[1].lower() in (".mp4", ".mkv", ".webm", ".m4a")]
    if not media:
        fail("未找到下载的视频文件。")
    newest = max(media, key=os.path.getmtime)
    return newest, os.path.splitext(os.path.basename(newest))[0]


def download(url, videos_dir, cookies, ytdlp_override):
    """优先用无头浏览器直取带音轨的 MP4 直链，失败再回退 yt-dlp。"""
    try:
        return douyin_download.download(url, videos_dir, emit, cookies=cookies)
    except douyin_download.DependencyError as exc:
        fail(f"下载环境不完整：{exc}")
    except douyin_download.DownloadError as exc:
        emit(stage="download", percent=-1,
             message=f"浏览器下载未成功：{exc} 尝试回退到 yt-dlp…")

    ytdlp = ytdlp_override or shutil.which("yt-dlp")
    if not ytdlp:
        fail("浏览器下载失败，且本机未安装 yt-dlp，无法回退。请检查网络后重试。")
    out_template = os.path.join(videos_dir, "%(title)s.%(ext)s")
    return download_ytdlp(ytdlp, url, out_template, cookies, videos_dir)


def extract_audio(ffmpeg, video, wav):
    emit(stage="audio", percent=-1, message="正在提取音频（16kHz 单声道）…")
    cmd = [ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
           "-i", video, "-vn", "-ac", "1", "-ar", "16000", "-f", "wav", wav]
    for proc, line in stream(cmd):
        if line:
            emit(stage="audio", message=line[:200])
    if proc.returncode != 0 or not os.path.isfile(wav):
        fail("音频提取失败（ffmpeg）。")
    return wav


def transcribe(python_exe, script, wav, out_base, device):
    emit(stage="transcribe", percent=-1, message="正在调用本地 FunASR 转写，首次加载模型较慢…")
    cmd = [python_exe, script, wav, "-o", out_base, "--device", device]
    for proc, line in stream(cmd):
        if line:
            emit(stage="transcribe", message=line[:200])
    txt = out_base + ".txt"
    if proc.returncode != 0 or not os.path.isfile(txt):
        fail("语音转写失败（FunASR）。")
    return txt


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--share", required=True)
    ap.add_argument("--data-dir", required=True)
    ap.add_argument("--cookies", default=None)
    ap.add_argument("--device", default="cuda:0")
    ap.add_argument("--ytdlp", default=None)
    ap.add_argument("--ffmpeg", default=None)
    ap.add_argument("--python", default=None, help="用于转写的 Python（默认与当前解释器相同）")
    args = ap.parse_args()

    data_dir = os.path.abspath(args.data_dir)
    videos_dir = os.path.join(data_dir, "videos")
    audio_dir = os.path.join(data_dir, "audio")
    transcripts_dir = os.path.join(data_dir, "transcripts")
    for d in (videos_dir, audio_dir, transcripts_dir):
        os.makedirs(d, exist_ok=True)

    emit(stage="parse", percent=-1, message="正在从分享文案中提取链接…")
    url = douyin_download.extract_url(args.share)
    if not url:
        fail("没有找到有效的抖音链接，请确认分享文案中包含 https://v.douyin.com/... 之类的地址。")
    emit(stage="parse", percent=100, message=f"已识别链接：{url}")

    ffmpeg = which("ffmpeg", args.ffmpeg)
    python_exe = args.python or sys.executable
    script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "transcribe_funasr.py")

    cookies = args.cookies or os.path.join(data_dir, "cookies.txt")

    video, forced_title = download(url, videos_dir, cookies, args.ytdlp)
    title = forced_title or os.path.splitext(os.path.basename(video))[0]
    emit(stage="download", percent=100, title=title, video=video,
         message=f"下载完成：{os.path.basename(video)}")

    wav = os.path.join(audio_dir, title + ".wav")
    extract_audio(ffmpeg, video, wav)
    emit(stage="audio", percent=100, audio=wav, message="音频提取完成")

    out_base = os.path.join(transcripts_dir, title)
    txt = transcribe(python_exe, script, wav, out_base, args.device)
    emit(stage="transcribe", percent=100, message=f"转写完成：{os.path.getsize(txt)} 字节")

    emit(stage="done", percent=100, title=title, video=video, audio=wav,
         transcript=txt, message="全部完成")


if __name__ == "__main__":
    main()