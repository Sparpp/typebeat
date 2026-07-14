#!/usr/bin/env python
"""
align_lyrics.py -- automatic word/syllable-level lyric timing for type!beat.

Given a song (mp3/wav/...) and its lyrics (plain text, or a line-level LRC),
produces:

  <stem>.lrc            line-level LRC (drop-in for the game's current LrcParser)
  <stem>.words.lrc      enhanced LRC with <mm:ss.xx> word tags
  <stem>.syllables.lrc  enhanced LRC with syllable-level tags (normalized text)
  <stem>.timing.json    rich word+syllable timings with confidence scores
  report.txt            QC report (vs reference times if available)

Pipeline:
  1. ffmpeg decode -> wav
  2. Demucs (htdemucs) two-stem separation -> isolated vocals   [cached]
  3. resample vocals to 16 kHz mono
  4. wav2vec2 CTC emissions via torchaudio MMS_FA, computed in overlapping
     chunks and stitched (full-song attention would blow up CPU RAM)
  5. torchaudio.functional.forced_align of normalized lyric characters,
     with '*' wildcards between lines absorbing unlisted vocals
  6. anchoring:
       --anchors ref   align each line inside its hand-stamped LRC window
       --anchors auto  global pass -> high-margin lines anchor a local
                       re-alignment of weak runs between them
       --anchors none  single global pass
     Lines whose acoustic evidence is ~zero (effects-heavy hooks etc.) fall
     back to char-proportional interpolation and are flagged "estimated".
  7. char spans -> syllables (pyphen + naive fallback) -> words -> lines;
     end times extended through sustained voiced audio (RMS gate)

Confidence: each word carries `score` = mean margin (0..1) between the
aligned char and the model's argmax at those frames. High = the model
actually hears this word here. `prob` = raw mean char probability.
"""

import os

os.environ.setdefault("OMP_NUM_THREADS", "8")
os.environ.setdefault("MKL_NUM_THREADS", "8")

import argparse
import json
import re
import subprocess
import sys
import time
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

if sys.stdout and hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

SAMPLE_RATE = 16000
FRAME_SAMPLES = 320          # wav2vec2 stride: 20 ms at 16 kHz
FRAME_SEC = FRAME_SAMPLES / SAMPLE_RATE

TS_RE = re.compile(r"\s*\[(\d+):(\d{1,2}(?:\.\d+)?)\]\s*")
VOWELS = set("aeiouy")

REF_SLACK_BEFORE_S = 0.75    # line window opens this much before its stamp
REF_SLACK_AFTER_S = 0.50     # ... and closes this much after the next stamp
DEAD_MARGIN = 0.08           # below this a line is considered evidence-free
ANCHOR_MARGIN = 0.25         # auto mode: lines above this anchor their region
MIN_ANCHOR_CHARS = 6


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


# --------------------------------------------------------------------------
# Lyrics parsing / normalization
# --------------------------------------------------------------------------

@dataclass
class Word:
    display: str                 # original text as shown to the player
    norm: str = ""               # normalized alignable chars (concat of tokens)
    tokens: list = field(default_factory=list)   # list[str] alignable tokens
    start_ms: int = 0
    end_ms: int = 0
    score: float = 0.0           # margin confidence 0..1
    prob: float = 0.0            # raw mean char probability
    untimed: bool = False
    syllables: list = field(default_factory=list)  # list[dict]


@dataclass
class Line:
    display: str
    words: list                  # list[Word]
    ref_ms: float | None = None  # hand-authored start time, if input was LRC
    start_ms: int = 0
    end_ms: int = 0
    estimated: bool = False      # True when timing was interpolated
    margin: float = 0.0


def parse_lyrics(path: Path):
    """Returns (lines, ref_end_ms). Accepts plain text or LRC-stamped lines."""
    lines: list[Line] = []
    ref_end_ms = None
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        s = raw.strip()
        if not s:
            continue
        ref = None
        m = TS_RE.match(s)
        while m:
            ref = (int(m.group(1)) * 60 + float(m.group(2))) * 1000.0
            s = s[m.end():].strip()
            m = TS_RE.match(s)
        if not s:
            if ref is not None:
                ref_end_ms = ref   # bare timestamp = end marker
            continue
        if re.fullmatch(r"\[[^\]]*\]", s):
            continue  # section header like [Chorus]
        words = [Word(display=w) for w in s.split()]
        lines.append(Line(display=s, words=words, ref_ms=ref))
    return lines, ref_end_ms


def normalize_word(display: str, dict_chars: set, num2words_fn) -> list:
    """Display word -> list of alignable tokens (letters/apostrophes only)."""
    w = unicodedata.normalize("NFKC", display).lower()
    w = w.replace("’", "'").replace("‘", "'").replace("`", "'")
    tokens = []
    for part in re.split(r"(\d+)", w):
        if not part:
            continue
        if part.isdigit():
            spelled = num2words_fn(int(part))
            tokens.extend(t for t in re.split(r"[\s,\-]+", spelled) if t)
        else:
            tokens.append(part)
    out = []
    for t in tokens:
        t = "".join(ch for ch in t if ch in dict_chars)
        if t:
            out.append(t)
    return out


# --------------------------------------------------------------------------
# Syllabification
# --------------------------------------------------------------------------

def naive_syllables(tok: str) -> list:
    """Vowel-group splitter fallback. Keeps concat(parts) == tok."""
    letters = tok
    groups = []  # (start, end) of vowel runs
    i = 0
    while i < len(letters):
        if letters[i] in VOWELS:
            j = i
            while j < len(letters) and letters[j] in VOWELS:
                j += 1
            groups.append((i, j))
            i = j
        else:
            i += 1
    if len(groups) <= 1:
        return [tok]
    bounds = []
    for k in range(1, len(groups)):
        prev_end = groups[k - 1][1]
        cur_start = groups[k][0]
        if cur_start <= prev_end:
            bounds.append(cur_start)
        elif cur_start - prev_end == 1:
            bounds.append(prev_end)      # single consonant opens next syllable
        else:
            bounds.append(cur_start - 1)  # cluster: last consonant moves right
    parts = []
    prev = 0
    for b in bounds:
        b = max(prev + 1, min(b, len(tok) - 1))
        parts.append(tok[prev:b])
        prev = b
    parts.append(tok[prev:])
    return [p for p in parts if p] or [tok]


def syllabify_token(tok: str, pyphen_dic) -> list:
    parts = None
    if pyphen_dic is not None:
        cand = pyphen_dic.inserted(tok).split("-")
        cand = [p for p in cand if p]
        if cand and "".join(cand) == tok and len(cand) > 1:
            parts = cand
    if parts is None:
        parts = naive_syllables(tok)
    if "".join(parts) != tok:
        parts = [tok]
    return parts


# --------------------------------------------------------------------------
# Audio helpers (ffmpeg CLI)
# --------------------------------------------------------------------------

def run_ffmpeg(args: list) -> None:
    subprocess.run(["ffmpeg", "-y", "-v", "error"] + args, check=True)


def ensure_wav(src: Path, dst: Path, rate: int, channels: int) -> None:
    if dst.exists() and dst.stat().st_mtime >= src.stat().st_mtime:
        return
    dst.parent.mkdir(parents=True, exist_ok=True)
    run_ffmpeg(["-i", str(src), "-ac", str(channels), "-ar", str(rate),
                "-c:a", "pcm_s16le", str(dst)])


def separate_vocals(song_wav: Path, work: Path, model: str, device: str,
                    threads: int) -> Path:
    out = work / model / song_wav.stem / "vocals.wav"
    if out.exists() and out.stat().st_mtime >= song_wav.stat().st_mtime:
        log(f"separation: cached ({out})")
        return out
    log(f"separation: running demucs ({model}) on {device} ...")
    env = dict(os.environ)
    env["OMP_NUM_THREADS"] = str(threads)
    env["MKL_NUM_THREADS"] = str(threads)
    subprocess.run(
        [sys.executable, "-m", "demucs.separate", "--two-stems", "vocals",
         "-n", model, "-d", device, "-o", str(work), str(song_wav)],
        check=True, env=env)
    if not out.exists():
        raise RuntimeError(f"demucs did not produce {out}")
    return out


# --------------------------------------------------------------------------
# Emissions + forced alignment
# --------------------------------------------------------------------------

def compute_emissions(model, wav, device, window_s: float, context_s: float):
    """Chunked wav2vec2 forward, stitched so frame g covers samples ~[g*320, g*320+400)."""
    import torch

    n = wav.size(1)
    win = int(window_s * SAMPLE_RATE) // FRAME_SAMPLES * FRAME_SAMPLES
    ctx = int(context_s * SAMPLE_RATE) // FRAME_SAMPLES * FRAME_SAMPLES
    chunks = []
    pos = 0
    n_chunks = (n + win - 1) // win
    ci = 0
    with torch.inference_mode():
        while pos < n:
            ci += 1
            s = max(0, pos - ctx)
            e = min(n, pos + win + ctx)
            em, _ = model(wav[:, s:e].to(device))
            em = em[0].cpu()  # [F, V]
            head = (pos - s) // FRAME_SAMPLES
            if pos + win >= n:
                kept = em[head:]
            else:
                kept = em[head:head + win // FRAME_SAMPLES]
            chunks.append(kept)
            log(f"emissions: chunk {ci}/{n_chunks} frames={kept.size(0)}")
            pos += win
    emission = torch.cat(chunks, dim=0)
    return torch.log_softmax(emission.float(), dim=-1)


def build_targets(line_items, dictionary, star_id):
    """line_items: [(global_line_idx, Line)]. '*' before/between/after lines."""
    char_ids, owners = [], []

    def star():
        if star_id is not None:
            char_ids.append(star_id)
            owners.append(None)

    star()
    for li, ln in line_items:
        for wi, w in enumerate(ln.words):
            for ch in w.norm:
                char_ids.append(dictionary[ch])
                owners.append((li, wi))
        star()
    return char_ids, owners


def align_window(log_probs, f0, f1, char_ids, owners):
    """Force-align char_ids to log_probs[f0:f1]. Returns {(li,wi): [span]}
    with frame indices shifted to global, span = (start_f, end_f, prob, margin)."""
    import torch
    import torchaudio.functional as taf

    window = log_probs[f0:f1]
    targets = torch.tensor([char_ids], dtype=torch.int32)
    alignments, scores = taf.forced_align(window.unsqueeze(0), targets, blank=0)
    spans = taf.merge_tokens(alignments[0], scores[0].exp(), blank=0)
    if len(spans) != len(char_ids):
        raise RuntimeError(f"got {len(spans)} spans for {len(char_ids)} chars")
    frame_max = window.max(dim=-1).values  # [F]
    out = {}
    for k, sp in enumerate(spans):
        if owners[k] is None:
            continue
        lp = window[sp.start: sp.end, sp.token]
        margin = float((lp - frame_max[sp.start: sp.end]).exp().mean())
        out.setdefault(owners[k], []).append(
            (sp.start + f0, sp.end + f0, float(sp.score), margin))
    return out


# --------------------------------------------------------------------------
# Voiced-region gate
# --------------------------------------------------------------------------

def frame_rms(wav):
    import numpy as np

    x = wav[0].numpy()
    n_frames = len(x) // FRAME_SAMPLES
    x = x[: n_frames * FRAME_SAMPLES].reshape(n_frames, FRAME_SAMPLES)
    return np.sqrt((x ** 2).mean(axis=1))


def voiced_mask(rms):
    import numpy as np

    p10 = float(np.percentile(rms, 10))
    p95 = float(np.percentile(rms, 95))
    thresh = max(3.0 * p10, 0.05 * p95, 1e-3)
    return rms > thresh


def shrink_to_voiced(voiced, f0: int, f1: int, pad: int = 25):
    """Tighten [f0,f1) to the voiced content inside it (plus pad frames)."""
    import numpy as np

    f0 = max(0, min(f0, len(voiced) - 1))
    f1 = max(f0 + 1, min(f1, len(voiced)))
    idx = np.nonzero(voiced[f0:f1])[0]
    if len(idx) == 0:
        return f0, f1
    return max(f0, f0 + int(idx[0]) - pad), min(f1, f0 + int(idx[-1]) + pad)


# --------------------------------------------------------------------------
# Output formatting
# --------------------------------------------------------------------------

def fmt_ts(ms: float, bracket: bool) -> str:
    cs = int(round(ms / 10.0))
    mm, rest = divmod(cs, 6000)
    body = f"{mm:02d}:{rest // 100:02d}.{rest % 100:02d}"
    return f"[{body}]" if bracket else f"<{body}>"


def write_outputs(out_dir: Path, stem: str, audio_name: str, lines: list,
                  song_end_ms: int, meta: dict):
    out_dir.mkdir(parents=True, exist_ok=True)

    plain = [f"{fmt_ts(ln.start_ms, True)} {ln.display}" for ln in lines]
    plain.append(fmt_ts(lines[-1].end_ms, True))
    (out_dir / f"{stem}.lrc").write_text("\n".join(plain) + "\n", encoding="utf-8")

    rows = []
    for ln in lines:
        parts = [fmt_ts(ln.start_ms, True)]
        for w in ln.words:
            parts.append(f"{fmt_ts(w.start_ms, False)}{w.display}")
        parts.append(fmt_ts(ln.end_ms, False))
        rows.append(" ".join(parts))
    (out_dir / f"{stem}.words.lrc").write_text("\n".join(rows) + "\n", encoding="utf-8")

    rows = []
    for ln in lines:
        parts = [fmt_ts(ln.start_ms, True)]
        for w in ln.words:
            frag = "".join(f"{fmt_ts(s['start_ms'], False)}{s['text']}" for s in w.syllables)
            parts.append(frag if frag else w.display)
        parts.append(fmt_ts(ln.end_ms, False))
        rows.append(" ".join(parts))
    (out_dir / f"{stem}.syllables.lrc").write_text("\n".join(rows) + "\n", encoding="utf-8")

    doc = {
        "version": 2,
        "audio": audio_name,
        "engine": meta,
        "song_end_ms": song_end_ms,
        "lines": [
            {
                "text": ln.display,
                "start_ms": ln.start_ms,
                "end_ms": ln.end_ms,
                "margin": round(ln.margin, 3),
                **({"estimated": True} if ln.estimated else {}),
                **({"ref_ms": int(ln.ref_ms)} if ln.ref_ms is not None else {}),
                "words": [
                    {
                        "text": w.display,
                        "norm": w.norm,
                        "start_ms": w.start_ms,
                        "end_ms": w.end_ms,
                        "score": round(w.score, 3),
                        "prob": round(w.prob, 3),
                        **({"untimed": True} if w.untimed else {}),
                        "syllables": w.syllables,
                    }
                    for w in ln.words
                ],
            }
            for ln in lines
        ],
    }
    (out_dir / f"{stem}.timing.json").write_text(
        json.dumps(doc, ensure_ascii=False, indent=1), encoding="utf-8")


def write_report(out_dir: Path, lines: list, voiced, mode: str):
    import numpy as np

    rows = []
    deltas = []
    for i, ln in enumerate(lines):
        d = None
        if ln.ref_ms is not None:
            d = ln.start_ms - ln.ref_ms
            deltas.append(d)
        tag = "EST" if ln.estimated else "   "
        rows.append(
            f"{i + 1:3d} {tag} m={ln.margin:.2f}  auto={ln.start_ms / 1000.0:7.2f}s"
            + (f"  ref={ln.ref_ms / 1000.0:7.2f}s  delta={d / 1000.0:+6.2f}s" if d is not None else "")
            + f"  | {ln.display[:52]}")
    n_words = sum(len(ln.words) for ln in lines)
    onset_voiced = 0
    for ln in lines:
        for w in ln.words:
            f0 = int(w.start_ms / 1000.0 / FRAME_SEC)
            if voiced[max(0, f0 - 1): f0 + 3].any():
                onset_voiced += 1

    out = [f"anchor mode: {mode}", "",
           "=== lines (EST = interpolated, m = acoustic margin 0..1) ==="]
    out.extend(rows)
    if deltas:
        a = np.abs(np.array(deltas)) / 1000.0
        sgn = np.array(deltas) / 1000.0
        out.append("")
        out.append(f"lines with reference: {len(deltas)}")
        out.append(f"mean |delta|:   {a.mean():.3f}s   median |delta|: {np.median(a):.3f}s"
                   f"   max |delta|: {a.max():.3f}s")
        out.append(f"median signed delta: {np.median(sgn):+.3f}s "
                   f"(auto minus hand; positive = auto is later)")
        out.append(f"within 300ms: {(a <= 0.3).mean() * 100:.0f}%    "
                   f"within 500ms: {(a <= 0.5).mean() * 100:.0f}%    "
                   f"within 1s: {(a <= 1.0).mean() * 100:.0f}%")
    out.append("")
    out.append(f"word onsets in voiced audio: {onset_voiced}/{n_words} "
               f"({onset_voiced / max(1, n_words) * 100:.0f}%)")
    est = [f"  line {i + 1}: {ln.display[:50]}" for i, ln in enumerate(lines) if ln.estimated]
    out.append("")
    out.append(f"=== estimated (evidence-free) lines: {len(est)} ===")
    out.extend(est or ["  (none)"])
    text = "\n".join(out)
    (out_dir / "report.txt").write_text(text, encoding="utf-8")
    return text


# --------------------------------------------------------------------------
# Timing assembly
# --------------------------------------------------------------------------

def median_char_dur_frames(per_word, lines) -> float:
    import numpy as np

    durs = []
    for (li, wi), spans in per_word.items():
        w = lines[li].words[wi]
        if not spans or len(w.norm) == 0:
            continue
        margin = float(np.mean([sp[3] for sp in spans]))
        if margin >= 0.3:
            durs.append((spans[-1][1] - spans[0][0]) / len(w.norm))
    return float(np.median(durs)) if durs else 4.0  # frames (~80 ms default)


def synthesize_line_spans(line, li, f0, f1, char_dur_f, per_word):
    """Evenly pace a line's chars across [f0, f1) (interpolation fallback)."""
    n_chars = sum(len(w.norm) for w in line.words if not w.untimed)
    n_words = sum(1 for w in line.words if not w.untimed)
    if n_chars == 0:
        return
    gap_f = 4  # 80 ms between words
    want = n_chars * char_dur_f + max(0, n_words - 1) * gap_f
    span = min(max(f1 - f0, 10), want)
    scale = span / want
    pos = float(f0)
    for wi, w in enumerate(line.words):
        if w.untimed:
            continue
        spans = []
        for _ in w.norm:
            e = pos + char_dur_f * scale
            spans.append((int(round(pos)), max(int(round(e)), int(round(pos)) + 1), 0.0, 0.0))
            pos = e
        per_word[(li, wi)] = spans
        pos += gap_f * scale
    line.estimated = True


def assemble(lines, per_word, voiced, offset_ms, pyphen_dic, n_frames_total):
    """per_word spans -> word/syllable/line times. Spans: (start_f, end_f, prob, margin)."""
    import numpy as np

    def frame_ms(f):
        return f * FRAME_SEC * 1000.0 + offset_ms

    flat_words = [(li, wi, w) for li, ln in enumerate(lines)
                  for wi, w in enumerate(ln.words)]
    timed = [(li, wi, w) for li, wi, w in flat_words
             if not w.untimed and (li, wi) in per_word]

    for idx, (li, wi, w) in enumerate(timed):
        ws = per_word[(li, wi)]
        raw_start_f, raw_end_f = ws[0][0], ws[-1][1]
        next_start_f = (per_word[(timed[idx + 1][0], timed[idx + 1][1])][0][0]
                        if idx + 1 < len(timed) else n_frames_total)
        cap = min(max(next_start_f, raw_end_f), raw_end_f + 75, n_frames_total)
        e = raw_end_f
        while e < cap and e < len(voiced) and voiced[e]:
            e += 1
        w.start_ms = int(round(frame_ms(raw_start_f)))
        w.end_ms = int(round(frame_ms(max(e, raw_end_f))))
        frames = np.array([max(1, sp[1] - sp[0]) for sp in ws], dtype=float)
        w.prob = float(np.average([sp[2] for sp in ws], weights=frames))
        w.score = float(np.average([sp[3] for sp in ws], weights=frames))

        w.syllables = []
        base = 0
        for tok in w.tokens:
            parts = syllabify_token(tok, pyphen_dic)
            off = 0
            for p in parts:
                seg = ws[base + off: base + off + len(p)]
                w.syllables.append({
                    "text": p,
                    "start_ms": int(round(frame_ms(seg[0][0]))),
                    "end_ms": int(round(frame_ms(seg[-1][1]))),
                })
                off += len(p)
            base += len(tok)
        for si in range(len(w.syllables) - 1):
            w.syllables[si]["end_ms"] = max(w.syllables[si]["end_ms"],
                                            w.syllables[si + 1]["start_ms"])
        if w.syllables:
            w.syllables[-1]["end_ms"] = max(w.syllables[-1]["end_ms"], w.end_ms)

    # words with no spans at all (untimed or skipped) inherit previous end
    prev_end = 0
    for li, wi, w in flat_words:
        if w.untimed or (li, wi) not in per_word:
            w.start_ms = w.end_ms = prev_end
            w.syllables = []
            w.untimed = True
        else:
            prev_end = w.end_ms

    for idx in range(len(timed) - 1):
        w, nxt = timed[idx][2], timed[idx + 1][2]
        if nxt.start_ms > w.start_ms:
            w.end_ms = min(w.end_ms, nxt.start_ms)

    for li, ln in enumerate(lines):
        tw = [w for w in ln.words if not w.untimed]
        if tw:
            ln.start_ms = tw[0].start_ms
            ln.end_ms = max(w.end_ms for w in tw)
            ln.margin = float(np.mean([w.score for w in tw]))
    for i in range(len(lines) - 1):
        if lines[i].end_ms > lines[i + 1].start_ms and lines[i + 1].start_ms > lines[i].start_ms:
            lines[i].end_ms = lines[i + 1].start_ms


# --------------------------------------------------------------------------
# Anchoring strategies
# --------------------------------------------------------------------------

def line_margin_of(per_word, lines, li) -> float:
    import numpy as np

    vals = []
    weights = []
    for wi, w in enumerate(lines[li].words):
        spans = per_word.get((li, wi))
        if spans:
            vals.append(np.mean([sp[3] for sp in spans]))
            weights.append(len(w.norm))
    return float(np.average(vals, weights=weights)) if vals else 0.0


def align_ref_mode(lines, ref_end_ms, log_probs, dictionary, star_id, voiced,
                   char_dur_holder):
    """Each line aligned inside its own hand-stamped window."""
    T = log_probs.size(0)
    per_word = {}
    stamps = [ln.ref_ms for ln in lines]
    for i, ln in enumerate(lines):
        w0 = stamps[i] / 1000.0 - REF_SLACK_BEFORE_S
        if i + 1 < len(lines):
            w1 = stamps[i + 1] / 1000.0 + REF_SLACK_AFTER_S
        elif ref_end_ms is not None:
            w1 = ref_end_ms / 1000.0 + REF_SLACK_AFTER_S
        else:
            w1 = stamps[i] / 1000.0 + 12.0
        f0 = max(0, int(w0 / FRAME_SEC))
        f1 = min(T, max(f0 + 25, int(w1 / FRAME_SEC)))
        char_ids, owners = build_targets([(i, ln)], dictionary, star_id)
        try:
            got = align_window(log_probs, f0, f1, char_ids, owners)
            per_word.update(got)
        except RuntimeError as exc:
            log(f"line {i + 1}: local align failed ({exc}); will interpolate")

    char_dur = median_char_dur_frames(per_word, lines)
    char_dur_holder.append(char_dur)
    for i, ln in enumerate(lines):
        m = line_margin_of(per_word, lines, i)
        if m >= DEAD_MARGIN:
            continue
        # evidence-free: pace chars from the stamp itself (human truth)
        f0 = int(stamps[i] / 1000.0 / FRAME_SEC)
        if i + 1 < len(lines):
            f1 = int(stamps[i + 1] / 1000.0 / FRAME_SEC)
        elif ref_end_ms is not None:
            f1 = int(ref_end_ms / 1000.0 / FRAME_SEC)
        else:
            f1 = min(T, f0 + 500)
        f0s, f1s = shrink_to_voiced(voiced, f0, f1)
        synthesize_line_spans(ln, i, max(f0, f0s), f1, char_dur, per_word)
    return per_word


def align_auto_mode(lines, log_probs, dictionary, star_id, voiced,
                    char_dur_holder):
    """Global pass; high-margin lines anchor local re-alignment of weak runs."""
    T = log_probs.size(0)
    char_ids, owners = build_targets(list(enumerate(lines)), dictionary, star_id)
    per_word = align_window(log_probs, 0, T, char_ids, owners)

    margins = [line_margin_of(per_word, lines, i) for i in range(len(lines))]
    is_anchor = [
        m >= ANCHOR_MARGIN and sum(len(w.norm) for w in ln.words) >= MIN_ANCHOR_CHARS
        for m, ln in zip(margins, lines)
    ]
    log("auto anchors: " + ",".join(str(i + 1) for i, a in enumerate(is_anchor) if a))

    def line_start_f(i):
        spans = [per_word[(i, wi)] for wi in range(len(lines[i].words))
                 if (i, wi) in per_word]
        return min(sp[0][0] for sp in spans) if spans else 0

    def line_end_f(i):
        spans = [per_word[(i, wi)] for wi in range(len(lines[i].words))
                 if (i, wi) in per_word]
        return max(sp[-1][1] for sp in spans) if spans else T

    # re-align each maximal run of non-anchor lines between anchors
    i = 0
    while i < len(lines):
        if is_anchor[i]:
            i += 1
            continue
        j = i
        while j < len(lines) and not is_anchor[j]:
            j += 1
        f0 = line_end_f(i - 1) if i > 0 else 0
        f1 = line_start_f(j) if j < len(lines) else T
        f0 = max(0, f0 - 25)
        f1 = min(T, f1 + 25)
        if f1 - f0 > 30:
            run = [(k, lines[k]) for k in range(i, j)]
            cids, owns = build_targets(run, dictionary, star_id)
            try:
                got = align_window(log_probs, f0, f1, cids, owns)
                per_word.update(got)
                log(f"re-aligned lines {i + 1}..{j} in "
                    f"[{f0 * FRAME_SEC:.1f}s, {f1 * FRAME_SEC:.1f}s]")
            except RuntimeError as exc:
                log(f"re-align of lines {i + 1}..{j} failed: {exc}")
        i = j

    # interpolate lines that remain evidence-free, between placed neighbours
    char_dur = median_char_dur_frames(per_word, lines)
    char_dur_holder.append(char_dur)
    margins = [line_margin_of(per_word, lines, i) for i in range(len(lines))]
    placed = [m >= DEAD_MARGIN for m in margins]
    i = 0
    while i < len(lines):
        if placed[i]:
            i += 1
            continue
        j = i
        while j < len(lines) and not placed[j]:
            j += 1
        f0 = line_end_f(i - 1) + 12 if i > 0 else 0
        f1 = line_start_f(j) - 12 if j < len(lines) else T
        f0, f1 = shrink_to_voiced(voiced, f0, max(f1, f0 + 10))
        # split window among the dead lines by char count
        counts = [max(1, sum(len(w.norm) for w in lines[k].words)) for k in range(i, j)]
        total = sum(counts)
        pos = f0
        for off, k in enumerate(range(i, j)):
            f_next = pos + (f1 - f0) * counts[off] / total
            synthesize_line_spans(lines[k], k, int(pos), int(f_next), char_dur, per_word)
            pos = f_next
        i = j
    return per_word


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description="Word/syllable-level lyric aligner")
    ap.add_argument("audio", type=Path)
    ap.add_argument("lyrics", type=Path)
    ap.add_argument("-o", "--out-dir", type=Path, default=None)
    ap.add_argument("--work-dir", type=Path, default=Path(__file__).parent / "work")
    ap.add_argument("--no-separate", action="store_true",
                    help="align against the full mix (faster, less accurate)")
    ap.add_argument("--demucs-model", default="htdemucs")
    ap.add_argument("--device", default="cpu")
    ap.add_argument("--threads", type=int, default=8)
    ap.add_argument("--window-s", type=float, default=30.0)
    ap.add_argument("--context-s", type=float, default=4.0)
    ap.add_argument("--offset-ms", type=float, default=0.0,
                    help="constant added to all output times")
    ap.add_argument("--anchors", choices=["auto", "ref", "none"], default=None,
                    help="ref: align inside hand-stamped line windows; "
                         "auto: two-pass margin anchoring; none: single pass. "
                         "Default: ref when the lyrics file has timestamps, else auto.")
    ap.add_argument("--language", default="en_US", help="pyphen hyphenation language")
    args = ap.parse_args()

    t0 = time.time()
    os.environ["OMP_NUM_THREADS"] = str(args.threads)
    os.environ["MKL_NUM_THREADS"] = str(args.threads)

    import numpy as np  # noqa: F401
    import soundfile as sf
    import torch
    import torchaudio
    from num2words import num2words

    try:
        import pyphen
        pyphen_dic = pyphen.Pyphen(lang=args.language)
    except Exception:
        pyphen_dic = None

    torch.set_num_threads(args.threads)

    stem = re.sub(r"[^\w\-]+", "_", args.audio.stem).strip("_")
    out_dir = args.out_dir or (Path(__file__).parent / "out" / stem)
    work = args.work_dir
    work.mkdir(parents=True, exist_ok=True)

    # ---- lyrics
    lines, ref_end_ms = parse_lyrics(args.lyrics)
    n_words = sum(len(ln.words) for ln in lines)
    has_ref = all(ln.ref_ms is not None for ln in lines) and lines
    mode = args.anchors or ("ref" if has_ref else "auto")
    if mode == "ref" and not has_ref:
        log("WARNING: --anchors ref requested but not all lines have stamps; using auto")
        mode = "auto"
    log(f"lyrics: {len(lines)} lines, {n_words} words; anchor mode: {mode}")

    # ---- audio prep
    song_wav = work / f"{stem}.wav"
    ensure_wav(args.audio, song_wav, 44100, 2)
    if args.no_separate:
        align_src = song_wav
        wav16 = work / f"{stem}.mix16k.wav"
    else:
        align_src = separate_vocals(song_wav, work, args.demucs_model,
                                    args.device, args.threads)
        wav16 = work / f"{stem}.vocals16k.wav"
    ensure_wav(align_src, wav16, SAMPLE_RATE, 1)

    data, sr = sf.read(wav16, dtype="float32")
    assert sr == SAMPLE_RATE
    wav = torch.from_numpy(data).unsqueeze(0)
    dur_s = wav.size(1) / SAMPLE_RATE
    log(f"audio: {dur_s:.1f}s at 16k mono ({wav16.name})")

    # ---- model + dictionary
    log("loading MMS_FA aligner model (first run downloads ~1.2 GB)...")
    bundle = torchaudio.pipelines.MMS_FA
    model = bundle.get_model(with_star=True).to(args.device).eval()
    dictionary = bundle.get_dict()
    star_id = dictionary.get("*", max(dictionary.values()) + 1)
    dict_chars = {k for k in dictionary if len(k) == 1 and (k.isalpha() or k == "'")}

    # ---- normalize
    dropped = set()
    for ln in lines:
        for w in ln.words:
            w.tokens = normalize_word(w.display, dict_chars, num2words)
            w.norm = "".join(w.tokens)
            if not w.norm:
                w.untimed = True
                dropped.add(w.display)
    if dropped:
        log(f"untimed words (no alignable chars): {sorted(dropped)}")

    # ---- emissions
    log("computing emissions...")
    log_probs = compute_emissions(model, wav, args.device, args.window_s, args.context_s)
    log(f"emissions: {log_probs.size(0)} frames x {log_probs.size(1)} labels")

    rms = frame_rms(wav)
    voiced = voiced_mask(rms)

    # ---- align per anchor mode
    char_dur_holder = []
    if mode == "ref":
        per_word = align_ref_mode(lines, ref_end_ms, log_probs, dictionary,
                                  star_id, voiced, char_dur_holder)
    elif mode == "auto":
        per_word = align_auto_mode(lines, log_probs, dictionary, star_id,
                                   voiced, char_dur_holder)
    else:
        char_ids, owners = build_targets(list(enumerate(lines)), dictionary, star_id)
        per_word = align_window(log_probs, 0, log_probs.size(0), char_ids, owners)
        char_dur_holder.append(median_char_dur_frames(per_word, lines))

    # ---- assemble timings + outputs
    assemble(lines, per_word, voiced, args.offset_ms, pyphen_dic, log_probs.size(0))

    song_end_ms = int(dur_s * 1000)
    meta = {
        "separator": ("none" if args.no_separate else args.demucs_model),
        "aligner": "torchaudio MMS_FA (wav2vec2 CTC forced alignment)",
        "anchor_mode": mode,
        "language": args.language,
        "offset_ms": args.offset_ms,
    }
    write_outputs(out_dir, stem, args.audio.name, lines, song_end_ms, meta)
    report = write_report(out_dir, lines, voiced, mode)
    print()
    print(report)
    log(f"outputs written to {out_dir}")
    log(f"total time: {time.time() - t0:.0f}s")


if __name__ == "__main__":
    main()
