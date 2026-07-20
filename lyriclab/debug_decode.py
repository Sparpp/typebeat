#!/usr/bin/env python
"""Greedy CTC decode of a time range of the (separated) vocal stem.
Shows what the MMS_FA model 'hears', second by second, to diagnose
forced-alignment failures.

usage: debug_decode.py <wav16k> [start_s] [end_s]
"""
import sys
import time
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
import torchaudio

from align_lyrics import FRAME_SEC, compute_emissions, frame_rms, voiced_mask

wav_path = Path(sys.argv[1])
t_start = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0
t_end = float(sys.argv[3]) if len(sys.argv) > 3 else 1e9

torch.set_num_threads(8)
data, sr = sf.read(wav_path, dtype="float32")
assert sr == 16000
wav = torch.from_numpy(data).unsqueeze(0)

bundle = torchaudio.pipelines.MMS_FA
model = bundle.get_model(with_star=False).eval()
try:
    dic = bundle.get_dict(star=None)
except TypeError:
    dic = bundle.get_dict()
id2ch = {v: k for k, v in dic.items()}

log_probs = compute_emissions(model, wav, "cpu", 30.0, 4.0)
rms = frame_rms(wav)
voiced = voiced_mask(rms)

ids = log_probs.argmax(dim=-1).tolist()
f0 = int(t_start / FRAME_SEC)
f1 = min(len(ids), int(t_end / FRAME_SEC))

# collapse per 1-second bucket for readability
out = []
sec = None
buf = []
prev = -1
for f in range(f0, f1):
    s = int(f * FRAME_SEC)
    if s != sec:
        if sec is not None:
            v = "V" if voiced[max(0, sec * 50):(sec + 1) * 50].mean() > 0.3 else "."
            out.append(f"{sec:4d}s {v} |{''.join(buf)}")
        sec, buf, prev = s, [], -1
    i = ids[f]
    if i != prev and i != 0:
        buf.append(id2ch.get(i, "?"))
    prev = i
if buf:
    v = "V" if voiced[max(0, sec * 50):(sec + 1) * 50].mean() > 0.3 else "."
    out.append(f"{sec:4d}s {v} |{''.join(buf)}")
print("\n".join(out))
