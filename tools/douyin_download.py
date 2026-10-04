"""抖音视频下载：用无头浏览器绕过网页接口签名，抓取带音轨的 MP4 直链。

抖音网页版接口 aweme/v1/web/aweme/detail 需要 a_bogus / msToken 签名，
直接用 HTTP 请求会返回 403。这里改用 Playwright 驱动的无头 Chromium 打开
视频页，让页面自身的 JS 完成签名并发起请求，再从响应里取出
video.play_addr 直链（该直链同时包含视频和音频），最后用 curl_cffi 下载。

对外主要接口：
    download(share_text_or_url, videos_dir, emit, cookies=None) -> (path, title)
"""

from __future__ import annotations

import os
import re
import time

UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
)
DETAIL_API = "aweme/v1/web/aweme/detail"
VIDEO_PAGE = "https://www.douyin.com/video/{vid}"

_URL_RE = re.compile(r"https?://[^\s,\u3001\uff0c\uff1b;()\u3010\u3011\[\]\"'<>]+")
_VIDEO_ID_RE = re.compile(r"/(?:video|note)/(\d{6,})")
_BAD_CHARS_RE = re.compile(r'[\\/:*?"<>|\r\n\t]')


class DownloadError(RuntimeError):
    """下载环节的可预期失败，交由上层转换为友好提示。"""


class DependencyError(DownloadError):
    """运行环境缺少依赖（例如 Playwright 未安装）；回退 yt-dlp 也无法解决。"""


def extract_url(text):
    """从分享文案里取出第一个链接（优先 douyin.com 域名）。"""
    candidates = _URL_RE.findall(text or "")
    for url in candidates:
        if "douyin.com" in url:
            return url.rstrip("/") + "/"
    if candidates:
        return candidates[0].rstrip("/") + "/"
    return None


def sanitize_name(name, fallback):
    name = _BAD_CHARS_RE.sub(" ", name or "")
    name = re.sub(r"\s+", " ", name).strip().strip(" .")
    if not name:
        name = fallback
    return name[:120]


def _resolve_final_url(url, timeout=25):
    """跟随短链跳转，返回最终 URL。"""
    try:
        from curl_cffi import requests as creq
        resp = creq.get(url, impersonate="chrome", headers={"User-Agent": UA},
                        timeout=timeout, allow_redirects=True)
        return resp.url or url
    except ImportError:
        import urllib.request
        req = urllib.request.Request(url, headers={"User-Agent": UA})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.geturl() or url


def resolve_video_id(url):
    """把分享链接或视频页链接统一解析成 (aweme_id, 用于访问的 URL)。"""
    m = _VIDEO_ID_RE.search(url)
    if m:
        return m.group(1), VIDEO_PAGE.format(vid=m.group(1))
    try:
        final = _resolve_final_url(url)
    except Exception as exc:  # 网络异常
        raise DownloadError(f"打开分享链接失败：{exc}") from exc
    m = _VIDEO_ID_RE.search(final)
    if not m:
        raise DownloadError(f"无法从链接中解析出视频 ID：{final}")
    return m.group(1), VIDEO_PAGE.format(vid=m.group(1))


def _load_netscape_cookies(path):
    """把 Netscape 格式 cookies.txt 转成 Playwright 的 cookie 列表。"""
    cookies = []
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) < 7:
                continue
            domain, _flag, cpath, secure, expires, name, value = parts[:7]
            item = {"name": name, "value": value, "path": cpath or "/",
                    "domain": domain, "secure": secure.upper() == "TRUE"}
            if expires.isdigit() and int(expires) > 0:
                item["expires"] = int(expires)
            cookies.append(item)
    return cookies


def fetch_detail(video_url, cookies_path, emit, timeout_ms=60000, settle_ms=9000):
    """用无头 Chromium 打开视频页，截获官方 detail 接口返回的 JSON。"""
    try:
        from playwright.sync_api import sync_playwright
    except ImportError as exc:
        raise DependencyError(
            "当前 Python 未安装 Playwright。请在“设置 → 视频下载与转写”中选择已安装 "
            "playwright 的 Python，或运行：pip install playwright && playwright install chromium"
        ) from exc

    captured = {"detail": None}

    with sync_playwright() as pw:
        try:
            browser = pw.chromium.launch(
                headless=True,
                args=["--disable-blink-features=AutomationControlled", "--no-sandbox"],
            )
        except Exception as exc:
            raise DependencyError(
                "启动无头浏览器失败，请确认已执行 playwright install chromium。"
                f"原始错误：{exc}"
            ) from exc

        try:
            context = browser.new_context(user_agent=UA, locale="zh-CN",
                                          viewport={"width": 1400, "height": 900})
            if cookies_path and os.path.isfile(cookies_path):
                try:
                    cookies = _load_netscape_cookies(cookies_path)
                    if cookies:
                        context.add_cookies(cookies)
                except Exception:
                    pass

            page = context.new_page()

            def on_response(resp):
                if captured["detail"] is not None or DETAIL_API not in resp.url:
                    return
                try:
                    data = resp.json()
                except Exception:
                    return
                if isinstance(data, dict) and data.get("aweme_detail"):
                    captured["detail"] = data

            page.on("response", on_response)
            emit(stage="download", percent=-1, message="正在用无头浏览器打开抖音视频页…")
            page.goto(video_url, wait_until="domcontentloaded", timeout=timeout_ms)

            deadline = time.time() + settle_ms / 1000.0
            while time.time() < deadline and captured["detail"] is None:
                page.wait_for_timeout(400)
        finally:
            browser.close()

    if captured["detail"] is None:
        raise DownloadError("浏览器未能获取视频信息（可能被抖音风控拦截），请稍后重试。")
    return captured["detail"]


def _stream_download(url, dest, on_progress, timeout=120):
    """下载单个直链到 dest，返回读取到的字节数。"""
    headers = {"User-Agent": UA, "Referer": "https://www.douyin.com/"}
    try:
        from curl_cffi import requests as creq
    except ImportError:
        creq = None

    if creq is not None:
        resp = creq.get(url, impersonate="chrome", headers=headers, timeout=timeout,
                        stream=True, allow_redirects=True)
        if resp.status_code != 200:
            raise DownloadError(f"HTTP {resp.status_code}")
        total = int(resp.headers.get("Content-Length") or 0)
        got = 0
        with open(dest, "wb") as fh:
            for chunk in resp.iter_content(1 << 16):
                if not chunk:
                    continue
                fh.write(chunk)
                got += len(chunk)
                on_progress(got, total)
        return got

    import urllib.request
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        total = int(resp.headers.get("Content-Length") or 0)
        got = 0
        with open(dest, "wb") as fh:
            while True:
                chunk = resp.read(1 << 16)
                if not chunk:
                    break
                fh.write(chunk)
                got += len(chunk)
                on_progress(got, total)
        return got


def download(share_text_or_url, videos_dir, emit, cookies=None, timeout_ms=60000):
    """完整下载流程，返回 (本地 mp4 路径, 标题)。"""
    url = extract_url(share_text_or_url) or share_text_or_url
    video_id, video_url = resolve_video_id(url)
    emit(stage="download", percent=-1, message=f"已识别视频 ID：{video_id}")

    detail = fetch_detail(video_url, cookies, emit, timeout_ms=timeout_ms)
    aweme = detail.get("aweme_detail") or {}
    title = sanitize_name(aweme.get("desc"), video_id)
    video = aweme.get("video") or {}
    url_list = [u for u in ((video.get("play_addr") or {}).get("url_list") or [])
                if isinstance(u, str) and u.startswith("http")]

    if not url_list:
        bit_rates = sorted((video.get("bit_rate") or []),
                           key=lambda b: b.get("bit_rate") or 0, reverse=True)
        for br in bit_rates:
            for u in ((br.get("play_addr") or {}).get("url_list") or []):
                if isinstance(u, str) and u.startswith("http"):
                    url_list.append(u)
    if not url_list:
        raise DownloadError("视频信息里没有可用的下载地址。")

    os.makedirs(videos_dir, exist_ok=True)
    dest = os.path.join(videos_dir, title + ".mp4")

    last_error = None
    for candidate in url_list:
        state = {"got": 0, "total": 0, "tick": 0.0, "name": title}

        def on_progress(got, total, _state=state):
            _state["got"] = got
            _state["total"] = total
            now = time.time()
            if now - _state["tick"] < 0.4 and got != total:
                return
            _state["tick"] = now
            percent = round(got * 100.0 / total, 1) if total else -1
            emit(stage="download", percent=percent,
                 message=f"下载中 {got // 1024} / {total // 1024 if total else '?'} KB")

        try:
            _stream_download(candidate, dest, on_progress)
            if os.path.getsize(dest) < 10 * 1024:
                raise DownloadError("下载内容过小，可能不是有效视频。")
            return dest, title
        except Exception as exc:
            last_error = exc
            continue

    raise DownloadError(f"视频直链下载失败：{last_error}")