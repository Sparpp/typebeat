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

# Most accurate evidence (about 4x the model time; the game's high-accuracy setting)
.venv\Scripts\python.exe align_lyrics.py "<song.mp3>" "<lyrics.txt>" --quality full -o out\mysong
```

First run per song does Demucs vocal separation (~1 min on this machine) and
the emission passes (two int8 passes by default, about 30 s for a 3.5 minute
song at 6 threads); both are cached in `work/`, so a re-run after nudging a
stamp takes a few seconds. Everything runs on the CPU unless the environment
was built with the CUDA wheels (`--device cuda`, which the game passes then).

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
                                    (--quality: 1, 2, 4 or 8 passes, posteriors averaged)
lyrics ─normalize (lowercase, num2words, dict chars)→ char targets
        └──── banded CTC Viterbi (char level; the version 6 decoders) ────┘
char spans → syllables (pyphen + vowel-group fallback) → words → lines
           → end-times extended through voiced audio (RMS gate) → LRC/JSON
```

- Emissions are computed in 30 s chunks with 4 s context and stitched exactly
  on the model's 20 ms frame grid (full-song attention would blow up CPU RAM).
  Since version 6 several such passes ("views") are averaged; see
  [Evidence tiers](#evidence-tiers---quality-version-6).
- `*` wildcard tokens between lines absorb unlisted vocals (ad-libs, extra
  hook repeats) so they can't drag real lines off position.
- **Confidence = margin**, not raw probability: mean of
  `exp(logP(aligned char) − logP(argmax))` over the word's frames. High margin
  = the model genuinely hears that word there. Raw CTC prob is useless for
  singing (correct lines score 0.004–0.30).

### Anchor modes (`--anchors`)

- **`ref`** (default as soon as ONE line has a `[mm:ss.xx]` stamp; version 6):
  a first pass aligns each section alone inside `[its stamp, next stamp)`;
  from its section openers the aligner measures the song's stamp error (the
  lead: how late confident lines start after their stamps, taken over at least
  5 openers; the spread of those onsets; openers heard before their stamps;
  and, from a pass that looks 1 s either side of each window, whether the
  stamps are LATE, as when a mapper stamps on reaction). Then ONE banded
  Viterbi over the whole song places every letter: each section's letters
  live in their window softly (an opener heard before its stamp opens its
  section's left wall by up to 0.4 s, late stamps open every wall, the window
  may spill past the next stamp by the lead minus 0.15 s), the first letter
  of each section is pulled toward stamp + lead, and version 5's even-pacing
  prior is laid out syllable by syllable. The model's evidence still decides
  wherever it is clear. Version 2's lesson stands: the former 0.75 s / 0.5 s
  hard slack let repeated syllables latch onto the previous line's tail, and
  an even-pacing fallback for low-confidence lines replaced a third of all
  lines; both lost on the ranked-map corpus (see below).
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
- **Sparse anchors (version 3)**: `ref` does not need every line stamped.
  Stamp only the section starts: a stamped line opens a section, the
  unstamped lines after it join it, and the model places the unstamped line
  starts itself. Lines before the first stamp form a section that opens at 0.
  Since version 6 a section of several lines is a section graph inside the
  whole-song pass: letter spacing costs, a soft 400 ms minimum gap between
  lines, and every line the first pass heard confidently as an anchor for the
  pacing prior of the lines around it.
- **`auto`** (plain lyrics; version 6): a duration-aware banded Viterbi over
  the whole song. A first pass measures the song's pace (frames per letter,
  from its confident lines); a second pass charges gaps inside a line, letters
  skipping their blank, and voiced audio the `*` between two lines swallows,
  all at that pace. The `*` charge applies only when the lyric sheet repeats
  its lines: a sheet that writes each chorus once must be free to skip the
  unlisted repeats. Voiced audio after the last line earns a small bonus (the
  lyrics end where the song's singing does), guarded against squeezing lines
  with no evidence. Lines laid out by a fallback, and lines whose letters'
  margin is under 0.08, are flagged `"estimated": true`.
- **`none`**: single global pass (diagnostics).

Both version 6 decoders are bounded in memory and time (linear in song
length; under 300 MB and 30 s for a 20 minute song) and never raise: the
stamped one falls back to even pacing per section, the auto one to an even
layout over the voiced audio. `--self-test-spacing`, `--self-test-late`,
`--self-test-band`, `--self-test-dup` and `--self-test-even-letters` pin
their pure parts with the standard library only (`--self-test-pacing`, the
version 5 name, runs the last); `--self-test` runs every self-test.

### Estimated vocals (`--vocal-mode estimated`, version 7)

For a song whose vocals the model cannot follow (screamed, effect-heavy, the
Shinigiwa class), the mapper can choose to drop the acoustic path entirely:
every line of a stamped file is paced evenly from its stamp plus the song's
stamp lead, at the song's median letter length (both read off the stamped
decoder's first pass), and flagged `"estimated": true`. It is a per-song
choice, never a default and never a fallback: pooled over the ranked corpus
it scores 60.66 % / 56.02 % (exact / human stamps, `single` evidence) against
the stamped decoder's 91.69 % / 90.58 %, but on Shinigiwa Satellite it goes
55.0 % -> 75.2 % (exact) and 51.3 % -> 59.3 % (human). It needs stamps;
without any the song is aligned as usual. In the game it is offered beside
the import and the editor's "Generate timing", and remembered per map set.
`--self-test-estimated` pins the layout.

### Fused evidence (`--evidence fused`, version 10, opt-in)

Without the new flags, version 10 is version 9: every output file is byte-identical apart
from `aligner_version`, and none of the new code is imported. `--evidence fused` adds a
second acoustic model for English lyrics: the QMUL singing-trained phoneme CTC
(LyricsAlignment-Multilingual, Huang, Benetos, Ewert; trained on DALI; MIT licensed; 57 MB;
5 to 10 s per song on one thread). It runs on the same 16 kHz stem as the MMS_FA views
and is combined three ways:

- the **QMUL path**: its evidence through the same version 9 decoders (each phone becomes a
  pseudo-letter, the posteriors are resampled to the 20 ms grid, the `*` column is MMS_FA's
  constant one, 2 nats dearer in stamped modes);
- the **ep path**: the MMS_FA letters through the stamped decoder with the QMUL evidence added
  per letter (each letter's aligned phone, `w * max(log p, floor)`);
- `--fuse median3` (default): each word's median start over the version 9, QMUL and ep paths
  (auto mode: the QMUL path alone, the ep hook exists only in the stamped decoder);
  `--fuse qmul` and `--fuse ep` keep one path.

Under `--fuse qmul` with stamps, a QMUL-path start more than 5 s from version 9's keeps
version 9's (the engine block reports `guard_ms` and `guarded_words`): on the ranked corpus
the QMUL path alone moved 26 words that far under exact stamps, and one of them landed within
200 ms of the map. The median and ep paths
have no guard; on the corpus a guard only made their few large moves worse.

Measured on the 135 English maps of the ranked corpus (154 maps, 37,083 words; word starts
within 200 ms of the map; paired map bootstrap; `bench/altmodels/results/CORPUS_TABLE_ENGLISH.md`):

| stamps | version 9 | version 10 fused | difference [95 % CI] | MAE ms |
|---|---|---|---|---|
| exact line stamps | 93.01 % | **95.72 %** | +2.71 [+2.24, +3.15] | 118 -> 101 |
| human-style stamps (250 +- 120 ms early) | 91.90 % | **95.20 %** | +3.29 [+2.78, +3.81] | 123 -> 105 |
| none (`--anchors auto`) | 83.59 % | **91.62 %** | +8.03 [+5.80, +10.31] | 1366 -> 488 |

On the 98 of those maps that played no part in choosing the method or its constants the differences are
+2.65, +3.21 and +7.57 points; on independent maps (no aligner seed in their history) +2.70,
+3.49 and +7.74. With the mappers' own stamps (10 maps) the gain is +1.79 [-0.50, +3.99],
too few maps to decide. The extra cost per song is the QMUL pass (5 to 10 s on one thread)
and two more decodes (a few seconds), against the MMS_FA views' two to three minutes.

Words where the version 9 and QMUL paths start more than 200 ms apart carry `"review": true`
in timing.json (an editor hint; the game ignores the field today). `--lyrics-language NAME`
routes: only `english` runs the fused path; without the flag a conservative detector decides
(ASCII letters and common English words). Anything else, a missing dependency (`phonemizer`,
`espeakng-loader`) or weights that cannot be fetched falls back to version 9 with a logged
reason. The weights are downloaded once from a pinned commit and checked by sha256. Measured
effect and the evidence behind every choice: `bench/altmodels/RESULTS.md`. Shipping it in the
game needs the steps in `bench/altmodels/PORTING.md`.

### espeak-ng on a non-ASCII path (version 11)

Version 11 changes no output; it keeps the fused path's espeak-ng start-up from ending the
whole run. espeak-ng reads its data dir through the narrow (ANSI) C file API, so on Windows
an `espeakng-loader` installed under a path with any non-ASCII character (a user profile named
José) could not be read: espeak fell back to the data path compiled into the wheel
(`D:/a/espeakng-loader/...`, its CI build dir) and then called C `exit(1)`, which no Python
`except` survives, so the run failed instead of falling back to version 9. Now:

- `espeak_data_path` hands espeak the wheel's own data dir when it (and what it resolves to) is
  ASCII, else a copy at `%ProgramData%\typebeat\espeak-ng-data-<espeakng-loader version>`
  (about 18 MB, made once per version into a temporary sibling renamed into place, reused
  after), else raises before espeak is touched. An 8.3 short name does not work: phonemizer
  resolves the data path it is given, which turns a short name back into the long one.
- `espeak_probe` first starts espeak and phonemizes one word in a child Python (15 s timeout,
  once per process), so any other `exit()` inside espeak's start-up is a logged fall back too.

`--self-test-espeak` pins both. The "version 11 candidates" in `bench/altmodels/RESULTS.md`
are version 12 candidates now.

### Separate only (`--separate-only`, version 11)

```powershell
.venv\Scripts\python.exe align_lyrics.py "<song.mp3>" --separate-only -o out\mysong
```

Runs the audio half of an alignment and stops: the song is decoded, Demucs separates the vocals
and the stem is resampled to 16 kHz mono exactly as an alignment run does it (same `work/` file
names, so either run reuses the other's separation; `--demucs-model`, `--device` and `--threads`
apply), and only `vocals.wav` is written to `-o`. The lyrics argument is optional under the flag
(given, it is not read) and still required without it; `--no-separate` contradicts it and is
refused. The game runs it after an import that never aligned (line or word stamps, a TTML, a blank
map) when the player asked for the vocals to be isolated, so the editor's vocals waveform has a
stem. No output changes for any other run; `--self-test-separate` pins the argument rules and the
file plumbing with ffmpeg and Demucs stubbed.

## Evidence tiers (`--quality`, version 6)

One MMS_FA pass guesses at what it barely hears, and its chunk seams every
30 s cost it context. Version 6 averages the posteriors of several passes
("views") over the same audio. The passes run on a dynamically int8-quantised
copy of the model (the Linear layers only), which makes each pass about half
the cost of today's fp32 pass and scores the same as fp32.

| `--quality` | views | model time, 212 s song, 6 / 2 threads | exact / human stamps / `auto`, within 200 ms |
|---|---|---|---|
| `single` | one fp32 pass (version 5's) | 26 s / 56 s | 91.69 / 90.58 / 80.08 % |
| **`fast`** (default) | stem on the stock grid + on a grid shifted 15 s | 29 s / 52 s | **92.20 / 91.07 / 83.11 %** |
| `mid` (hidden) | + a grid shifted 22.5 s, a loudness-levelled stem | 57 s / 103 s | 92.44 / 91.65 / 84.31 % |
| `full` | + a grid shifted 7.5 s, levelled + shifted, 150 Hz high-pass, the full mix | 112 s / 206 s | 92.75 / 92.01 / 85.50 % |

(The fused tiers' times include the one-off quantisation, 1.6 s; accuracy
with the version 6 decoders on the ranked corpus below.) Every view is cached in the work dir,
named by the audio's content, the view, the precision and, for int8, the
quantised engine (engines differ in output), so an fp32 fallback never reads
an int8 entry or the other way round. The torch thread count (`--threads`) is
deliberately not in the name: int8 output moves with it (words by up to about
0.5 points on a stamped variant, 1 on `auto`; the per-frame best letter is
identical), and a re-run at another thread count reuses the views on disk
rather than paying for them again and moving words by that noise. The count
that produced a view is stored in its file and reported as `torch_threads`. A
tier reuses the views a smaller tier cached. The fp32 pass of `single` keeps
version 5's file name. Cache files are written to a temporary name and renamed
into place, so a run killed mid-write (the game kills the aligner on cancel)
leaves nothing truncated; an entry that does not load as a finite matrix is
logged, deleted and recomputed.

Fallbacks, all logged: on `--device cuda` the same views run in fp32 on the
GPU (dynamic int8 is a CPU feature); a torch build without a quantised engine
(ARM builds without qnnpack) runs the same views in fp32 on the CPU (the same
quality, about twice the time); `--device cuda` without a usable CUDA device
runs on the CPU. The view count never changes silently. A view that fails
(other than the stock-grid stem pass), or whose emissions are not finite, is
logged and the others are fused; a stem digitally silent in 95 % of its frames
fails the loudness-levelled views that way. The timing.json `engine` block
records `quality`, the `views` fused, `quant` (`int8` or `fp32`) and, for int8,
`quant_engine` and `torch_threads` (the count that produced the views; a list
when the cached views came from different counts).

## Model-swap bench (`bench/altmodels/`, 2026-10-06)

A self-contained bench that holds lyrics, audio and decoding fixed and swaps the acoustic
model, to answer whether a singing-trained aligner, a different speech CTC model or Whisper
would beat MMS_FA. It also refreshes the ranked-map corpus from typebeat.sh with per-map
provenance (TTML import, hand-stamped LRC, plain text, or an aligner seed) and runs the shipped
CLI on it as the baseline. Start with `bench/altmodels/RESULTS.md`; `PLAN.md` has the protocol,
`ENVIRONMENT.md` the cloud-container compute and network findings, `CANDIDATES.md` the models
not yet integrated.

```bash
# the refreshed corpus (154 ranked maps) and the paired comparison against the shipped aligner
python -m bench.altmodels.corpus fetch --out <corpus dir>
python -m bench.altmodels.corpus_queue --corpus <corpus dir> --threads 3
python -m bench.altmodels.corpus_table
```

## Accuracy, version 6 (ranked-map corpus, 2026-09-30)

Same corpus and scorer as below (85 maps, 20,783 word starts). Besides exact
(`ref`) and human (`href`, 250 ± 120 ms early) stamps: three more human seeds
(`hrefB`, `hrefC`), stamps late by 80 ± 60 ms (`hlate`), sloppy stamps
400 ± 200 ms early (`hwide`), and every 2nd / 4th / 8th / a random third of
the stamps kept (`sref`, `s4ref`, ..., `s3rand`). Word starts within 200 ms:

| variant | version 5 | 6 `single` | **6 `fast`** | 6 `full` |
|---|---|---|---|---|
| ref (exact) | 90.84 % | 91.69 % | **92.20 %** | 92.75 % |
| href (human) | 88.97 % | 90.58 % | **91.07 %** | 92.01 % |
| sref (every 2nd, exact) | 88.21 % | 90.00 % | **90.91 %** | 91.57 % |
| shref (every 2nd, human) | 86.69 % | 89.10 % | **90.24 %** | 91.11 % |
| hlate (late stamps) | 88.07 % | 90.51 % | **91.15 %** | 91.83 % |
| hwide (sloppy stamps) | 84.90 % | 88.76 % | **89.23 %** | 90.39 % |
| s4href (every 4th, human) | 83.53 % | 87.11 % | **88.85 %** | 89.56 % |
| s8href (every 8th, human) | 80.66 % | 84.91 % | **86.52 %** | 88.37 % |
| `auto` (no stamps) | 66.76 % | 80.08 % | **83.11 %** | 85.50 % |

On `fast` every one of the 14 variants and both halves of a split by set
gain over version 5, and every bootstrap interval excludes 0 (stamped +1.36
to +5.87 points, `auto` +16.35). Stamp models never tuned on: stamps 300 ms
late 67.13 % -> 82.34 % (version 5 vs 6 `single`), drifting late 67.41 % ->
80.05 %, 600 ms early 79.26 % -> 88.09 %. The two changes are independent:
the new decoders on version 5's single pass gain on every variant too, and
the fused evidence gains with version 5's decoders as well.

Known failure modes (measured; none is a crash):

- Voiced non-lyric audio before the vocals (a leaky or unseparated intro)
  can pull plain lyrics into it, because the RMS voicing gate calls it sung:
  with 60 s of instrumental before the song, `auto` loses about 4 points of
  its own score and 5 to 7 of 85 maps collapse (version 5 lost 1.3 to 2.6,
  from a much lower score). Stamped files are unaffected. Mili's Between Two Worlds
  (a 136 s voiced intro) collapses in `auto` on fused evidence.
- An abbreviated lyric sheet (each chorus written once) gains about +6
  points in `auto` instead of +14.
- An unheard slow opening can drift late (slow choral songs: VOCES8's Locus
  Iste loses 3 to 17 points against version 5 on most variants).
- The `*` between two lines can drag unstamped lines across a long
  instrumental inside a sparsely stamped section.

Rejected on the same data: the full mix in the middle tier (it helped one
half of the corpus, not the other), a bonus for starting `auto` lyrics
early (-0.58), a stamp window spilling with no margin (a trade between exact
and human stamps), a late-stamp call on 3 openers (false calls on exact
stamps), one voiced-`*` cap for complete and abbreviated sheets alike.
Numbers, tables and the porting spec: `bench/exp/v6/` (`FINAL.txt`,
`EVIDENCE.txt`, `PORTING.txt`).

## Accuracy, version 2 (ranked-map corpus, 2026-09-28)

Truth = the word timings of the ranked maps on typebeat.sh (every set,
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
   stamps if needed, re-run (seconds: the separation and emissions are cached).
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
# optional, the fused evidence path (version 10); without it the aligner runs version 9's
uv pip install --python .venv\Scripts\python.exe phonemizer espeakng-loader
```

(torch 2.5.x pinned deliberately: 2.6 flips `torch.load(weights_only=True)`
which breaks Demucs checkpoint loading. Python 3.11 pinned for wheel
coverage.)

`setup.ps1` / `setup.sh` do the same (the fused-evidence pair best effort, by
its own call, then its weights through `qmul_weights_path`) and, as their last
step, write `.venv/.typebeat-setup-ok`. The game treats the aligner as
installed only when that sentinel exists; a venv built by hand without it shows
as "Repair" in the game's settings, and Repair keeps it (writing the sentinel)
if its packages import, or rebuilds it if they do not. The pair is not part of
that import check, so a platform without an `espeakng-loader` wheel still
installs. `setup.ps1 -Update` / `setup.sh --update` bring a completed install
up to the script's package set in place (torch untouched, nothing removed);
the game's Update button runs it after copying newer scripts.

Models cache in `%USERPROFILE%\.cache\torch\hub\checkpoints` (~1.3 GB total:
MMS_FA aligner + htdemucs, plus 57 MB for the QMUL weights of the fused path).

## Known limitations / future work

- **English-first**: syllabification is pyphen `en_US` + naive fallback. For
  Japanese maps, romanize first (pykakasi) and align romaji; MMS_FA is
  multilingual, and a typing game wants romaji anyway. Wire-up is ~30 lines.
- `auto` mode can still misplace lines when near-identical hook lines repeat
  over sparse evidence, and has the version 6 failure modes listed under its
  accuracy section (voiced intros, abbreviated sheets); a low margin marks
  such lines for review. Stamps (`ref`) eliminate this class.
- The syllable-even pacing prior of `ref` splits syllables with the
  `--language` pyphen dictionary; only `en_US` is benchmarked.
- Word *end* times are heuristic (voiced-region extension capped by next word);
  starts are the reliable quantity.
- `debug_decode.py <wav16k> [start_s] [end_s]` prints what the model hears;
  use it whenever a section refuses to align.
