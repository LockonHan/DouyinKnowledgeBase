"""常驻转写服务客户端：发现本机服务并发送转写请求。"""

from __future__ import annotations

import json
import os
import urllib.request

SERVER_INFO = "asr-server.json"


def _post(url: str, payload: dict, token: str, timeout: float):
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        url, data=body,
        headers={"Content-Type": "application/json; charset=utf-8", "X-DouKB-Token": token},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def discover(runtime_dir: str):
    """读取 asr-server.json 并探活；不可用时返回 None。"""
    path = os.path.join(runtime_dir, SERVER_INFO)
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as fh:
            info = json.load(fh)
    except (OSError, ValueError):
        return None

    host, port = info.get("host"), info.get("port")
    if not host or not port:
        return None
    try:
        req = urllib.request.Request(
            f"http://{host}:{port}/health",
            headers={"X-DouKB-Token": info.get("token", "")},
        )
        with urllib.request.urlopen(req, timeout=5) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        if data.get("status") == "ok":
            info.update(device=data.get("device"), model=data.get("model"))
            return info
    except Exception:
        return None
    return None


def transcribe(runtime_dir: str, audio: str, output: str, timeout: float = 3600):
    """请求常驻服务转写；返回 {"output","chars","elapsed"}。失败抛 RuntimeError。"""
    info = discover(runtime_dir)
    if not info:
        raise RuntimeError("未发现可用的常驻转写服务")
    data = _post(
        f"http://{info['host']}:{info['port']}/transcribe",
        {"audio": audio, "output": output},
        info.get("token", ""),
        timeout,
    )
    if data.get("status") != "ok":
        raise RuntimeError(data.get("message") or "常驻服务转写失败")
    return data


def shutdown(runtime_dir: str, timeout: float = 10) -> bool:
    info = discover(runtime_dir)
    if not info:
        return False
    try:
        _post(f"http://{info['host']}:{info['port']}/shutdown", {}, info.get("token", ""), timeout)
        return True
    except Exception:
        return False