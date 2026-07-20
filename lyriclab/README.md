# typebeat-lyriclab

Automatic **word- and syllable-level lyric timing** for type!beat. Given a song
(mp3) and its lyrics, produces LRC/JSON timing files. Lives outside the game
repo on purpose — nothing here touches the game build.

## Quickstart

```powershell
cd path\to\typebeat-lyriclab   # this folder

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
| `<stem>.lrc` | line-level LRC — drop-in for the game's current `LrcParser` (incl. trailing end-marker) |
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
  aligned only inside `[its stamp − 0.75 s, next stamp + 0.5 s]`. Lines whose
  audio carries no phonetic evidence (heavily effected hooks, screams,
  vocoder) fall back to pacing chars from the stamp itself and are flagged
  `"estimated": true`.
- **`auto`**: global pass → lines with margin ≥ 0.25 become anchors → each run
  of weak lines is re-aligned locally between its anchors → still-dead lines
  are interpolated char-proportionally across the voiced part of their window,
  flagged `estimated`.
- **`none`**: single global pass (research baseline).

## Accuracy (Friday Pilots Club – Spectator, 183 s, vs hand line stamps)

| mode | median \|Δ\| | max \|Δ\| | ≤ 0.5 s | ≤ 1 s |
|---|---|---|---|---|
| none (global CTC) | 0.55 s | **18.5 s** | 45 % | 72 % |
| auto | 0.55 s | 9.7 s | 45 % | 78 % |
| **ref** | **0.40 s** | **0.98 s** | 68 % | **100 %** |

- Median **signed** delta is ~+0.4 s in every mode: hand stamps lead the sung
  onset (mappers stamp on the beat / before the voice) while CTC fires on the
  vowel. For gameplay you likely want line *display* slightly early anyway —
  use `--offset-ms` if desired; word-relative timing is unaffected.
- 97 % of word onsets land in voiced audio (ref mode).
- Root cause of all large errors: sections where the actual vocals are
  phonetically opaque (chorus-1 hook and outro here are effects-heavy; the
  16 s instrumental tail attracts desperate DP paths). Verified by greedy
  decode (`debug_decode.py`): those regions produce zero character evidence
  on both the vocal stem *and* the raw mix — no aligner can hear what isn't
  there. That is exactly what the `estimated` flag + stamps workflow solves.

## Practical recipe for type!beat maps

1. Author `lyrics.txt` as today: one `[mm:ss.xx]` stamp per line (fast, tap
   along) + trailing end-marker.
2. Run the aligner (defaults to `ref` mode) → per-word/per-syllable timing.
3. Open the demo page, click through low-confidence (underlined) words, nudge
   stamps if needed, re-run (20 s).
4. Ship `<stem>.timing.json` (or `words.lrc`) next to the map.

Game-side integration: current `LrcParser` already reads the plain `.lrc`.
For word timing, either parse `words.lrc` (extend the regex to capture
`<mm:ss.xx>` tags) or — better — read `timing.json` directly; it carries
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
  Japanese maps, romanize first (pykakasi) and align romaji — MMS_FA is
  multilingual, and a typing game wants romaji anyway. Wire-up is ~30 lines.
- `auto` mode can still misplace lines when near-identical hook lines repeat
  over sparse evidence (Spectator outro: 3 lines ~9.5 s off, margins ≤ 0.17 —
  low margin marks them for review). Stamps (`ref`) eliminate this class.
- Word *end* times are heuristic (voiced-region extension capped by next word);
  starts are the reliable quantity.
- `debug_decode.py <wav16k> [start_s] [end_s]` prints what the model hears —
  use it whenever a section refuses to align.
