"""一次性 FunASR 转写（命令行）。

用法:
    python transcribe_funasr.py <audio> [-o <输出基名>] [--device auto|cpu|cuda:0] [--dtype fp32]

输出：<基名>.txt（转写稿）、<基名>.srt / <基名>.json（分句，若模型返回）。
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import asr_core  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("audio")
    ap.add_argument("-o", "--output", default=None)
    ap.add_argument("--device", default="auto", help="auto / cpu / cuda:0")
    ap.add_argument("--dtype", default="fp32")
    ap.add_argument("--model", default=None)
    ap.add_argument("--vad", default=None)
    ap.add_argument("--punc", default=None)
    args = ap.parse_args()

    base = args.output or os.path.splitext(args.audio)[0]
    device = asr_core.resolve_device(args.device)
    print(f"device: {asr_core.device_summary(device)}", flush=True)

    model = asr_core.load_model(
        device=device, dtype=args.dtype,
        model=args.model, vad=args.vad, punc=args.punc,
    )
    txt, chars, elapsed = asr_core.transcribe(model, args.audio, base)
    print(f"done: {txt} ({chars} 字符, {elapsed:.1f}s)", flush=True)


if __name__ == "__main__":
    main()