import sys, os, time, json, argparse
from funasr import AutoModel

MODEL_ID = "iic/speech_paraformer-large-vad-punc_asr_nat-zh-cn-16k-common-vocab8404-pytorch"
VAD_ID = "iic/speech_fsmn_vad_zh-cn-16k-common-pytorch"
PUNC_ID = "iic/punc_ct-transformer_cn-en-common-vocab471067-large"

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("audio")
    ap.add_argument("-o", "--output", default=None)
    ap.add_argument("--device", default="cuda:0")
    args = ap.parse_args()

    base = args.output or os.path.splitext(args.audio)[0]
    t0 = time.time()
    print("loading FunASR Paraformer-large (ASR+VAD+punc) ...", flush=True)
    model = AutoModel(
        model=MODEL_ID,
        vad_model=VAD_ID,
        punc_model=PUNC_ID,
        device=args.device,
        dtype="fp32",
        disable_update=True,
        disable_pbar=True,
    )
    print(f"model loaded in {time.time()-t0:.1f}s", flush=True)

    t1 = time.time()
    res = model.generate(input=args.audio, batch_size_s=300, is_final=True)
    print(f"generate done in {time.time()-t1:.1f}s", flush=True)

    if not res:
        print("no result!", flush=True)
        sys.exit(1)

    r = res[0]
    text = r.get("text", "").replace(" ", "")
    print(f"chars: {len(text)}", flush=True)
    with open(base + ".txt", "w", encoding="utf-8") as f:
        f.write(text.strip() + "\n")

    sentences = r.get("sentence_info") or []
    if sentences:
        srt = []
        for i, s in enumerate(sentences, 1):
            st, en, t = s.get("start", 0), s.get("end", 0), s.get("text", "")
            srt.append(f"{i}\n{fmt_ts(st/1000)} --> {fmt_ts(en/1000)}\n{t}\n")
        with open(base + ".srt", "w", encoding="utf-8") as f:
            f.write("\n".join(srt))
        with open(base + ".json", "w", encoding="utf-8") as f:
            json.dump({"sentences": sentences}, f, ensure_ascii=False, indent=1)
        print(f"srt segments: {len(sentences)}", flush=True)
    print("done", flush=True)

def fmt_ts(sec):
    h = int(sec // 3600); m = int(sec % 3600 // 60); s = int(sec % 60); ms = int((sec - int(sec)) * 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"

if __name__ == "__main__":
    main()