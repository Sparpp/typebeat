# typebeat-lyriclab

Automatic **word- and syllable-level lyric timing** for type!beat. Given a song
(mp3) and its lyrics, produces LRC/JSON timing files. Lives outside the game
repo on purpose; nothing here touches the game build.

## Quickstart

```powershell
cd typebeat-lyriclab

# Recommended workflow: lyrics.txt already has hand line-stamps ([mm:ss.xx] per line)
.venv\Scripts\python.exe align_lyrics.py "<song.mp3>" "<lyrics.txt>" -o out\mysong

# Fully automatic (plain-text lyrics, no stamps)
.venv\Scripts\python.exe align_lyrics.py "<song.mp3>" "<lyrics.txt>" --anchors auto -o out\mysong
```

First run per song does Demucs vocal separation (~1 min on this machine, cached
in `work/`). Re-runs take ~20 s. Everything is CPU-only.

### Outputs (`out/<name>/`)

| file | contents |
|---|---|
| `<stem>.lrc` | line-level LRC; drop-in for the game's current `LrcParser` (incl. trailing end-marker) |
| `<stem>.words.lrc` | enhanced LRC: `[line]<mm:ss.xx>word …` + trailing line-end tag |
| `<stem>.syllables.lrc` | enhanced LRC with mid-word syllable tags (`<t>spec<t>ta<t>tor`), normalized text |
| `<stem>.timing.json` | **richest**: lines → words → syllables with `start_ms`/`end_ms`, confidence `score` (0..1 acoustic margin), `prob`, `estimated` flags |
| `report.txt` | QC: per-line deltas vs hand stamps (if present), margins, voiced-onset %, estimated-line list |

### Checking a result by ear/eye

```powershell
.venv\Scripts\python.exe -m http.server 8613 --directory .
# then open http://localhost:8613/demo/index.html
```

Copy `out/<name>/<stem>.timing.json` → `demo/data.json` and the song →
`demo/audio.mp3` first. The page plays the song, highlights words/syllables in
real time, shows per-line delta vs hand stamps, and click-a-word seeks the
audio there. Words with dotted red underline = low confidence.

## How it works

```
mp3 ─ffmpeg→ wav ─Demucs htdemucs→ vocals ─16 kHz→ wav2vec2 (MMS_FA) CTC emissions
lyrics ─normalize (lowercase, num2words, dict chars)→ char targets
        └────────── torchaudio forced_align (char level) ──────────┘
char spans → syllables (pyphen + vowel-group fallback) → words → lines
           → end-times extended through voiced audio (RMS gate) → LRC/JSON
```

- Emissions are computed in 30 s chunks with 4 s context and stitched exactly
  on the model's 20 ms frame grid (full-song attention would blow up CPU RAM).
- `*` wildcard tokens between lines absorb unlisted vocals (ad-libs, extra
  hook repeats) so they can't drag real lines off position.
- **Confidence = margin**, not raw probability: mean of
  `exp(logP(aligned char) − logP(argmax))` over the word's frames. High margin
  = the model genuinely hears that word there. Raw CTC prob is useless for
  singing (correct lines score 0.004–0.30).

### Anchor modes (`--anchors`)

- **`ref`** (default when every line has a `[mm:ss.xx]` stamp): each line is
  aligned only inside exactly `[its stamp, next stamp)`, and the model's word
  positions are kept whatever their confidence. Version 2: the former
  0.75 s / 0.5 s slack let repeated syllables latch onto the previous line's
  tail, and the even-pacing fallback for low-confidence lines was replacing a
  third of all lines; both lost on the ranked-map corpus (see below). Only a
  line the aligner cannot place at all is paced from its stamp and flagged
  `"estimated": true`.
- **Garbage-path detector (version 4)**: a `ref` path is also replaced when
  its SHAPE says the model heard nothing: most words sung one letter per
  frame (`crammed`), the whole line under 0.6x the song's median time per
  letter (`fast`), or a stamped line whose path opens 0.8 s or more after its
  stamp while running no slower than the median (`late`). The replacement is
  paced at the song's median rate from the stamp plus the song's stamp lead
  (how late the confident lines start after their stamps), flagged
  `"estimated": true` and logged with its reason.
  `python align_lyrics.py --self-test-garbage` pins the rules on synthetic
  paths; `--self-test` runs every self-test.
- **Sparse anchors (version 3)**: `ref` no longer needs every line stamped.
  Stamp only the section starts: a stamped line opens a section, the
  unstamped lines after it join it, and the whole section is aligned inside
  exactly `[its stamp, next stamp)` as one CTC target with `*` between its
  lines, so the model places the unstamped line starts itself. Lines before
  the first stamp form a section that opens at 0. A fully stamped file gives
  byte-identical output to version 2; `ref` is the default as soon as ONE
  line is stamped.
- **`auto`**: global pass → lines with margin ≥ 0.25 become anchors → each run
  of weak lines is re-aligned locally between its anchors → still-dead lines
  are interpolated char-proportionally across the voiced part of their window,
  flagged `estimated`.
- **`none`**: single global pass (research baseline).

## Accuracy, version 2 (ranked-map corpus, 2026-09-28)

Truth = the word timings of the ranked maps on typebeat.mingda.sh (every set,
one difficulty each; the 17 maps that were this aligner's own untouched output
excluded as circular), 84 maps, 20,603 word starts. Input = the map's line
starts ("exact") or those starts moved 250 ± 120 ms early ("human", how
mappers actually stamp). Word starts within 200 ms of the map:

| ref mode | exact stamps | human stamps | syllable boundaries | p90 error |
|---|---|---|---|---|
| version 1 (slack windows, even pacing under margin 0.08) | 85 % | 74 % | 87 % | 319 ms |
| **version 2 (exact windows, CTC kept)** | **90 %** | **87 %** | **93 %** | **210 ms** |
| `auto` (no stamps) | 67 % | – | 75 % | 10.4 s |

Sparse anchors (version 3, same corpus, 85 maps, 20,783 words; stamps
250 ± 120 ms early, a stamp dropped from all but every Nth line):

| stamps | word starts within 200 ms | non-first words | lines within 1 s | p90 |
|---|---|---|---|---|
| every line | 88 % | 88 % | 97 % | 250 ms |
| every 2nd line | 85 % | 86 % | 94 % | 342 ms |
| every 4th line | 83 % | 84 % | 89 % | 590 ms |
| none (`auto`) | 67 % | 69 % | 71 % | 10.9 s |

Every map scores above `auto` with half its stamps; fully stamped and `auto`
outputs are byte-identical to version 2 on all 101 maps.

Better on 43 maps, within 3 points on 36, worse on 5 (all screamed or
effect-heavy vocals, where even pacing from the stamp beat a garbage CTC path
under EXACT stamps). Pinning the first word to its stamp was also measured and
rejected: 76 % under human stamps. Bench scripts: `bench/` (build the corpus
from the site, run variants, score).

Garbage-path detector (version 4, same 85 maps, word starts within 200 ms):

| stamps | version 3 | version 4 | lines replaced |
|---|---|---|---|
| every line, exact | 89.5 % | 90.2 % | 103 |
| every line, human | 87.8 % | 88.0 % | 103 |
| every 2nd line, exact | 86.5 % | 86.9 % | 146 |
| every 2nd line, human | 85.5 % | 85.8 % | 152 |
| none (`auto`, untouched) | 66.8 % | 66.8 % | – |

Exact stamps: 18 maps up by a point or more, none down; Crimson Dance 66 % ->
79 %. Human stamps: 10 up, 2 down by at most 2.5 points. Shinigiwa Satellite
stays at 46 %: its wrong paths are spread at the song's own pace, so no shape
rule sees them, and the margin rule that does (version 1's, margin < 0.08)
costs 5 to 11 points corpus-wide under human stamps.

## Accuracy, version 1 (Friday Pilots Club – Spectator, 183 s, vs hand line stamps)

| mode | median \|Δ\| | max \|Δ\| | ≤ 0.5 s | ≤ 1 s |
|---|---|---|---|---|
| none (global CTC) | 0.55 s | **18.5 s** | 45 % | 72 % |
| auto | 0.55 s | 9.7 s | 45 % | 78 % |
| **ref** | **0.40 s** | **0.98 s** | 68 % | **100 %** |

- Median **signed** delta is ~+0.4 s in every mode: hand stamps lead the sung
  onset (mappers stamp on the beat / before the voice) while CTC fires on the
  vowel. For gameplay you likely want line *display* slightly early anyway;
  use `--offset-ms` if desired; word-relative timing is unaffected.
- 97 % of word onsets land in voiced audio (ref mode).
- Root cause of all large errors: sections where the actual vocals are
  phonetically opaque (chorus-1 hook and outro here are effects-heavy; the
  16 s instrumental tail attracts desperate DP paths). Verified by greedy
  decode (`debug_decode.py`): those regions produce zero character evidence
  on both the vocal stem *and* the raw mix; no aligner can hear what isn't
  there. That is exactly what the `estimated` flag + stamps workflow solves.

## Practical recipe for type!beat maps

1. Author `lyrics.txt` as today: one `[mm:ss.xx]` stamp per line (fast, tap
   along) + trailing end-marker. Short on time? Stamp only the first line of
   each verse and chorus; the rest are placed between the stamps.
2. Run the aligner (defaults to `ref` mode) → per-word/per-syllable timing.
3. Open the demo page, click through low-confidence (underlined) words, nudge
   stamps if needed, re-run (20 s).
4. Ship `<stem>.timing.json` (or `words.lrc`) next to the map.

Game-side integration: current `LrcParser` already reads the plain `.lrc`.
For word timing, either parse `words.lrc` (extend the regex to capture
`<mm:ss.xx>` tags) or, better, read `timing.json` directly; it carries
syllables, confidences and `estimated` flags the game/editor can surface.

## Environment

Recreate with [uv](https://docs.astral.sh/uv/):

```powershell
uv venv .venv --python 3.11
uv pip install --python .venv\Scripts\python.exe --index-url https://download.pytorch.org/whl/cpu torch==2.5.1 torchaudio==2.5.1
uv pip install --python .venv\Scripts\python.exe demucs==4.0.1 soundfile pyphen num2words tqdm
```

(torch 2.5.x pinned deliberately: 2.6 flips `torch.load(weights_only=True)`
which breaks Demucs checkpoint loading. Python 3.11 pinned for wheel
coverage.)

Models cache in `%USERPROFILE%\.cache\torch\hub\checkpoints` (~1.3 GB total:
MMS_FA aligner + htdemucs).

## Known limitations / future work

- **English-first**: syllabification is pyphen `en_US` + naive fallback. For
  Japanese maps, romanize first (pykakasi) and align romaji; MMS_FA is
  multilingual, and a typing game wants romaji anyway. Wire-up is ~30 lines.
- `auto` mode can still misplace lines when near-identical hook lines repeat
  over sparse evidence (Spectator outro: 3 lines ~9.5 s off, margins ≤ 0.17;
  low margin marks them for review). Stamps (`ref`) eliminate this class.
- Word *end* times are heuristic (voiced-region extension capped by next word);
  starts are the reliable quantity.
- `debug_decode.py <wav16k> [start_s] [end_s]` prints what the model hears;
  use it whenever a section refuses to align.
