#!/usr/bin/env python
"""
align_lyrics.py -- automatic word/syllable-level lyric timing for type!beat.

Given a song (mp3/wav/...) and its lyrics (plain text, or a line-level LRC),
produces:

  <stem>.lrc            line-level LRC (drop-in for the game's current LrcParser)
  <stem>.words.lrc      enhanced LRC with <mm:ss.xx> word tags
  <stem>.syllables.lrc  enhanced LRC with syllable-level tags (normalized text)
  <stem>.timing.json    rich word+syllable timings with confidence scores
  vocals.wav            the isolated vocals stem, 16 kHz mono (a --no-separate run writes none)
  report.txt            QC report (vs reference times if available)

Pipeline:
  1. ffmpeg decode -> wav
  2. Demucs (htdemucs) two-stem separation -> isolated vocals   [cached]
  3. resample vocals to 16 kHz mono
  4. wav2vec2 CTC emissions via torchaudio MMS_FA, computed in overlapping
     chunks and stitched (full-song attention would blow up CPU RAM); since
     version 6 the posteriors of several such passes ("views", --quality)
     are averaged, and every view is cached in the work dir
  5. CTC Viterbi alignment of normalized lyric characters, with '*'
     wildcards between lines absorbing unlisted vocals
  6. anchoring:
       --anchors ref   (version 6) one banded whole-song alignment in which
                       each section's letters live inside [its stamp, the next
                       stamp), softened by a per-song stamp error model (lead,
                       late stamps, onset prior, per-line left walls) and an
                       even-pacing prior; unstamped lines join the section of
                       the stamped line above them and are placed by a section
                       graph; garbage paths are replaced by even pacing
       --anchors auto  (version 6) a duration-aware banded Viterbi over the
                       whole song, run twice around the song's measured pace;
                       lines without evidence are flagged "estimated"
       --anchors none  single global pass
       --vocal-mode estimated (version 7, a stamped file only, chosen per song)
                       no acoustic path: every line paced evenly from its
                       stamp plus the song's stamp lead
       (version 8)     the demucs child runs with PYTHONUTF8/PYTHONIOENCODING set, so a
                       non-Latin song title no longer kills the separation on Windows
       --evidence fused (version 10, opt-in; the default is version 9 unchanged)
                       English lyrics also get a second acoustic model: the QMUL
                       singing-trained phoneme CTC (Huang, Benetos, Ewert; DALI, MIT
                       licensed, 57 MB) on the same stem. Its evidence goes through the
                       same decoders (pseudo-letters, a constant '*' at -2 nats in stamped
                       modes), and inside the stamped decoder as a per-letter emission
                       product on the MMS_FA letters; --fuse median3 takes each word's
                       median start of the three paths (auto: the QMUL path alone).
                       --fuse qmul under stamps keeps version 9's start where the QMUL
                       path's is more than 5 s from it. Words
                       where the MMS_FA and QMUL paths disagree by more than 200 ms carry
                       "review": true. Other languages (--lyrics-language, else a
                       conservative English detector), missing dependencies (phonemizer,
                       espeakng-loader) or missing weights fall back to version 9 with a
                       logged reason. Measured on the ranked-map corpus in
                       bench/altmodels (RESULTS.md, results/exp_*.md).
  7. char spans -> syllables (authored hyphens first, else pyphen + naive
     fallback) -> words -> lines; end times extended through sustained
     voiced audio (RMS gate); a validator repairs/rejects impossible output

Confidence: each word carries `score` = mean margin (0..1) between the
aligned char and the model's argmax at those frames. High = the model
actually hears this word here. `prob` = raw mean char probability.

Version 2 (2026-09-28), decided on the ranked-map corpus (84 maps, 20.6k
words, the maps' own timings as truth; see typebeat-lyriclab/bench):
  - ref windows lost their 0.75 s / 0.5 s slack: with slack, a line whose
    first syllables repeat the previous line's last ones latched onto that
    tail. Word starts within 200 ms of the map: 85% -> 90% (exact stamps),
    74% -> 87% (stamps 250 ms early, how mappers actually stamp).
  - ref mode no longer replaces a low-margin line by even pacing; the CTC
    path is kept. Even pacing was governing a third of all lines, most of
    which had usable evidence. The five maps it loses on are screamed or
    effect-heavy vocals; a detector for garbage paths is the follow-up.
  - accented letters are folded (è -> e) instead of dropped (which made "è"
    an untimed word and aligned "perché" as "perch").
  - authored hyphens are syllable boundaries ("pa-pa-ta-ta" aligns and splits
    at the hyphens); pyphen is only consulted for unhyphenated words.
  - runs of three or more identical letters collapse to one for ALIGNMENT
    only ("piiiiiii" -> "pi"); display text is untouched.
  - CTC emissions are cached in the work dir, so a re-run after nudging a
    stamp takes seconds instead of a model pass.
  - "pin the first word to its stamp" was measured and REJECTED: it only
    helps when stamps are exact acoustic onsets and hurts with human stamps.

Version 3 (2026-09-28): sparse anchors. ref mode no longer needs EVERY line
stamped (it used to fall back to auto on a single missing stamp): a stamped
line opens a section, the unstamped lines after it join that section, and the
section is aligned inside exactly [its stamp, the next stamp) as one CTC
target with '*' between its lines. A fully stamped file gives byte-identical
output to version 2. Ranked corpus (85 maps, 20.8k words), word starts within
200 ms with stamps 250 ms early: every line 88%, every second line 85%, every
fourth line 83%, no stamps (auto) 67%.

Version 4 (2026-09-28): garbage-path detector. ref mode still keeps the CTC
path, unless its SHAPE says the model found nothing (garbage_reasons): most
words sung one letter per frame ("crammed"), the whole line under 0.6x the
song's median time per letter ("fast"), or a stamped line whose path opens
0.8 s or more after its stamp while no slower than the median ("late", every
word piled at the far end of the window). Such a line is paced at the song's
median rate from its stamp PLUS the song's stamp lead (how late confident
lines start after their stamps: ~0 for exact stamps, ~250 ms for a mapper who
taps early) and flagged estimated. Without the lead, even pacing from an early
stamp lost on every map, the five effect-heavy ones included. A margin
threshold was measured and rejected again: at 0.03 it costs 1 point with exact
stamps and 2 to 6 with human ones. Shinigiwa Satellite (the worst of the five)
is NOT recovered: its wrong paths look like singing, spread at the song's pace.
Ranked corpus, word starts within 200 ms: exact 89.5% -> 90.2%, human 87.8% ->
88.0%, every second stamp 86.5% -> 86.9% (exact) / 85.5% -> 85.8% (human).

Version 5 (2026-09-29): soft even-pacing prior (backlog 325). After the unbiased pass has measured
the song's pace and stamp lead, every stamped section is aligned again with a log-prior on each
letter: free within 0.1 s of where even pacing from the stamp (plus the lead) puts it, then 0.5
nats per frame per second further, saturating 1 s beyond (realign_with_pacing_prior; version 5,
removed in version 6). The model's
evidence still wins wherever it is clear; the prior only settles paths the emissions barely
prefer, which is what Shinigiwa-class lines are: their wrong paths have a normal shape and a
margin a threshold cannot separate (backlog 302), but they sit inside a near-flat ridge of the
emissions. The prior runs on the per-position emission matrix (prior_columns; version 5, removed
in version 6): same Viterbi,
prob and margin still read off the unbiased emissions. Weight 0 reproduces version 4 exactly.
Ranked corpus (85 maps, 20.8k words), word starts within 200 ms: exact 90.2% -> 90.8%, human
88.0% -> 89.0%, every second stamp 86.9% -> 88.2% (exact) / 85.8% -> 86.7% (human), auto
byte-identical; a two-fold split by set tunes to the same setting and gains out of fold on every
variant. Shinigiwa Satellite 46% -> 51% (exact) / 46% -> 50% (human, where even pacing itself
tops out at 52%). REJECTED on the same data: a heavy prior recovers Shinigiwa (63% at weight 8,
76% when pacing fills the window) but costs 12 points or more corpus-wide, because most songs
sing unevenly and their evidence is just as quiet; gating the heavy prior by line margin, by song
margin or by how many nats it costs the path trades lines both ways again (the best gate reaches
Shinigiwa 56% for less corpus gain than no gate, and does not survive the split). An uncapped
prior wrecks slow choral maps (Locus Iste 54% -> 17%), hence the cap.

Version 6 (2026-09-30): new evidence and new decoders, two independent changes measured on the same
corpus (bench/exp/v6: PORTING.txt, EVIDENCE.txt, FINAL.txt). EVIDENCE: the emissions are the
average of the posteriors of several MMS_FA passes ("views") over the same audio instead of one pass.
The default tier (--quality fast) runs two passes of a dynamically int8-quantised copy of the model,
one on the stock 30 s chunk grid and one on a grid shifted by 15 s, so every chunk seam of one pass
is heard from inside a chunk by the other; int8 makes the two passes cost about what version 5's
single fp32 pass cost (1.1x at 6 threads, 0.9x at 2). --quality full averages eight views (four
chunk grids, a loudness-levelled copy of the stem and one shifted by 15 s, a 150 Hz high-passed copy,
the full mix) for about 4x the model time; --quality single is version 5's single fp32 pass. The
fused matrix keeps version 5's shape and its constant '*' column, so prob, margin and everything
downstream read it exactly as they read the single pass. DECODERS: stamped files (fully or sparsely
stamped) are aligned by ONE banded whole-song Viterbi instead of a forced alignment per window: each
section's letters live in [its stamp, the next stamp) softly, not exactly. A per-song stamp error
model measured on the section openers of a first pass sets the lead (how late confident lines start
after their stamps), an onset prior on each section's first letter, per-line left walls where an
opener is heard before its stamp and, when the confident openers say the stamps are LATE, opens every
section's left wall by that much; version 5's even-pacing prior is laid out syllable by syllable, and
an unstamped line inside a section is placed by a section graph (letter spacing costs, a soft minimum
gap between lines, confidently heard lines as anchors). Plain lyrics are aligned by a duration-aware
banded Viterbi: a first pass measures the song's pace in frames per letter, a second pass charges
in-line gaps, letter-to-letter skips and voiced audio swallowed by the '*' between lines at that pace
(the '*' charge only when the lyric sheet repeats its lines, so a chorus written once can still skip
its unlisted repeats), with a compaction guard against squeezing blind lines. Both decoders are
bounded in memory and time per call (linear in song length) and never raise: the stamped one falls
back to even pacing per section, the auto one to an even layout over the voiced audio. "estimated"
keeps its version 5 meaning in both modes (paced by a fallback, or an auto line whose letters' margin
is under 0.08). Ranked corpus (85 maps, 20.8k words), word starts within 200 ms, version 5 -> 6 on the
fast tier: exact stamps 90.84% -> 92.20%, human 88.97% -> 91.07%, every second stamp 88.21% -> 90.91%
(exact) / 86.69% -> 90.24% (human), every eighth human stamp 80.66% -> 86.52%, auto 66.76% -> 83.11%;
all 14 variants (13 stamped and auto) gain, on both halves of the corpus split by set id (half A the
even ids, half B the odd, the split every constant was tuned out of sample on), and every bootstrap
interval excludes 0. With evidence off (--quality single) the decoders alone give 91.69 / 90.58 / 90.00 / 89.10
/ 80.08 on the first five; full gives 92.75 / 92.01 / 91.57 / 91.11 / 85.50. Stamps 300 ms late (a
mapper stamping on reaction, never tuned on) go from 67.13% (version 5) to 82.34% (version 6 with
evidence off) and 82.41% (fast). REJECTED: the full mix as a
view of the middle tier (it helps one half of the corpus and not the other), a leading-edge bonus for
auto (moves one choral map, costs 0.58 pooled), letting a stamp's window spill with no margin (a trade
between exact and human stamps), a late-stamp call on 3 openers (false late calls on exact stamps).
Int8 output depends on the torch thread count and the quantised engine (about 0.3 nats per frame in
the low-probability classes, the per-frame argmax identical), which moves a score by up to about 0.5
points on a stamped variant and 1 on auto: noise, not a regression. The engine is part of each int8
view's cache name, the thread count deliberately is not (a re-run at another --threads reuses the
views on disk; the count that produced them is stored with them and reported). Where int8 cannot run (a GPU, or a torch build with no quantised
engine) the same views run in fp32, which scores the same at about twice the time. KNOWN FAILURE
MODES (measured, none a crash): voiced non-lyric audio before the vocals (a leaky or unseparated
intro) can pull plain lyrics into it, because the RMS voicing gate calls it sung (60 s of intro before
the song: auto loses about 4 points of its own score, 5 to 7 maps collapse; stamped files are
unaffected; a 136 s voiced intro collapses Mili's Between Two Worlds on fused evidence); an
abbreviated lyric sheet (each chorus written once) gains about +6 instead of +14 in auto; the free
leading '*' lets an unheard slow opening drift late (slow choral songs); the voiced '*' can drag
unstamped lines across a long instrumental inside a sparse section. Inputs with more words than the
audio has milliseconds still fail the output validator, as they must.

Version 7 (2026-10-02): --vocal-mode estimated (backlog 354), a mapper-chosen per-song mode for
vocals the acoustic model cannot follow. Every line of a stamped file is paced evenly from its stamp
plus the song's stamp lead at the song's median letter length (align_estimated_mode); the emissions
are read only by the stamped decoder's unbiased first pass, for those two numbers, and no CTC path is
kept. It is never a default and never an automatic fallback: on the ranked corpus (85 maps, 20.8k
words, single-tier emissions, word starts within 200 ms) it scores 60.66% under exact stamps against
the stamped decoder's 91.69%, and 56.02% against 90.58% under human stamps. Its case is the
Shinigiwa class: Shinigiwa Satellite 55.0% -> 75.2% (exact) and 51.3% -> 59.3% (human), where backlog
346 measured version 5's even-pacing oracle at 71.9% and about 52%; Now Is Gold 57.3% -> 59.1% exact
but 54.3% -> 52.4% human, so its value tracks stamp quality. The first pass earns its cost: without
it (80 ms a letter, no lead) Shinigiwa falls to 54.6% (exact) and 10.9% (human). Without the flag
every output is version 6's; the version is bumped so the game knows the flag exists.
"""

import os
import sys


def argv_threads(argv):
    """The --threads value on a command line, read before argparse runs (see below); None when it
    is absent or not a whole number (argparse then reports the error itself). Pure."""
    for i, a in enumerate(argv):
        v = argv[i + 1] if a == "--threads" and i + 1 < len(argv) else (a[10:] if a.startswith("--threads=") else None)
        if v is not None:
            return v if v.isdigit() else None
    return None


# The OpenMP / BLAS pools size themselves from these when their library loads, and numpy loads below,
# at import time. Run as a script, the command line's --threads (default 8) is therefore applied HERE,
# before argparse, as version 5 applied it by importing numpy only inside main; main sets it again
# before torch loads. Imported as a module (the lab scripts), the caller's environment wins and 8 is
# only the default.
if __name__ == "__main__":
    os.environ["OMP_NUM_THREADS"] = os.environ["MKL_NUM_THREADS"] = argv_threads(sys.argv[1:]) or "8"
else:
    os.environ.setdefault("OMP_NUM_THREADS", "8")
    os.environ.setdefault("MKL_NUM_THREADS", "8")

import argparse
import json
import math
import re
import shutil
import subprocess
import time
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

# numpy is the version 6 decoders' only dependency beyond the standard library. It is imported here
# (after the thread defaults above) rather than inside every decoder function, but optionally, so the
# standard-library self-tests still run under a plain Python without it.
try:
    import numpy as np
except ImportError:  # pragma: no cover - plain Python running a self-test
    np = None

if sys.stdout and hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

# Bumped when the output of the same inputs changes. The game compares the shipped copy's
# version with the installed one and offers a reinstall; `--version` prints it.
ALIGNER_VERSION = "10"

SAMPLE_RATE = 16000
FRAME_SAMPLES = 320          # wav2vec2 stride: 20 ms at 16 kHz
FRAME_SEC = FRAME_SAMPLES / SAMPLE_RATE

TS_RE = re.compile(r"\s*\[(\d+):(\d{1,2}(?:\.\d+)?)\]\s*")
VOWELS = set("aeiouy")
HYPHENS = "-\u2010\u2011\u2012\u2013\u2014"   # authored syllable boundaries inside a word

# ref mode garbage-path detector (version 4). A path is judged by its SHAPE, never by its margin
# alone (a margin threshold trades lines evenly both ways; see the module docstring). Measured on
# the ranked-map corpus; a flagged line is paced from its stamp plus the song's robust stamp lead
# (REF LEAD_MARGIN, version 6) and marked estimated.
GARBAGE_CRAMMED_FRAC = 2 / 3   # this share of a line's multi-letter words sung one letter per frame
GARBAGE_SUB_MEDIAN = 0.6       # whole path shorter than this x the song's median time per letter
GARBAGE_LATE_S = 0.8           # a stamped line whose path opens this long after the stamp ...
GARBAGE_LATE_RATE = 1.0        # ... while no slower than the song's median pace

# ref mode even-pacing prior (version 5, kept by version 6). A log-prior per letter and frame: 0
# within PACING_PRIOR_TOL_S of where even pacing from the stamp puts the letter, then
# PACING_PRIOR_WEIGHT nats per frame for every further second, saturating PACING_PRIOR_CAP_S beyond
# the tolerance (so a slow choral line the song's median pace cannot describe is not dragged seconds
# early). Version 5 measured the plateau 0.25..1 x 0.05..0.5 s x 0.75..2 s; version 6 lays the
# expectation out syllable by syllable (REF SYL_PACE) and reads these three in its softwall pieces
# (sparse sections weigh the same prior at REF SP_PW instead of the weight).
PACING_PRIOR_WEIGHT = 0.5
PACING_PRIOR_TOL_S = 0.1
PACING_PRIOR_CAP_S = 1.0

# Version 6 evidence tiers (--quality; the recipe is bench/exp/v6/EVIDENCE.txt). A VIEW is one
# chunked MMS_FA pass over a waveform derived from the song's own audio: (waveform, offset of the
# chunk grid in seconds). "stem" is the separated vocals at 16 kHz, exactly what version 5 aligned;
# "agc" the stem levelled to a constant loudness (agc_waveform); "hp" the stem high-passed at
# HIGHPASS_HZ; "mix" the full mix at 16 kHz. An offset shortens the first chunk, so every later chunk
# seam moves by that much and falls inside a chunk of the stock grid.
EVIDENCE_VIEWS = {
    "stem": ("stem", 0.0),
    "shift": ("stem", 15.0),
    "shift75": ("stem", 7.5),
    "shift225": ("stem", 22.5),
    "agc": ("agc", 0.0),
    "agcshift": ("agc", 15.0),
    "hp": ("hp", 0.0),
    "mix": ("mix", 0.0),
}
# The tiers, in fusion order (the order changes nothing beyond float rounding; it is fixed so a
# re-run is bit-identical). "single" is version 5's one fp32 pass with no fusion. "fast" costs about
# what that pass cost (the two views run int8). "mid" adds the two views that helped most on one half
# of the corpus and confirmed on the other (the mix view did not confirm, so it is only in "full").
# "full" is every view, about 4x the model time of "fast". Measured on the ranked corpus with the
# version 6 decoders (within 200 ms, exact / human stamps / auto): single 91.69 / 90.58 / 80.08,
# fast 92.20 / 91.07 / 83.11, mid 92.44 / 91.65 / 84.31, full 92.75 / 92.01 / 85.50.
QUALITY_TIERS = {
    "single": ["stem"],
    "fast": ["stem", "shift"],
    "mid": ["stem", "shift", "shift225", "agc"],
    "full": ["stem", "shift75", "shift", "shift225", "agc", "agcshift", "hp", "mix"],
}
DEFAULT_QUALITY = "fast"
AGC_WINDOW_S = 0.4           # agc: the loudness envelope is a moving average over +-this
AGC_FLOOR = 0.1              # ... floored at this x the song's 95th percentile frame RMS
AGC_PEAK = 0.9               # ... and the levelled waveform peak-normalised to this
HIGHPASS_HZ = 150.0          # hp: two biquad high-passes at this cutoff (4th order)


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
    authored: bool = False       # the display carries hyphens: tokens ARE the syllables
    review: bool = False         # version 10, fused evidence only: the two evidence paths disagree


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


def fold_accents(text: str) -> str:
    """è -> e, ñ -> n: decompose and drop the combining marks. The MMS_FA dictionary is a-z plus
    apostrophe, so an accented letter that is not folded is DROPPED by the dictionary filter,
    which used to make "è" an untimed word and align "perché" as "perch"."""
    return "".join(ch for ch in unicodedata.normalize("NFKD", text) if not unicodedata.combining(ch))


def collapse_runs(text: str) -> str:
    """Three or more identical letters in a row become one, for the ALIGNMENT text only: a
    stretched "piiiiiii" is one sung vowel, and CTC would otherwise demand a blank between every
    pair. Doubles are kept ("carry", "brr"): no word carries a triple, so nothing real is lost."""
    return re.sub(r"(.)\1{2,}", r"\1", text)


def split_fragments(display: str) -> list:
    """The pieces an authored hyphen separates ("pa-ta-pim" -> ["pa", "ta", "pim"]); pieces with
    no word character are dropped. A word without hyphens is one fragment."""
    parts = [p for p in re.split(f"[{re.escape(HYPHENS)}]", display) if re.search(r"\w", p)]
    return parts if len(parts) > 1 else [display]


def normalize_word(display: str, dict_chars: set, num2words_fn) -> list:
    """Display word -> list of alignable tokens (letters/apostrophes only)."""
    w = unicodedata.normalize("NFKC", display).lower()
    w = fold_accents(w)
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
        t = collapse_runs("".join(ch for ch in t if ch in dict_chars))
        if t:
            out.append(t)
    return out


def normalize_display(display: str, dict_chars: set, num2words_fn):
    """A display word -> (tokens, authored). Authored hyphens make each fragment ONE token that
    is also one syllable; otherwise the word normalizes as a whole and pyphen decides the split."""
    frags = split_fragments(display)
    if len(frags) == 1:
        return normalize_word(display, dict_chars, num2words_fn), False
    tokens = ["".join(normalize_word(f, dict_chars, num2words_fn)) for f in frags]
    return [t for t in tokens if t], True


# Pinned by `--self-test-normalize` (standard library only, like the syllable self-test).
NORMALIZE_CASES = {
    # accents fold instead of vanishing
    "è": ["e"], "perché": ["perche"], "señor": ["senor"], "naïve": ["naive"],
    # stretched vowels collapse; doubles survive
    "piiiiiiiii": ["pi"], "brr": ["brr"], "carry": ["carry"], "Nooooo!": ["no"],
    # authored hyphens are the syllables
    "pa-ta-pim": ["pa", "ta", "pim"], "Patapi-pi,": ["patapi", "pi"], "well-known": ["well", "known"],
    # everything else is unchanged
    "don't": ["don't"], "Hello,": ["hello"],
}


def self_test_normalize() -> int:
    dict_chars = set("abcdefghijklmnopqrstuvwxyz'")
    failed = []
    for word, want in NORMALIZE_CASES.items():
        got, _ = normalize_display(word, dict_chars, lambda n: str(n))
        if got != want:
            failed.append((word, got, want))
    for word, got, want in failed:
        print(f"FAIL {word!r}: got {got}, expected {want}")
    print(f"normalize self-test: {len(NORMALIZE_CASES) - len(failed)}/{len(NORMALIZE_CASES)}")
    return 1 if failed else 0


# --------------------------------------------------------------------------
# Syllabification
# --------------------------------------------------------------------------

# A final "-es" is pronounced (rather than silent) when the stem already ends in a sibilant,
# because there is no way to say the plural without it: "wishes", "faces", "boxes" are two,
# while "makes" and "hopes" are one.
SIBILANT_ES = ("ses", "zes", "xes", "ches", "shes", "ges", "ces")


def vowel_groups(word: str) -> list:
    """
    Runs of vowel letters in `word`, as (start, end) index pairs: one group is one syllable
    nucleus before the silent-ending corrections.

    Two refinements around 'y', which is the only letter that is a vowel some of the time:

    * A word-initial 'y' before another vowel is a CONSONANT glide, so "yes" and "you" are one
      nucleus, not two.
    * Elsewhere a 'y' directly before another vowel ENDS its run rather than merging with it,
      because that pair spans a real syllable break: "cry|ing", "play|er", "dy|ing". A 'y' after
      a vowel still merges, which is right for the diphthongs: "day", "they", "boy".
    """
    groups = []
    i = 0
    while i < len(word):
        if word[i] == "y" and i == 0 and i + 1 < len(word) and word[i + 1] in VOWELS:
            i += 1                        # glide, not a nucleus
            continue
        if word[i] in VOWELS:
            j = i
            while j < len(word) and word[j] in VOWELS:
                j += 1
                if word[j - 1] == "y" and j < len(word) and word[j] in VOWELS:
                    break                 # "y" + vowel is a break, not a diphthong
            groups.append((i, j))
            i = j
        else:
            i += 1
    return groups


def syllable_count(tok: str) -> int:
    """
    How many syllables `tok` has, from its spelling. Always at least 1.

    This is vowel-group counting with the refinements English actually needs, and it exists
    because the alignment underneath is CHARACTER level (the MMS_FA dictionary is a-z plus
    apostrophe), so there are no phonemes to read a syllable count off: spelling is the only
    evidence available, and the alignment only supplies the times once the split is decided.

    Corrections applied to the raw nucleus count, each firing only when the final 'e' opened a
    nucleus of its OWN (in "value", "cities" and "played" it merely extends the run before it,
    so it never added a count to cancel):

    * silent final 'e': "life", "breathe" and "fire" are one, not two. The exception is a
      consonant plus "-le", where the l becomes syllabic and the group stands: "table",
      "little", "people" are two, while "while" and "smile" are one.
    * silent final "-ed", unless the stem ends in t or d, which forces the vowel to be said:
      "breathed" is one, "wanted" and "needed" are two.
    * silent final "-es", unless the stem ends in a sibilant (see SIBILANT_ES).
    * a final syllabic consonant, which carries a syllable with no vowel letter to show for it:
      "rhythm" and "prism" are two. Kept narrow (-sm and -thm only), where it is unambiguous.

    Known imperfect, because spelling alone cannot settle it: "every" counts 3 (dictionaries
    list both 2 and 3, and it is usually sung as 2), "fire" and "hour" count 1 though they are
    often sung as 2, and any word whose vowel letters lie about its pronunciation. The count is
    only ever used to decide HOW MANY parts a word may split into, never where they fall, so an
    error costs a subdivision rather than a wrong time.
    """
    w = "".join(ch for ch in tok.lower() if ch.isalpha())
    if not w:
        return 1

    groups = vowel_groups(w)
    n = len(groups)
    if n == 0:
        return 1

    starts = {g[0] for g in groups}

    if w.endswith("e") and len(w) - 1 in starts:
        # Consonant + "-le" keeps its group: the e is silent but the l is syllabic.
        if not (len(w) >= 3 and w.endswith("le") and w[-3] not in VOWELS):
            n -= 1
    elif w.endswith("ed") and len(w) >= 3 and len(w) - 2 in starts:
        if w[-3] not in "td":
            n -= 1
    elif w.endswith("es") and len(w) >= 3 and len(w) - 2 in starts:
        if not w.endswith(SIBILANT_ES):
            n -= 1

    if w.endswith("sm") or w.endswith("thm"):
        n += 1

    return max(1, n)


def naive_syllables(tok: str, limit: int = 0) -> list:
    """
    Vowel-group splitter fallback. Keeps concat(parts) == tok.

    `limit` caps the number of parts at the true syllable count. The nuclei the counting rules
    discount are always TRAILING ones (a silent final e, -ed or -es), so the surplus is folded
    into the last part rather than dropped.
    """
    groups = vowel_groups(tok)
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
    parts = [p for p in parts if p] or [tok]
    if limit and len(parts) > limit:
        parts = parts[:limit - 1] + ["".join(parts[limit - 1:])]
    return parts


def syllabify_token(tok: str, pyphen_dic) -> list:
    """
    Split `tok` into syllable text parts, with concat(parts) == tok always.

    syllable_count decides HOW MANY parts; pyphen only decides WHERE they fall. That division
    matters: pyphen is a Liang hyphenation dictionary, so it is good at boundaries but returns a
    single part both for a genuine monosyllable AND for any word it has no pattern for, and the
    two are indistinguishable. Treating "no hyphen" as "fall back to splitting on vowel groups"
    is what used to cut "life" into "li|fe" and "breathe" into "breat|he", while "remember" came
    back correctly subdivided because pyphen did have a pattern for it.
    """
    if syllable_count(tok) <= 1:
        return [tok]

    if pyphen_dic is not None:
        cand = [p for p in pyphen_dic.inserted(tok).split("-") if p]
        if len(cand) > 1 and "".join(cand) == tok:
            return cand

    parts = naive_syllables(tok, syllable_count(tok))
    return parts if "".join(parts) == tok else [tok]


# Pinned by `python align_lyrics.py --self-test-syllables`, which needs nothing but the standard
# library (every heavy import in this file lives inside main), so it runs without the venv.
SYLLABLE_CASES = {
    # The reported bug: a silent final e is not a syllable of its own.
    "life": 1, "breathe": 1, "fire": 1, "love": 1, "one": 1, "alone": 2, "the": 1,
    # ... but a consonant plus "-le" is, because the l becomes syllabic.
    "table": 2, "little": 2, "people": 2, "while": 1, "smile": 1,
    # The case that was already right, and stays right.
    "remember": 3, "beautiful": 3,
    # Silent "-ed" unless the stem ends in t or d.
    "breathed": 1, "played": 1, "wanted": 2, "needed": 2,
    # Silent "-es" unless the stem ends in a sibilant.
    "makes": 1, "goes": 1, "wishes": 2, "faces": 2, "cities": 2,
    # 'y': a glide at the front of a word, a nucleus in the middle, a diphthong after a vowel.
    "yes": 1, "you": 1, "yellow": 2, "day": 1, "they": 1, "always": 2, "eye": 1,
    "crying": 2, "trying": 2, "player": 2,
    # Syllabic consonants, which have no vowel letter at all to be counted.
    "rhythm": 2, "prism": 2,
    # Vowel runs that are one nucleus however long they look.
    "see": 1, "hour": 1, "value": 2, "a": 1,
}


def self_test_syllables() -> int:
    """Checks syllable_count against SYLLABLE_CASES; prints failures and returns an exit code."""
    failed = [(w, syllable_count(w), want) for w, want in SYLLABLE_CASES.items()
              if syllable_count(w) != want]

    # Whatever the count says, a split must never lose or invent characters.
    lossy = []
    for w in SYLLABLE_CASES:
        parts = syllabify_token(w, None)
        if "".join(parts) != w or len(parts) > syllable_count(w):
            lossy.append((w, parts))

    for w, got, want in failed:
        print(f"FAIL {w}: counted {got}, expected {want}")
    for w, parts in lossy:
        print(f"FAIL {w}: bad split {parts}")

    total = len(SYLLABLE_CASES)
    bad = len(failed) + len(lossy)
    print(f"syllable self-test: {total - len(failed)}/{total} counts, "
          f"{total - len(lossy)}/{total} splits")
    return 1 if bad else 0


# --------------------------------------------------------------------------
# Audio helpers (ffmpeg CLI)
# --------------------------------------------------------------------------

def run_ffmpeg(args: list) -> None:
    subprocess.run(["ffmpeg", "-y", "-v", "error"] + args, check=True)


def ensure_wav(src: Path, dst: Path, rate: int, channels: int) -> None:
    """Converts src to a PCM wav at dst unless dst is already newer than src. Written to a
    per-process temporary name and renamed into place, so two runs sharing a work dir (the same
    song in two anchor modes at once) never read a file another one is still writing: ffmpeg -y
    truncates in place, and a reader meeting a half-rewritten wav decodes a full-length file with
    the wrong samples in it."""
    if dst.exists() and dst.stat().st_mtime >= src.stat().st_mtime:
        return
    dst.parent.mkdir(parents=True, exist_ok=True)
    tmp = dst.with_name(f"{dst.stem}.{os.getpid()}.part{dst.suffix}")
    try:
        run_ffmpeg(["-i", str(src), "-ac", str(channels), "-ar", str(rate),
                    "-c:a", "pcm_s16le", str(tmp)])
        os.replace(tmp, dst)
    finally:
        if tmp.exists():
            tmp.unlink()


def persist_vocals_stem(out_dir: Path, stem: str, vocals_wav: Path) -> Path:
    """Copies the isolated vocals stem into the output dir so a caller can keep it beside the map's
    audio (backlog 392: the editor draws a waveform of the vocals alone from it).

    The stem is the 16 kHz mono wav Demucs produced for the alignment, copied as `vocals.wav`; the
    game re-encodes it to `vocals.ogg` when it has an encoder, because 16 kHz mono PCM is about
    1.9 MB a minute and a map a mapper downloads should not carry that for a view-only surface. The
    name is fixed (`vocals`, no per-song stem) so the game can find it without reading the document.
    """
    out_dir.mkdir(parents=True, exist_ok=True)
    dst = out_dir / "vocals.wav"
    shutil.copyfile(vocals_wav, dst)
    log(f"vocals stem written to {dst}")
    return dst


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
    # demucs prints the track path ("Separating track ...") through the child's own stdout,
    # which on Windows defaults to the console code page (cp1252) and raises UnicodeEncodeError
    # on a Japanese or Chinese title, killing the separation before it starts. The game sets
    # these for the whole run too; repeated here so a direct invocation behaves the same.
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
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

def chunk_bounds(n: int, win: int, off: int = 0) -> list:
    """(start, length) in samples of each chunk of an n-sample waveform: the first chunk is `off`
    samples long when off > 0 (a shifted grid), else `win`; every later chunk is `win`; the last one
    ends at n. Standard library only."""
    bounds, pos = [], 0
    first = off if off > 0 else win
    while pos < n:
        step = first if pos == 0 else win
        bounds.append((pos, step))
        pos += step
    return bounds


def compute_emissions(model, wav, device, window_s: float, context_s: float, offset_s: float = 0.0,
                      progress=None):
    """
    Chunked wav2vec2 forward, stitched so frame g covers samples ~[g*320, g*320+400). Each chunk is
    run with context_s of audio either side and only its own frames are kept. offset_s shortens the
    first chunk and so moves every later seam (version 6's shifted views); 0 is version 5's grid,
    byte for byte. progress(frames) is called once per chunk; without it each chunk is logged.
    """
    import torch

    n = wav.size(1)
    win = int(window_s * SAMPLE_RATE) // FRAME_SAMPLES * FRAME_SAMPLES
    ctx = int(context_s * SAMPLE_RATE) // FRAME_SAMPLES * FRAME_SAMPLES
    off = int(offset_s * SAMPLE_RATE) // FRAME_SAMPLES * FRAME_SAMPLES
    bounds = chunk_bounds(n, win, off)
    chunks = []
    with torch.inference_mode():
        for ci, (pos, step) in enumerate(bounds, 1):
            s = max(0, pos - ctx)
            e = min(n, pos + step + ctx)
            em, _ = model(wav[:, s:e].to(device))
            em = em[0].cpu()  # [F, V]
            head = (pos - s) // FRAME_SAMPLES
            if pos + step >= n:
                kept = em[head:]
            else:
                kept = em[head:head + step // FRAME_SAMPLES]
            chunks.append(kept)
            if progress is None:
                log(f"emissions: chunk {ci}/{len(bounds)} frames={kept.size(0)}")
            else:
                progress(kept.size(0))
    emission = torch.cat(chunks, dim=0)
    return torch.log_softmax(emission.float(), dim=-1)


def agc_waveform(wav):
    """
    The "agc" views' waveform: the stem levelled to a constant loudness, so a quiet verse reaches
    the model as loud as the chorus. Per 20 ms frame RMS; a centred moving average of the power over
    +-AGC_WINDOW_S; floored at AGC_FLOOR x the 95th percentile RMS (silence is not amplified into
    noise); the gain p95 / envelope interpolated per sample; peak-normalised to AGC_PEAK.

    A stem digitally silent in 95% of its frames or more has no loudness to level to (p95 is 0, and
    0 / 0 would turn the whole view into NaN, which the fusion would spread to every frame); it
    raises, so the caller leaves the view out as it does any failed view. The epsilon floor on the
    envelope only guards the division: on real audio p95 x AGC_FLOOR is far above it, so the output
    is unchanged.
    """
    import torch

    x = wav[0]
    nf = x.numel() // FRAME_SAMPLES
    rms = x[: nf * FRAME_SAMPLES].reshape(nf, FRAME_SAMPLES).pow(2).mean(1).sqrt()
    k = max(1, int(AGC_WINDOW_S / FRAME_SEC))
    ker = torch.ones(1, 1, 2 * k + 1) / (2 * k + 1)
    env = torch.nn.functional.conv1d(rms.pow(2).view(1, 1, -1), ker, padding=k).view(-1).sqrt()
    p95 = float(torch.quantile(rms, 0.95)) if nf else 0.0
    if not p95 > 0:
        raise ValueError("the stem is digitally silent in 95% of its frames or more; nothing to level")
    env = torch.clamp(env, min=max(p95 * AGC_FLOOR, 1e-8))
    g = p95 / env
    gs = torch.nn.functional.interpolate(g.view(1, 1, -1), size=x.numel(), mode="linear",
                                         align_corners=False).view(-1)
    y = x * gs
    y = y / max(1e-6, float(y.abs().max())) * AGC_PEAK
    return y.unsqueeze(0)


def highpass_waveform(wav):
    """The "hp" view's waveform: the stem through two 150 Hz biquad high-passes (4th order), which
    takes the low end a leaky separation leaves under the voice."""
    import torchaudio.functional as taf

    return taf.highpass_biquad(taf.highpass_biquad(wav, SAMPLE_RATE, HIGHPASS_HZ), SAMPLE_RATE, HIGHPASS_HZ)


def fit_frames(lp, T: int):
    """A view padded (its last frame repeated) or trimmed to the stem view's T frames. Shifted grids
    give T or T +- 1 frames, the mix can differ by a little more."""
    import torch

    if lp.size(0) < T:
        lp = torch.cat([lp, lp[-1:].repeat(T - lp.size(0), 1)])
    return lp[:T]


def fuse_views(lps):
    """
    The version 6 fusion: [T, 29] view matrices of one song -> one [T, 29] float32 matrix in the
    same layout. Per view the '*' column (the last; the model appends it) is dropped and the model's
    own 28-class log-posteriors recovered; their PROBABILITIES are averaged (accumulated one view at a
    time in float64, so the peak memory is two matrices whatever the view count); then the '*' column
    is appended as 0 and the rows renormalised exactly as the single pass is. That keeps the '*'
    column at the constant -ln 2 the decoders are calibrated on (see the Version 6 docstring). Pure.
    """
    import torch

    p = None
    for lp in lps:
        q = torch.log_softmax(lp[:, :-1].double(), dim=-1)
        p = q.exp() if p is None else p + q.exp()
    f = torch.log(p / len(lps))
    q = torch.log_softmax(f, dim=-1)
    s = torch.zeros(q.size(0), 1, dtype=q.dtype)
    return torch.log_softmax(torch.cat([q, s], 1), dim=-1).float()


def int8_engine(torch):
    """The quantised engine dynamic int8 runs on, set as torch's current one; None when this torch
    build has none (ARM builds without qnnpack). The default engine is kept when it is usable."""
    engines = [e for e in torch.backends.quantized.supported_engines if e and e != "none"]
    current = torch.backends.quantized.engine
    if current in engines:
        return current
    for cand in ("x86", "fbgemm", "qnnpack", "onednn"):
        if cand in engines:
            try:
                torch.backends.quantized.engine = cand
                return cand
            except Exception:
                continue
    return None


def quantize_int8(model):
    """
    The int8 model of the fused tiers: dynamic quantisation of the Linear layers only (the
    transformer projections, feed-forward and output head; the convolutional feature extractor and
    the layer norms stay fp32), about 1.5 s once per process. Probed on 1 s of silence, because a
    build whose engine cannot run it raises only at the first forward. Raises on any failure.
    """
    import torch

    qmodel = torch.ao.quantization.quantize_dynamic(model, {torch.nn.Linear}, dtype=torch.qint8)
    with torch.inference_mode():
        qmodel(torch.zeros(1, SAMPLE_RATE))
    return qmodel


EMISSION_LABELS = 29   # MMS_FA's 28 labels plus the '*' the model appends (with_star=True)


def view_cache_name(key: str, window_s: float, context_s: float, view: str, prec: str, engine=None) -> str:
    """
    The file one view's emissions are cached under in the work dir. key: the content address of the
    audio the view is computed from (the stem's for every view but "mix", which has its own). The
    fp32 stem view keeps version 5's name, so either version reuses the other's pass; every other
    view adds its name and precision, so an fp32 fallback never reads an int8 entry or the other way
    round, and an int8 view its quantised engine, because engines differ in output. The torch thread
    count is deliberately NOT in the name, although int8 output moves with it (about 0.3 nats per
    frame, bench/exp/v6/EVIDENCE.txt, Numerics): a re-run at another --threads reuses the views on
    disk instead of paying for them again and moving words by that noise. The count that produced
    a view is stored inside its file (evidence_emissions). Pure.
    """
    base = f"emissions_{key}_w{window_s:g}_c{context_s:g}_star"
    if prec == "fp32":
        return f"{base}.pt" if view == "stem" else f"{base}_{view}_fp32.pt"
    return f"{base}_{view}_q8-{engine}.pt"


def evidence_emissions(quality, wav, audio_key, work: Path, device: str, window_s: float,
                       context_s: float, mix_wav=None, quant: str = "auto"):
    """
    The emission matrix the decoders read, for one --quality tier (QUALITY_TIERS), with every
    view's pass cached in `work`. wav: the stem at 16 kHz, [1, N]; audio_key: the content address
    of its samples; mix_wav(): the full mix at 16 kHz, [1, M], or None when the stem IS the mix
    (--no-separate), which drops the mix view. Returns (log_probs [T, 29] float32, info for the
    timing.json meta).

    Precision: int8 on the CPU (quant "auto" or "int8"); fp32 on any other device, with --quant
    fp32, for the single tier, and when this torch build cannot run int8 (no quantised engine, or
    the probe fails), which is logged: the SAME views run, never fewer. quant "int8" (testing) fails
    loudly instead of falling back, on a GPU and on the single tier included.

    Cache (content-addressed, so a re-run after nudging a stamp costs no model pass): one file per
    view, named by view_cache_name. An int8 file holds {"lp": matrix, "threads": the torch thread
    count that produced it}, which the meta reports (torch_threads; a list when the views on disk
    came from different counts); an fp32 file holds the bare matrix, version 5's format. Writes go
    to a temporary file renamed into place, so a run killed mid-write (the game kills the process
    on cancel) leaves no truncated entry; an entry that does not load as a finite [T, 29] matrix is
    logged, deleted and recomputed. The model is loaded only when a view is missing.

    Log order: when a view is missing, "loading MMS_FA aligner model" comes BEFORE the first
    "emissions:" line, as in version 5. The game's import screen (ImportProgressParser) opens a
    stage the first time it sees the stage's keyword and never walks back, so a tier or "cached"
    line ahead of the load would open the aligning stage first, and every chunk's progress would
    then land on the loading row.

    A failed view other than the stem one (an exception, or a non-finite matrix) is logged, left
    out of the fusion and not cached; the stem view on the stock grid defines T, and its failure is
    the import's failure, as in version 5.
    """
    import gc

    import torch
    import torchaudio

    views = [v for v in QUALITY_TIERS[quality] if not (v == "mix" and mix_wav is None)]
    cpu = str(device).startswith("cpu")
    if quant == "int8" and (quality == "single" or not cpu):
        raise SystemExit("--quant int8: int8 runs only on the CPU and only on a fused tier (not --quality single)")
    threads = torch.get_num_threads()
    notes = []   # logged after the model load (see Log order)
    engine = None
    if quality == "single" or quant == "fp32":
        prec = "fp32"
    elif not cpu:
        # dynamic int8 is a CPU feature; a GPU runs the same views in fp32 (the same quality)
        prec = "fp32"
        notes.append(f"emissions: {len(views)} views in fp32 on {device} (int8 quantisation is CPU-only)")
    else:
        prec = "int8"
        engine = int8_engine(torch)
        if engine is None:
            if quant == "int8":
                raise SystemExit("--quant int8: this torch build has no quantised engine")
            prec = "fp32"
            notes.append(f"WARNING: this torch build has no int8 quantisation engine; the {len(views)} views "
                         f"run in fp32 (the same quality, about twice the time)")

    mix = {}

    def mix_view():
        if "wav" not in mix:
            import hashlib
            mix["wav"] = mix_wav()
            mix["key"] = hashlib.sha256(mix["wav"].numpy().tobytes()).hexdigest()[:16]
        return mix["wav"], mix["key"]

    def cache_path(view):
        key = mix_view()[1] if view == "mix" else audio_key
        return work / view_cache_name(key, window_s, context_s, view, prec, engine)

    def read_cached():
        """The views already on disk at the current precision: ({view: matrix}, {view: threads or
        None}, the lines to log). Nothing is logged here (see Log order)."""
        got, made, lines = {}, {}, []
        for v in views:
            p = cache_path(v)
            if not p.exists():
                continue
            try:
                obj = torch.load(p, weights_only=True)
                lp, th = (obj.get("lp"), obj.get("threads")) if isinstance(obj, dict) else (obj, None)
                ok = (torch.is_tensor(lp) and lp.dim() == 2 and lp.size(1) == EMISSION_LABELS
                      and bool(torch.isfinite(lp).all()))
            except Exception:
                ok = False
            if not ok:
                lines.append(f"emissions: cache unreadable ({p.name}), recomputing")
                try:
                    p.unlink()
                except OSError:
                    pass
                continue
            got[v], made[v] = lp, th
            lines.append(f"emissions: cached ({p.name})")
        return got, made, lines

    def save(v, lp):
        path = cache_path(v)
        tmp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
        try:
            torch.save({"lp": lp, "threads": threads} if prec == "int8" else lp, tmp)
            os.replace(tmp, path)
        except Exception as exc:
            # a cache that cannot be written (disk full, a locked file) costs the next run a pass,
            # never this one its result
            log(f"WARNING: could not cache the {v} view ({type(exc).__name__}: {exc})")
            try:
                tmp.unlink()
            except OSError:
                pass

    got, made, cached = read_cached()
    missing = [v for v in views if v not in got]
    model = None
    if missing:
        log("loading MMS_FA aligner model (first run downloads ~1.2 GB)...")
        model = torchaudio.pipelines.MMS_FA.get_model(with_star=True).to(device).eval()
        if prec == "int8":
            try:
                qmodel = quantize_int8(model)
            except Exception as exc:
                if quant == "int8":
                    raise SystemExit(f"--quant int8: int8 quantisation failed ({type(exc).__name__}: {exc})")
                notes.append(f"WARNING: int8 quantisation failed on this machine ({type(exc).__name__}: {exc}); "
                             f"the {len(views)} views run in fp32 (the same quality, about twice the time)")
                prec, engine = "fp32", None
                got, made, cached = read_cached()
                missing = [v for v in views if v not in got]
            else:
                # the fp32 model (about 1.2 GB) is not needed again: the working set falls to ~1 GB
                model = qmodel
                del qmodel
                gc.collect()
                log(f"int8 model ready ({engine} engine, {threads} threads)")
    log(f"emissions: quality {quality} ({len(views)} view(s))")
    for line in notes + cached:
        log(line)

    if missing:
        def waveform(view):
            kind = EVIDENCE_VIEWS[view][0]
            if kind == "stem":
                return wav
            if kind == "agc":
                return agc_waveform(wav)
            if kind == "hp":
                return highpass_waveform(wav)
            return mix_view()[0]

        win = int(window_s * SAMPLE_RATE) // FRAME_SAMPLES * FRAME_SAMPLES
        total = 0
        for v in missing:
            n = mix_view()[0].size(1) if EVIDENCE_VIEWS[v][0] == "mix" else wav.size(1)
            off = int(EVIDENCE_VIEWS[v][1] * SAMPLE_RATE) // FRAME_SAMPLES * FRAME_SAMPLES
            total += len(chunk_bounds(n, win, off))
        done = [0]
        for v in missing:
            label = f"{v}, {prec}" if len(views) > 1 else None

            def progress(frames, label=label):
                done[0] += 1
                log(f"emissions: chunk {done[0]}/{total} frames={frames}" + (f" ({label})" if label else ""))
            try:
                lp = compute_emissions(model, waveform(v), device, window_s, context_s,
                                       EVIDENCE_VIEWS[v][1], progress)
                if not bool(torch.isfinite(lp).all()):
                    raise RuntimeError("non-finite emissions")
            except Exception as exc:
                if v == "stem":
                    raise
                log(f"emissions: the {v} view failed ({type(exc).__name__}: {exc}); fusing the others")
                continue
            if v != "stem":
                lp = fit_frames(lp, got["stem"].size(0))
            save(v, lp)
            got[v], made[v] = lp, (threads if prec == "int8" else None)
    if model is not None:
        del model
        gc.collect()

    T = got["stem"].size(0)
    used = [v for v in views if v in got]
    lps = [fit_frames(got[v], T) for v in used]
    info = {"quality": quality, "views": used, "quant": prec}
    if prec == "int8":
        counts = sorted({made[v] for v in used if made.get(v) is not None})
        info.update(quant_engine=engine, torch_threads=counts[0] if len(counts) == 1 else counts)
    if len(lps) == 1:
        return lps[0], info
    log(f"emissions: fused {len(lps)} views ({', '.join(used)}; {prec})")
    return fuse_views(lps), info


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


def even_letter_frames(line, f0, f1, char_dur_f) -> list:
    """
    Where even pacing from frame f0 puts each alignable letter of `line` (the start frame of each,
    in target order): synthesize_line_spans' layout (the song's median letter length, 80 ms between
    words, squeezed into [f0, f1) when the line would not fit). Standard library only.
    """
    n_chars = sum(len(w.norm) for w in line.words if not w.untimed)
    n_words = sum(1 for w in line.words if not w.untimed)
    if n_chars == 0:
        return []
    gap_f = 4
    want = n_chars * char_dur_f + max(0, n_words - 1) * gap_f
    scale = min(max(f1 - f0, 10), want) / want
    pos, out = float(f0), []
    for w in line.words:
        if w.untimed:
            continue
        for _ in w.norm:
            out.append(pos)
            pos += char_dur_f * scale
        pos += gap_f * scale
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
                        **({"review": True} if w.review else {}),
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


def validate_and_repair(lines: list, song_end_ms: int) -> list:
    """
    The loader collapses a run of identical word spans into zero-length points, so an output must
    never carry two timed words at the same start, a word that ends before it starts, or a time
    outside the song. Trivial cases are repaired in place (a 1 ms nudge, a 20 ms minimum width)
    and reported; a structural failure (lines out of order) raises, because the map it would
    produce is wrong in a way no nudge fixes.
    """
    notes = []
    prev_start = -1
    for li, ln in enumerate(lines):
        timed = [w for w in ln.words if not w.untimed]
        for w in timed:
            if w.start_ms <= prev_start:
                notes.append(f"line {li + 1}: '{w.display}' started at/before the previous word; nudged +{prev_start + 1 - w.start_ms} ms")
                w.start_ms = prev_start + 1
            if w.end_ms < w.start_ms + 20:
                w.end_ms = w.start_ms + 20
            if w.syllables:
                w.syllables[0]["start_ms"] = w.start_ms
                for k in range(1, len(w.syllables)):
                    if w.syllables[k]["start_ms"] <= w.syllables[k - 1]["start_ms"]:
                        w.syllables[k]["start_ms"] = w.syllables[k - 1]["start_ms"] + 1
                for k in range(len(w.syllables) - 1):
                    w.syllables[k]["end_ms"] = w.syllables[k + 1]["start_ms"]
                w.syllables[-1]["end_ms"] = max(w.syllables[-1]["end_ms"], w.end_ms)
            if w.start_ms > song_end_ms:
                raise RuntimeError(f"line {li + 1}: '{w.display}' is timed at {w.start_ms} ms, after the song ends ({song_end_ms} ms)")
            prev_start = w.start_ms
        if timed:
            ln.start_ms = timed[0].start_ms
            ln.end_ms = max(ln.end_ms, max(w.end_ms for w in timed))
    # A line with no alignable characters at all ("...", a bare number the dictionary lost) carries
    # no timing (0 ms, as it always has) and is dropped by the loader, so it takes no part here.
    placed = [(i, ln) for i, ln in enumerate(lines) if any(not w.untimed for w in ln.words)]
    for (pi, prev), (ci, cur) in zip(placed, placed[1:]):
        if cur.start_ms < prev.start_ms:
            raise RuntimeError(f"lines {pi + 1} and {ci + 1} are out of order ({prev.start_ms} ms then {cur.start_ms} ms)")
    return notes


def write_report(out_dir: Path, lines: list, voiced, mode: str, extra=None):
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
    if extra:
        # version 10: the fused evidence path's section (never written on the default path)
        out.append("")
        out.extend(extra)
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
            # An authored fragment IS a syllable; only unhyphenated words consult pyphen.
            parts = [tok] if w.authored else syllabify_token(tok, pyphen_dic)
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


def ref_sections(lines) -> list:
    """
    Sparse anchors (version 3): a stamped line opens a SECTION and every unstamped line after it
    joins that section, so a lyrics file may stamp only its section starts. Lines before the first
    stamp form a leading section that opens at the top of the song. With every line stamped, each
    section is one line and ref mode is exactly what it was.
    """
    sections = []
    for i, ln in enumerate(lines):
        if ln.ref_ms is not None or not sections:
            sections.append([i])
        else:
            sections[-1].append(i)
    return sections


def self_test_sections() -> int:
    """Pins ref_sections (standard library only, like the other self-tests). None = unstamped."""
    cases = [
        ([0, 1000, 2000], [[0], [1], [2]]),                  # fully stamped: one line each, as in v2
        ([0, None, None, 5000, None], [[0, 1, 2], [3, 4]]),  # section starts only
        ([None, None, 3000, None], [[0, 1], [2, 3]]),        # leading unstamped lines open at 0
        ([0, None, None], [[0, 1, 2]]),                      # one stamp is enough
        ([], []),
    ]
    failed = 0
    for stamps, want in cases:
        got = ref_sections([Line(display="x", words=[], ref_ms=s) for s in stamps])
        if got != want:
            failed += 1
            print(f"FAIL {stamps}: got {got}, expected {want}")
    print(f"sections self-test: {len(cases) - failed}/{len(cases)}")
    return 1 if failed else 0


def path_shape(word_spans, char_dur: float) -> dict:
    """
    The shape of one line's CTC path. `word_spans` is the line's placed words in order, each a
    list of char spans (start_f, end_f, ...); `char_dur` is the song's median frames per letter.

      crammed  share of the multi-letter words whose letters sit in consecutive frames (the word
               spans at most letters + 1 frames): nobody sings a word at 20 ms a letter, the
               path parked the word on whatever frames were left
      rate     frames from the first letter to the last, over letters x char_dur: 1.0 is the
               song's own median pace, 0.4 is the line squeezed into 40% of the time it needs
      start_f  the frame the path opens on
    Standard library only, so the self-test runs without the venv.
    """
    multi = [spans for spans in word_spans if len(spans) >= 2]
    crammed = (sum(1 for s in multi if s[-1][1] - s[0][0] <= len(s) + 1) / len(multi)) if multi else 0.0
    letters = sum(len(s) for s in word_spans)
    extent = word_spans[-1][-1][1] - word_spans[0][0][0]
    return {"crammed": crammed, "rate": extent / max(1e-6, letters * char_dur),
            "start_f": word_spans[0][0][0]}


def garbage_reasons(shape: dict, late_f=None) -> list:
    """
    Why a ref-mode path is garbage, empty when it is kept. `late_f` is how many frames after the
    line's expected onset (its stamp plus the song's stamp lead) the path opens, or None for a
    line that does not open its section (its stamp, if any, says nothing about where it starts).

      crammed  most words sung one letter per frame (GARBAGE_CRAMMED_FRAC)
      fast     the whole line far faster than the song's median pace (GARBAGE_SUB_MEDIAN)
      late     the words piled at the far end of the window: the path skips the stamp by more
               than GARBAGE_LATE_S while running no slower than the median, so it is not a
               slow line that merely starts late
    """
    reasons = []
    if shape["crammed"] >= GARBAGE_CRAMMED_FRAC - 1e-9:
        reasons.append("crammed")
    if shape["rate"] < GARBAGE_SUB_MEDIAN:
        reasons.append("fast")
    if late_f is not None and late_f * FRAME_SEC >= GARBAGE_LATE_S - 1e-9 and shape["rate"] < GARBAGE_LATE_RATE:
        reasons.append("late")
    return reasons


def self_test_garbage() -> int:
    """Pins the shape detector on synthetic paths (standard library only)."""
    def word(start, letters, step):
        return [(start + k * step, start + k * step + 1, 0.0, 0.0) for k in range(letters)]

    def line(starts_letters, step):
        return [word(s, n, step) for s, n in starts_letters]

    cd = 4.0                                            # song median: 4 frames (80 ms) a letter
    sung = line([(0, 4), (20, 5), (44, 3)], 4)          # letters 4 frames apart: rate 53/48 = 1.10
    crammed = line([(0, 4), (20, 5), (44, 3)], 1)       # every word in consecutive frames, spread out: rate 0.98
    two_of_three = [word(0, 4, 1), word(6, 5, 1), word(22, 3, 4)]   # rate 31/48 = 0.65
    one_of_three = [word(0, 4, 1), word(20, 5, 4), word(44, 3, 4)]  # a single crammed word is kept
    fast = line([(0, 4), (9, 5), (20, 3)], 2)           # blanks between letters, but rate 25/48 = 0.52
    brisk = line([(0, 4), (16, 5), (36, 3)], 3)         # rate 43/48 = 0.90: not fast, not slow
    slow = line([(0, 4), (40, 5), (80, 3)], 6)          # rate 93/48 = 1.94: a drawn-out line
    cases = [
        ("sung at the song's pace", sung, None, []),
        ("crammed words", crammed, None, ["crammed"]),
        ("two of three crammed", two_of_three, None, ["crammed"]),
        ("one crammed word", one_of_three, None, []),
        ("sub-median pace", fast, None, ["fast"]),
        ("piled late after its stamp", brisk, 50, ["late"]),
        ("late by exactly 0.8 s", brisk, 40, ["late"]),
        ("late by less than 0.8 s", brisk, 39, []),
        ("late but slow", slow, 50, []),
        ("late at the song's pace", sung, 50, []),
        ("late, not a section opener", brisk, None, []),
        ("crammed, fast and late", fast[:1] + [word(9, 5, 1), word(20, 3, 1)], 60, ["crammed", "fast", "late"]),
    ]
    failed = 0
    for name, path, late, want in cases:
        got = garbage_reasons(path_shape(path, cd), late)
        if got != want:
            failed += 1
            print(f"FAIL {name}: got {got}, expected {want} (shape {path_shape(path, cd)})")
    print(f"garbage self-test: {len(cases) - failed}/{len(cases)}")
    return 1 if failed else 0


def line_word_spans(per_word, lines, i) -> list:
    """Line i's placed words in order, each its list of char spans."""
    return [per_word[(i, wi)] for wi in range(len(lines[i].words)) if (i, wi) in per_word]


# --------------------------------------------------------------------------
# Version 6 decoders: shared helpers
# --------------------------------------------------------------------------
#
# Ported from bench/exp/v6/impl_v6.py (PORTING.txt is the spec) with no change of behaviour: on the
# ranked corpus the port reproduces bench/exp/v6's word starts on every map, variant and evidence
# tier. Two decoders, both numpy only, both bounded in memory and time per call and neither
# ever raising: the AUTO decoder for plain lyrics (align_auto) and the STAMPED decoder for any file
# with a stamp (align_ref). Nothing is kept between calls (the pacing prior's pyphen dictionary
# aside), and the emissions and the voiced mask passed in are never written.
#
# Two calibration dependencies every constant below was tuned on, so do NOT "fix" either without
# re-tuning them all (bench/exp/v6/PORTING.txt section 11): the '*' column is the constant -ln 2 at
# every frame (the double log_softmax of compute_emissions produces it and fuse_views keeps it), and
# "margin" (read_spans, line_margin_of) is exp(lp - frame max) where that max is always the '*'
# column, so margin = 2 x the model's own letter probability, not a margin over the runner-up.
# Calibrated on both: AUTO EDGE_BONUS, SV, STAR_A, STAR_CAP, PACE_MARGIN, DEAD_MARGIN; REF ANCH_M,
# LW_MARGIN, LEAD_MARGIN, ONSET_MARGIN; and median_char_dur_frames' 0.3.

def voiced_bool(voiced, T):
    """The RMS voiced mask as a bool array of exactly T frames (False past its end)."""
    v = np.zeros(T, dtype=bool)
    m = min(T, len(voiced))
    v[:m] = np.asarray(voiced[:m], dtype=bool)
    return v


def unvoiced_letter_cost(voiced_b, nats, dtype):
    """Per frame, what a letter pays for sitting there: `nats` where the RMS mask calls the frame
    unvoiced, else 0. Subtracted from the letter's log score (auto: AUTO LETTER_U, float64;
    stamped: REF LU and SP_LU, float32)."""
    return np.where(voiced_b, 0.0, float(nats)).astype(dtype)


def read_spans(runs, lab, owner, L, fmax):
    """Decoded spans -> {(li, wi): [(start_f, end_f, prob, margin), ...]} in lyric order.
    runs: (k, a, b) per decoded state or target k, frames [a, b) global. lab[k]: the emission column
    k reads; owner[k]: its (line, word) or None (skipped). prob = mean exp(log prob of the column),
    margin = mean exp(log prob - frame max), both read off the UNBIASED emissions L (a numpy array or
    a torch tensor, whichever the caller decoded; fmax = L's per-frame max), exactly as version 5
    read them. The auto decoder hands in its float64 copy, the stamped one the float32 tensor: keep
    it that way, the published numbers depend on it."""
    out = {}
    for k, a, b in runs:
        own = owner[k]
        if own is None:
            continue
        col = L[a:b, int(lab[k])]
        if isinstance(col, np.ndarray):
            prob, margin = np.exp(col).mean(), np.exp(col - fmax[a:b]).mean()
        else:
            prob, margin = col.exp().mean(), (col - fmax[a:b]).exp().mean()
        out.setdefault(own, []).append((a, b, float(prob), float(margin)))
    return out


def estimate_pace(per_word, lines, voiced):
    """The song's frames per letter: median (line span / letters) over the confident lines of a
    first pass (mean letter margin >= PACE_MARGIN, >= PACE_CHARS letters), blended geometrically with
    voiced frames / letters / VOICED_DIV when there are fewer than PACE_NLINES of them; clamped to
    PACE_MIN..PACE_MAX. Guarded against an empty mask and zero spans."""
    ests = []
    for i, ln in enumerate(lines):
        letters = [sp for wi in range(len(ln.words)) if (i, wi) in per_word for sp in per_word[(i, wi)]]
        if len(letters) >= AUTO["PACE_CHARS"] and np.mean([sp[3] for sp in letters]) >= AUTO["PACE_MARGIN"]:
            e = (letters[-1][1] - letters[0][0]) / len(letters)
            if e > 0:
                ests.append(e)
    ntot = sum(len(w.norm) for ln in lines for w in ln.words if not w.untimed)
    pv = max(1e-6, float(np.asarray(voiced, dtype=bool).sum()) / max(1, ntot) / AUTO["VOICED_DIV"])
    k = len(ests)
    if k >= AUTO["PACE_NLINES"]:
        p = float(np.median(ests))
    elif k:
        w = k / AUTO["PACE_NLINES"]
        p = float(np.exp(w * np.log(np.median(ests)) + (1 - w) * np.log(pv)))
    else:
        p = pv
    return min(AUTO["PACE_MAX"], max(AUTO["PACE_MIN"], p))


# --------------------------------------------------------------------------
# Version 6 decoders: AUTO (plain lyrics), the duration-aware banded Viterbi
# --------------------------------------------------------------------------
#
# THE DECODER (decode) runs the CTC graph of build_targets ('*' before, between and after the lines)
# as a max-product Viterbi, one vector step per frame, with costs torchaudio's forced_align cannot
# express:
#   letters        lp(letter) - LETTER_U at frames the RMS voiced mask calls unvoiced
#   in-line blank  a counted state: free for GK x pace frames, then GAP_G nats per frame
#   letter->letter a direct step with no blank between two letters of a line costs SKIP_C
#   inner '*'      a counted state over VOICED frames: SV x pace of them free, then STAR_A nats per
#                  voiced frame, never more than STAR_A x STAR_CAP x pace in all (the cap: a chorus the
#                  lyrics write once can still be skipped; STAR_CAP_UNIQ = 0 instead, no voiced cost at
#                  all, when the lyrics repeat almost no line); a line break inside continuous singing
#                  (an inner '*' with no unvoiced frame) pays LB once
#   edge '*'       the leading and trailing '*' pay nothing; the trailing one earns EDGE_BONUS per voiced
#                  frame (voiced audio the lyrics do not list belongs outside the lyric span), the
#                  leading one LEAD_BONUS (0: of two equally good placements, e.g. a chorus the lyrics
#                  write once, the earliest wins); a '*' beside a line with no alignable letters ("&&&",
#                  "...") is priced like the trailing one
# The blanks next to every '*' are removed, so the gap between two lines is always the '*'.
#
# AUTO = pass 1 (letter voicing cost only) -> pace (frames per letter: median span / letters of the
# confident lines, blended with voiced frames / letters when there are few) -> pass 2 with every
# duration constant scaled by the pace. COMPACTION GUARD: when the edge bonus makes COMPACT_GUARD or
# more of the lines (>= 6 letters) newly faster than half the measured pace, it is squeezing blind
# lyrics to earn voiced frames (a slow choral song with no evidence), and pass 2 is redone without it.
#
# BOUNDED RESOURCES. Each decode runs inside a band of states per frame: BAND_HW targets either side of
# a guide (pass 1: the targets spread in proportion to the voiced frames; pass 2: the pass-1 path). A
# pass-1 path that runs along its band's edge is decoded again around itself (at most twice).
# Backpointers are stored for the band only, so memory and time are linear in song length. The band
# is narrowed further when it would exceed BUDGET_MB of backpointers or BUDGET_SF state-frames, and
# the CALL is bounded too: all its decodes together use at most BUDGET_CALL_SF state-frames (the
# recentres, a retry and the guard's alternative run only when they fit; without room for pass 2 the
# pass-1 path is the result). None of the resource constants binds on the corpus.
#
# FALLBACKS. A decode with no path (lyrics longer than the audio can hold) or any other failure
# (memory included) lays the words out evenly over the voiced audio (or over all frames); every
# alignable word is placed, inside the audio, in lyric order, and no pile of words at the last frame
# can be nudged past the end by the output validator. Lines the fallbacks placed, and lines whose
# letters' margin is below DEAD_MARGIN, are flagged estimated (version 5's semantics).
#
# The constants were tuned out of sample on the fast tier, on two halves of the corpus split by set id
# (half A the even ids, half B the odd; pooled auto within-200 83.11, A 86.74, B 79.43); the neighbours
# quoted are pooled scores. Auto is the sensitive decoder (about 1 point on half B per half-step of
# LETTER_U or EDGE_BONUS),
# so a change of the int8 engine, the thread count or the model must be re-checked on auto.
AUTO = dict(
    LETTER_U=1.5,        # nats per unvoiced frame on a letter (1.0: 82.62, 2.0: 82.89; half A would pick
                         # 1.0 by 0.03, which costs half B 1.03, so not a plateau: kept at 1.5)
    GK=3.0,              # in-line blank free for GK x pace frames ... (2.5 / 4: 82.82; both halves pick 3)
    GAP_G=0.15,          # ... then this many nats per frame (0.1: 83.07, 0.3: 82.65)
    SKIP_C=3.0,          # direct letter-to-letter step (1.5: 82.99, 6: 82.62)
    SV=25.0,             # inner '*': SV x pace voiced frames free ... (15: 82.81, 40: 82.70)
    STAR_A=0.2,          # ... then this many nats per voiced frame ... (0.1: 82.50, 0.3: 83.09)
    STAR_CAP=50.0,       # ... charged for at most STAR_CAP x pace voiced frames (None: no cap; 10: 82.50,
                         # 25: 82.63, 100: 83.10, none: 83.14; half B picks 50). Complete lyrics only:
    STAR_CAP_UNIQ=0.0,   # the cap instead when the lyrics repeat (almost) no line (an abbreviated sheet: a
                         # chorus written once is sung several times, so skipping its unlisted repeats
                         # must be cheap): 0 = the inner voiced '*' is free (abbreviated lyrics 80.71;
                         # 5: 80.49, 50: 78.45); complete lyrics are identical on all 85 maps
    UNIQ_DUP=0.1,        # ... "almost no line": at most this share of the lines repeats an earlier one
    LB=3.0,              # an inner '*' that covers no unvoiced frame pays this once (0: 82.37, 6: 82.87)
    EDGE_BONUS=0.05,     # nats per voiced frame credited to the trailing '*' (and EMPTY_EDGE ones). A
                         # PEAK, not a plateau: 0 / 0.025 / 0.1 cost half B 1.67 / 1.61 / 0.37
    LEAD_BONUS=0.0,      # the same for the leading '*' (0: lyrics start as early as the evidence allows;
                         # 0.05: 82.53, half B -1.15)
    EMPTY_EDGE=True,     # the '*' beside a line with no alignable letters is priced like an edge
    COMPACT_GUARD=0.3,   # drop the edge bonus when it pushes this share of lines under half pace (off:
                         # 83.07; 0.2 to 0.4 identical)
    PACE_MARGIN=0.25,    # pace: confident lines have mean letter margin >= this ... (0.15: 83.03)
    PACE_CHARS=6,        # ... and at least this many letters
    PACE_NLINES=5,       # with fewer confident lines, blend with the voiced estimate
    VOICED_DIV=1.2,
    PACE_MIN=3.0,
    PACE_MAX=30.0,
    BAND_HW=600,         # band half-width in targets (None: exact whenever the budgets allow); the exact
                         # paths stay within 400 targets of their guides on the corpus
    BUDGET_MB=200.0,     # backpointer bytes per decode
    BUDGET_SF=1.0e8,     # state-frames per decode (the time budget of one decode)
    BUDGET_CALL_SF=2.5e8,  # state-frames all the decodes of one align call may use together (~20 s)
    BAND_MIN=50,         # narrowest band (targets either side) the budgets may impose
    DENSE_MAX=0.5,       # more targets than this share of the frames (2.5x the densest real song): no
                         # decode, the even layout
    BAND=None,           # testing only: force this band half-width
    DEAD_MARGIN=0.08,    # a line whose letters' mean margin is below this is flagged estimated (version
                         # 5's DEAD_MARGIN, same value and units; it moves no time)
)

ANEG = -1e30              # the auto decoder's float64 "minus infinity"


class Infeasible(RuntimeError):
    pass


# --- the banded decoder

def graph(char_ids, star_id):
    n = len(char_ids)
    S = 2 * n + 1
    tok = np.zeros(S, dtype=np.int64)
    is_star = np.zeros(S, dtype=bool)
    is_letter = np.zeros(S, dtype=bool)
    skip = np.zeros(S, dtype=bool)
    for k, cid in enumerate(char_ids):
        s = 2 * k + 1
        tok[s] = cid
        is_star[s] = cid == star_id
        is_letter[s] = cid != star_id
        skip[s] = k > 0 and char_ids[k - 1] != cid
    inb = np.zeros(S, dtype=bool)
    for k in range(1, n):
        if char_ids[k - 1] != star_id and char_ids[k] != star_id:
            inb[2 * k] = True
    return S, tok, is_star, is_letter, skip, inb


def band_halfwidth(T, n_targets):
    """None when the exact decode of T frames x n_targets fits the budgets, else the band
    half-width in targets that does. Raises Infeasible when not even the narrowest band fits."""
    S = 2 * n_targets + 1
    bps = 2.0 if T < 65535 else 3.0                        # backpointer bytes per state, about
    budget_b = AUTO["BUDGET_MB"] * 1e6
    if T * S * bps <= budget_b and T * S <= AUTO["BUDGET_SF"]:
        return None
    w = min(budget_b / (T * bps), AUTO["BUDGET_SF"] / T)      # states
    hw = int(w // 4)                                       # half-width in targets
    if hw < AUTO["BAND_MIN"]:
        raise Infeasible(f"no band fits the budget ({T} frames x {S} states)")
    return hw


def make_band(center, hw, n_targets):
    """State band [lo, hi) per frame around a per-frame target index `center` (float, T values),
    hw targets either side, monotone. The centre is first clipped into the feasible corridor (target k
    can be live at frame t only when k <= t and n - 1 - k <= T - 1 - t), so a guide that runs ahead of
    or behind every possible path (lyrics packed against the audio) still gives a band that holds one."""
    S = 2 * n_targets + 1
    c = np.asarray(center, dtype=np.float64)
    tt = np.arange(len(c), dtype=np.float64)
    c = np.clip(c, n_targets - len(c) + tt, tt)
    c = np.maximum.accumulate(c)
    lo = np.clip(2 * np.floor(c - hw).astype(np.int64), 0, S)
    hi = np.clip(2 * np.ceil(c + hw).astype(np.int64) + 3, 0, S)
    return np.maximum.accumulate(lo), np.maximum.accumulate(hi)


def cells_of(T, n_targets, band):
    """State-frames (the time and backpointer measure) a decode of T frames x n_targets would visit,
    inside `band` (None: the exact decode), exactly as decode() bounds its frames."""
    S = 2 * n_targets + 1
    tt = np.arange(T)
    lo = np.maximum(0, 2 * (n_targets - T + tt) + 1)
    hi = np.minimum(S, 2 * tt + 2)
    if band is not None:
        lo = np.maximum(lo, band[0])
        hi = np.minimum(hi, band[1])
        hi = np.maximum(hi, np.minimum(lo + 1, S))
    return float(np.maximum(hi - lo, 0).sum())


def voiced_guide(voiced, n_targets):
    """Per frame, the target index the lyrics would reach if their targets were spread in
    proportion to the voiced frames (a guide for the band of a long song's first pass)."""
    v = np.asarray(voiced, dtype=np.float64)
    cv = np.cumsum(v) / max(1.0, v.sum()) if v.sum() > 0 else np.linspace(0, 1, len(v))
    return cv * (n_targets - 1)


def fast_share(spans, owners, pace):
    """Share of lines (>= 6 letters) whose span is under half the song's pace per letter."""
    first, last, cnt = {}, {}, {}
    for k, own in enumerate(owners):
        if own is None:
            continue
        li = own[0]
        first.setdefault(li, spans[k][0])
        last[li] = spans[k][1]
        cnt[li] = cnt.get(li, 0) + 1
    r = [(last[i] - first[i]) / cnt[i] < 0.5 * pace for i in cnt if cnt[i] >= 6]
    return float(np.mean(r)) if r else 0.0


def touches(spans, band, T, n_targets):
    """True when a banded path runs along its band's edge somewhere (the band may have cut it)."""
    S = 2 * n_targets + 1
    st = 2 * path_center(spans, T) + 1
    lo, hi = band
    return bool(np.any((lo > 0) & (st < lo + 4)) or np.any((hi < S) & (st > hi - 5)))


def path_center_mid(band, T):
    """The per-frame target index at the middle of a band (to widen it in place)."""
    return (band[0] + band[1]) / 4.0


def path_center(spans, T):
    """Per frame, the target index of a decoded path (blank frames between two targets: k + 0.5)."""
    p = np.zeros(T)
    for k, (a, b) in enumerate(spans):
        p[a:b] = k
        if k + 1 < len(spans):
            p[b:spans[k + 1][0]] = k + 0.5
    return p


def decode(lp, char_ids, star_id, voiced, letter_u=0.0, gap_k=0, gap_g=0.0, skip_c=0.0,
           star_v=0, star_a=0.0, star_cap=None, edge_bonus=0.0, lead_bonus=0.0, lb=0.0, empty_edge=False,
           band=None):
    """lp [T, V] float64 (align_lyrics' doubly normalised emissions, '*' column included, read
    only); char_ids from build_targets (starting and ending with '*'). Returns one (start_f, end_f)
    span per target.
    band: optional (lo, hi) state arrays per frame (make_band) to decode inside; without it the
    decode is exact. Raises Infeasible when there is no path."""
    T = lp.shape[0]
    n = len(char_ids)
    if n == 0:
        return []
    if char_ids[0] != star_id or char_ids[-1] != star_id:
        raise ValueError("targets must start and end with '*'")
    if T < n:
        raise Infeasible("fewer frames than targets")
    S, tok, is_star, is_letter, skip, inb = graph(char_ids, star_id)
    B_idx = np.nonzero(inb)[0]
    St_idx = np.nonzero(is_star)[0]
    L_idx = np.nonzero(is_letter)[0]
    nB, nS, nL = len(B_idx), len(St_idx), len(L_idx)

    v = voiced_bool(voiced, T)
    upen = unvoiced_letter_cost(v, letter_u, np.float64)
    L_tok = tok[L_idx]
    eb = lp[:, 0]
    Cb = np.concatenate([[0.0], np.cumsum(eb)])            # Cb[t + 1] = sum of eb[0..t]
    es = lp[:, star_id]
    Cs = np.concatenate([[0.0], np.cumsum(es)])            # star emission, cumulative
    Cv = np.concatenate([[0.0], np.cumsum(v.astype(np.float64))])
    Cst = np.stack([Cs, Cs + lead_bonus * Cv, Cs + edge_bonus * Cv])   # inner, leading, trailing

    # in-line blanks: entries of the last K frames are fresh (free), older ones long (gap_g/frame);
    # sliding max of the fresh entries by blocks of K (van Herk / Gil-Werman)
    K = int(max(1, gap_k)) if gap_g > 0 else 1
    Hcur = np.full((nB, K), ANEG)
    Hprev = np.full((nB, K), ANEG)
    sufV = np.full((nB, K + 1), ANEG)
    sufA = np.zeros((nB, K + 1), dtype=np.int64)
    preV = np.full(nB, ANEG)
    preA = np.zeros(nB, dtype=np.int64)
    arK = np.arange(K)
    Lval = np.full(nB, ANEG)
    Lent = np.zeros(nB, dtype=np.int64)
    Bval_all = np.full(nB, ANEG)
    wr_cur = [nB, 0]                                       # Hcur rows written in this block
    wr_prev = [nB, 0]                                      # Hprev rows (written in the last block)

    # '*': sub-state = voiced frames swallowed so far (0..V, the top one merged and paying star_a
    # per voiced frame). Stored values exclude the cumulative star emission (Cst: plus the bonus
    # for the edge kinds), so a sub-state carries the entry score and moves only on voiced frames.
    V = int(max(0, star_v)) if star_a > 0 else 0
    M = V + 1
    if M == 2:
        M = 3                                              # keep a distinct count-1 sub-state
    top_rate = star_a if star_a > 0 else 0.0
    # the cap: a '*' may instead pay star_a x C once, at entry, into a free sub-state (column M);
    # the Viterbi max over both gives exactly star_a x clip(count - V, 0, C)
    use_sat = star_cap is not None and star_a > 0 and M > 1
    sat_cost = star_a * int(max(0, star_cap)) if use_sat else 0.0
    is_edge = np.zeros(nS, dtype=bool)
    kind = np.zeros(nS, dtype=np.int64)
    if nS:
        is_edge[0] = True
        is_edge[-1] = True
        kind[-1] = 2
        kind[0] = 1
        if empty_edge:
            # a '*' next to another '*' stands beside a line with no alignable letters ("...", a
            # symbol line): the lyrics mark unlisted audio there, so it is priced like an edge
            ks = (St_idx - 1) // 2
            nb = np.zeros(nS, dtype=bool)
            nb[:-1] |= np.diff(ks) == 1
            nb[1:] |= np.diff(ks) == 1
            inner = nb & ~is_edge
            is_edge[inner] = True
            kind[inner] = 2
    lbv = np.where(is_edge, 0.0, lb)
    trv = np.where(is_edge, 0.0, top_rate)                # the edge '*' pay no voiced cost
    satc = np.where(is_edge, 0.0, sat_cost)
    use_lb = lb > 0
    Z1 = np.full((nS, M + 1), ANEG)                         # seen an unvoiced frame (or lb off)
    Z1E = np.zeros((nS, M + 1), dtype=np.int64)            # (column M: the capped sub-state)
    if use_lb:
        Z0 = np.full((nS, M + 1), ANEG)                     # every frame so far voiced
        Z0E = np.zeros((nS, M + 1), dtype=np.int64)
    Sval_all = np.full(nS, ANEG)

    # bands
    odt = np.uint16 if T < 65535 else np.uint32
    tt = np.arange(T)
    lo_t = np.maximum(0, 2 * (n - T + tt) + 1)
    hi_t = np.minimum(S, 2 * tt + 2)
    if band is not None:
        lo_t = np.maximum(lo_t, band[0])
        hi_t = np.minimum(hi_t, band[1])
        hi_t = np.maximum(hi_t, np.minimum(lo_t + 1, S))
    aL = np.searchsorted(L_idx, lo_t); bL = np.searchsorted(L_idx, hi_t)
    aB = np.searchsorted(B_idx, lo_t); bB = np.searchsorted(B_idx, hi_t)
    aS = np.searchsorted(St_idx, lo_t); bS = np.searchsorted(St_idx, hi_t)
    oL = np.concatenate([[0], np.cumsum(bL - aL)])
    oB = np.concatenate([[0], np.cumsum(bB - aB)])
    oS = np.concatenate([[0], np.cumsum(bS - aS)])
    capL, capB, capS = int(oL[-1]), int(oB[-1]), int(oS[-1])
    bpL = np.zeros(capL, dtype=np.uint8)
    offB = np.zeros(capB, dtype=odt)
    offS = np.zeros(capS, dtype=odt)

    Lm2 = L_idx - 2
    Bm1 = B_idx - 1
    prev_is_letter = np.zeros(nL, dtype=bool)
    prev_is_letter[:] = is_letter[np.maximum(Lm2, 0)] & (Lm2 >= 0)
    skipmask = np.where(skip[L_idx], np.where(prev_is_letter, -skip_c, 0.0), ANEG)
    delta = np.full(S, ANEG)
    prev_lo = 0

    for t in range(T):
        a_l, b_l, a_b, b_b, a_s, b_s = aL[t], bL[t], aB[t], bB[t], aS[t], bS[t]
        lo = lo_t[t]
        if lo > prev_lo:
            delta[prev_lo:lo] = ANEG
            prev_lo = lo

        # ---- letters
        Li = L_idx[a_l:b_l]
        if t == 0:
            newL = np.full(b_l - a_l, ANEG)
            argL = np.zeros(b_l - a_l, dtype=np.uint8)
        else:
            a0 = delta[Li]
            a1 = delta[Li - 1]
            a2 = delta[Li - 2] + skipmask[a_l:b_l]
            argL = (a1 > a0).astype(np.uint8)
            newL = np.maximum(a0, a1)
            m2 = a2 > newL
            argL[m2] = 2
            np.maximum(newL, a2, out=newL)
        eL = lp[t, L_tok[a_l:b_l]] - upen[t]
        newL += eL
        bpL[oL[t]:oL[t + 1]] = argL

        # ---- stars
        if b_s > a_s:
            sl = slice(a_s, b_s)
            si = St_idx[sl]
            if t == 0:
                entry = np.where(si == 1, 0.0, ANEG)
            else:
                entry = np.where(si >= 3, delta[np.maximum(si - 2, 0)], ANEG)
            base = Cst[kind[sl], t]                           # stored = actual - cum emission
            ent = entry - base
            if M == 1:
                if use_lb and v[t]:
                    tk = ent > Z0[sl][:, 0]
                    Z0[sl, 0] = np.where(tk, ent, Z0[sl][:, 0])
                    Z0E[sl, 0] = np.where(tk, t, Z0E[sl][:, 0])
                else:
                    if use_lb:
                        Y, YE = Z0[sl], Z0E[sl]
                        tk = Y[:, 0] > Z1[sl][:, 0]
                        Z1[sl, 0] = np.where(tk, Y[:, 0], Z1[sl][:, 0])
                        Z1E[sl, 0] = np.where(tk, YE[:, 0], Z1E[sl][:, 0])
                        Z0[sl] = ANEG
                    tk = ent > Z1[sl][:, 0]
                    Z1[sl, 0] = np.where(tk, ent, Z1[sl][:, 0])
                    Z1E[sl, 0] = np.where(tk, t, Z1E[sl][:, 0])
            elif v[t]:
                if use_lb:
                    groups = ((Z1, Z1E), (Z0, Z0E))
                else:
                    groups = ((Z1, Z1E),)
                for gi, (ZZ, ZZE) in enumerate(groups):
                    Zs, ZEs = ZZ[sl], ZZE[sl]
                    tr = trv[sl]
                    topv = np.maximum(Zs[:, M - 2], Zs[:, M - 1] - tr)
                    topE = np.where(Zs[:, M - 2] >= Zs[:, M - 1] - tr, ZEs[:, M - 2], ZEs[:, M - 1])
                    Zs[:, 2:M - 1] = Zs[:, 1:M - 2].copy()
                    ZEs[:, 2:M - 1] = ZEs[:, 1:M - 2].copy()
                    Zs[:, M - 1] = topv
                    ZEs[:, M - 1] = topE
                    if use_lb and gi == 0:
                        Zs[:, 1] = Zs[:, 0]
                        ZEs[:, 1] = ZEs[:, 0]
                    else:
                        tk = ent > Zs[:, 0]
                        Zs[:, 1] = np.where(tk, ent, Zs[:, 0])
                        ZEs[:, 1] = np.where(tk, t, ZEs[:, 0])
                    Zs[:, 0] = ANEG
                    if use_sat and not (use_lb and gi == 0):
                        es_ = ent - satc[sl]
                        tk = es_ > Zs[:, M]
                        Zs[:, M] = np.where(tk, es_, Zs[:, M])
                        ZEs[:, M] = np.where(tk, t, ZEs[:, M])
                    ZZ[sl] = Zs
                    ZZE[sl] = ZEs
            else:
                Zs, ZEs = Z1[sl], Z1E[sl]
                if use_lb:
                    Y, YE = Z0[sl], Z0E[sl]
                    tk = Y > Zs
                    Zs = np.where(tk, Y, Zs)
                    ZEs = np.where(tk, YE, ZEs)
                    Z0[sl] = ANEG
                tk = ent > Zs[:, 0]
                Zs[:, 0] = np.where(tk, ent, Zs[:, 0])
                ZEs[:, 0] = np.where(tk, t, ZEs[:, 0])
                if use_sat:
                    es_ = ent - satc[sl]
                    tk = es_ > Zs[:, M]
                    Zs[:, M] = np.where(tk, es_, Zs[:, M])
                    ZEs[:, M] = np.where(tk, t, ZEs[:, M])
                Z1[sl] = Zs
                Z1E[sl] = ZEs
            Zs, ZEs = Z1[sl], Z1E[sl]
            W1 = Zs
            zb = W1.argmax(axis=1)
            rr = np.arange(b_s - a_s)
            sv = W1[rr, zb]
            se = ZEs[rr, zb]
            if use_lb:
                W0 = Z0[sl] - lbv[sl][:, None]
                zb0 = W0.argmax(axis=1)
                sv0 = W0[rr, zb0]
                t0 = sv0 > sv
                sv = np.where(t0, sv0, sv)
                se = np.where(t0, Z0E[sl][rr, zb0], se)
            sval = sv + Cst[kind[sl], t + 1]
            sval = np.where(sval < ANEG / 2, ANEG, sval)
            Sval_all[sl] = sval
            offS[oS[t]:oS[t + 1]] = t - se

        # ---- in-line blanks
        if nB and b_b > a_b:
            pos = t % K
            if pos == 0 and t > 0:
                Hprev, Hcur = Hcur, Hprev
                wr_prev, wr_cur = wr_cur, wr_prev
                if wr_cur[1] > wr_cur[0]:
                    Hcur[wr_cur[0]:wr_cur[1]] = ANEG
                wr_cur = [nB, 0]
                r0 = a_b
                r1 = min(nB, int(np.searchsorted(B_idx, min(S, hi_t[t] + 2 * K + 2))))
                r0 = min(r0, r1)
                if r1 > r0:
                    rev = Hprev[r0:r1, ::-1]
                    cm = np.maximum.accumulate(rev, axis=1)
                    idx = np.maximum.accumulate(np.where(rev == cm, arK[None, :], 0), axis=1)
                    sufV[r0:r1, :K] = cm[:, ::-1]
                    sufA[r0:r1, :K] = (K - 1 - idx)[:, ::-1] + (t - K)
                preV[:] = ANEG
            sl = slice(a_b, b_b)
            if t > 0:
                old = Hprev[sl, pos] + Cb[t]
                lv = Lval[sl]
                takeL = old > lv
                lv = np.maximum(lv, old)
                Lent[sl] = np.where(takeL, t - K, Lent[sl])
                Lval[sl] = lv + eb[t] - gap_g
                new = delta[Bm1[sl]] - Cb[t]
            else:
                Lval[sl] = Lval[sl] + eb[t]
                new = np.full(b_b - a_b, ANEG)
            Hcur[sl, pos] = new
            wr_cur = [min(wr_cur[0], a_b), max(wr_cur[1], b_b)]
            pv = preV[sl]
            takeP = new > pv
            preV[sl] = np.maximum(pv, new)
            preA[sl] = np.where(takeP, t, preA[sl])
            svv = sufV[sl, pos + 1]
            useS = svv > preV[sl]
            fres = np.where(useS, svv, preV[sl]) + Cb[t + 1]
            freA = np.where(useS, sufA[sl, pos + 1], preA[sl])
            lv = Lval[sl]
            useL = lv > fres
            bval = np.where(useL, lv, fres)
            bval = np.where(bval < ANEG / 2, ANEG, bval)
            Bval_all[sl] = bval
            offB[oB[t]:oB[t + 1]] = t - np.where(useL, Lent[sl], freA)

        # ---- commit
        delta[Li] = np.where(newL < ANEG / 2, ANEG, newL)
        if b_s > a_s:
            delta[St_idx[a_s:b_s]] = Sval_all[a_s:b_s]
        if nB and b_b > a_b:
            delta[B_idx[a_b:b_b]] = Bval_all[a_b:b_b]

    s = S - 2
    if delta[s] <= ANEG / 2:
        raise Infeasible("no path")
    Lpos = np.full(S, -1)
    Lpos[L_idx] = np.arange(nL)
    Spos = np.full(S, -1)
    Spos[St_idx] = np.arange(nS)
    Bpos = np.full(S, -1)
    Bpos[B_idx] = np.arange(nB)
    starts = np.full(n, -1, dtype=np.int64)
    ends = np.full(n, -1, dtype=np.int64)
    t = T - 1
    while t >= 0:
        if is_star[s]:
            j = Spos[s]
            tau = t - int(offS[oS[t] + j - aS[t]])
            k = (s - 1) // 2
            starts[k], ends[k] = tau, t + 1
            t, s = tau - 1, s - 2
        elif inb[s]:
            j = Bpos[s]
            tau = t - int(offB[oB[t] + j - aB[t]])
            t, s = tau - 1, s - 1
        else:
            j = Lpos[s]
            k = (s - 1) // 2
            if ends[k] < 0:
                ends[k] = t + 1
            starts[k] = t
            a = int(bpL[oL[t] + j - aL[t]])
            t, s = t - 1, s - a
    if (starts < 0).any() or s != -1:
        raise Infeasible("backtrace did not reach the first target")
    return [(int(a), int(b)) for a, b in zip(starts, ends)]


def to_per_word(spans, owners, lp, char_ids):
    """One span per target (frames of lp, float64) -> per_word, via the shared read_spans."""
    return read_spans(((k, a, b) for k, (a, b) in enumerate(spans)), char_ids, owners, lp, lp.max(axis=1))


def dup_share(lines):
    """Share of the lines (with alignable letters) whose text repeats an earlier line's. A lyric sheet
    that repeats (almost) no line writes each chorus once while the song sings it several times."""
    seen, dup, tot = set(), 0, 0
    for ln in lines:
        key = " ".join(w.norm for w in ln.words if not w.untimed and w.norm)
        if not key:
            continue
        tot += 1
        dup += key in seen
        seen.add(key)
    return dup / tot if tot else 0.0


def duration_costs(pace, star_cap=None):
    """The pass-2 costs at the song's pace. star_cap: the voiced-'*' cap in pace units (default AUTO
    STAR_CAP; align_auto passes STAR_CAP_UNIQ for lyrics that repeat almost no line)."""
    eb = AUTO["EDGE_BONUS"]
    cap = AUTO["STAR_CAP"] if star_cap is None else star_cap
    return dict(empty_edge=AUTO["EMPTY_EDGE"], letter_u=AUTO["LETTER_U"], gap_k=int(round(AUTO["GK"] * pace)), gap_g=AUTO["GAP_G"],
                skip_c=AUTO["SKIP_C"], star_v=int(round(AUTO["SV"] * pace)), star_a=AUTO["STAR_A"],
                star_cap=None if cap is None else int(round(cap * pace)),
                edge_bonus=eb, lead_bonus=AUTO["LEAD_BONUS"], lb=AUTO["LB"])


# --- fallbacks and helpers

def spread(items, f0, f1, voiced, per_word):
    """Words items = [(li, wi, letters)] laid out in order over [f0, f1) (0 <= f0 < f1 <= T):
    one letter per frame step over the voiced frames when they are enough, else over all frames,
    else (fewer frames than letters) word starts spread evenly, a word's letters packed behind its
    start. Used only where no decode placed the words."""
    N = sum(c for *_, c in items)
    if N == 0:
        return
    f0 = max(0, int(f0))
    f1 = max(int(f1), f0 + 1)
    fr = np.nonzero(np.asarray(voiced[f0:f1], dtype=bool))[0] + f0
    if len(fr) < N:
        fr = np.arange(f0, f1)
    F = len(fr)
    if F >= N:
        pos = [int(fr[(j * F) // N]) for j in range(N)] + [f1]
        j = 0
        for li, wi, c in items:
            spans = []
            for _ in range(c):
                a = pos[j]
                spans.append((a, max(a + 1, min(pos[j + 1], a + 10, f1)), 0.0, 0.0))
                j += 1
            per_word[(li, wi)] = spans
        return
    W = len(items)
    for i, (li, wi, c) in enumerate(items):
        a = int(fr[(i * F) // W])
        nxt = int(fr[((i + 1) * F) // W]) if i + 1 < W and ((i + 1) * F) // W < F else f1
        room = max(1, nxt - a)
        per_word[(li, wi)] = [(a + (k * room) // c, a + (k * room) // c + 1, 0.0, 0.0) for k in range(c)]


def fill_missing(lines, per_word, T, voiced):
    """Every alignable word the decode did not place (normally none) is spread between the placed
    words around it, so the lyric order is kept."""
    order = [(li, wi, len(w.norm)) for li, ln in enumerate(lines) for wi, w in enumerate(ln.words)
             if not w.untimed and w.norm]
    i = 0
    while i < len(order):
        if (order[i][0], order[i][1]) in per_word:
            i += 1
            continue
        j = i
        while j < len(order) and (order[j][0], order[j][1]) not in per_word:
            j += 1
        f0 = per_word[order[i - 1][:2]][-1][1] if i > 0 else 0
        f1 = per_word[order[j][:2]][0][0] if j < len(order) else T
        f0 = min(max(0, f0), T - 1)
        f1 = min(max(f1, f0 + 1), T)
        spread(order[i:j], f0, f1, voiced, per_word)
        i = j
    return per_word


def clip_spans(per_word, T):
    for k, sps in per_word.items():
        per_word[k] = [(min(max(0, a), T - 1), min(max(b, min(max(0, a), T - 1) + 1), T), p, m)
                       for a, b, p, m in sps]
    return per_word


def settle(per_word, T):
    """Every span inside [0, T), and no pile of word starts at the end of the audio: going backwards
    from the last word, the i-th word from the end starts no later than T - 1 - i // K (K = 10
    words per frame, more only when the words outnumber the frames 10 to 1), so the output
    validator's 1 ms nudges between equal starts never push a word past the end. A no-op unless
    words crowd the last frames."""
    clip_spans(per_word, T)
    keys = sorted(per_word)
    Wn = len(keys)
    if Wn == 0 or T <= 0:
        return per_word
    K = max(10, -(-Wn // T))
    for r, k in enumerate(reversed(keys)):
        lim = max(0, T - 1 - r // K)
        sp = per_word[k]
        if sp[0][0] <= lim:
            continue
        per_word[k] = [(min(a, lim), min(a, lim) + 1, p, m) for a, b, p, m in sp]
    return per_word


# --- auto

def decode_auto(lp, char_ids, owners, lines, vv, star_id, info):
    """Pass 1 (letter voicing cost only) -> pace -> pass 2 (the duration decode), each inside a
    band when the song is long (see the section comment above). Returns the pass-2 spans."""
    T, n = lp.shape[0], len(char_ids)
    if n > AUTO["DENSE_MAX"] * T:
        # 2.5 times the densest real song (0.19 letters per frame): wrong lyrics, and a decode
        # would only prove there is no path at great cost. The even layout of align_auto instead.
        raise Infeasible(f"{n} targets in {T} frames")
    hb = band_halfwidth(T, n)                # None: the exact decode fits one decode's budgets
    if AUTO["BAND"] is not None:
        hw = AUTO["BAND"]
    else:
        hw = AUTO["BAND_HW"] if hb is None else (hb if AUTO["BAND_HW"] is None else min(hb, AUTO["BAND_HW"]))
    if hw is not None and 2 * hw >= n:
        hw = None                            # the band would hold every target: decode exactly
    info["band"] = hw
    # the call's budget: every decode is charged its state-frames; the optional ones (a recentred
    # first pass, a wider retry, the guard's alternative) run only when they fit next to what must
    # still run, so one call never costs more than BUDGET_CALL_SF state-frames
    left = [float(AUTO["BUDGET_CALL_SF"])]
    info["cells"] = 0.0

    def run(band, **kw):
        c = cells_of(T, n, band)
        left[0] -= c
        info["cells"] += c
        return decode(lp, char_ids, star_id, vv, band=band, **kw)

    def fits(band, reserve=0.0):
        return cells_of(T, n, band) <= left[0] - reserve

    def banded(band, reserve=0.0, **kw):
        try:
            return run(band, **kw)
        except Infeasible:
            if band is None:
                raise
            # the band cut every path (lyrics packed against the audio): the exact decode when one
            # decode's budgets allow it, else a band twice as wide but never wider than those budgets
            # allow; and only when the call's budget still holds it
            if hb is None:
                wide = None
            else:
                w = min(2 * hw, hb)
                if w <= hw:
                    raise
                wide = make_band(path_center_mid(band, T), w, n)
            if not fits(wide, reserve):
                raise
            info["retry"] = info.get("retry", 0) + 1
            return run(wide, **kw)

    def band_within(center, hw0, reserve=0.0):
        """A band around `center` of hw0 targets (None: exact), narrowed to fit what the call has
        left after `reserve`; None, False when not even BAND_MIN fits."""
        b = None if hw0 is None else make_band(center, hw0, n)
        if fits(b, reserve):
            return b, True
        w = int((left[0] - reserve) / (4.0 * T))
        if hw0 is not None:
            w = min(w, hw0 - 1)
        if w < AUTO["BAND_MIN"] or 2 * w >= n:
            return None, False
        b = make_band(center, w, n)
        return b, fits(b, reserve)

    band = None if hw is None else make_band(voiced_guide(vv, n), hw, n)
    reserve2 = cells_of(T, n, band)          # pass 2 will cost about what pass 1 does
    s1 = banded(band, reserve=reserve2, letter_u=AUTO["LETTER_U"])
    for _ in range(2):                      # a banded first pass that ran along its band: recentre
        if band is None or not touches(s1, band, T, n):
            break
        nb = make_band(path_center(s1, T), hw, n)
        if not fits(nb, reserve2):
            info["recentre_skipped"] = True
            break
        band = nb
        s1 = banded(band, reserve=reserve2, letter_u=AUTO["LETTER_U"])
    pace = estimate_pace(to_per_word(s1, owners, lp, char_ids), lines, vv)
    info["pace"] = pace
    uniq = dup_share(lines) <= AUTO["UNIQ_DUP"]
    info["uniq"] = uniq
    costs = duration_costs(pace, AUTO["STAR_CAP_UNIQ"] if uniq else None)
    band, ok = band_within(path_center(s1, T), hw)
    if not ok:
        info["pass2"] = "skipped (budget)"
        return s1                           # the first pass is a complete alignment
    spans = banded(band, **costs)
    g = AUTO["COMPACT_GUARD"]
    if g is not None and costs["edge_bonus"] > 0:
        # the edge bonus must not buy its voiced frames by squeezing lines far below the song's
        # measured pace: when it raises the share of lines under half pace by g or more, drop it
        fast_eb = fast_share(spans, owners, pace)
        if fast_eb >= g:
            # the alternative decodes in pass 2's band when the call can afford it,
            # else in a band around the pass-2 path narrowed to what is left, else not at all
            nb = dict(costs, edge_bonus=0.0, lead_bonus=0.0)
            if fits(band):
                gb, ok = band, True
            else:
                gb, ok = band_within(path_center(spans, T), hw if hw is not None else n)
            if ok:
                try:
                    alt = banded(gb, **nb) if gb is band else run(gb, **nb)
                except Infeasible:
                    alt = None
                if alt is not None and fast_eb - fast_share(alt, owners, pace) >= g:
                    spans = alt
                    info["guard"] = True
            else:
                info["guard"] = "skipped (budget)"
    return spans


def align_auto(lines, log_probs, dictionary, star_id, voiced, info=None):
    """
    Plain lyrics (or a stamped file without a single stamp): the AUTO decoder over the whole song.
    Works on a float64 copy of the emissions and reads prob and margin off that copy. Never raises:
    any failure lays the words out evenly over the voiced audio. Sets line.estimated on every line
    a fallback placed and every line whose margin is under AUTO DEAD_MARGIN. `info`, when given,
    receives band / pace / cells / uniq / guard / fallback for the log.
    """
    info = {} if info is None else info
    lp = log_probs.detach().double().numpy()            # read only (a copy unless already float64)
    T = lp.shape[0]
    vv = voiced_bool(voiced, T)
    per_word = {}
    if T > 0:
        try:
            char_ids, owners = build_targets(list(enumerate(lines)), dictionary, star_id)
            if any(o is not None for o in owners):
                spans = decode_auto(lp, char_ids, owners, lines, vv, star_id, info)
                per_word = to_per_word(spans, owners, lp, char_ids)
        except Exception as exc:                 # no path (lyrics longer than the audio holds),
            per_word = {}                        # memory, anything: the even layout below
            info["fallback"] = f"{type(exc).__name__}: {exc}"
        decoded = set(per_word)
        fill_missing(lines, per_word, T, vv)       # every alignable word placed, inside the audio
        # version 5's "estimated" semantics (timing.json "estimated": true, the editor's badge): a line
        # the fallbacks laid out, or one whose letters the evidence barely supports
        for i, ln in enumerate(lines):
            keys = [(i, wi) for wi, w in enumerate(ln.words) if not w.untimed and w.norm]
            if keys and (any(k not in decoded for k in keys)
                         or line_margin_of(per_word, lines, i) < AUTO["DEAD_MARGIN"]):
                ln.estimated = True
    return settle(per_word, T)


# --------------------------------------------------------------------------
# Version 6 decoders: STAMPED, the banded whole-song softwall decoder
# --------------------------------------------------------------------------
#
# A first pass per section (align_window inside [stamp, next stamp)), a per-song stamp error model
# from the section openers (the lead: how late confident openers start after their stamps, robust over
# at least LEAD_MIN_N of them; the onset prior's mu and sigma), a per-line left wall where a wide pass
# (each section in [stamp - 1 s, next stamp + 1 s)) hears a confident opener before its stamp, and a
# song-level LATE-stamp model (the first pass cannot see a negative lead, the wide pass can: when the
# confident openers say the stamps are late, every stamped section's left wall opens by that much).
# Then ONE whole-song Viterbi in which each section's letters live in the band [left wall, next stamp
# + spill), spill = max(0, lead - X), with version 5's pacing prior laid out syllable by syllable, a
# Gaussian onset prior on each stamped section's first letter and LU nats off letters at unvoiced
# frames; then version 4's garbage-path replacement, paced from the stamp plus the robust lead.
#
# SPARSE SECTIONS: a stamped section of 2+ lines is decoded inside the same whole-song chain by
# sparse_piece, a section graph:
#   line 1: c1 [s1 s2] b c2 ... cn | G1..G20 | line 2: ... | TG   (TG = the section's closing '*')
#   * letter spacing (SPACE 2.0 / 0.75 / 0.3): a letter entered straight from the previous letter of
#     its line costs 2.0 nats (only when the two labels differ), after exactly 1 blank frame 0.75,
#     after 2 frames 0.3, after 3 or more nothing (s1, s2 one-frame blanks, b a self-looping blank);
#   * a soft minimum gap between lines: G1..G19 are one-frame '*' states, G20 self-loops; the next
#     line may start after i < GM = 20 frames at GP (1 - i / 20), GP = 8 nats scaled by
#     min(1, the shorter adjacent line's letters / GP_L0 = 10);
#   * letters: the model's log prob, the pacing prior at weight SP_PW laid out syllable-even per line
#     from line boundaries where the stamp (plus the lead) and every line the unbiased first pass heard
#     with margin >= ANCH_M anchor (the others are spread by letters + LSH_K), SP_LU nats off at
#     unvoiced frames; the section opener keeps the softwall treatment.
#
# The whole-song pass (decode_chain) is a banded CTC Viterbi over a chain of pieces: every state has a
# frame band, int8 backpointers (checkpointed above BP_BYTES), float32 arithmetic in torchaudio's
# order and tie rule, and a per-call cell budget (CELL_LIMIT per pass, CELL_BUDGET per call) with
# graceful fallbacks: a window over budget keeps no first pass and is laid out by the garbage
# replacement; a joint pass over budget halves the sparse line bands, then bands unstamped sections
# around an even letter share, then keeps the first pass. Stamps are clamped into the audio; any
# exception falls back to an even layout per section (even_fallback); sanitize keeps every span inside
# the audio and the word starts in lyric order. Every alignable word gets a span.
#
# The constants were tuned on the fast tier on the same two halves of the corpus (half A the even set
# ids, half B the odd); none depends on the evidence tier. The resource constants never bind on the corpus.
REF = {
    "X": 0.15,            # spill = max(0, lead - X) seconds past the next stamp. A TRADE, not a
                          # plateau: 0 gives human stamps +0.10 to +0.15 and costs exact stamps up to 0.16
    "LEAD_MARGIN": 0.3,   # confidence a first-pass opener needs to measure the lead (0.15 to 0.5 flat)
    "ONSET_MARGIN": 0.3,  # the same for the onset prior's mu / sigma
    "LEAD_RANGE": (-0.5, 1.0),  # plausible stamp error, seconds
    "LEAD_MIN_N": 5,      # fewer confident openers than this: lower the margin (0.15, 0.05, any); fewer
                          # at any margin: lead 0 (3 is flat, 1 costs every eighth human stamp 0.23)
    "ONSET_MIN_N": 5,     # the same for the onset prior's mu / sigma (below it: mu 0, sigma SIG_CEIL)
    "ONSET_S": 1.0,       # Gaussian onset prior on each section's first letter: sigma multiplier (0 = off;
                          # 0.5 and 2 worse on both halves)
    "ONSET_CAP": 3.0,     # nats (6 flat, 1.5 worse)
    "SIG_FLOOR": 0.04,    # seconds (0.03 to 0.06 identical)
    "SIG_CEIL": 0.4,      # seconds (untuned)
    "WALL_MAX": 0.4,      # seconds, the most a per-line left wall opens
    "LU": 1.0,            # nats off every letter at a frame the RMS mask calls unvoiced (0 to 1 flat
                          # within 0.03; kept so the published numbers stay exact)
    "LW": True,           # per-line left wall: an opener the wide pass hears confidently (margin
    "LW_MARGIN": 0.4,     # >= LW_MARGIN) at least LW_MIN frames before its stamp opens its section's
    "LW_MIN": 2,          # left wall by that much + LW_PAD frames (at most WALL_MAX), and with LW_CENTER
    "LW_PAD": 5,          # the onset prior is centred there; an opener word spread over more than
    "LW_CENTER": True,    # LW_WORD x its letters x the median letter length is not trusted
    "LW_WORD": 2.0,
    "LATE_FIX": True,     # song-level late stamps: the median signed wide-pass lead of the confident openers
    "LATE_MIN_N": 5,      # ... over at least this many of them (a trade: 3 gains stamps 300 ms late +1.55
                          # but makes false late calls on exact stamps; 8 loses them 2.10), when it is
    "LATE_MAX": 1.0,      # ... LW_MIN frames or more late, opens every stamped section's left wall by it
                          # (+ LW_PAD + sigma), at most this (s; 0.6 to 1.5 identical, never binding)
    "SYL_PACE": 1.0,      # 0 = version 5's letter-even layout; 1 = every syllable the same length (0.5
                          # worse by 0.1 to 0.2 on both halves)
    # resources
    "BLOCK": 64,          # frames per emission block
    "BP_BYTES": 48e6,     # int8 backpointers kept at once; above this the decoder checkpoints
    "CKPT": 2048,         # frames per checkpoint segment (a multiple of BLOCK)
    "CELL_LIMIT": 2.5e8,  # live (state, frame) cells above which one pass is not run (fallback)
    "CELL_BUDGET": 5e8,   # live cells all passes of one align call may use together
    "WIDE_CELLS": 2e7,    # the +-1 s wide pass runs only on windows up to this many cells
    "TA_BYTES": 48e6,     # torchaudio's dense backpointer size above which our decoder runs a window
    "FP_BAND_CELLS": 6e7,  # a window pass above this many cells is banded around an even letter share
    "FP_BAND_MIN_S": 60.0,  # ... never narrower than this (seconds either side)
    "TIE": "torchaudio",  # Viterbi tie rule, see decode_chain
    "RAISE": False,       # tests only: let an exception out of align_ref_mode instead of the even fallback
    # ---- multi-line (sparse) sections: sparse_piece
    "SPARSE": True,       # False = every section gets softwall_piece
    "SPACE": [2.0, 0.75, 0.3],  # nats for 0, 1, 2 blank frames between two letters of a line
    "GM": 20,             # soft minimum gap between two lines (frames) ... (25: -0.02)
    "GP": 8.0,            # ... leaving it after i < GM frames costs GP * (1 - i / GM) (6: -0.12, 10: +0.05)
    "GP_L0": 10,          # > 0: GP scaled by min(1, shorter adjacent line's letters / GP_L0)
    "LSH_K": 30,          # line boundaries: a line weighs its letters plus this
    "ANCH_M": 0.25,       # ... and a first-pass line margin this high makes a line start an anchor
    "SP_LU": 1.0,         # nats off letters at unvoiced frames inside sparse sections (0.5: -0.04)
    "SP_ONSET": True,     # the Gaussian onset prior on the section opener
    "SP_PW": 0.35,        # pacing weight inside sparse sections (0.15 to 0.45 within 0.15, 0.7 worse)
    "LINE_BAND_S": 20.0,  # letters of a line only within this many seconds of its prior / first-pass place
    "UNST_BAND_S": 120.0,  # over budget only: an unstamped section's letters within this many seconds of an
                           # even letter share (halved down to UNST_BAND_MIN_S until the joint pass fits)
    "UNST_BAND_MIN_S": 15.0,
}

BIGF = 1 << 40
NEG = np.float32(-np.inf) if np is not None else float("-inf")

# The pacing prior's syllable split when a caller passes a language code instead of a pyphen
# dictionary: one Pyphen per language, loaded on first use.
_PYPHEN = {}


def pacing_pyphen(pyphen_dic):
    """The pyphen dictionary the syllable-even layout splits words with: a language code loads (and
    caches) that dictionary, None when pyphen is missing; anything else is used as is (main passes the
    --language dictionary assemble splits the output with, or None, where syllabify_token falls back to
    its vowel-group split)."""
    if not isinstance(pyphen_dic, str):
        return pyphen_dic
    if pyphen_dic not in _PYPHEN:
        try:
            import pyphen
            _PYPHEN[pyphen_dic] = pyphen.Pyphen(lang=pyphen_dic)
        except Exception:
            _PYPHEN[pyphen_dic] = None
    return _PYPHEN[pyphen_dic]


# --- chain decoder

class Piece:
    """One section's states and costs (see the section comment above).

    skip: float32 [n], the cost of entering state i from i - 2 (-inf: no such edge).
    stay: None (every state self-loops for free) or float32 [n], the cost of staying in state i
          for one more frame (-inf: a one-frame state).
    step: None (every state is entered from i - 1 for free) or float32 [n], the cost of that edge.
    jumps: {d: float32 [n]} for d >= 3, the cost of entering state i from i - d (-inf: none).
    An edge whose source lies before the piece's first state reaches into the previous piece."""

    def __init__(self, lab, owner, lo, hi, skip, emit, stay=None, step=None, jumps=None, groups=None):
        self.n = len(lab)
        self.lab = np.asarray(lab, dtype=np.int64)
        self.owner = owner
        self.lo = np.asarray(lo, dtype=np.int64)
        self.hi = np.asarray(hi, dtype=np.int64)
        self.skip = np.asarray(skip, dtype=np.float32)
        self.emit = emit
        self.stay = None if stay is None else np.asarray(stay, dtype=np.float32)
        self.step = None if step is None else np.asarray(step, dtype=np.float32)
        self.jumps = {int(d): np.asarray(c, dtype=np.float32) for d, c in (jumps or {}).items()}
        if any(d < 3 for d in self.jumps):
            raise ValueError("jumps are for d >= 3; d = 1 is step, d = 2 is skip")
        # groups: {key: (dst [g], src [g, W], cost [g, W])}, piece-local state indices. Sparse long
        # edges: state dst[r] may be entered from any src[r, :] at cost[r, :] (pad with src = dst,
        # cost -inf). dst sorted ascending, unique per key; 3 <= dst - src <= 127. A group edge
        # wins only when strictly better than every step/skip/jump into that state.
        self.groups = {}
        for key, (gd, gs, gc) in (groups or {}).items():
            gd = np.asarray(gd, dtype=np.int64)
            if len(gd) == 0:
                continue
            gs = np.asarray(gs, dtype=np.int64).reshape(len(gd), -1)
            gc = np.asarray(gc, dtype=np.float32).reshape(len(gd), -1)
            dd = gd[:, None] - gs
            live = gc > NEG
            if (np.diff(gd) <= 0).any() or (live & ((dd < 3) | (dd > 127))).any():
                raise ValueError("group edges: dst sorted and unique, 3 <= dst - src <= 127")
            self.groups[key] = (gd, gs, gc)


def plain_emit(lab):
    lab = np.asarray(lab, dtype=np.int64)

    def emit(lp, t0, t1, a, b):
        return lp[t0:t1][:, lab[a:b]]
    return emit


def fixed_piece(labels, prev_label=None):
    """A run of free CTC states: [blank, labels[0], blank, labels[1], ...] (used for the chain's
    leading [blank, '*'] and, with labels = [], for the final blank)."""
    lab, skip = [], []
    prev = prev_label
    for x in labels:
        lab += [0, x]
        skip += [NEG, NEG if (prev is None or prev == x) else 0.0]
        prev = x
    return lab, skip


def ctc_piece(labels, owners, lo_t, hi_t, prior_fn, prev_label):
    """CTC states for targets `labels` (letters and '*'), each preceded by a blank. owners: (li, wi)
    for a letter, None for a star. lo_t, hi_t: raw band per target (stars: free). prior_fn(t0, t1,
    pos) -> float32 [t1 - t0, len(pos)] is the prior of letter positions pos (-inf off the band),
    or None. A CTC path cannot tell adjacent identical letters apart, so they share one prior column,
    the max over the run."""
    n_t = len(labels)
    lab = np.zeros(2 * n_t, dtype=np.int64)
    owner = [None] * (2 * n_t)
    lo = np.zeros(2 * n_t, dtype=np.int64)
    hi = np.full(2 * n_t, BIGF, dtype=np.int64)
    skip = np.full(2 * n_t, NEG, dtype=np.float32)
    letter_state, letter_pos = [], []
    pos = 0
    prev = prev_label
    for j, (x, own) in enumerate(zip(labels, owners)):
        s = 2 * j + 1
        lab[s] = x
        owner[s] = own
        if prev is not None and x != prev:
            skip[s] = 0.0
        if own is not None:
            letter_state.append(s)
            letter_pos.append(pos)
            pos += 1
        prev = x
    letter_state = np.array(letter_state, dtype=np.int64)
    n_let = len(letter_state)
    # runs of identical adjacent targets among letters (a star breaks a run: stars are never letters)
    run_id = np.arange(n_let)
    for q in range(1, n_let):
        if letter_state[q] == letter_state[q - 1] + 2 and lab[letter_state[q]] == lab[letter_state[q - 1]]:
            run_id[q] = run_id[q - 1]
    runs = {}
    for q in range(n_let):
        runs.setdefault(run_id[q], []).append(q)
    multi = [m for m in runs.values() if len(m) > 1]
    run_lo = np.array([min(lo_t[q] for q in runs[run_id[q]]) for q in range(n_let)], dtype=np.int64) if n_let else np.zeros(0, np.int64)
    run_hi = np.array([max(hi_t[q] for q in runs[run_id[q]]) for q in range(n_let)], dtype=np.int64) if n_let else np.zeros(0, np.int64)
    if n_let:
        lo[letter_state] = run_lo
        hi[letter_state] = run_hi
    state_to_q = np.full(2 * n_t, -1, dtype=np.int64)
    state_to_q[letter_state] = np.arange(n_let)

    def emit(lp, t0, t1, a, b):
        E = lp[t0:t1][:, lab[a:b]]
        if prior_fn is None or n_let == 0:
            return E
        qs = state_to_q[a:b]
        sel = qs >= 0
        if not sel.any():
            return E
        q = qs[sel]
        q_lo, q_hi = int(q[0]), int(q[-1]) + 1
        # widen to whole runs at the edges
        while q_lo > 0 and run_id[q_lo - 1] == run_id[q_lo]:
            q_lo -= 1
        while q_hi < n_let and run_id[q_hi] == run_id[q_hi - 1]:
            q_hi += 1
        P = prior_fn(t0, t1, np.arange(q_lo, q_hi))
        if multi:
            for m in multi:
                if m[-1] < q_lo or m[0] >= q_hi:
                    continue
                cm = P[:, m[0] - q_lo]
                for x in m[1:]:
                    cm = np.maximum(cm, P[:, x - q_lo])
                for x in m:
                    P[:, x - q_lo] = cm
        cols = np.nonzero(sel)[0]
        E[:, cols] = E[:, cols] + P[:, q - q_lo]
        return E

    return Piece(lab, owner, lo, hi, skip, emit)


class Chain:
    def __init__(self, parts):
        """parts: [(lab, owner, lo, hi, skip, emit)] or Piece objects, in chain order."""
        labs, owners, los, his, skips, spans = [], [], [], [], [], []
        off = 0
        for p in parts:
            labs.append(p.lab)
            owners.extend(p.owner)
            los.append(p.lo)
            his.append(p.hi)
            skips.append(p.skip)
            spans.append((off, off + p.n, p.emit))
            off += p.n
        self.n = off
        self.lab = np.concatenate(labs)
        self.owner = owners
        self.lo = np.concatenate(los)
        self.hi = np.concatenate(his)
        self.skip = np.concatenate(skips)
        self.parts = spans
        # general transitions: None where every piece keeps the CTC default (free stay, free step)
        self.stay = self._join(parts, "stay", 0.0)
        self.step = self._join(parts, "step", 0.0)
        ds = sorted({d for p in parts for d in p.jumps})
        self.jumps = []
        for d in ds:
            arr = np.full(self.n, NEG, dtype=np.float32)
            o = 0
            for p in parts:
                if d in p.jumps:
                    arr[o:o + p.n] = p.jumps[d]
                o += p.n
            self.jumps.append((d, arr))
        self.D = max([2] + ds)
        # groups merged per key across pieces (global state indices)
        gk = {}
        o = 0
        for p in parts:
            for key, (gd, gs, gc) in getattr(p, "groups", {}).items():
                gk.setdefault(key, []).append((gd + o, gs + o, gc))
            o += p.n
        self.groups = []
        for key in sorted(gk):
            items = gk[key]
            W = max(x[1].shape[1] for x in items)
            gd = np.concatenate([x[0] for x in items])
            gs = np.concatenate([np.concatenate([x[1], np.repeat(x[0][:, None], W - x[1].shape[1], 1)], 1)
                                 for x in items])
            gc = np.concatenate([np.concatenate([x[2], np.full((len(x[0]), W - x[2].shape[1]), NEG, np.float32)], 1)
                                 for x in items])
            dd = gd[:, None] - gs
            self.groups.append((gd, gs, gc, np.where(gc > NEG, dd, 0).astype(np.int8)))

    @staticmethod
    def _join(parts, name, default):
        if all(getattr(p, name) is None for p in parts):
            return None
        return np.concatenate([getattr(p, name) if getattr(p, name) is not None
                               else np.full(p.n, default, dtype=np.float32) for p in parts])

    def emissions(self, lp, t0, t1, a, b):
        E = np.empty((t1 - t0, b - a), dtype=np.float32)
        for s0, s1, emit in self.parts:
            if s1 <= a or s0 >= b:
                continue
            x0, x1 = max(a, s0), min(b, s1)
            E[:, x0 - a:x1 - a] = emit(lp, t0, t1, x0 - s0, x1 - s0)
        return E


def free_piece(lab, skip):
    n = len(lab)
    return Piece(lab, [None] * n, np.zeros(n, np.int64), np.full(n, BIGF, np.int64), skip, plain_emit(lab))


def decode_chain(chain, lp, F0, F1, allow=None):
    """Viterbi of the chain over frames [F0, F1). Returns (path states per frame, stats) or
    (None, stats) when no path respects the bands or the pass is over budget.

    Tie rule. torchaudio's forced_align keeps x0 (stay) on a tie x1 == x2 > x0, which is not the
    max. Ties are common late in a long song: the alphas reach -1e4 where float32's step is 1e-3,
    so a blank with probability near 1 adds nothing and a blank cell equals the letter before it.
    Where x0 is -inf the rule kills a live cell. The dense version then pays a -1e4 wall instead
    (a letter placed outside its band; 1 map of 85 on stem ref, set 120); in a band there is no
    such cell. TIE "torchaudio" (default) decodes with torchaudio's rule, which reproduces the dense
    path wherever the dense path respects its walls, and only if that finds no path decodes again
    with the exact rule; TIE "exact" always uses the exact rule (a true max on those ties)."""
    path, stats = _decode_chain(chain, lp, F0, F1, REF["TIE"] == "exact", allow)
    if path is None and stats["mode"] in ("flat", "checkpoint") and REF["TIE"] != "exact":
        path, stats = _decode_chain(chain, lp, F0, F1, True, allow)
        stats["rescued"] = path is not None
    return path, stats


def _decode_chain(chain, lp, F0, F1, exact, allow=None):
    S = chain.n
    stats = {"cells": 0, "mode": "none"}
    if F1 - F0 < 1 or S < 2:
        return None, stats
    lo_n = np.clip(np.maximum.accumulate(chain.lo), F0, F1)
    hi_n = np.clip(np.minimum.accumulate(chain.hi[::-1])[::-1], F0, F1)
    frames = np.arange(F0, F1)
    act_lo = np.searchsorted(hi_n, frames, side="right")
    act_hi = np.searchsorted(lo_n, frames, side="right")
    width = act_hi - act_lo
    if (width <= 0).any() or act_lo[0] > 0 or act_hi[-1] < S - 1 or act_hi[0] < 1:
        return None, stats
    cells = int(width.sum())
    stats["cells"] = cells
    if cells > REF["CELL_LIMIT"] or (allow is not None and not allow(cells)):
        stats["mode"] = "over_budget"
        return None, stats
    B = int(REF["BLOCK"])
    store_all = cells <= REF["BP_BYTES"]
    C = max(B, int(REF["CKPT"]) // B * B)
    stats["mode"] = "flat" if store_all else "checkpoint"
    PAD = chain.D
    skip, stay, step, jumps = chain.skip, chain.stay, chain.step, chain.jumps
    nF = F1 - F0
    # group edges: the live rows of each group set per frame (dst inside [act_lo, act_hi))
    groups = [(gd, gs + PAD, gc, gdd, np.searchsorted(gd, act_lo), np.searchsorted(gd, act_hi))
              for gd, gs, gc, gdd in chain.groups]

    def run(i0, i1, A, bp_out, snaps):
        """frames F0+i0 .. F0+i1-1 (i0 >= 1 means A holds alpha of frame i0-1). bp_out: (buf, off)
        where off[i] indexes frame i's backpointers (local to the buffer), or None. snaps: dict to
        fill with alpha slices at segment ends, or None."""
        prev_lo = act_lo[i0 - 1] if i0 > 0 else act_lo[0]
        for b0 in range(i0, i1, B):
            b1 = min(b0 + B, i1)
            a = int(act_lo[b0])
            bb = int(act_hi[b1 - 1])
            E = chain.emissions(lp, F0 + b0, F0 + b1, a, bb)
            for i in range(b0, b1):
                lo, hi = int(act_lo[i]), int(act_hi[i])
                row = E[i - b0, lo - a:hi - a]
                if i == 0:
                    A[PAD:PAD + S] = NEG
                    A[PAD] = row[0]
                    if hi > 1:
                        A[PAD + 1] = row[1]
                    prev_lo = lo
                    continue
                x0 = A[PAD + lo:PAD + hi]
                if stay is not None:
                    x0 = x0 + stay[lo:hi]
                x1 = A[PAD + lo - 1:PAD + hi - 1]
                if step is not None:
                    x1 = x1 + step[lo:hi]
                x2 = A[PAD + lo - 2:PAD + hi - 2] + skip[lo:hi]
                c2 = (x2 > x1) & (x2 > x0)
                c1 = (x1 > x0) & (x1 > x2)
                if exact:
                    # torchaudio keeps x0 on a float32 tie x1 == x2 > x0; where x0 is -inf that
                    # kills a live cell. The exact rule takes x1 there (see decode_chain's doc).
                    c1 |= (~c2) & (~c1) & (x0 == NEG) & (x1 > NEG)
                best = np.where(c2, x2, np.where(c1, x1, x0))
                bp = c1.view(np.int8) + 2 * c2.view(np.int8)
                for d, cost in jumps:   # longer edges win only when strictly better
                    xd = A[PAD + lo - d:PAD + hi - d] + cost[lo:hi]
                    better = xd > best
                    if better.any():
                        best = np.where(better, xd, best)
                        bp = np.where(better, np.int8(d), bp)
                for gd, gsp, gc, gdd, g_r0, g_r1 in groups:
                    r0, r1 = int(g_r0[i]), int(g_r1[i])
                    if r1 <= r0:
                        continue
                    cx = A[gsp[r0:r1]] + gc[r0:r1]
                    kk = cx.argmax(axis=1)
                    rows = np.arange(r1 - r0)
                    v = cx[rows, kk]
                    dl = gd[r0:r1] - lo
                    better = v > best[dl]
                    if better.any():
                        best = best.copy()
                        bp = bp.copy()
                        best[dl[better]] = v[better]
                        bp[dl[better]] = gdd[r0:r1][rows, kk][better]
                new = best + row
                if bp_out is not None:
                    buf, off = bp_out
                    o = off[i]
                    buf[o:o + hi - lo] = bp
                if lo > prev_lo:
                    A[PAD + prev_lo:PAD + lo] = NEG
                A[PAD + lo:PAD + hi] = new
                prev_lo = lo
                if snaps is not None and (i + 1) % C == 0:
                    snaps[i] = A[PAD + lo:PAD + hi].copy()
        return A

    def backtrack(i_end, i_start, s, buf, off, path):
        """fill path[i] for i in [i_start, i_end] given state s at i_end; returns the state at
        i_start - 1 (or s at i_start when i_start == 0)."""
        for i in range(i_end, i_start - 1, -1):
            path[i] = s
            if i == 0:
                break
            s -= int(buf[off[i] + s - act_lo[i]])
        return s

    A = np.full(S + PAD, NEG, dtype=np.float32)
    path = np.empty(nF, dtype=np.int64)
    if store_all:
        off = np.zeros(nF + 1, dtype=np.int64)
        off[1:] = np.cumsum(width)
        buf = np.empty(int(off[-1]), dtype=np.int8)
        run(0, nF, A, (buf, off), None)
        final = _final_state(A, PAD, S, act_lo[-1], act_hi[-1])
        if final is None:
            return None, stats
        backtrack(nF - 1, 0, final, buf, off, path)
        return path, stats
    snaps = {}
    run(0, nF, A, None, snaps)
    final = _final_state(A, PAD, S, act_lo[-1], act_hi[-1])
    if final is None:
        return None, stats
    s = final
    seg_starts = list(range(0, nF, C))
    for i0 in reversed(seg_starts):
        i1 = min(i0 + C, nF)
        A2 = np.full(S + PAD, NEG, dtype=np.float32)
        if i0 > 0:
            snap = snaps[i0 - 1]
            A2[PAD + act_lo[i0 - 1]:PAD + act_hi[i0 - 1]] = snap
        off = np.zeros(i1 - i0 + 1, dtype=np.int64)
        off[1:] = np.cumsum(width[i0:i1])
        buf = np.empty(int(off[-1]), dtype=np.int8)
        offg = np.full(nF, 0, dtype=np.int64)  # frame -> offset (only this segment's entries used)
        offg[i0:i1] = off[:-1]
        run(i0, i1, A2, (buf, offg), None)
        s = backtrack(i1 - 1, i0, s, buf, offg, path)   # now the state at frame i0 - 1
    return path, stats


def _final_state(A, PAD, S, lo, hi):
    x1 = A[PAD + S - 1] if S - 1 < hi else NEG
    x2 = A[PAD + S - 2] if lo <= S - 2 < hi else NEG
    if x1 == NEG and x2 == NEG:
        return None
    return S - 1 if x1 > x2 else S - 2


def spans_from_path(chain, path, F0, log_probs, frame_max):
    """{(li, wi): [(start_f, end_f, prob, margin), ...]} from a state path; prob and margin are read
    off the unbiased emissions (the float32 tensor) by the shared read_spans."""
    if path is None:
        return None
    change = np.nonzero(np.diff(path))[0] + 1
    starts = np.concatenate([[0], change])
    ends = np.concatenate([change, [len(path)]])
    runs = ((int(path[st]), st + F0, en + F0) for st, en in zip(starts.tolist(), ends.tolist()))
    out = read_spans(runs, chain.lab, chain.owner, log_probs, frame_max)
    return out


def spend(ctx, cells, reserve=0.0):
    """Charge a pass's live cells to the call's budget; False (nothing charged) when the pass would
    exceed CELL_LIMIT or leave less than `reserve` for what must still run."""
    if cells > REF["CELL_LIMIT"] or cells > ctx["budget"] - reserve:
        return False
    ctx["budget"] -= cells
    return True


def window_align(ctx, f0, f1, char_ids, owners, reserve=0.0):
    """align_window over [f0, f1): torchaudio when its dense backpointers are small, else our
    banded decoder (same float32 arithmetic and tie rule, bounded memory). Raises RuntimeError when
    no path exists, as align_window does, or when the pass does not fit the budget."""
    log_probs, lp_np, frame_max = ctx["log_probs"], ctx["lp"], ctx["frame_max"]
    f0, f1 = max(0, f0), min(ctx["T"], f1)
    S = 2 * len(char_ids) + 1
    n_let = sum(1 for o in owners if o is not None)
    cells = (f1 - f0) * S
    Wf = 0
    if cells > REF["FP_BAND_CELLS"] and n_let and f1 > f0 and owners[0] is None:
        # a huge window (a long song with very few stamps): letters only within Wf frames of
        # where an even letter share puts them, Wf as wide as FP_BAND_CELLS allows (>= FP_BAND_MIN_S)
        # (the floor gives way when even it would not fit 80 % of one pass's CELL_LIMIT)
        fit = int(0.8 * REF["CELL_LIMIT"] / (4 * n_let))
        Wf = max(min(int(REF["FP_BAND_MIN_S"] / FRAME_SEC), fit), int(REF["FP_BAND_CELLS"] / (4 * n_let)))
        if 2 * Wf >= f1 - f0:
            Wf = 0
        else:
            cells = min(cells, (f1 - f0) * (int(4 * Wf * n_let / (f1 - f0)) + 8))
    if f1 <= f0 or not spend(ctx, cells, reserve):
        raise RuntimeError("window empty or over budget")
    if not Wf and ((f1 - f0) * S <= REF["TA_BYTES"] or not char_ids or owners[0] is not None):
        return align_window(log_probs, f0, f1, char_ids, owners)
    lab0, sk0 = fixed_piece([char_ids[0]])
    body_lab = char_ids[1:]
    body_own = owners[1:]
    lo_t, hi_t = [0] * n_let, [BIGF] * n_let
    if Wf:
        span = f1 - f0
        lo_t = [int(f0 + span * q / n_let) - Wf for q in range(n_let)]
        hi_t = [int(f0 + span * (q + 1) / n_let) + Wf for q in range(n_let)]
    body = ctc_piece(body_lab, body_own, lo_t, hi_t, None, char_ids[0])
    tail = free_piece([0], [NEG])
    chain = Chain([free_piece(lab0, sk0), body, tail])
    path, _ = decode_chain(chain, lp_np, f0, f1)
    if path is None:
        raise RuntimeError("no path")
    return spans_from_path(chain, path, f0, log_probs, frame_max)


# --- the stamp error model and the softwall pieces

def syl_even_letter_frames(line, f0, f1, char_dur_f, mix, pyphen_dic):
    """
    even_letter_frames laid out by SYLLABLE: at mix 1 every syllable of the line gets the same share
    of the room whatever its spelling (a sung syllable's length follows the tune, not its letter
    count; REF SYL_PACE 1 beat 0.5 on both halves of the corpus), at mix 0 every letter does (which IS
    even_letter_frames). Syllables are split as assemble splits them: authored hyphens, else
    syllabify_token with pyphen_dic (see pacing_pyphen). The benchmark ran pyphen en_US; another
    --language changes the layout of this prior, and is not benchmarked.
    """
    if mix <= 0:
        return even_letter_frames(line, f0, f1, char_dur_f)
    dic = pyphen_dic
    words = [w for w in line.words if not w.untimed]
    n_chars = sum(len(w.norm) for w in words)
    if n_chars == 0:
        return []
    gap_f = 4
    want = n_chars * char_dur_f + max(0, len(words) - 1) * gap_f
    scale = min(max(f1 - f0, 10), want) / want
    sylls = []
    for w in words:
        parts = []
        for tok in w.tokens:
            ps = [tok] if w.authored else syllabify_token(tok, dic)
            parts.extend(len(p) for p in ps)
        if sum(parts) != len(w.norm):
            parts = [len(w.norm)]
        sylls.append(parts)
    n_syl = sum(len(p) for p in sylls)
    mean_l = n_chars / n_syl
    weights = [[mix * mean_l + (1 - mix) * c for c in p] for p in sylls]
    tot_w = sum(sum(x) for x in weights)
    body = n_chars * char_dur_f
    pos, out = float(f0), []
    for p, wts in zip(sylls, weights):
        for c, wt in zip(p, wts):
            room = body * wt / tot_w * scale
            for j in range(c):
                out.append(pos + room * j / c)
            pos += room
        pos += gap_f * scale
    return out


def window_bounds(lines, sections, ref_end_ms, T):
    """The section windows [stamp, next stamp) in frames, with every stamp clamped into the audio
    (f0 <= T - 25): a stamp at or past the end gave version 5 f0 > f1 and a crash. A window ends at
    the next section's CLAMPED start, so clamping a late stamp never leaves the previous window
    reaching past it. The last window ends at the end marker, else 12 s a line after its stamp."""
    cap = max(0, T - 25)

    def frame(ms):
        return min(cap, max(0, int((ms / 1000.0) / FRAME_SEC)))

    starts = [frame(lines[sec[0]].ref_ms if lines[sec[0]].ref_ms is not None else 0.0) for sec in sections]
    windows = []
    for k, sec in enumerate(sections):
        f0 = starts[k]
        if k + 1 < len(sections):
            end_f = starts[k + 1]
        else:
            head = lines[sec[0]].ref_ms
            end = ref_end_ms if ref_end_ms is not None else (head if head is not None else 0.0) + 12000.0 * len(sec)
            end_f = int((end / 1000.0) / FRAME_SEC)
        f1 = min(T, max(f0 + 25, end_f))
        windows.append((f0, f1))
    return windows


def first_pass(ctx):
    """Each section aligned alone inside its window, without priors (version 5's forced alignment):
    the unbiased evidence the stamp error model and the sparse line anchors read, and the result
    when the whole-song pass does not fit its budget. A window with no path or over its budget places
    nothing (the garbage replacement lays its lines out)."""
    lines, sections, windows = ctx["lines"], ctx["sections"], ctx["windows"]
    per_word = {}
    for (f0, f1), sec in zip(windows, sections):
        ci, ow = build_targets([(i, lines[i]) for i in sec], ctx["dictionary"], ctx["star_id"])
        try:
            per_word.update(window_align(ctx, f0, f1, ci, ow, ctx["reserve"]))
        except (RuntimeError, ValueError, IndexError):
            pass
    return per_word


def opener_leads(lines, sections, windows, per_word):
    """(frames from its stamp to the first letter, mean letter margin) of every stamped section's
    opener in the first pass: the evidence of the song's stamp error model."""
    out = []
    for (f0, _), sec in zip(windows, sections):
        if lines[sec[0]].ref_ms is None:
            continue
        ws = line_word_spans(per_word, lines, sec[0])
        chars = [sp for spans in ws for sp in spans]
        if chars:
            out.append((ws[0][0][0] - f0, sum(sp[3] for sp in chars) / len(chars)))
    return out


def plausible(pairs):
    """The (lead, margin) pairs whose lead is a plausible stamp error (REF LEAD_RANGE)."""
    lo, hi = REF["LEAD_RANGE"]
    if lo is None:
        return pairs
    return [(l, g) for l, g in pairs if lo / 0.02 <= l <= hi / 0.02]


def robust_median(pairs, margin, min_n):
    """The song's stamp lead in frames: the median lead of the plausible openers at least `margin`
    confident, lowering the bar (0.15, 0.05, any) until min_n of them count; 0 when even that finds
    fewer (then the stamps are taken at their word). Standard library only."""
    pairs = plausible(pairs)
    for m in (margin, 0.15, 0.05, -1.0):
        if m > margin:
            continue
        v = sorted(l for l, g in pairs if g >= m)
        if len(v) >= min_n:
            return float(v[len(v) // 2])
    return 0.0


def robust_mu_sigma(pairs, margin, min_n=5):
    """robust_median's cascade for the onset prior: (median lead, 1.4826 x the median absolute
    deviation, openers counted), or (0, None, 0) when fewer than min_n count at any margin."""
    pairs = plausible(pairs)
    for m in (margin, 0.15, 0.05, -1.0):
        if m > margin:
            continue
        v = sorted(l for l, g in pairs if g >= m)
        if len(v) >= min_n:
            mu = float(v[len(v) // 2])
            dev = sorted(abs(x - mu) for x in v)
            return mu, 1.4826 * float(dev[len(dev) // 2]), len(v)
    return 0.0, None, 0


def wide_pass(ctx, pad=50):
    """Each stamped section aligned alone in [stamp - pad, next stamp + pad): the opener's first
    word (signed lead, margin, extent, letters) per section."""
    lines, sections, windows = ctx["lines"], ctx["sections"], ctx["windows"]
    T = ctx["T"]
    first_word = {}
    for k, ((f0, f1), sec) in enumerate(zip(windows, sections)):
        if lines[sec[0]].ref_ms is None:
            continue
        g0, g1 = max(0, f0 - pad), min(T, f1 + pad)
        ci, ow = build_targets([(i, lines[i]) for i in sec], ctx["dictionary"], ctx["star_id"])
        try:
            if (g1 - g0) * (2 * len(ci) + 1) > REF["WIDE_CELLS"]:
                continue
            pw = window_align(ctx, g0, g1, ci, ow, ctx["reserve"])
        except (RuntimeError, ValueError, IndexError):
            continue
        ws = line_word_spans(pw, lines, sec[0])
        chars = [sp for spans in ws for sp in spans]
        if chars:
            w0 = ws[0]
            first_word[k] = (w0[0][0] - f0, sum(sp[3] for sp in w0) / len(w0), w0[-1][1] - w0[0][0], len(w0))
    return first_word


def replace_garbage_paths(lines, sections, windows, per_word, char_dur, lead):
    """
    Version 4: the path of each ref-mode line is kept UNLESS its shape says it is garbage (see
    garbage_reasons) or the aligner could not place it at all. Those lines are paced at the song's
    median rate from the one thing known to be true about them, and flagged estimated: a section
    opener from its stamp plus the song's stamp lead `lead` (frames; version 6 passes robust_median's,
    measured on at least LEAD_MIN_N openers, else 0), a line inside a sparse section from the end of
    the kept line before it. A run of replaced lines shares the room up to the next kept line (or the
    section's end) by character count.
    """
    replaced = 0
    for (f0, f1), sec in zip(windows, sections):
        stamped = lines[sec[0]].ref_ms is not None
        bad = {}
        for pos, i in enumerate(sec):
            if not any(not w.untimed for w in lines[i].words):
                continue
            ws = line_word_spans(per_word, lines, i)
            if not ws:
                bad[i] = ["unplaced"]
                continue
            late = ws[0][0][0] - (f0 + lead) if pos == 0 and stamped else None
            reasons = garbage_reasons(path_shape(ws, char_dur), late)
            if reasons:
                bad[i] = reasons
        pos = 0
        while pos < len(sec):
            if sec[pos] not in bad:
                pos += 1
                continue
            end = pos
            while end < len(sec) and sec[end] in bad:
                end += 1
            run = sec[pos:end]
            for i in run:
                for wi in range(len(lines[i].words)):
                    per_word.pop((i, wi), None)
            # the room: from the kept (or already paced) line before the run, or the stamp plus
            # the lead, to the next kept line, or the section's end
            before = [sp[1] for i in sec[:pos] for spans in line_word_spans(per_word, lines, i) for sp in spans]
            after = [sp[0] for i in sec[end:] if i not in bad for spans in line_word_spans(per_word, lines, i) for sp in spans]
            lo = max(before) if before else f0
            start = lo if before or not stamped else f0 + lead
            stop = min(after) if after else f1
            start = max(lo, min(start, stop - 10))
            counts = [max(1, sum(len(w.norm) for w in lines[i].words if not w.untimed)) for i in run]
            at = float(start)
            for i, n in zip(run, counts):
                nxt = at + (stop - start) * n / sum(counts)
                synthesize_line_spans(lines[i], i, int(round(at)), int(round(nxt)), char_dur, per_word)
                log(f"line {i + 1}: {'+'.join(bad[i])} path replaced by even pacing (estimated)")
                at = nxt
            replaced += len(run)
            pos = end
    if replaced:
        log(f"garbage paths replaced: {replaced} line(s); stamp lead {lead * FRAME_SEC * 1000:.0f} ms")


# --- the section piece (the plug-in boundary)

def section_expect(ctx, k):
    """The pacing prior's layout for section k (None when the section has no stamp, pacing is
    off, or the layout does not cover every letter)."""
    lines, sections, windows = ctx["lines"], ctx["sections"], ctx["windows"]
    sec = sections[k]
    f0, f1 = windows[k]
    if lines[sec[0]].ref_ms is None or PACING_PRIOR_WEIGHT <= 0:
        return None
    start = min(f0 + ctx["plead"], f1 - 10)
    counts = [max(1, sum(len(w.norm) for w in lines[i].words if not w.untimed)) for i in sec]
    expect, at = [], float(start)
    for i, n in zip(sec, counts):
        nxt = at + (f1 - start) * n / sum(counts)
        expect.extend(syl_even_letter_frames(lines[i], at, nxt, ctx["char_dur"], REF["SYL_PACE"], ctx["pyphen"]))
        at = nxt
    return expect


def section_band(ctx, k):
    """(lo, hi, first_hi): the band of section k's letters and of its first letter."""
    lines, sections, windows, T = ctx["lines"], ctx["sections"], ctx["windows"], ctx["T"]
    f0, f1 = windows[k]
    sec = sections[k]
    hi = min(T, f1 + ctx["spill_f"])
    stamped = lines[sec[0]].ref_ms is not None
    lo = max(0, f0 - ctx["line_left"].get(k, 0)) if stamped else f0
    return lo, hi, min(hi, f1)


def softwall_piece(ctx, k):
    """Section k as the whole-song pass sees it: its lines' letters, a '*' after each line;
    letters walled to [lo, hi) (the first letter to [lo, f1)); pacing, onset and LU priors."""
    lines, sections, windows = ctx["lines"], ctx["sections"], ctx["windows"]
    sec = sections[k]
    f0, f1 = windows[k]
    labels, owners = [], []
    for i in sec:
        for wi, w in enumerate(lines[i].words):
            for ch in w.norm:
                labels.append(ctx["dictionary"][ch])
                owners.append((i, wi))
        labels.append(ctx["star_id"])
        owners.append(None)
    n_let = sum(1 for o in owners if o is not None)
    lo, hi, first_hi = section_band(ctx, k)
    stamped = lines[sec[0]].ref_ms is not None
    expect = section_expect(ctx, k)
    if expect is not None and len(expect) != n_let:
        expect = None
    exp_arr = np.asarray(expect, dtype=np.float64) if expect is not None else None
    q_lo = np.full(n_let, lo, dtype=np.int64)
    q_hi = np.full(n_let, hi, dtype=np.int64)
    ub = ctx.get("unst_band_s")
    if ub and not stamped and n_let:
        # over budget only (joint_banded): an unstamped section's letters are walled to the whole
        # window, which no sparse line band narrows; keep each within ub seconds of an even letter share
        Wf = int(round(ub / FRAME_SEC))
        cen = lo + (hi - lo) * (np.arange(n_let) + 0.5) / n_let
        q_lo = np.maximum(lo, (cen - Wf).astype(np.int64))
        q_hi = np.maximum(q_lo + 1, np.minimum(hi, np.ceil(cen + Wf).astype(np.int64)))
    if n_let:
        q_hi[0] = min(q_hi[0], first_hi)
    onset = None
    if REF["ONSET_S"] > 0 and stamped and n_let:
        onset = (f0 + ctx["line_mu"].get(k, ctx["mu_f"]), REF["ONSET_S"] * ctx["sig_f"])
    upen = ctx["lu_pen"]
    W, TOL, CAP, FS = PACING_PRIOR_WEIGHT, PACING_PRIOR_TOL_S, PACING_PRIOR_CAP_S, FRAME_SEC

    def prior_fn(t0, t1, pos):
        tt = np.arange(t0, t1)
        if exp_arr is not None:
            t = tt.astype(np.float64)[:, None]
            excess = np.clip(np.abs(t - exp_arr[pos][None, :]) * FS - TOL, 0.0, CAP)
            v = ((-W) * excess).astype(np.float32)
        else:
            v = np.zeros((t1 - t0, len(pos)), dtype=np.float32)
        if onset is not None and pos[0] == 0:
            rows = (tt >= lo) & (tt < first_hi)
            if rows.any():
                z = (tt[rows].astype(np.float64) - onset[0]) / onset[1]
                v[rows, 0] = v[rows, 0] + (-np.minimum(0.5 * z * z, REF["ONSET_CAP"])).astype(np.float32)
        if upen is not None:
            v -= upen[t0:t1, None]
        off = (tt[:, None] < q_lo[pos][None, :]) | (tt[:, None] >= q_hi[pos][None, :])
        v[off] = NEG
        return v

    return ctc_piece(labels, owners, q_lo, q_hi, prior_fn, ctx["star_id"])


def letters_of(line):
    return max(1, sum(len(w.norm) for w in line.words if not w.untimed))


def sparse_boundaries(ctx, idx, start, end):
    """Expected start frame of each line idx[q] (the section's line boundaries): the stamp (plus lead) and every
    line the first pass heard confidently (margin >= ANCH_M) anchor; the others are spread between
    the anchors around them by letters + LSH_K. Monotone."""
    lines = ctx["lines"]
    counts = [letters_of(lines[i]) for i in idx]
    K = len(idx)
    w = [c + REF["LSH_K"] for c in counts]
    anchors = [(0, float(start))]
    for q in range(1, K):
        ws = line_word_spans(ctx["first"], lines, idx[q])
        if ws and ctx["margin"](idx[q]) >= REF["ANCH_M"]:
            anchors.append((q, float(ws[0][0][0])))
    anchors.append((K, float(end)))
    out = [float(start)]
    for q in range(1, K):
        lo = max(a for a in anchors if a[0] <= q)
        hi = min((a for a in anchors if a[0] > q), key=lambda a: a[0])
        if lo[0] == q:
            out.append(max(out[-1], lo[1]))
            continue
        frac = sum(w[lo[0]:q]) / max(1e-9, sum(w[lo[0]:hi[0]]))
        out.append(max(out[-1], lo[1] + (hi[1] - lo[1]) * frac))
    return out


def letter_spacing_costs(same_label):
    """
    Letter spacing inside a sparse section's lines: the log score of entering a letter from the
    previous letter of its line after 0, 1, .. len(SPACE) - 1 one-frame blanks, -SPACE[i] (a letter
    entered after more blank frames, through the self-looping blank, pays nothing). Two identical
    labels get no direct edge at all (NEG): CTC must separate them by a blank. Standard library only.
    """
    sp = [float(x) for x in REF["SPACE"]]
    return [NEG if same_label else -sp[0]] + [-c for c in sp[1:]]


def line_gap_costs(prev_letters, letters):
    """
    The soft minimum gap between two lines of a sparse section: what starting the next line after
    i = 1 .. GM - 1 frames of '*' costs, GP (1 - i / GM) nats, with GP scaled by min(1, the shorter
    adjacent line's letters / GP_L0) so two short lines may sit closer. After GM frames it is free.
    Standard library only.
    """
    m = max(1, int(REF["GM"]))
    gp = float(REF["GP"])
    if REF["GP_L0"]:
        gp *= min(1.0, min(prev_letters, letters) / float(REF["GP_L0"]))
    return [gp * (1.0 - gi / m) for gi in range(1, m)]


def sparse_piece(ctx, k):
    """A stamped section of 2+ lines as the sparse section graph, inside the whole-song chain:

      line 1: c1 [s1..sK] b c2 ... cn | G1..Gm | line 2: ... | TG

    c = a letter (self-loop), s = a one-frame blank, b = a self-looping blank, G = one-frame '*'
    states (Gm self-loops), TG = the closing '*'. A letter entered straight from the previous letter
    of its line costs SPACE[0] (only when the two labels differ), after i blank frames SPACE[i],
    after K+1 or more nothing. The next line may start after i < m gap frames at GP (1 - i/m).
    Frame scores: the model's log prob; on letters v5's pacing prior laid out from the lsh line
    boundaries, LU off at unvoiced frames, the section band [left wall, next stamp + spill), the
    opener's onset prior and first_hi (the softwall treatment of a section opener)."""
    lines, sections, windows = ctx["lines"], ctx["sections"], ctx["windows"]
    sec = sections[k]
    f0, f1 = windows[k]
    dic, star = ctx["dictionary"], ctx["star_id"]
    LL = []
    for i in sec:
        letters = [(dic[ch], (i, wi)) for wi, w in enumerate(lines[i].words) if not w.untimed for ch in w.norm]
        if letters:
            LL.append((i, letters))
    if lines[sec[0]].ref_ms is None or not LL:
        return softwall_piece(ctx, k)
    idx = [i for i, _ in LL]
    FS = FRAME_SEC
    lo_b, hi_b, first_hi = section_band(ctx, k)
    start = min(f0 + ctx["plead"], f1 - 10)
    bnds = sparse_boundaries(ctx, idx, start, f1)
    char_dur = ctx["char_dur"]
    expect = []
    for q, i in enumerate(idx):
        at = bnds[q]
        nxt = bnds[q + 1] if q + 1 < len(idx) else f1
        expect.extend(syl_even_letter_frames(lines[i], at, max(nxt, at + 1), char_dur, REF["SYL_PACE"],
                                             ctx["pyphen"]))
    # per-line bands (resources): within LINE_BAND_S of the layout's or the first pass's place
    band = {}
    band_s = ctx.get("line_band_s", REF["LINE_BAND_S"])
    if band_s:
        W = int(round(band_s / FS))
        for q, i in enumerate(idx):
            c0 = bnds[q]
            c1 = bnds[q + 1] if q + 1 < len(idx) else f1
            ws = line_word_spans(ctx["first"], lines, i)
            if ws:
                c0, c1 = min(c0, ws[0][0][0]), max(c1, ws[-1][-1][1])
            band[i] = (int(c0) - W, int(math.ceil(c1)) + W)

    NB = len(REF["SPACE"]) - 1               # one-frame blanks between two letters of a line
    m = max(1, int(REF["GM"]))
    lab, owner, lo, hi, stay, skip = [], [], [], [], [], []
    jumps = {}
    gap_g = ([], [], [])
    let_g = ([], [], [])
    let_state = []
    q_lo, q_hi = [], []

    def add(x, own, a=0, b=BIGF, st=0.0, sk=NEG):
        lab.append(x)
        owner.append(own)
        lo.append(a)
        hi.append(b)
        stay.append(st)
        skip.append(sk)
        return len(lab) - 1

    pending = []
    prev_n = None
    for q, (i, letters) in enumerate(LL):
        if q > 0:
            gap = line_gap_costs(prev_n, len(letters))
            pending = []
            for gi in range(1, m + 1):
                s = add(star, None, st=0.0 if gi == m else NEG)
                if gi < m:
                    pending.append((s, gap[gi - 1]))
        l_lo, l_hi = lo_b, hi_b
        if i in band:
            l_lo, l_hi = max(lo_b, band[i][0]), min(hi_b, band[i][1])
            if l_hi - l_lo < 2 * len(letters) + 2:   # never narrower than the line can use
                l_lo, l_hi = lo_b, hi_b
        prev_c = None
        for j, (x, own) in enumerate(letters):
            a_, b_ = l_lo, l_hi
            if not let_state:
                b_ = min(b_, first_hi)
            s = add(x, own, a_, b_)
            q_lo.append(a_)
            q_hi.append(b_)
            let_state.append(s)
            if j == 0 and pending:
                far = [(p, -c) for p, c in pending if s - p >= 3]
                for p, c in pending:
                    if s - p == 2:
                        skip[s] = -c
                if far:
                    gap_g[0].append(s)
                    gap_g[1].append([p for p, _ in far])
                    gap_g[2].append([c for _, c in far])
            if prev_c is not None:
                # entries: from b by step (free); from s_i after i blank frames; from prev_c direct
                srcs = [(prev_c + bi, c) for bi, c in enumerate(letter_spacing_costs(x == lab[prev_c]))]
                longs = []
                for src, c in srcs:
                    d = s - src
                    if d == 2:
                        skip[s] = c
                    elif d >= 3:
                        longs.append((src, c))
                if NB <= 3:
                    for src, c in longs:
                        jumps.setdefault(s - src, {})[s] = c
                else:
                    let_g[0].append(s)
                    let_g[1].append([src for src, _ in longs])
                    let_g[2].append([c for _, c in longs])
            if j + 1 < len(letters):
                for _ in range(NB):
                    add(0, None, st=NEG)
                add(0, None, st=0.0)
            prev_c = s
        prev_n = len(letters)
    add(star, None)
    n = len(lab)
    jarr = {}
    for d, items in jumps.items():
        arr = np.full(n, NEG, dtype=np.float32)
        for s, c in items.items():
            arr[s] = c
        jarr[d] = arr
    groups = {}
    for key, g in (("gap", gap_g), ("let", let_g)):
        if g[0]:
            W_ = max(len(x) for x in g[1])
            gs = [x + [d] * (W_ - len(x)) for d, x in zip(g[0], g[1])]
            gc = [x + [NEG] * (W_ - len(x)) for x in g[2]]
            groups[key] = (g[0], gs, gc)

    labA = np.asarray(lab, dtype=np.int64)
    st2q = np.full(n, -1, dtype=np.int64)
    st2q[np.asarray(let_state)] = np.arange(len(let_state))
    exp_arr = np.asarray(expect, dtype=np.float64) if len(expect) == len(let_state) else None
    q_lo = np.asarray(q_lo, dtype=np.int64)
    q_hi = np.asarray(q_hi, dtype=np.int64)
    onset = None
    if REF["SP_ONSET"] and REF["ONSET_S"] > 0:
        onset = (f0 + ctx["line_mu"].get(k, ctx["mu_f"]), REF["ONSET_S"] * ctx["sig_f"])
    upen = ctx["sp_lu_pen"]
    W = float(REF["SP_PW"])
    TOL, CAP = PACING_PRIOR_TOL_S, PACING_PRIOR_CAP_S

    def emit(lp, t0, t1, a, b):
        E = lp[t0:t1][:, labA[a:b]]
        qs = st2q[a:b]
        sel = qs >= 0
        tt = np.arange(t0, t1)
        if sel.any():
            qv = qs[sel]
            cols = np.nonzero(sel)[0]
            if exp_arr is not None and W > 0:
                t = tt.astype(np.float64)[:, None]
                excess = np.clip(np.abs(t - exp_arr[qv][None, :]) * FS - TOL, 0.0, CAP)
                v = ((-W) * excess).astype(np.float32)
            else:
                v = np.zeros((t1 - t0, len(qv)), dtype=np.float32)
            if onset is not None and qv[0] == 0:
                rows = (tt >= lo_b) & (tt < first_hi)
                if rows.any():
                    z = (tt[rows].astype(np.float64) - onset[0]) / onset[1]
                    v[rows, 0] = v[rows, 0] + (-np.minimum(0.5 * z * z, REF["ONSET_CAP"])).astype(np.float32)
            if upen is not None:
                v -= upen[t0:t1, None]
            off = (tt[:, None] < q_lo[qv][None, :]) | (tt[:, None] >= q_hi[qv][None, :])
            v[off] = NEG
            E[:, cols] = E[:, cols] + v
        return E

    return Piece(labA, owner, lo, hi, skip, emit, stay=stay, step=None, jumps=jarr, groups=groups)


def section_piece(ctx, k):
    if len(ctx["sections"][k]) > 1 and REF["SPARSE"]:
        return sparse_piece(ctx, k)
    return softwall_piece(ctx, k)


def joint_banded(ctx):
    """The whole-song pass. When it does not fit the budget and the song has sparse sections, the
    per-line bands of those sections are halved (down to 5 s) and the pass is tried again."""
    star = ctx["star_id"]
    band_s = REF["LINE_BAND_S"]
    sparse = REF["SPARSE"] and any(len(sec) > 1 for sec in ctx["sections"])
    unst = any(ctx["lines"][sec[0]].ref_ms is None for sec in ctx["sections"])
    ub = None
    while True:
        ctx["line_band_s"] = band_s
        ctx["unst_band_s"] = ub
        lab0, sk0 = fixed_piece([star])
        parts = [free_piece(lab0, sk0)]
        hook = ctx.get("emit_hook")     # version 10, --evidence fused only (fused_ep_hook)
        for k in range(len(ctx["sections"])):
            parts.append(section_piece(ctx, k) if hook is None else hook(section_piece(ctx, k)))
        parts.append(free_piece([0], [NEG]))
        chain = Chain(parts)
        path, stats = decode_chain(chain, ctx["lp"], 0, ctx["T"], allow=lambda cells: spend(ctx, cells))
        ctx["joint_stats"] = stats
        if path is None and stats["mode"] == "over_budget":
            if sparse and band_s and band_s / 2 >= 5.0:
                band_s = band_s / 2
                continue
            # a long unstamped section (lyrics before the first stamp) walls its letters to its whole
            # window: band them around an even letter share, narrowing until the pass fits
            if unst:
                ub = REF["UNST_BAND_S"] if ub is None else ub / 2
                if ub >= REF["UNST_BAND_MIN_S"]:
                    ctx["joint_unst_band"] = ub
                    continue
        break
    if path is None:
        return None
    return spans_from_path(chain, path, 0, ctx["log_probs"], ctx["frame_max"])


# --- safety

def even_fallback(lines, sections, windows, T, char_dur=4.0):
    """Every alignable line paced evenly inside its section window (clamped to the audio)."""
    per_word = {}
    for (f0, f1), sec in zip(windows, sections):
        idx = [i for i in sec if any(not w.untimed for w in lines[i].words)]
        if not idx:
            continue
        counts = [max(1, sum(len(w.norm) for w in lines[i].words if not w.untimed)) for i in idx]
        at = float(f0)
        for i, n in zip(idx, counts):
            nxt = at + (f1 - f0) * n / sum(counts)
            synthesize_line_spans(lines[i], i, int(round(at)), int(round(nxt)), char_dur, per_word)
            at = nxt
    return per_word


def sanitize(lines, per_word, T):
    """Every span inside the audio, and word starts that validate_and_repair can nudge apart
    without passing the end of the song. Touches nothing on a well-formed result."""
    if T <= 0:
        return per_word
    keys = [(li, wi) for li, ln in enumerate(lines) for wi, w in enumerate(ln.words)
            if not w.untimed and per_word.get((li, wi))]
    for key in keys:
        spans = per_word[key]
        fixed = []
        for sp in spans:
            a = min(max(0, int(sp[0])), T - 1)
            b = min(max(int(sp[1]), a + 1), T)
            fixed.append((a, b) + tuple(sp[2:]))
        if fixed != spans:
            per_word[key] = fixed
    # word starts in lyric order: a word that starts before an earlier word (a garbage-replaced or
    # even-fallback line laid out from a clamped stamp, out-of-order stamps) would otherwise be put
    # back in order only by validate_and_repair's 1 ms nudges, a pile of words. Each run of such
    # words is spread evenly between the latest earlier start and the next word in order.
    starts = [per_word[k][0][0] for k in keys]
    j = 0
    run_max = -1
    while j < len(keys):
        if starts[j] >= run_max:
            run_max = starts[j]
            j += 1
            continue
        e = j
        while e < len(keys) and starts[e] < run_max:
            e += 1
        lo = run_max
        hi = starts[e] if e < len(keys) else T - 1
        hi = max(hi, lo)
        m = e - j
        for q in range(m):
            s = int(lo + (hi - lo) * (q + 1) / (m + 1))
            key = keys[j + q]
            spans = per_word[key]
            d = s - spans[0][0]
            per_word[key] = [(min(T - 1, a + d), min(T, max(b + d, min(T - 1, a + d) + 1))) + tuple(sp[2:])
                             for sp in spans for a, b in [sp[:2]]]
        j = e
    # simulate validate_and_repair's +1 ms nudges; if they would pass the last frame, spread the
    # words evenly over the audio (only when lyrics are far denser than the audio can hold)
    prev = -1
    limit = int(round((T - 1) * FRAME_SEC * 1000.0))
    over = False
    for key in keys:
        ms = int(round(per_word[key][0][0] * FRAME_SEC * 1000.0))
        ms = max(ms, prev + 1)
        prev = ms
        if ms > limit:
            over = True
            break
    if over:
        n = len(keys)
        for j, key in enumerate(keys):
            s = int(j * (T - 1) / max(1, n))
            per_word[key] = [(s, s + 1, 0.0, 0.0) for _ in per_word[key]]
    return per_word


def left_walls(fw, char_dur, sig_f, stamped):
    """
    The per-section left walls of the stamp error model, from the wide pass's openers
    fw = {section: (signed lead in frames, margin, extent, letters) of its first word}: (line_left =
    {section: frames the band opens before the stamp}, line_mu = {section: the onset prior's centre
    relative to the stamp}). A section whose opener is heard confidently (LW_MARGIN, and not spread
    over more than LW_WORD x its letters x char_dur) at least LW_MIN frames before its stamp opens by
    that much + LW_PAD (at most WALL_MAX). stamped: the stamped sections' indices, in order, which
    the late-stamp model below opens. Standard library only.
    """
    line_left, line_mu = {}, {}
    for k, (l, g, ext, nl) in fw.items():
        if REF["LW_WORD"] > 0 and ext > REF["LW_WORD"] * nl * char_dur:
            g = -1.0
        if g >= REF["LW_MARGIN"] and -l >= REF["LW_MIN"]:
            line_left[k] = min(int(round(REF["WALL_MAX"] / FRAME_SEC)), int(-l) + REF["LW_PAD"])
            if REF["LW_CENTER"]:
                line_mu[k] = float(max(l, -line_left[k]))
    if REF["LATE_FIX"]:
        # SONG-LEVEL LATE STAMPS. The first pass cannot see a negative lead (its windows start at
        # the stamps), so robust_median only ever measures early stamps; the wide pass (which
        # starts 1 s before each stamp) can. When the confident wide-pass openers (the per-line
        # wall's own filters) say the song's stamps are late, open every stamped section's left
        # wall by that much (plus LW_PAD and the song's sigma, at most LATE_MAX) and centre its
        # onset prior there. A per-line wall already wider, or already centred, is kept.
        late = sorted(l for (l, g, ext, nl) in fw.values()
                      if g >= REF["LW_MARGIN"] and not (REF["LW_WORD"] > 0 and ext > REF["LW_WORD"] * nl * char_dur))
        if len(late) >= REF["LATE_MIN_N"]:
            med = float(late[len(late) // 2])
            if -med >= REF["LW_MIN"]:
                wall = min(int(round(REF["LATE_MAX"] / FRAME_SEC)), int(-med) + REF["LW_PAD"] + int(round(sig_f)))
                for k in stamped:
                    if line_left.get(k, 0) < wall:
                        line_left[k] = wall
                        if REF["LW_CENTER"] and k not in line_mu:
                            line_mu[k] = max(med, -wall)
    return line_left, line_mu


def ref_context(lines, ref_end_ms, log_probs, dictionary, star_id, voiced, pyphen_dic="en_US"):
    """The stamped decoder's per-call context: the sections, their windows, the emissions in both
    forms and the cell budget, with the joint pass's share reserved. Shared by align_ref and the
    estimated vocal mode's first pass, which reads the song's pace and stamp lead off it."""
    T = log_probs.size(0)
    lp = log_probs.detach().cpu().numpy()   # a view; never written
    sections = ref_sections(lines)
    windows = window_bounds(lines, sections, ref_end_ms, T)
    v = voiced_bool(voiced, T)
    ctx = {"lines": lines, "sections": sections, "windows": windows, "T": T,
           "log_probs": log_probs, "lp": lp, "frame_max": log_probs.max(dim=-1).values,
           "dictionary": dictionary, "star_id": star_id, "budget": float(REF["CELL_BUDGET"]),
           "pyphen": pacing_pyphen(pyphen_dic),
           # the letter cost at unvoiced frames: softwall pieces (LU) and sparse sections (SP_LU)
           "lu_pen": unvoiced_letter_cost(v, REF["LU"], np.float32) if REF["LU"] else None,
           "sp_lu_pen": unvoiced_letter_cost(v, REF["SP_LU"], np.float32) if REF["SP_LU"] else None}
    # the joint pass matters most: the window passes may not eat the cells it will need
    est = sum((f1 - f0 + 70) * (2 * sum(len(w.norm) for i in sec for w in lines[i].words) + 2 * len(sec) + 1)
              for (f0, f1), sec in zip(windows, sections))
    ctx["reserve"] = float(min(est, REF["CELL_LIMIT"]))
    return ctx


def align_ref(lines, ref_end_ms, log_probs, dictionary, star_id, voiced, pyphen_dic="en_US", emit_hook=None):
    """
    Any stamped file: the STAMPED decoder (see the section comment above). Works in float32 on the
    emission tensor and reads prob and margin off it. May raise; align_ref_mode is the entry point
    that never does. pyphen_dic: see pacing_pyphen. emit_hook (version 10, the fused path's emission
    product): Piece -> Piece, applied to every section piece of the whole-song pass; None (always, on
    the default path) leaves the pass exactly as version 9 runs it.
    """
    ctx = ref_context(lines, ref_end_ms, log_probs, dictionary, star_id, voiced, pyphen_dic)
    if emit_hook is not None:
        ctx["emit_hook"] = emit_hook
    sections, windows = ctx["sections"], ctx["windows"]
    per_word = first_pass(ctx)
    char_dur = median_char_dur_frames(per_word, lines)
    pairs = opener_leads(lines, sections, windows, per_word)
    lead = robust_median(pairs, REF["LEAD_MARGIN"], REF["LEAD_MIN_N"])
    lead_s = lead * FRAME_SEC
    spill_s = max(0.0, lead_s - REF["X"])
    mu_f, sig_f, _ = robust_mu_sigma(pairs, REF["ONSET_MARGIN"], REF["ONSET_MIN_N"])
    if sig_f is None:
        sig_f = REF["SIG_CEIL"] / FRAME_SEC
    sig_f = min(max(sig_f, REF["SIG_FLOOR"] / FRAME_SEC), REF["SIG_CEIL"] / FRAME_SEC)
    line_left, line_mu = {}, {}
    if REF["LW"]:
        stamped = [k for k, sec in enumerate(sections) if lines[sec[0]].ref_ms is not None]
        line_left, line_mu = left_walls(wide_pass(ctx), char_dur, sig_f, stamped)
    log(f"stamp model: lead {lead * FRAME_SEC * 1000:.0f} ms (onset {mu_f * FRAME_SEC * 1000:.0f} ms, "
        f"sigma {sig_f * FRAME_SEC * 1000:.0f} ms), left walls open on {len(line_left)} of {len(sections)} section(s)")
    ctx.update(char_dur=char_dur, plead=lead, spill_f=int(round(spill_s / FRAME_SEC)),
               mu_f=mu_f, sig_f=sig_f, line_left=line_left, line_mu=line_mu)
    first = dict(per_word)
    margins = {}

    def margin(i):
        if i not in margins:
            margins[i] = line_margin_of(first, lines, i)
        return margins[i]
    ctx.update(first=first, margin=margin)
    joint = joint_banded(ctx)
    if joint is not None:
        per_word = joint
    else:
        log("whole-song pass over its budget or without a path; the per-section first pass is kept")
    char_dur2 = median_char_dur_frames(per_word, lines)
    replace_garbage_paths(lines, sections, windows, per_word, char_dur2, max(0.0, lead))
    return per_word


# --------------------------------------------------------------------------
# Anchoring entry points (main calls exactly these)
# --------------------------------------------------------------------------

def align_ref_mode(lines, ref_end_ms, log_probs, dictionary, star_id, voiced, char_dur_holder=None,
                   pyphen_dic="en_US", emit_hook=None):
    """
    --anchors ref (a stamped file, fully or sparsely): the version 6 STAMPED decoder. Never raises:
    on any failure every section is paced evenly inside its (clamped) window and flagged estimated
    (REF RAISE lets the exception out instead, for tests). sanitize then keeps every span inside the
    audio and the word starts in lyric order, so validate_and_repair's 1 ms nudges never reorder
    words. char_dur_holder, when a list, receives the song's median letter length (version 5's
    signature; nothing downstream reads it). pyphen_dic: see pacing_pyphen. emit_hook: see align_ref.
    """
    T = log_probs.size(0)
    try:
        if star_id is None:
            raise RuntimeError("no '*' in the dictionary")
        per_word = align_ref(lines, ref_end_ms, log_probs, dictionary, star_id, voiced, pyphen_dic,
                             emit_hook=emit_hook)
    except Exception as exc:
        if REF["RAISE"]:
            raise
        log(f"stamped decoder failed ({type(exc).__name__}: {exc}); every section paced evenly (estimated)")
        sections = ref_sections(lines)
        windows = window_bounds(lines, sections, ref_end_ms, T)
        per_word = even_fallback(lines, sections, windows, T)
    per_word = sanitize(lines, per_word, T)
    if char_dur_holder is not None:
        char_dur_holder.append(median_char_dur_frames(per_word, lines))
    return per_word


def even_from_stamps(lines, sections, windows, char_dur, lead):
    """
    The estimated vocal mode's layout (version 7): every line paced evenly at the song's letter
    length `char_dur` (frames) from where its section opens, its stamp plus the song's stamp lead
    `lead` (frames; the leading unstamped section opens at its window, the top of the song), and
    squeezed into its window when it would not fit (synthesize_line_spans). The lines of a sparse
    section share the room from that start to the next stamp by letter count, as even_fallback
    shares it. Every placed line is flagged estimated. Standard library only.
    """
    per_word = {}
    for (f0, f1), sec in zip(windows, sections):
        idx = [i for i in sec if any(not w.untimed for w in lines[i].words)]
        if not idx:
            continue
        start = f0 + lead if lines[sec[0]].ref_ms is not None else f0
        start = max(f0, min(start, f1 - 10))
        counts = [max(1, sum(len(w.norm) for w in lines[i].words if not w.untimed)) for i in idx]
        at = float(start)
        for i, n in zip(idx, counts):
            nxt = at + (f1 - start) * n / sum(counts)
            synthesize_line_spans(lines[i], i, int(round(at)), int(round(nxt)), char_dur, per_word)
            at = nxt
    return per_word


def align_estimated_mode(lines, ref_end_ms, log_probs, dictionary, star_id, voiced, pyphen_dic="en_US"):
    """
    --vocal-mode estimated (version 7, backlog 354): the mapper's choice for a song whose vocals the
    acoustic model cannot follow (screamed, effect-heavy, Shinigiwa-class), never a default and never
    an automatic fallback. No CTC path is kept: every stamped line is paced evenly from its stamp
    plus the song's stamp lead (even_from_stamps). The emissions are still read once, by the stamped
    decoder's unbiased first pass, for the two song-level numbers the layout needs and nothing else:
    the median letter length and the robust stamp lead (robust_median over the confident section
    openers, as the garbage-path replacement uses it, floored at 0). Without them (80 ms a letter, no
    lead) it loses 4 to 25 points pooled and most of its Shinigiwa gain. Its value tracks stamp
    quality; see the module docstring's version 7 entry for the numbers. Never raises: when the
    first pass fails, the layout runs at 80 ms a letter and no lead.
    """
    T = log_probs.size(0)
    char_dur, lead = 4.0, 0.0
    try:
        if star_id is None:
            raise RuntimeError("no '*' in the dictionary")
        ctx = ref_context(lines, ref_end_ms, log_probs, dictionary, star_id, voiced, pyphen_dic)
        first = first_pass(ctx)
        char_dur = median_char_dur_frames(first, lines)
        lead = max(0.0, robust_median(opener_leads(lines, ctx["sections"], ctx["windows"], first),
                                      REF["LEAD_MARGIN"], REF["LEAD_MIN_N"]))
    except Exception as exc:
        if REF["RAISE"]:
            raise
        log(f"first pass failed ({type(exc).__name__}: {exc}); pacing at 80 ms a letter with no stamp lead")
    sections = ref_sections(lines)
    windows = window_bounds(lines, sections, ref_end_ms, T)
    log(f"estimated vocals: every line paced evenly from its stamp; stamp lead {lead * FRAME_SEC * 1000:.0f} ms, "
        f"{char_dur * FRAME_SEC * 1000:.0f} ms a letter")
    per_word = even_from_stamps(lines, sections, windows, char_dur, lead)
    return sanitize(lines, per_word, T)


def align_auto_mode(lines, log_probs, dictionary, star_id, voiced, char_dur_holder=None):
    """
    --anchors auto (plain lyrics): the version 6 AUTO decoder (align_auto), which never raises and
    flags evidence-free lines estimated. char_dur_holder as in align_ref_mode.
    """
    info = {}
    per_word = align_auto(lines, log_probs, dictionary, star_id, voiced, info)
    parts = []
    if "pace" in info:
        parts.append(f"pace {info['pace'] * FRAME_SEC * 1000:.0f} ms a letter")
    if "band" in info:
        parts.append("exact decode" if info["band"] is None else f"band +-{info['band']} targets")
    if info.get("uniq"):
        parts.append("the lyrics repeat almost no line, so the voiced '*' is free")
    if info.get("guard") is True:
        parts.append("compaction guard: edge bonus dropped")
    if "fallback" in info:
        parts.append(f"even layout ({info['fallback']})")
    n_est = sum(1 for ln in lines if ln.estimated)
    log(f"auto decoder: {'; '.join(parts) or 'nothing to decode'}; {n_est} of {len(lines)} lines estimated")
    if char_dur_holder is not None:
        char_dur_holder.append(median_char_dur_frames(per_word, lines))
    return per_word


# --------------------------------------------------------------------------
# Version 6 self-tests (standard library only, like the others: `--self-test` runs every one)
# --------------------------------------------------------------------------

def _check_all(title, cases):
    """cases: [(name, got, want)]; prints the failures and a summary line, returns an exit code."""
    failed = [(name, got, want) for name, got, want in cases if got != want]
    for name, got, want in failed:
        print(f"FAIL {name}: got {got}, expected {want}")
    print(f"{title} self-test: {len(cases) - len(failed)}/{len(cases)}")
    return 1 if failed else 0


def self_test_even_letters() -> int:
    """Pins even_letter_frames, the letter-even layout under the pacing prior (at REF SYL_PACE 0)."""
    def line(*norms):
        return Line(display="x", words=[Word(display=n, norm=n) for n in norms])

    # 4 frames a letter, 4 frames between words; a window too short squeezes, a long one does not stretch
    return _check_all("even letters", [
        ("even: roomy window", even_letter_frames(line("ab", "c"), 10, 100, 4.0), [10.0, 14.0, 22.0]),
        ("even: squeezed to half", even_letter_frames(line("ab", "c"), 0, 14, 8.0), [0.0, 4.0, 10.0]),
        ("even: never under 10 frames", even_letter_frames(line("ab", "c"), 0, 4, 4.0), [0.0, 2.5, 7.5]),
        ("even: untimed words skipped", even_letter_frames(
            Line(display="x", words=[Word(display="ab", norm="ab"), Word(display="?", untimed=True),
                                     Word(display="c", norm="c")]), 0, 100, 4.0), [0.0, 4.0, 12.0]),
        ("even: nothing alignable", even_letter_frames(line(""), 0, 100, 4.0), []),
    ])


def self_test_estimated() -> int:
    """Pins even_from_stamps, the estimated vocal mode's layout: word start frames per line."""
    def line(ref, *norms):
        return Line(display="x", ref_ms=ref, words=[Word(display=n, norm=n) for n in norms])

    def starts(lines, sections, windows, char_dur, lead):
        pw = even_from_stamps(lines, sections, windows, char_dur, lead)
        return [[pw[(li, wi)][0][0] for wi in range(len(ln.words)) if (li, wi) in pw] for li, ln in enumerate(lines)]

    two = [line(0.0, "ab", "c"), line(2000.0, "d")]
    sparse = [line(0.0, "ab"), line(None, "cd")]
    flagged = [line(0.0, "ab")]
    even_from_stamps(flagged, [[0]], [(0, 100)], 4.0, 0.0)
    return _check_all("estimated", [
        # 4 frames a letter and 4 between words, from the stamp plus the lead
        ("estimated: stamp + lead", starts(two, [[0], [1]], [(0, 100), (100, 200)], 4.0, 5.0), [[5, 17], [105]]),
        ("estimated: no lead", starts(two, [[0], [1]], [(0, 100), (100, 200)], 4.0, 0.0), [[0, 12], [100]]),
        # a lead that leaves no room starts 10 frames before the next stamp and squeezes the line
        ("estimated: squeezed", starts([line(0.0, "ab", "c")], [[0]], [(0, 20)], 4.0, 15.0), [[10, 18]]),
        # the leading unstamped section opens at the top of the song, never at a lead
        ("estimated: unstamped opener", starts([line(None, "ab")], [[0]], [(0, 100)], 4.0, 5.0), [[0]]),
        # a sparse section's lines share the window by letter count
        ("estimated: sparse section", starts(sparse, [[0, 1]], [(0, 100)], 4.0, 0.0), [[0], [50]]),
        ("estimated: flagged", flagged[0].estimated, True),
    ])


def self_test_spacing() -> int:
    """Pins the sparse section graph's costs: letter spacing (REF SPACE) and the soft minimum gap
    between two lines (REF GM, GP, GP_L0)."""
    def r(xs):
        return [round(x, 9) for x in xs]

    gap = line_gap_costs(12, 30)                         # both lines longer than GP_L0: the full GP
    return _check_all("spacing", [
        ("letters: straight from a different letter", letter_spacing_costs(False), [-2.0, -0.75, -0.3]),
        ("letters: no direct edge between identical letters", letter_spacing_costs(True)[0], float("-inf")),
        ("letters: identical letters after blanks", letter_spacing_costs(True)[1:], [-0.75, -0.3]),
        ("gap: one cost per frame short of GM", len(gap), 19),
        ("gap: GP (1 - i / GM)", r([gap[0], gap[9], gap[18]]), [7.6, 4.0, 0.4]),
        ("gap: a short line scales GP", r(line_gap_costs(5, 30)[:1]), [3.8]),
        ("gap: the shorter of the two lines counts", r(line_gap_costs(30, 2)[:1]), [1.52]),
        ("gap: never scaled up", r(line_gap_costs(10, 40)[:1]), [7.6]),
    ])


def self_test_late() -> int:
    """Pins the stamp error model's pure parts: the robust lead (robust_median's margin cascade and
    LEAD_MIN_N), the per-line left walls and the song-level late-stamp model (left_walls)."""
    def opener(lead, margin=0.5, ext=8, letters=4):
        return (lead, margin, ext, letters)

    early = [(10, 0.6), (12, 0.6), (14, 0.5), (15, 0.35), (16, 0.9)]
    late5 = {k: opener(-10) for k in range(5)}
    late4 = {k: opener(-10) for k in range(4)}
    one_early = {0: opener(-8), 1: opener(3), 2: opener(-30, margin=0.2), 3: opener(-12, ext=60)}
    walls5, mu5 = left_walls(late5, 4.0, 4.0, list(range(7)))
    walls4, _ = left_walls(late4, 4.0, 4.0, list(range(7)))
    walls1, mu1 = left_walls(one_early, 4.0, 4.0, list(range(4)))
    return _check_all("late stamps", [
        ("lead: median of five confident openers", robust_median(early, 0.3, 5), 14.0),
        ("lead: the margin cascade counts the less confident", robust_median(early[:4] + [(20, 0.1)], 0.3, 5), 14.0),
        ("lead: fewer than LEAD_MIN_N at any margin is no lead", robust_median(early[:4], 0.3, 5), 0.0),
        ("lead: implausible stamp errors are not counted", robust_median(early + [(80, 0.9)] * 3, 0.3, 5), 14.0),
        ("walls: a confident early opener opens its section", walls1, {0: 13}),
        ("walls: ... centred on it", mu1, {0: -8.0}),
        ("late: four late openers open nothing song-wide", walls4, {k: 15 for k in range(4)}),
        ("late: five late openers open every stamped section", walls5, {k: 19 for k in range(7)}),
        ("late: an opener's own centre is kept", [mu5[k] for k in range(5)], [-10.0] * 5),
        ("late: the others centre on the song's late lead", [mu5[5], mu5[6]], [-10.0, -10.0]),
        ("late: never beyond LATE_MAX", left_walls({k: opener(-80) for k in range(5)}, 4.0, 4.0, [0])[0][0], 50),
    ])


def self_test_band() -> int:
    """Pins the auto decoder's band arithmetic (band_halfwidth: the exact decode when it fits one
    decode's budgets, else the widest band that does, else Infeasible) and the chunk grids of the
    shifted views (chunk_bounds)."""
    try:
        band_halfwidth(1_000_000, 1000)
        tiny = "decoded"
    except Infeasible:
        tiny = "Infeasible"
    win = 30 * SAMPLE_RATE
    return _check_all("band", [
        ("band: a 4 minute song decodes exactly", band_halfwidth(12000, 2000), None),
        ("band: 20 minutes gets the budget's band", band_halfwidth(60000, 9645), 416),
        ("band: 3-byte backpointers past 65535 frames", band_halfwidth(70000, 20000), 238),
        ("band: no band fits the budget", tiny, "Infeasible"),
        ("chunks: the stock grid", chunk_bounds(2 * win + 100, win), [(0, win), (win, win), (2 * win, win)]),
        ("chunks: a 15 s shift", chunk_bounds(2 * win + 100, win, win // 2),
         [(0, win // 2), (win // 2, win), (3 * win // 2, win)]),
        ("chunks: a song shorter than the shift", chunk_bounds(100, win, win // 2), [(0, win // 2)]),
        ("chunks: no audio", chunk_bounds(0, win), []),
    ])


def self_test_cache() -> int:
    """Pins the emission cache's file names (view_cache_name): version 5's name for the fp32 stem
    pass, precision separation (an fp32 fallback must never read an int8 entry, nor the other way
    round), the engine in an int8 name and the thread count in none, and one name per view; and the
    command-line --threads read before numpy loads (argv_threads)."""
    def name(view, prec, engine=None, key="0123456789abcdef"):
        return view_cache_name(key, 30.0, 4.0, view, prec, engine)

    every = [name(v, p, e) for v in EVIDENCE_VIEWS for p, e in (("fp32", None), ("int8", "x86"), ("int8", "qnnpack"))]
    return _check_all("cache names", [
        ("cache: the fp32 stem pass keeps version 5's name", name("stem", "fp32"),
         "emissions_0123456789abcdef_w30_c4_star.pt"),
        ("cache: an fp32 extra view", name("shift", "fp32"), "emissions_0123456789abcdef_w30_c4_star_shift_fp32.pt"),
        ("cache: an int8 view names its engine", name("shift", "int8", "x86"),
         "emissions_0123456789abcdef_w30_c4_star_shift_q8-x86.pt"),
        ("cache: int8 and fp32 of every view differ",
         all(name(v, "fp32") != name(v, "int8", "x86") for v in EVIDENCE_VIEWS), True),
        ("cache: one name per view, precision and engine", len(set(every)), len(every)),
        ("cache: no thread count in any name", [n for n in every if re.search(r"-t\d|thread", n)], []),
        ("cache: the key is the audio's (the mix view passes its own)", name("mix", "int8", "x86", key="feedc0de").split("_")[1],
         "feedc0de"),
        ("threads: --threads N", argv_threads(["a.mp3", "l.txt", "--threads", "2"]), "2"),
        ("threads: --threads=N", argv_threads(["--threads=6", "a.mp3"]), "6"),
        ("threads: absent", argv_threads(["a.mp3", "l.txt"]), None),
        ("threads: not a number is argparse's to report", argv_threads(["--threads", "x"]), None),
        ("threads: no value", argv_threads(["--threads"]), None),
    ])


def self_test_fused() -> int:
    """Pins the pure parts of the version 10 fused evidence path (standard library only): the
    language name and detector, the QMUL column layout, the letter-to-phone DTW, the median of three
    paths, the review flags, and apply_word_starts keeping every syllable inside its word."""
    def word(text, start, end, syls):
        return Word(display=text, norm=text, start_ms=start, end_ms=end,
                    syllables=[{"text": t, "start_ms": a, "end_ms": b} for t, a, b in syls])

    lines = [Line(display="hello world", words=[word("hello", 1000, 1600, [("hel", 1000, 1300), ("lo", 1300, 1600)]),
                                                word("world", 1700, 2200, [("world", 1700, 2200)])])]
    notes = apply_word_starts(lines, [1200, 1500], 10000)
    w0, w1 = lines[0].words
    _, _, qdic, qstar, _ = qmul_columns()
    return _check_all("fused", [
        ("lang: en", fused_language_name("en"), "english"),
        ("lang: en_GB", fused_language_name("EN_gb"), "english"),
        ("lang: other names lower-cased", fused_language_name(" Japanese "), "japanese"),
        ("detect: English", detect_english(["I know you love me baby", "and you can never leave"])[0], True),
        ("detect: romaji", detect_english(["Yoru no tobari dake ga", "machi mo nemuru koro"])[0], False),
        ("detect: Spanish", detect_english(["Si senor efectos especiales", "te quiero mucho"])[0], False),
        ("detect: nothing", detect_english(["...", ""])[0], False),
        ("columns: blank 0, '*' last", (qdic[chr(PSEUDO_BASE)], qstar, qdic["*"], len(qdic)), (0, 71, 71, 72)),
        ("dtw: cat", letters_to_phones("cat", [False, True, False]), [0, 1, 2]),
        ("dtw: no phones", letters_to_phones("hm", []), [-1, -1]),
        ("median3", median3_starts([1, None, 5], [2, 3, None], [3, 4, 6]), [2, None, 5]),
        ("review", review_flags([0, 0, None], [201, 200, 0]), [True, False, False]),
        ("guard", guard_starts([0, 6001, 4000, None, 9000], [1000, 1000, 1000, 5, None], 5000),
         ([0, 1000, 4000, None, 9000], 1)),
        ("guard: none", guard_starts([0, 99999], [0, 0], None), ([0, 99999], 0)),
        ("apply: starts", (w0.start_ms, w1.start_ms), (1200, 1500)),
        ("apply: end cut at the next start", w0.end_ms, 1500),
        ("apply: syllables inside the word",
         all(w.start_ms <= x["start_ms"] < x["end_ms"] <= w.end_ms for w in (w0, w1) for x in w.syllables), True),
        ("apply: no repairs", notes, []),
    ])


def self_test_dup() -> int:
    """Pins dup_share, which decides whether auto's voiced '*' is charged (complete lyrics) or free
    (a sheet that repeats almost no line, AUTO UNIQ_DUP)."""
    def lines(*texts):
        out = []
        for t in texts:
            words = [Word(display=w, norm=w.lower().strip(".,!?")) for w in t.split()]
            for w in words:
                w.untimed = not w.norm
            out.append(Line(display=t, words=words))
        return out

    return _check_all("dup share", [
        ("dup: nothing repeats", dup_share(lines("a b", "c d", "e")), 0.0),
        ("dup: one of three repeats", round(dup_share(lines("a b", "c", "a b")), 9), round(1 / 3, 9)),
        ("dup: punctuation and case do not count", dup_share(lines("Hey you", "hey, you!")), 0.5),
        ("dup: lines with no letters are not lines", dup_share(lines("...", "a", "...", "a")), 0.5),
        ("dup: one repeat in ten is 'almost none'",
         dup_share(lines(*"a b c d e f g h i a".split())) <= AUTO["UNIQ_DUP"], True),
        ("dup: no lyrics", dup_share([]), 0.0),
    ])


# --------------------------------------------------------------------------
# Version 10: the opt-in fused evidence path (--evidence fused)
# --------------------------------------------------------------------------
#
# Nothing in this section runs, and none of its dependencies (phonemizer, espeakng_loader, the
# QMUL weights) is imported or loaded, unless --evidence fused is on the command line; the default
# path never calls into it. See the module docstring's version 10 entry for what it does and the
# measured effects.

FUSED_REVIEW_MS = 200            # |version 9 start - QMUL path start| above this flags a word "review"
FUSED_STAR_PENALTY = {"ref": 2.0, "auto": 0.0}   # the QMUL path's constant '*' column: -penalty nats
FUSED_EP_WEIGHT = 1.0            # the emission product: w * max(log P_QMUL(phone), floor) per letter
FUSED_EP_FLOOR = -8.0
FUSED_GUARD_MS = {"ref": 5000, "auto": None}   # --fuse qmul only: a QMUL-path start this far
                                 # from version 9's keeps version 9's (None: no guard); guard_starts
FUSE_CHOICES = ("median3", "qmul", "ep")
FUSED_ENGLISH_ASCII = 0.9        # the language detector: this share of letters ASCII ...
FUSED_ENGLISH_COMMON = 0.2       # ... and this share of word tokens among ENGLISH_COMMON

# QMUL multilingual (Jiawen Huang, Emmanouil Benetos, Sebastian Ewert; ISMIR 2025 LBD; MIT), a mel
# CRNN trained with CTC on 16 kHz DALI vocals: 69 espeak IPA phones, ' ' (69, between words), unk
# (70, any phone outside the inventory) and blank (71). The checkpoint is fetched once from a pinned
# commit and checked against its sha256.
QMUL_COMMIT = "ca1a3923d6c8bf7d20eefa1080f3f104a6173d38"
QMUL_URL = ("https://raw.githubusercontent.com/jhuang448/LyricsAlignment-Multilingual/"
            f"{QMUL_COMMIT}/checkpoints/checkpoint_Baseline")
QMUL_SHA256 = "d541bde0a0e2759c5eac1ee7b70e06169a2aada504bc2359a98c7de4344d4dfe"
QMUL_FILE = f"qmul_multilingual_baseline_{QMUL_COMMIT[:10]}.pt"
QMUL_SR = 22050
QMUL_N_FFT = 512
QMUL_MEL_HOP = 256
QMUL_POOL_T = 3
QMUL_HOP_S = QMUL_POOL_T * QMUL_MEL_HOP / QMUL_SR      # 768 / 22050 s per output frame
# output frame k is the max-pool of mel frames 3k .. 3k + 2 (centred on samples (3k + 1) * 256), so
# its 768-sample cell starts 128 samples before 768k
QMUL_OFFSET_S = -(QMUL_MEL_HOP // 2) / QMUL_SR
QMUL_PROB_FLOOR = 5.5e-11        # the authors add uniform(1e-11, 1e-10) before the log: its mean
QMUL_CNN_CHUNK = 1500            # output frames per CNN chunk (exact: the CNN sees +-3 mel frames)
QMUL_IPA = ['a', 'aɪ', 'aʊ', 'b', 'd', 'dʒ', 'e', 'ee', 'eɪ', 'eː', 'f', 'h', 'i', 'iː', 'j',
            'k', 'l', 'm', 'n', 'o', 'oʊ', 'oː', 'p', 'r', 's', 'ss', 't', 'ts', 'tʃ', 'tː',
            'u', 'uː', 'v', 'w', 'x', 'y', 'z', 'æ', 'ç', 'ð', 'ŋ', 'ɐ', 'ɑː', 'ɑːɹ', 'ɑ̃',
            'ɔ', 'ɔː', 'ɔ̃', 'ə', 'ɚ', 'ɛ', 'ɛ̃', 'ɜ', 'ɜː', 'ɡ', 'ɣ', 'ɪ', 'ɲ', 'ɹ', 'ɾ', 'ʁ',
            'ʃ', 'ʊ', 'ʊɹ', 'ʌ', 'ʒ', 'ʝ', 'β', 'θ', ' ']
QMUL_VOCAB = QMUL_IPA + ["unk", "blank"]
QMUL_SEP, QMUL_UNK, QMUL_BLANK = 69, 70, 71
# Every QMUL column the decoders place becomes one pseudo-letter from the CJK Unified Ideographs
# block: alphabetic (syllable_count keeps it), caseless and matched by no pyphen pattern.
PSEUDO_BASE = 0x4E00
IPA_VOWEL_LEADS = set("aeiouyæɐɑɒɔəɚɛɜɞɘɤɨɪɯɵøœɶʉʊʌ")
LETTER_VOWELS = set("aeiouy")

# The ~150 most common words of English song lyrics, minus the short ones that are just as common
# in romaji or the other big lyric languages (a, i, no, to, me, so, do, in, on, an, am, he, man, go,
# oh, yeah): a romaji or Spanish sheet must not read as English through its particles.
ENGLISH_COMMON = frozenset("""
the and you it is of that my your we for with this all what don't can when are know just like love
but not be was have will they she her his him there from out up get got now one time never every
baby see say way make take want need feel heart night day down let come if how why where who would
could should been had has our us them their then than only still back into over away too more here
can't won't i'm i'll i've i'd you're you'll you've we're we'll they're that's there's it's what's
she's he's gonna wanna think tell give keep look life world eyes mind light right something nothing
everything always really good little some about because cause were did does didn't doesn't isn't
ain't said at by or as ever again around through without inside tonight forever together alone
hold home dream fall run stay leave find found lost free bring remember believe everybody someone
anything maybe girl boy hand hands face these those which while same other another even much many
own new old made knew let's myself yourself far long last
""".split())


def fused_language_name(name: str) -> str:
    """The game's canonical language name ('english', 'japanese', ...) from --lyrics-language,
    lower-cased; English codes (en, en-us, en_GB, ...) read as 'english'. Pure."""
    n = (name or "").strip().lower()
    if n in ("en", "eng") or re.fullmatch(r"en[-_][a-z]{2}", n):
        return "english"
    return n


def detect_english(texts) -> tuple:
    """The conservative detector behind --evidence fused when --lyrics-language is absent: English
    only when the lyrics are ASCII-dominant (FUSED_ENGLISH_ASCII of their letters) AND at least
    FUSED_ENGLISH_COMMON of their word tokens are ENGLISH_COMMON words. texts: the lyric lines.
    Returns (is_english, ascii share of letters, common-word share of tokens). Standard library only."""
    letters = [c for t in texts for c in t if c.isalpha()]
    ascii_share = sum(1 for c in letters if c.isascii()) / len(letters) if letters else 0.0
    toks = []
    for t in texts:
        for w in t.replace("’", "'").replace("‘", "'").lower().split():
            w = re.sub(r"^[^\w']+|[^\w']+$", "", w).strip("'")
            if any(c.isalpha() for c in w):
                toks.append(w)
    common = sum(1 for w in toks if w in ENGLISH_COMMON) / len(toks) if toks else 0.0
    return (bool(toks) and ascii_share >= FUSED_ENGLISH_ASCII and common >= FUSED_ENGLISH_COMMON,
            ascii_share, common)


def fused_plan(lines, mode: str, vocal_mode: str, lyrics_language, fuse: str) -> dict:
    """Decides whether --evidence fused applies to this song (logged either way). It applies to an
    aligned stamped (ref) or plain (auto) song whose language is English: given by
    --lyrics-language, else by detect_english. Returns {"apply", "meta", ...}; the meta goes into the
    timing.json engine block whether or not the path applies, so the game can see why."""
    import copy

    meta = {"evidence": "mms", "evidence_requested": "fused"}
    plan = {"apply": False, "fuse": fuse, "meta": meta}
    if lyrics_language:
        lang, how = fused_language_name(lyrics_language), "flag"
    else:
        ok, a, c = detect_english([ln.display for ln in lines])
        lang = "english" if ok else "unknown"
        how = f"detected (ASCII share {a:.2f} of letters, common English share {c:.2f} of words)"
    meta.update(lyrics_language=lang, lyrics_language_source=how)
    if lang != "english":
        log(f"fused evidence: lyrics language {lang} ({how}); only English is validated, "
            f"running the version 9 path")
        meta["evidence_note"] = "not English"
        return plan
    if vocal_mode != "aligned" or mode not in ("ref", "auto"):
        why = f"vocal mode {vocal_mode}" if vocal_mode != "aligned" else f"anchor mode {mode}"
        log(f"fused evidence: not available with {why}; running the version 9 path")
        meta["evidence_note"] = f"not available with {why}"
        return plan
    log(f"fused evidence: lyrics language english ({how}); fuse {fuse}")
    plan.update(apply=True, pristine=copy.deepcopy(lines))
    return plan


# --- evidence -> the decoders' matrix (the same construction the bench validated)

def fused_lse(x, axis: int = 1):
    m = x.max(axis=axis, keepdims=True)
    m = np.where(np.isfinite(m), m, 0.0)
    return (m + np.log(np.exp(x - m).sum(axis=axis, keepdims=True))).squeeze(axis)


def fused_resample(logp, hop_s: float, offset_s: float, n_out: int, frame_s: float = FRAME_SEC):
    """[T, V] log-posteriors whose frame k covers offset_s + [k, k + 1) x hop_s -> [n_out, V] on the
    decoders' frame_s grid (frame g covers [g, g + 1) x frame_s), each target frame the
    overlap-weighted mean of the source PROBABILITIES, then the log, renormalised per frame (a
    20 ms frame straddling two source frames holds a mixture of what each heard). Frames outside
    the source repeat its edge frame. numpy only."""
    T, V = logp.shape
    if abs(hop_s - frame_s) < 1e-12 and abs(offset_s) < 1e-12:
        if T >= n_out:
            return logp[:n_out]
        return np.concatenate([logp, np.repeat(logp[-1:], n_out - T, axis=0)], axis=0)
    X = np.exp(logp.astype(np.float64))
    C = np.concatenate([np.zeros((1, V)), np.cumsum(X, axis=0)], axis=0)
    edges = (np.arange(n_out + 1) * frame_s - offset_s) / hop_s
    u = np.clip(edges, 0.0, float(T))

    def cum(x):
        k = np.minimum(np.floor(x).astype(np.int64), T - 1)
        return C[k] + (x - k)[:, None] * X[k]

    a, b = u[:-1], u[1:]
    width = b - a
    out = np.empty((n_out, V))
    ok = width > 1e-9
    out[ok] = (cum(b[ok]) - cum(a[ok])) / width[ok, None]
    left = (~ok) & (edges[:-1] < 0.5 * T)
    out[left] = X[0]
    out[(~ok) & ~left] = X[T - 1]
    out = np.log(np.maximum(out, 1e-38))
    out -= fused_lse(out)[:, None]
    return out.astype(np.float32)


def qmul_columns():
    """The QMUL columns the decoders see, in matrix order after the blank: every column but the
    blank and the word separator (merged into the blank). Returns (tokens, char_of {column: pseudo
    letter}, dictionary {char: matrix column, '*' last}, star_id, vowel_chars). Pure."""
    tokens = [c for c in range(len(QMUL_VOCAB)) if c not in (QMUL_BLANK, QMUL_SEP)]
    char_of = {c: chr(PSEUDO_BASE + j) for j, c in enumerate(tokens, start=1)}
    dictionary = {chr(PSEUDO_BASE): 0}
    dictionary.update({ch: j for j, ch in enumerate(char_of.values(), start=1)})
    star_id = len(tokens) + 1
    dictionary["*"] = star_id
    # a word's syllables are its vowel-phone groups (syllabify_token reads VOWELS); the unk class
    # counts as a vowel (on English lyrics 98 % of the phones it stands for are vowels)
    vowel_cols = {i for i, v in enumerate(QMUL_VOCAB) if i != QMUL_BLANK and v and v[0] in IPA_VOWEL_LEADS}
    vowel_cols.add(QMUL_UNK)
    vowels = {char_of[c] for c in vowel_cols if c in char_of}
    return tokens, char_of, dictionary, star_id, vowels


def qmul_prepare(logp, n_frames: int, star_penalty: float):
    """QMUL log-posteriors [Tq, 72] on its own grid -> float32 [n_frames, 72] on the 20 ms grid:
    blank first, the 70 placed columns, then a constant '*' column of -star_penalty, renormalised
    (MMS_FA's untrained '*' is the same construction with penalty 0). numpy only."""
    tokens, _, _, _, _ = qmul_columns()
    lp = logp.astype(np.float32).copy()
    cols = [QMUL_BLANK, QMUL_SEP]
    lp[:, QMUL_BLANK] = fused_lse(lp[:, cols].astype(np.float64)).astype(np.float32)
    lp = lp[:, [QMUL_BLANK] + tokens]
    lp = fused_resample(lp, QMUL_HOP_S, QMUL_OFFSET_S, n_frames)
    full = np.concatenate([lp.astype(np.float64), np.full((len(lp), 1), -float(star_penalty))], axis=1)
    return np.ascontiguousarray((full - fused_lse(full)[:, None]).astype(np.float32))


# --- lyrics -> QMUL pseudo-letters

def fused_normalise_word(word: str) -> str:
    """Lower-case, curly apostrophes to straight, accents stripped."""
    w = word.replace("’", "'").replace("‘", "'").replace("`", "'").lower()
    return "".join(c for c in unicodedata.normalize("NFD", w) if unicodedata.category(c) != "Mn")


class QmulTokenizer:
    """English espeak IPA (phonemizer's EspeakBackend, en-us), one phone per QMUL inventory entry,
    an unknown phone -> the trained unk class. Raises ImportError when phonemizer or the espeak-ng
    library (espeakng_loader, or PHONEMIZER_ESPEAK_LIBRARY) is missing."""

    def __init__(self):
        if not os.environ.get("PHONEMIZER_ESPEAK_LIBRARY"):
            import espeakng_loader
            from phonemizer.backend.espeak.wrapper import EspeakWrapper
            EspeakWrapper.set_library(espeakng_loader.get_library_path())
            EspeakWrapper.set_data_path(espeakng_loader.get_data_path())
        from phonemizer.backend import EspeakBackend
        from phonemizer.punctuation import Punctuation
        from phonemizer.separator import Separator
        self.ids = {p: i for i, p in enumerate(QMUL_IPA)}
        self._punct = Punctuation(';:,.!"?()-')
        self._sep = Separator(phone=";", word=" ")
        self._backend = EspeakBackend("en-us", language_switch="remove-flags")
        self._cache = {}

    def phones(self, word: str) -> list:
        w = self._punct.remove(fused_normalise_word(word))
        w = "".join(" " if unicodedata.category(c).startswith("P") and c != "'" else c for c in w).strip()
        if not any(c.isalnum() for c in w):
            return []
        s = self._backend.phonemize([w], separator=self._sep, strip=True)[0]
        return [p for chunk in s.split(" ") for p in chunk.split(";") if p]

    def tokenize(self, word: str) -> list:
        if word not in self._cache:
            out = []
            for p in self.phones(word):
                i = self.ids.get(p, QMUL_UNK)
                if i != QMUL_SEP:
                    out.append(i)
            self._cache[word] = out
        return list(self._cache[word])


def spell_digits(frag: str, num2words_fn) -> list:
    """A fragment with digits -> its pieces with every number spelled out (as normalize_word spells
    them for MMS_FA). Pure given num2words."""
    if not re.search(r"\d", frag):
        return [frag]
    out = []
    for part in re.split(r"(\d+)", frag):
        if part.isdigit():
            out += [t for t in re.split(r"[\s,\-]+", num2words_fn(int(part))) if t]
        elif part:
            out.append(part)
    return out


def qmul_groups(display: str, tokenize, num2words_fn):
    """One display word -> (token groups as QMUL column lists, authored). Authored hyphen fragments
    are tokenized one by one and stay separate groups (each is one syllable, as for MMS_FA)."""
    frags = split_fragments(display)
    gs = [[i for piece in spell_digits(f, num2words_fn) for i in tokenize(piece) if i != QMUL_SEP] for f in frags]
    return [g for g in gs if g], len(frags) > 1


def qmul_lines(pristine, tokenize, num2words_fn):
    """The freshly parsed lyrics with QMUL pseudo-letter tokens (a deep copy; pristine is not touched)."""
    import copy

    _, char_of, _, _, _ = qmul_columns()
    lines = copy.deepcopy(pristine)
    for ln in lines:
        for w in ln.words:
            gs, authored = qmul_groups(w.display, tokenize, num2words_fn)
            toks = ["".join(char_of[i] for i in g if i in char_of) for g in gs]
            w.tokens = [t for t in toks if t]
            w.authored = authored
            w.norm = "".join(w.tokens)
            w.untimed = not w.norm
    return lines


def mms_lines(pristine, dict_chars, num2words_fn):
    """The freshly parsed lyrics normalised for MMS_FA exactly as main normalises them (a deep copy)."""
    import copy

    lines = copy.deepcopy(pristine)
    for ln in lines:
        for w in ln.words:
            w.tokens, w.authored = normalize_display(w.display, dict_chars, num2words_fn)
            w.norm = "".join(w.tokens)
            if not w.norm:
                w.untimed = True
    return lines


# --- the emission product (ep): QMUL's phone evidence added to each MMS_FA letter state

def letters_to_phones(letters: str, phone_is_vowel: list) -> list:
    """Monotone DTW of a word's letters against its phones (vowel letters a e i o u y to vowel phones
    and consonants to consonants at no cost, any other pairing 1, the apostrophe 0.5): per letter,
    the index of the first phone on the path (-1 for every letter when there is no phone). Pure."""
    n, m = len(letters), len(phone_is_vowel)
    if m == 0:
        return [-1] * n
    C = [[0.5 if ch == "'" else (0.0 if (ch in LETTER_VOWELS) == v else 1.0) for v in phone_is_vowel]
         for ch in letters]
    inf = float("inf")
    D = [[inf] * (m + 1) for _ in range(n + 1)]
    D[0][0] = 0.0
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            D[i][j] = C[i - 1][j - 1] + min(D[i - 1][j - 1], D[i - 1][j], D[i][j - 1])
    i, j = n, m
    first = [m - 1] * n
    while i > 0 and j > 0:
        first[i - 1] = j - 1
        moves = [(D[i - 1][j - 1], i - 1, j - 1), (D[i - 1][j], i - 1, j), (D[i][j - 1], i, j - 1)]
        _, i, j = min(moves, key=lambda x: x[0])
    return first


def ep_columns(lines, tokenize, num2words_fn) -> dict:
    """{(line, word): [QMUL matrix column per letter of w.norm, -1 where none]} for MMS_FA lines."""
    _, char_of, dictionary, _, _ = qmul_columns()
    vow = {i for i, v in enumerate(QMUL_VOCAB) if i != QMUL_BLANK and v and v[0] in IPA_VOWEL_LEADS} | {QMUL_UNK}
    out = {}
    for i, ln in enumerate(lines):
        for wi, w in enumerate(ln.words):
            if w.untimed or not w.norm:
                continue
            gs, _ = qmul_groups(w.display, tokenize, num2words_fn)
            ph = [c for g in gs for c in g if c in char_of]
            if not ph:
                continue
            cols = [dictionary[char_of[c]] for c in ph]
            first = letters_to_phones(w.norm, [c in vow for c in ph])
            out[(i, wi)] = [cols[j] if j >= 0 else -1 for j in first]
    return out


def fused_ep_hook(pcols: dict, qx, star_id: int, errors: list):
    """The stamped decoder's emit_hook for the emission product. qx: float32 [T, C + 1], the
    weighted, floored QMUL log-probs with a zero column last; each letter state of a section piece
    gets qx's column of the phone its letter aligns to added to its emissions. A piece the hook
    cannot read is left as it is (counted in errors)."""
    Z = qx.shape[1] - 1

    def hook(p):
        try:
            pc = np.full(p.n, Z, dtype=np.int64)
            cnt = {}
            for s, own in enumerate(p.owner):
                if own is None or p.lab[s] == 0 or p.lab[s] == star_id:
                    continue
                c = cnt.get(own, 0)
                cnt[own] = c + 1
                cols = pcols.get(tuple(own))
                if cols is not None and c < len(cols) and cols[c] >= 0:
                    pc[s] = cols[c]
            if (pc == Z).all():
                return p
            orig = p.emit

            def emit(lp, t0, t1, a, b):
                E = orig(lp, t0, t1, a, b)
                sub = pc[a:b]
                sel = np.nonzero(sub != Z)[0]
                if len(sel):
                    E = np.array(E, dtype=np.float32, copy=True)
                    E[:, sel] += qx[t0:t1][:, sub[sel]]
                return E
            p.emit = emit
        except Exception:
            errors.append(1)
        return p
    return hook


# --- combining the paths

def median3_starts(a, b, c) -> list:
    """Per word, the median of three start lists (None: that path did not time the word). A word
    fewer than three paths timed keeps the first list's start (version 9's). Pure."""
    out = []
    for x, y, z in zip(a, b, c):
        if x is None or y is None or z is None:
            out.append(x)
        else:
            out.append(sorted((x, y, z))[1])
    return out


def apply_word_starts(lines, starts, song_end_ms: int) -> list:
    """Moves every timed word of `lines` (in place) to its new start in `starts` (one per word in
    lyric order; None keeps it), its end and syllables by the same offset; word ends are then cut
    at the next word's start as assemble cuts them, line spans rebuilt from their words, and
    validate_and_repair keeps starts strictly increasing and every end after its start. Returns the
    repair notes. Pure (standard library)."""
    flat = [w for ln in lines for w in ln.words]
    for w, s in zip(flat, starts):
        if s is None or w.untimed:
            continue
        d = int(s) - w.start_ms
        if d:
            w.start_ms += d
            w.end_ms += d
            for syl in w.syllables:
                syl["start_ms"] += d
                syl["end_ms"] += d
    timed = [w for w in flat if not w.untimed]
    for w, nxt in zip(timed, timed[1:]):
        if nxt.start_ms > w.start_ms:
            w.end_ms = min(w.end_ms, nxt.start_ms)
    for w in timed:
        # a word whose end was cut keeps its syllables inside it, their proportions kept
        if w.syllables and w.syllables[-1]["end_ms"] > w.end_ms:
            s0, old_len = w.start_ms, max(1, w.syllables[-1]["end_ms"] - w.start_ms)
            for syl in w.syllables:
                for k in ("start_ms", "end_ms"):
                    syl[k] = s0 + (syl[k] - s0) * (w.end_ms - s0) // old_len
    for ln in lines:
        tw = [w for w in ln.words if not w.untimed]
        if tw:
            ln.start_ms = tw[0].start_ms
            ln.end_ms = max(w.end_ms for w in tw)
    for i in range(len(lines) - 1):
        if lines[i].end_ms > lines[i + 1].start_ms and lines[i + 1].start_ms > lines[i].start_ms:
            lines[i].end_ms = lines[i + 1].start_ms
    return validate_and_repair(lines, song_end_ms)


def guard_starts(fused_starts, v9_starts, guard_ms) -> tuple:
    """Per word: the fused start, unless it is more than guard_ms from version 9's, when version 9's
    is kept (guard_ms None: no guard). fused_stage applies it to --fuse qmul under line stamps
    only. Ranked corpus, English maps, exact stamps: the QMUL path alone moves 26 words more than
    5 s from version 9's start, 1 of them to within 200 ms of the map and 12 of them away from a
    correct version 9 start (within 200 ms 95.13 -> 95.16 %, MAE 126 -> 110 ms with the guard).
    The median needs two paths to agree and made only 5 such moves (all on one song, none right),
    and the guard made those words worse; the ep path's large moves were mostly right (3 of 5).
    Plain lyrics are unguarded: there version 9 is the path that fails. Returns (starts, number
    of words guarded). Pure."""
    if guard_ms is None:
        return list(fused_starts), 0
    out, n = [], 0
    for f, v in zip(fused_starts, v9_starts):
        if f is not None and v is not None and abs(f - v) > guard_ms:
            out.append(v)
            n += 1
        else:
            out.append(f)
    return out, n


def review_flags(v9_starts, qmul_starts, threshold_ms: int = FUSED_REVIEW_MS) -> list:
    """Per word: True when both paths timed it and their starts are more than threshold_ms apart. Pure."""
    return [a is not None and b is not None and abs(a - b) > threshold_ms for a, b in zip(v9_starts, qmul_starts)]


def word_starts(lines) -> list:
    return [None if w.untimed else w.start_ms for ln in lines for w in ln.words]


# --- the QMUL model

def qmul_network(n_class: int = 72):
    """The QMUL CRNN (the authors' model.py structure with identical parameter names, so the
    checkpoint loads with strict=True): one conv, one residual CNN block, a (2, 3) max-pool, a linear
    to 256, three BiLSTMs and the classifier. forward_chunked runs the CNN part in time chunks with
    enough overlap to be exact (it sees +-3 mel frames), so the peak memory does not grow with the
    song's length (the whole-song CNN activations of a 5-minute song are over a gigabyte)."""
    import torch
    import torch.nn as nn
    import torch.nn.functional as F

    n_feats, rnn_dim, dropout = 32, 256, 0.1

    class CNNLayerNorm(nn.Module):
        def __init__(self, n_feats):
            super().__init__()
            self.layer_norm = nn.LayerNorm(n_feats)

        def forward(self, x):
            x = x.transpose(2, 3).contiguous()
            x = self.layer_norm(x)
            return x.transpose(2, 3).contiguous()

    class ResidualCNN(nn.Module):
        def __init__(self, in_channels, out_channels, kernel, stride, dropout, n_feats):
            super().__init__()
            self.cnn1 = nn.Conv2d(in_channels, out_channels, kernel, stride, padding=kernel // 2)
            self.cnn2 = nn.Conv2d(out_channels, out_channels, kernel, stride, padding=kernel // 2)
            self.dropout1 = nn.Dropout(dropout)
            self.dropout2 = nn.Dropout(dropout)
            self.layer_norm1 = CNNLayerNorm(n_feats)
            self.layer_norm2 = CNNLayerNorm(n_feats)

        def forward(self, x):
            residual = x
            x = self.cnn1(self.dropout1(F.gelu(self.layer_norm1(x))))
            x = self.cnn2(self.dropout2(F.gelu(self.layer_norm2(x))))
            return x + residual

    class BidirectionalLSTM(nn.Module):
        def __init__(self, rnn_dim, hidden_size, dropout, batch_first):
            super().__init__()
            self.BiLSTM = nn.LSTM(input_size=rnn_dim, hidden_size=hidden_size, num_layers=1,
                                  batch_first=batch_first, bidirectional=True)
            self.dropout = nn.Dropout(dropout)

        def forward(self, x):
            x, _ = self.BiLSTM(x)
            return self.dropout(x)

    class AcousticModel(nn.Module):
        def __init__(self):
            super().__init__()
            self.n_class = n_class
            self.cnn_layers = nn.Sequential(nn.Conv2d(1, n_feats, 3, stride=1, padding=1), nn.ReLU())
            self.rescnn_layers = nn.Sequential(ResidualCNN(n_feats, n_feats, kernel=3, stride=1, dropout=dropout,
                                                           n_feats=128))
            self.maxpooling = nn.MaxPool2d(kernel_size=(2, QMUL_POOL_T))
            self.fully_connected = nn.Linear(n_feats * 64, rnn_dim)
            # As published: only the first BiLSTM is batch_first; with batch 1 the second and third
            # see a length-1 sequence per frame (frame-wise), which is how the weights were trained.
            self.bilstm = nn.Sequential(
                BidirectionalLSTM(rnn_dim=rnn_dim, hidden_size=rnn_dim, dropout=dropout, batch_first=True),
                BidirectionalLSTM(rnn_dim=rnn_dim * 2, hidden_size=rnn_dim, dropout=dropout, batch_first=False),
                BidirectionalLSTM(rnn_dim=rnn_dim * 2, hidden_size=rnn_dim, dropout=dropout, batch_first=False))
            self.classifier = nn.Sequential(nn.Linear(rnn_dim * 2, n_class))

        def front(self, x):                          # (1, 1, 128 mels, T) -> (1, T // 3, 256)
            x = self.maxpooling(self.rescnn_layers(self.cnn_layers(x)))
            sizes = x.size()
            x = x.view(sizes[0], sizes[1] * sizes[2], sizes[3]).transpose(1, 2)
            return self.fully_connected(x)

        def forward(self, x):
            return self.classifier(self.bilstm(self.front(x)))

        def forward_chunked(self, x, chunk: int = QMUL_CNN_CHUNK):
            n_mel = x.size(3)
            n_out = n_mel // QMUL_POOL_T
            parts = []
            for o0 in range(0, n_out, chunk):
                o1 = min(n_out, o0 + chunk)
                m0 = max(0, QMUL_POOL_T * o0 - 2 * QMUL_POOL_T)
                m1 = min(n_mel, QMUL_POOL_T * o1 + 2 * QMUL_POOL_T)
                y = self.front(x[:, :, :, m0:m1])
                k0 = o0 - m0 // QMUL_POOL_T
                parts.append(y[:, k0:k0 + (o1 - o0)])
            return self.classifier(self.bilstm(torch.cat(parts, dim=1)))

    return AcousticModel()


def qmul_weights_path():
    """The QMUL checkpoint in torch hub's checkpoint dir (where torchaudio keeps MMS_FA), fetched
    from the pinned commit on first use and checked against QMUL_SHA256. Raises on any failure (a
    failed download, a hash mismatch); a bad file is deleted so the next run fetches it again."""
    import hashlib

    import torch

    d = Path(torch.hub.get_dir()) / "checkpoints"
    path = d / QMUL_FILE

    def sha(p):
        h = hashlib.sha256()
        with open(p, "rb") as f:
            for block in iter(lambda: f.read(1 << 20), b""):
                h.update(block)
        return h.hexdigest()

    if not path.exists():
        d.mkdir(parents=True, exist_ok=True)
        log(f"fused evidence: downloading the QMUL multilingual weights (57 MB, {QMUL_URL})")
        tmp = path.with_name(f"{path.name}.{os.getpid()}.part")
        try:
            torch.hub.download_url_to_file(QMUL_URL, str(tmp), progress=False)
            os.replace(tmp, path)
        finally:
            if tmp.exists():
                tmp.unlink()
    got = sha(path)
    if got != QMUL_SHA256:
        path.unlink()
        raise RuntimeError(f"the QMUL weights' sha256 is {got}, expected {QMUL_SHA256}; deleted")
    return path


def qmul_evidence(wav, audio_key: str, work: Path, device: str):
    """QMUL log-posteriors, float32 [Tq, 72] on QMUL's own grid (QMUL_HOP_S, QMUL_OFFSET_S), for the
    16 kHz stem the MMS_FA views were computed from (wav [1, N]), upsampled to the model's 22050 Hz
    (the model was trained on 16 kHz audio upsampled the same way). Cached in the work dir by the
    audio's content address and the weights' hash, written atomically like the MMS_FA views."""
    import warnings

    import torch
    import torchaudio

    cache = work / f"qmul_{audio_key}_{QMUL_SHA256[:12]}.npy"
    if cache.exists():
        try:
            lp = np.load(cache)
            if lp.ndim == 2 and lp.shape[1] == len(QMUL_VOCAB) and np.isfinite(lp).all():
                log(f"fused evidence: QMUL evidence cached ({cache.name})")
                return lp
        except Exception:
            pass
        log(f"fused evidence: QMUL cache unreadable ({cache.name}), recomputing")
    weights = qmul_weights_path()
    t0 = time.time()
    state = torch.load(weights, map_location="cpu", weights_only=True)
    model = qmul_network(len(QMUL_VOCAB))
    model.load_state_dict(state["model_state_dict"])
    model.eval().to(device)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")              # 128 mels on 257 bins: one empty filter, as published
        mel = torchaudio.transforms.MelSpectrogram(sample_rate=QMUL_SR, n_mels=128, n_fft=QMUL_N_FFT).to(device)
    with torch.no_grad():
        x = torchaudio.functional.resample(wav.reshape(1, -1).float(), SAMPLE_RATE, QMUL_SR)
        out = model.forward_chunked(mel(x.to(device)).unsqueeze(1))
        lp = torch.log_softmax(out, dim=2)[0].float().cpu().numpy()
    lp = np.logaddexp(lp, np.float32(np.log(QMUL_PROB_FLOOR))).astype(np.float32)
    log(f"fused evidence: QMUL multilingual pass, {lp.shape[0]} frames in {time.time() - t0:.1f}s")
    tmp = cache.with_name(f"{cache.name}.{os.getpid()}.tmp.npy")
    try:
        np.save(tmp, lp)
        os.replace(tmp, cache)
    except Exception as exc:
        log(f"WARNING: could not cache the QMUL evidence ({type(exc).__name__}: {exc})")
        try:
            tmp.unlink()
        except OSError:
            pass
    return lp


# --- the fused stage

def fused_decode(lines, mode: str, ref_end_ms, lp, dictionary: dict, star_id: int, voiced, pyphen_dic,
                 offset_ms: float, song_end_ms: int, vowel_chars=(), emit_hook=None):
    """One decode exactly as main runs it (the mode's decoder, assemble, validate_and_repair) on
    `lines` (already tokenized for `dictionary`) and the float32 matrix lp [T, C]. vowel_chars:
    pseudo-letters syllabify_token must read as vowels, added to VOWELS for the call only."""
    import torch

    t = torch.from_numpy(lp)
    added = set(vowel_chars) - VOWELS
    VOWELS.update(added)
    try:
        if mode == "ref":
            per_word = align_ref_mode(lines, ref_end_ms, t, dictionary, star_id, voiced, pyphen_dic=pyphen_dic,
                                      emit_hook=emit_hook)
        else:
            per_word = align_auto_mode(lines, t, dictionary, star_id, voiced)
        assemble(lines, per_word, voiced, offset_ms, pyphen_dic, t.size(0))
        validate_and_repair(lines, song_end_ms)
    finally:
        VOWELS.difference_update(added)
    return lines


def fused_stage(plan: dict, lines, mode: str, ref_end_ms, wav, audio_key: str, work: Path, device: str,
                voiced, log_probs, dictionary: dict, star_id: int, pyphen_dic, offset_ms: float,
                song_end_ms: int, dict_chars, num2words_fn):
    """--evidence fused, after the version 9 decode (`lines`, final): the QMUL path, the ep path
    and their combination. Returns (lines, report section); on any failure logs one WARNING and
    returns the version 9 lines unchanged (the meta then says why)."""
    import copy

    meta = plan["meta"]
    fuse = plan["fuse"]
    applied = "qmul" if mode == "auto" else fuse
    t0 = time.time()
    try:
        try:
            tok = QmulTokenizer()
        except ImportError as exc:
            raise RuntimeError(f"a dependency is missing ({exc}); install phonemizer and espeakng-loader") from None
        qlp_raw = qmul_evidence(wav, audio_key, work, device)
        n = len(voiced)
        tokens, char_of, qdic, qstar, qvowels = qmul_columns()
        penalty = FUSED_STAR_PENALTY[mode]
        v9_starts = word_starts(lines)

        log(f"fused evidence: decoding the QMUL path ('*' penalty {penalty:g})")
        ql = fused_decode(qmul_lines(plan["pristine"], tok.tokenize, num2words_fn), mode, ref_end_ms,
                          qmul_prepare(qlp_raw, n, penalty), qdic, qstar, voiced, pyphen_dic, offset_ms,
                          song_end_ms, vowel_chars=qvowels)
        q_starts = word_starts(ql)
        if len(q_starts) != len(v9_starts):
            raise RuntimeError("the QMUL path's lyrics do not match")

        el, ep_errors = None, []
        if applied in ("median3", "ep"):
            log(f"fused evidence: decoding the ep path (weight {FUSED_EP_WEIGHT:g}, floor {FUSED_EP_FLOOR:g})")
            T = log_probs.size(0)
            q0 = qmul_prepare(qlp_raw, n, 0.0)[:, :-1].astype(np.float64)
            q0 = q0 - fused_lse(q0)[:, None]
            if len(q0) < T:
                q0 = np.concatenate([q0, np.repeat(q0[-1:], T - len(q0), axis=0)], axis=0)
            qx = (FUSED_EP_WEIGHT * np.maximum(q0[:T], FUSED_EP_FLOOR)).astype(np.float32)
            qx = np.concatenate([qx, np.zeros((T, 1), np.float32)], axis=1)
            el = mms_lines(plan["pristine"], dict_chars, num2words_fn)
            hook = fused_ep_hook(ep_columns(el, tok.tokenize, num2words_fn), qx, star_id, ep_errors)
            el = fused_decode(el, mode, ref_end_ms, log_probs.detach().cpu().numpy(), dictionary, star_id, voiced,
                              pyphen_dic, offset_ms, song_end_ms, emit_hook=hook)
            if ep_errors:
                log(f"fused evidence: {len(ep_errors)} section(s) of the ep path decoded without the product")

        ep_starts = word_starts(el) if el is not None else None
        if applied == "ep":
            raw = ep_starts
        elif applied == "qmul":
            raw = q_starts
        else:
            raw = median3_starts(v9_starts, q_starts, ep_starts)
        guard_ms = FUSED_GUARD_MS[mode] if applied == "qmul" else None
        new, n_guarded = guard_starts(raw, v9_starts, guard_ms)
        if applied == "ep":
            out = el
            if n_guarded:
                for note in apply_word_starts(out, new, song_end_ms):
                    log(f"validator (fused): {note}")
        else:
            out = copy.deepcopy(lines)
            members = [ql] if applied == "qmul" else [lines, ql, el]
            for i, ln in enumerate(out):
                votes = sum(1 for m in members if m[i].estimated)
                ln.estimated = votes * 2 > len(members)
            for note in apply_word_starts(out, new, song_end_ms):
                log(f"validator (fused): {note}")
        flags = review_flags(v9_starts, q_starts)
        flat = [w for ln in out for w in ln.words]
        for w, f in zip(flat, flags):
            w.review = bool(f) and not w.untimed
        n_review = sum(1 for w in flat if w.review)
        moved = sum(1 for a, w in zip(v9_starts, flat) if a is not None and not w.untimed and w.start_ms != a)
    except Exception as exc:
        log(f"WARNING: fused evidence failed ({type(exc).__name__}: {exc}); keeping the version 9 path")
        meta["evidence_note"] = f"failed: {type(exc).__name__}: {exc}"[:300]
        return lines, None

    if plan.get("paths_file"):
        def spans(ls):
            # per word [start_ms, end_ms, [[syllable text, start_ms, end_ms], ...]], None if untimed
            return None if ls is None else [
                None if w.untimed else [w.start_ms, w.end_ms, [[x["text"], x["start_ms"], x["end_ms"]] for x in w.syllables]]
                for ln in ls for w in ln.words]

        try:
            Path(plan["paths_file"]).parent.mkdir(parents=True, exist_ok=True)
            Path(plan["paths_file"]).write_text(json.dumps({
                "anchor_mode": mode, "fuse_applied": applied, "guard_ms": guard_ms,
                "words": [w.display for ln in lines for w in ln.words],
                "v9": v9_starts, "qmul": q_starts, "ep": ep_starts, "combined": raw, "guarded": new,
                "spans": {"v9": spans(lines), "qmul": spans(ql), "ep": spans(el), "out": spans(out)},
            }), encoding="utf-8")
        except Exception as exc:
            log(f"WARNING: could not write the fused paths ({type(exc).__name__}: {exc})")

    meta.update(evidence="fused", fuse=fuse, fuse_applied=applied)
    meta.pop("evidence_requested", None)
    meta.update({
        "qmul_weights": QMUL_URL,
        "qmul_weights_sha256": QMUL_SHA256,
        "qmul_star_penalty": FUSED_STAR_PENALTY[mode],
        **({"ep_weight": FUSED_EP_WEIGHT, "ep_floor": FUSED_EP_FLOOR} if applied in ("median3", "ep") else {}),
        **({"guard_ms": guard_ms, "guarded_words": n_guarded} if guard_ms is not None else {}),
        "review_threshold_ms": FUSED_REVIEW_MS,
        "review_words": n_review,
    })
    if mode == "auto" and fuse != "qmul":
        meta["fuse_note"] = "auto mode: the ep path has no hook in the auto decoder, so the QMUL path is used alone"
    guarded = f", {n_guarded} kept at version 9 by the {guard_ms / 1000:g} s guard" if guard_ms is not None else ""
    log(f"fused evidence: fuse {applied}, {moved} word start(s) moved from version 9{guarded}, {n_review} word(s) "
        f"flagged for review; {time.time() - t0:.0f}s")
    rows = [f"=== review (version 9 and QMUL starts more than {FUSED_REVIEW_MS} ms apart): {n_review} words ==="]
    for li, ln in enumerate(out):
        for wi, w in enumerate(ln.words):
            if w.review:
                k = sum(len(x.words) for x in out[:li]) + wi
                rows.append(f"  line {li + 1}: '{w.display}' at {w.start_ms / 1000.0:.2f}s "
                            f"(version 9 {v9_starts[k] / 1000.0:.2f}s, QMUL {q_starts[k] / 1000.0:.2f}s)")
    if n_review == 0:
        rows.append("  (none)")
    head = [f"evidence: fused (fuse {applied}; {moved} of {sum(1 for s in v9_starts if s is not None)} word starts "
            f"moved from version 9{guarded})", ""]
    return out, head + rows


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
                    help="ref: the stamped decoder, each section inside its stamps (a stamped line "
                         "opens a section, unstamped lines join the section above them); "
                         "auto: the whole-song decoder for plain lyrics; none: single pass "
                         "(diagnostics). Default: ref when any line has a timestamp, else auto.")
    ap.add_argument("--vocal-mode", choices=["aligned", "estimated"], default="aligned",
                    help="aligned: the anchor mode's decoder (default); estimated: a stamped file's "
                         "lines are paced evenly from their stamps (plus the song's stamp lead) and "
                         "no acoustic path is kept, for vocals the model cannot follow. Chosen per "
                         "song by the mapper; it scores far below aligned on ordinary songs. Needs "
                         "line stamps: without any, the song is aligned as usual.")
    ap.add_argument("--language", default="en_US", help="pyphen hyphenation language")
    # "mid" works but is left out of the help: fast is the default and full the opt-in.
    ap.add_argument("--quality", choices=list(QUALITY_TIERS), default=DEFAULT_QUALITY,
                    metavar="{fast,full,single}",
                    help="evidence tier: fast = two int8 passes on offset chunk grids (default, "
                         "about the cost of one fp32 pass); full = eight passes (about 4x the "
                         "model time of fast, most accurate); single = one fp32 pass, no fusion")
    # Testing and diagnostics only: auto = int8 on the CPU when this torch build can run it, else
    # fp32; int8 = fail loudly when it cannot (no quantised engine, a failed probe, a GPU device or
    # --quality single, which is fp32 by definition); fp32 = every view in fp32.
    ap.add_argument("--quant", choices=["auto", "int8", "fp32"], default="auto", help=argparse.SUPPRESS)
    # Version 10, opt-in: without --evidence fused every output is version 9's.
    ap.add_argument("--evidence", choices=["mms", "fused"], default="mms",
                    help="mms: MMS_FA evidence only (default, version 9); fused: also run the QMUL "
                         "multilingual phoneme model on the same stem and combine (English only; "
                         "needs phonemizer and espeakng-loader, downloads 57 MB of weights once)")
    ap.add_argument("--lyrics-language", default=None, metavar="NAME",
                    help="the map's lyric language (english, japanese, spanish, ...); with --evidence "
                         "fused only English takes the fused path. Absent: a conservative detector decides")
    ap.add_argument("--fuse", choices=list(FUSE_CHOICES), default="median3",
                    help="with --evidence fused: median3 = per-word median of the version 9, QMUL and "
                         "emission-product starts (default); qmul = the QMUL path alone; ep = MMS_FA "
                         "with the QMUL emission product. Plain lyrics (auto) always use the QMUL path")
    ap.add_argument("--fused-paths", type=Path, default=None, metavar="FILE",
                    help="with --evidence fused: also write every path's word starts (version 9, QMUL, "
                         "ep, their combination before and after the guard) and each path's word and "
                         "syllable spans to this JSON file, for benchmarking; the outputs are unchanged")
    args = ap.parse_args()

    t0 = time.time()
    os.environ["OMP_NUM_THREADS"] = str(args.threads)
    os.environ["MKL_NUM_THREADS"] = str(args.threads)

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
    if str(args.device).startswith("cuda") and not torch.cuda.is_available():
        # The game passes --device cuda for an environment built with the CUDA wheels; a driver
        # that went missing since must not cost the player the import, so run on the CPU instead.
        log(f"WARNING: --device {args.device} was requested but torch sees no usable CUDA device "
            f"here (torch {torch.__version__}); running on the CPU instead")
        args.device = "cpu"

    stem = re.sub(r"[^\w\-]+", "_", args.audio.stem).strip("_")
    out_dir = args.out_dir or (Path(__file__).parent / "out" / stem)
    work = args.work_dir
    work.mkdir(parents=True, exist_ok=True)

    # ---- lyrics
    lines, ref_end_ms = parse_lyrics(args.lyrics)
    n_words = sum(len(ln.words) for ln in lines)
    # Sparse anchors (version 3): ONE stamped line is enough for ref mode; the unstamped lines are
    # placed inside their section's window (see ref_sections).
    n_stamped = sum(1 for ln in lines if ln.ref_ms is not None)
    has_ref = n_stamped > 0
    mode = args.anchors or ("ref" if has_ref else "auto")
    if mode == "ref" and not has_ref:
        log("WARNING: --anchors ref requested but no line has a stamp; using auto")
        mode = "auto"
    stamped = f" ({n_stamped} of {len(lines)} lines stamped)" if mode == "ref" and n_stamped < len(lines) else ""
    log(f"lyrics: {len(lines)} lines, {n_words} words; anchor mode: {mode}{stamped}")
    # The estimated vocal mode paces from the stamps, so it needs ref mode; anything else is aligned
    # as usual and the output records the mode that actually ran.
    vocal_mode = args.vocal_mode
    if vocal_mode == "estimated" and mode != "ref":
        log(f"WARNING: --vocal-mode estimated needs line stamps (anchor mode ref, here {mode}); aligning as usual")
        vocal_mode = "aligned"
    # version 10: decided (and logged) here, before the lyrics are normalised for MMS_FA
    fused = fused_plan(lines, mode, vocal_mode, args.lyrics_language, args.fuse) if args.evidence == "fused" else None
    if fused is not None:
        fused["paths_file"] = args.fused_paths

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

    # ---- dictionary (the model itself is loaded only if an emission view is not cached)
    dictionary = torchaudio.pipelines.MMS_FA.get_dict()
    star_id = dictionary.get("*", max(dictionary.values()) + 1)
    dict_chars = {k for k in dictionary if len(k) == 1 and (k.isalpha() or k == "'")}

    # ---- normalize
    dropped = set()
    for ln in lines:
        for w in ln.words:
            w.tokens, w.authored = normalize_display(w.display, dict_chars, num2words)
            w.norm = "".join(w.tokens)
            if not w.norm:
                w.untimed = True
                dropped.add(w.display)
    if dropped:
        log(f"untimed words (no alignable chars): {sorted(dropped)}")

    # ---- emissions (cached per view: the model passes depend on the audio alone, not on the
    # lyrics or the anchor mode, and they are the slow part of every re-run after a stamp is nudged)
    import hashlib
    audio_key = hashlib.sha256(wav.numpy().tobytes()).hexdigest()[:16]

    def mix_wav():
        mix16 = work / f"{stem}.mix16k.wav"
        ensure_wav(song_wav, mix16, SAMPLE_RATE, 1)
        mdata, msr = sf.read(mix16, dtype="float32")
        assert msr == SAMPLE_RATE
        return torch.from_numpy(mdata).unsqueeze(0)

    # the tier line is logged inside, after the model load when there is one (see its Log order)
    log_probs, evidence = evidence_emissions(args.quality, wav, audio_key, work, args.device, args.window_s,
                                             args.context_s, None if args.no_separate else mix_wav, args.quant)
    log(f"emissions: {log_probs.size(0)} frames x {log_probs.size(1)} labels")

    rms = frame_rms(wav)
    voiced = voiced_mask(rms)

    # ---- align per anchor mode (version 6: both decoders never raise; see their entry points)
    if vocal_mode == "estimated":
        per_word = align_estimated_mode(lines, ref_end_ms, log_probs, dictionary, star_id, voiced,
                                        pyphen_dic=pyphen_dic)
    elif mode == "ref":
        per_word = align_ref_mode(lines, ref_end_ms, log_probs, dictionary, star_id, voiced,
                                  pyphen_dic=pyphen_dic)
    elif mode == "auto":
        per_word = align_auto_mode(lines, log_probs, dictionary, star_id, voiced)
    else:
        char_ids, owners = build_targets(list(enumerate(lines)), dictionary, star_id)
        per_word = align_window(log_probs, 0, log_probs.size(0), char_ids, owners)

    # ---- assemble timings + outputs
    assemble(lines, per_word, voiced, args.offset_ms, pyphen_dic, log_probs.size(0))

    song_end_ms = int(dur_s * 1000)
    repairs = validate_and_repair(lines, song_end_ms)
    for note in repairs:
        log(f"validator: {note}")
    report_extra = None
    if fused is not None and fused["apply"]:
        lines, report_extra = fused_stage(fused, lines, mode, ref_end_ms, wav, audio_key, work, args.device, voiced,
                                          log_probs, dictionary, star_id, pyphen_dic, args.offset_ms, song_end_ms,
                                          dict_chars, num2words)
    meta = {
        "separator": ("none" if args.no_separate else args.demucs_model),
        "aligner": "torchaudio MMS_FA (wav2vec2 CTC forced alignment)",
        "aligner_version": ALIGNER_VERSION,
        "anchor_mode": mode,
        # only when it ran, so an aligned run's engine block carries no new key
        **({"vocal_mode": vocal_mode} if vocal_mode != "aligned" else {}),
        **evidence,
        "language": args.language,
        "offset_ms": args.offset_ms,
        "repairs": len(repairs),
        # version 10: only when --evidence fused was asked for (fused_plan, fused_stage)
        **(fused["meta"] if fused is not None else {}),
    }
    write_outputs(out_dir, stem, args.audio.name, lines, song_end_ms, meta)
    # Keep the isolated vocals stem beside the outputs (backlog 392). Only when this run actually
    # separated them: a --no-separate run aligned on the full mix, so wav16 IS the mix and there is
    # no vocals stem to persist.
    if not args.no_separate:
        try:
            persist_vocals_stem(out_dir, stem, wav16)
        except Exception as e:
            log(f"WARNING: could not write the vocals stem ({e})")
    report = write_report(out_dir, lines, voiced, mode, report_extra)
    print()
    print(report)
    log(f"outputs written to {out_dir}")
    log(f"total time: {time.time() - t0:.0f}s")


if __name__ == "__main__":
    if "--version" in sys.argv:
        print(ALIGNER_VERSION)
        sys.exit(0)
    if "--self-test-syllables" in sys.argv:
        sys.exit(self_test_syllables())
    if "--self-test-normalize" in sys.argv:
        sys.exit(self_test_normalize())
    if "--self-test-sections" in sys.argv:
        sys.exit(self_test_sections())
    if "--self-test-garbage" in sys.argv:
        sys.exit(self_test_garbage())
    # --self-test-pacing is the version 5 name: the prior's pure parts it pinned are gone, and what
    # is left of it (even_letter_frames) is the even-letters test
    if "--self-test-even-letters" in sys.argv or "--self-test-pacing" in sys.argv:
        sys.exit(self_test_even_letters())
    if "--self-test-spacing" in sys.argv:
        sys.exit(self_test_spacing())
    if "--self-test-estimated" in sys.argv:
        sys.exit(self_test_estimated())
    if "--self-test-late" in sys.argv:
        sys.exit(self_test_late())
    if "--self-test-band" in sys.argv:
        sys.exit(self_test_band())
    if "--self-test-dup" in sys.argv:
        sys.exit(self_test_dup())
    if "--self-test-cache" in sys.argv:
        sys.exit(self_test_cache())
    if "--self-test-fused" in sys.argv:
        sys.exit(self_test_fused())
    if "--self-test" in sys.argv:
        sys.exit(max(self_test_syllables(), self_test_normalize(), self_test_sections(), self_test_garbage(),
                     self_test_even_letters(), self_test_estimated(), self_test_spacing(), self_test_late(), self_test_band(),
                     self_test_dup(), self_test_cache(), self_test_fused()))
    main()
