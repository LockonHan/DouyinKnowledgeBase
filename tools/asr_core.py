"""FunASR 转写核心：模型解析、加载与转写，供一次性脚本与常驻服务共用。"""

from __future__ import annotations

import json
import os
import time

DEFAULT_MODEL = os.environ.get("DOUKB_MODEL") or (
    "iic/speech_paraformer-large-vad-punc_asr_nat-zh-cn-16k-common-vocab8404-pytorch"
)
DEFAULT_VAD = os.environ.get("DOUKB_VAD") or "iic/speech_fsmn_vad_zh-cn-16k-common-pytorch"
DEFAULT_PUNC = os.environ.get("DOUKB_PUNC") or "iic/punc_ct-transformer_cn-en-common-vocab471067-large"


def cuda_available() -> bool:
    try:
        import torch
        return bool(torch.cuda.is_available())
    except Exception:
        return False


def resolve_device(requested: str) -> str:
    """把 auto / cuda / cpu 解析为具体设备；请求 GPU 但不可用时自动回落 CPU。"""
    requested = (requested or "auto").strip().lower()
    if requested in ("", "auto"):
        return "cuda:0" if cuda_available() else "cpu"
    if requested.startswith("cuda") and not cuda_available():
        return "cpu"
    return requested


def device_summary(device: str) -> str:
    if device.startswith("cuda"):
        try:
            import torch
            return f"{device} ({torch.cuda.get_device_name(0)})"
        except Exception:
            return device
    return device


def load_model(device, dtype="fp32", model=None, vad=None, punc=None, log=print):
    """加载 ASR + VAD + 标点模型，返回 AutoModel 实例。"""
    from funasr import AutoModel

    model = model or DEFAULT_MODEL
    log(f"loading FunASR: model={model} device={device} dtype={dtype}")
    t0 = time.time()
    instance = AutoModel(
        model=model,
        vad_model=vad or DEFAULT_VAD,
        punc_model=punc or DEFAULT_PUNC,
        device=device,
        dtype=dtype,
        disable_update=True,
        disable_pbar=True,
    )
    log(f"model loaded in {time.time() - t0:.1f}s")
    return instance


def _fmt_ts(sec: float) -> str:
    h = int(sec // 3600)
    m = int(sec % 3600 // 60)
    s = int(sec % 60)
    ms = int((sec - int(sec)) * 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def transcribe(model, audio: str, out_base: str, log=print):
    """转写单个音频，写出 .txt / .srt / .json，返回 (txt 路径, 字符数, 耗时秒)。"""
    t0 = time.time()
    res = model.generate(input=audio, batch_size_s=300, is_final=True)
    elapsed = time.time() - t0
    if not res:
        raise RuntimeError("FunASR 没有返回结果")

    item = res[0]
    text = (item.get("text") or "").replace(" ", "")
    if not text.strip():
        raise RuntimeError("FunASR 返回了空文本")

    folder = os.path.dirname(os.path.abspath(out_base))
    if folder:
        os.makedirs(folder, exist_ok=True)
    txt_path = out_base + ".txt"
    with open(txt_path, "w", encoding="utf-8") as fh:
        fh.write(text.strip() + "\n")

    sentences = item.get("sentence_info") or []
    if sentences:
        lines = []
        for i, s in enumerate(sentences, 1):
            st, en, t = s.get("start", 0), s.get("end", 0), s.get("text", "")
            lines.append(f"{i}\n{_fmt_ts(st / 1000)} --> {_fmt_ts(en / 1000)}\n{t}\n")
        with open(out_base + ".srt", "w", encoding="utf-8") as fh:
            fh.write("\n".join(lines))
        with open(out_base + ".json", "w", encoding="utf-8") as fh:
            json.dump({"sentences": sentences}, fh, ensure_ascii=False, indent=1)

    log(f"chars: {len(text)}")
    return txt_path, len(text), elapsed