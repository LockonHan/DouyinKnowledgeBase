"""常驻转写服务：模型只加载一次，之后通过本地 HTTP 接收转写请求。

由应用启动（设置里的“转写常驻加速”开关）。启动就绪后会把
<runtime-dir>/asr-server.json 写出来，供 process_douyin.py 发现并连接：

    {"host": "127.0.0.1", "port": 51234, "token": "...", "pid": 1234,
     "device": "cpu", "model": "...", "started": 1690000000}

接口（都带 X-DouKB-Token 头）：
    GET  /health                -> {"status":"ok", ...}
    POST /transcribe            -> {"audio":..., "output":...} 返回转写结果
    POST /shutdown              -> 退出服务
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import signal
import socket
import subprocess
import sys
import threading
import time
import urllib.request
import secrets
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import asr_core  # noqa: E402

LOCK = threading.Lock()
STATE = {"model": None, "device": "cpu", "model_id": "", "started": 0.0, "token": ""}
SERVER_INFO = "asr-server.json"
SERVER_PIDS = "asr-server.pids"


def emit(**fields):
    payload = {"status": "info", "message": ""}
    payload.update(fields)
    print("@@ASR@@" + json.dumps(payload, ensure_ascii=False), flush=True)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # 静默默认访问日志
        return

    def _send(self, code: int, payload: dict):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_json(self) -> dict:
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return {}
        return json.loads(self.rfile.read(length).decode("utf-8"))

    def _authorized(self) -> bool:
        return self.headers.get("X-DouKB-Token") == STATE["token"]

    def do_GET(self):
        if self.path.split("?")[0] != "/health":
            self._send(404, {"status": "error", "message": "not found"})
            return
        self._send(200, {
            "status": "ok",
            "device": STATE["device"],
            "model": STATE["model_id"],
            "started": STATE["started"],
            "pid": os.getpid(),
        })

    def do_POST(self):
        if not self._authorized():
            self._send(403, {"status": "error", "message": "token 校验失败"})
            return

        path = self.path.split("?")[0]
        try:
            data = self._read_json()
        except Exception as exc:
            self._send(400, {"status": "error", "message": f"请求体解析失败：{exc}"})
            return

        if path == "/shutdown":
            self._send(200, {"status": "ok", "message": "shutting down"})
            threading.Thread(target=self.server.shutdown, daemon=True).start()
            return

        if path != "/transcribe":
            self._send(404, {"status": "error", "message": "not found"})
            return

        audio = data.get("audio")
        output = data.get("output")
        if not audio or not output:
            self._send(400, {"status": "error", "message": "缺少 audio / output 参数"})
            return
        if not os.path.isfile(audio):
            self._send(400, {"status": "error", "message": f"音频不存在：{audio}"})
            return

        try:
            with LOCK:  # 模型不是线程安全的，串行处理
                emit(stage="transcribe", message=f"开始转写 {os.path.basename(audio)}")
                txt, chars, elapsed = asr_core.transcribe(
                    STATE["model"], audio, output,
                    log=lambda msg: emit(stage="transcribe", message=msg),
                )
            self._send(200, {"status": "ok", "output": txt, "chars": chars, "elapsed": elapsed})
        except Exception as exc:
            self._send(500, {"status": "error", "message": str(exc)})


def _is_python_process(pid: int) -> bool:
    """确认该 PID 现在是 python 进程（防止 PID 被复用后误杀无关程序）。"""
    try:
        out = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, timeout=15,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        ).stdout
    except Exception:
        return False
    return "python" in (out or "").lower()


def _reap_stale_servers(pid_file: str) -> None:
    """回收上一次留下的常驻服务，避免多个服务同时把模型读进内存。

    发现文件可能被清理掉，所以另存一份 PID 清单做兜底。
    """
    try:
        with open(pid_file, "r", encoding="utf-8") as fh:
            pids = [int(x) for x in fh.read().split() if x.strip().isdigit()]
    except (OSError, ValueError):
        pids = []

    for pid in pids:
        if pid == os.getpid() or not _is_python_process(pid):
            continue
        try:
            os.kill(pid, signal.SIGTERM)
            emit(stage="stopped", message=f"已结束后台残留的常驻转写服务（PID {pid}）")
        except OSError:
            pass

    try:
        with open(pid_file, "w", encoding="utf-8") as fh:
            fh.write("\n".join(str(x) for x in pids[-8:]) + ("\n" if pids else ""))
            fh.write(str(os.getpid()) + "\n")
    except OSError:
        pass


def _stop_existing_server(info_path: str) -> None:
    """启动前先关掉上一次残留的常驻服务，避免多个服务同时把模型读进内存。"""
    try:
        with open(info_path, "r", encoding="utf-8") as fh:
            info = json.load(fh)
    except (OSError, ValueError):
        return
    host, port = info.get("host"), info.get("port")
    if not host or not port or info.get("pid") == os.getpid():
        return
    request = urllib.request.Request(
        f"http://{host}:{port}/shutdown", data=b"{}",
        headers={"Content-Type": "application/json; charset=utf-8",
                 "X-DouKB-Token": info.get("token", "")})
    try:
        with urllib.request.urlopen(request, timeout=5):
            pass
        emit(stage="stopped", message="已关闭上一次残留的常驻转写服务")
    except Exception:
        return


def _parent_alive(pid: int) -> bool:
    """在 Windows 上判断宿主进程是否仍存在。"""
    if not pid:
        return True
    try:
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            return False
        kernel32.CloseHandle(handle)
        return True
    except Exception:
        return True


def watch_parent(pid: int, stop: threading.Event):
    """宿主进程消失后关闭服务。"""
    while not stop.wait(5):
        if not _parent_alive(pid):
            emit(stage="stopped", message="宿主进程已退出，常驻转写服务自动结束")
            os._exit(0)


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runtime-dir", required=True)
    ap.add_argument("--device", default="auto")
    ap.add_argument("--dtype", default="fp32")
    ap.add_argument("--model", default=None)
    ap.add_argument("--vad", default=None)
    ap.add_argument("--punc", default=None)
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--parent-pid", type=int, default=0,
                    help="宿主进程 PID；宿主退出后本服务自动结束，避免残留")
    args = ap.parse_args()

    os.makedirs(args.runtime_dir, exist_ok=True)
    info_path = os.path.join(args.runtime_dir, SERVER_INFO)

    # 先关闭上一次残留的常驻服务（同一时间只允许一个，避免重复占用内存）。
    _stop_existing_server(info_path)
    _reap_stale_servers(os.path.join(args.runtime_dir, SERVER_PIDS))

    # 清理上一次运行残留的发现文件，避免客户端连接到已经没有的旧服务。
    for stale in (info_path, info_path + ".error", info_path + ".tmp"):
        try:
            if os.path.exists(stale):
                os.remove(stale)
        except OSError:
            pass

    if args.parent_pid:
        threading.Thread(target=watch_parent, args=(args.parent_pid, threading.Event()), daemon=True).start()

    device = asr_core.resolve_device(args.device)
    STATE["device"] = device
    STATE["token"] = secrets.token_urlsafe(24)
    STATE["started"] = time.time()

    emit(stage="load", message=f"正在加载转写模型（设备：{asr_core.device_summary(device)}）")
    try:
        STATE["model"] = asr_core.load_model(
            device=device, dtype=args.dtype,
            model=args.model, vad=args.vad, punc=args.punc,
            log=lambda msg: emit(stage="load", message=msg),
        )
    except Exception as exc:
        emit(stage="error", message=f"模型加载失败：{exc}")
        with open(info_path + ".error", "w", encoding="utf-8") as fh:
            fh.write(str(exc))
        sys.exit(1)

    STATE["model_id"] = args.model or asr_core.DEFAULT_MODEL
    port = args.port or free_port()
    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    server.daemon_threads = True

    info = {
        "host": "127.0.0.1",
        "port": port,
        "token": STATE["token"],
        "pid": os.getpid(),
        "device": device,
        "model": STATE["model_id"],
        "started": STATE["started"],
    }
    tmp = info_path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(info, fh, ensure_ascii=False)
    os.replace(tmp, info_path)

    emit(stage="ready", port=port, device=device, pid=os.getpid(),
         message=f"常驻转写服务就绪：127.0.0.1:{port}（{asr_core.device_summary(device)}）")


    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        pass
    finally:
        try:
            if os.path.exists(info_path):
                os.remove(info_path)
        except OSError:
            pass
        emit(stage="stopped", message="常驻转写服务已停止")


if __name__ == "__main__":
    main()