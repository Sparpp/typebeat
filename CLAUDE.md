# CLAUDE.md

Guidance for anyone (human or agent) working inside `typebeat-osu`, the **type!beat game client**:
a C#/.NET 8 fork of ppy/osu-framework. `TypeBeat` namespace, `type!beat` product name.

There is **no `DESIGN.md`** in this repo, despite what older notes say. Two separate agents have gone
looking for it. The canonical sources are the code itself:

- **Judgement policy and the window ladder**: `Gameplay/Judgement.cs`, which says in its own header
  that it is the single tuning point. A keypress is graded on MILLISECONDS, in three tiers
  (250/400, 600/1000, 1200/2000 at Line granularity). WHICH milliseconds is decided by
  `TypingEngine.judgedDeltaFor`: since backlog 179 a cell inside a syllable group is graded on
  distance from that syllable's SUNG SPAN (0 anywhere inside it), and only a cell in no group (a
  space, a stylised token) is graded on distance from its own point target. The point rule survives
  as the ERA a stored replay re-derives under, selected by its CONFIG frame's flags bit 2, and since
  backlog 180 as the LIVE rule under Hard Rock, which reverts to point targets because span
  judgement undercuts its halved windows (so an HR replay records bit 2 clear). Backlog 133
  replaced that with a four-tier character-distance ladder and backlog 147 reverted the whole arc, so
  a note dated between the two describing a character axis, a `SyncMeasure`, a fourth `Perfect` tier
  or a Rhythmic mod is describing code that no longer exists. What backlog 133 left behind on
  purpose: `JudgementType`'s tiers are named `Great`/`Ok`/`Meh` for the osu results they map to, so
  `TypeBeatResultMapping.CellResult` is the identity and "Perfect" no longer means two things.
  A second, orthogonal axis is the INPUT MODEL: which cells a wrong key lands in rather than what a
  landed key is worth. `AllowWrongInput` (off only under Gatekeeper) types a wrong letter through on
  a lyric cell, and since backlog 181 `WrongInputOnWordGaps` extends that to the SPACE cell, where a
  typo now takes the gap, renders as the typed char in error red (`LyricLineDisplay.CellGlyph`) and
  is backspaceable. That is a second ERA, CONFIG flags bit 3, because a stored replay's rejected
  gap keystroke must stay rejected or its caret desynchronises; unlike bit 2 it is set for EVERY
  live stack, Hard Rock included, since HR halves windows and not the input model. Backlog 184 adds
  `StrictSpaces` (a third ERA, CONFIG flags bit 4, set for every live stack, not a user setting),
  which makes the spacebar the WORD BOUNDARY: with `SpaceSkipsWord` ON a gap typo PARKS the caret on
  the gap (space steps over it, backspace clears it in place) instead of carrying the caret into the
  next word where the follow-up space fed a spoiled gap to the skip gate and gave up a whole word;
  with `SpaceSkipsWord` OFF the space KEY is typed through on a lyric cell as an ordinary typo
  instead of being rejected. The space key is still refused by a FREESTYLE slot under every arm.
  Backlog 209 adds `CharTimedStretch` (a fourth ERA, CONFIG flags bit 6, set for every live stack),
  which NARROWS the span rule: a STRETCH cell, a freestyle slot or a cell of a run of three or more
  identical characters inside one syllable (`TypingLine.IsCharTimedStretch`), reverts to its own
  character target, because those cells are interchangeable to the matcher and a span paid a whole
  mashed run delta 0 seconds ahead of the vocal (a field report had accuracy going UP for spamming
  a freestyle section). Everything else keeps the span rule, and the threshold of three is
  `Syllabifier.IsSyllabifiable`'s own, so a doubled letter ("goo") is untouched.
  A third axis, orthogonal to both, is WHICH LINE THE CARET IS ON. Backlog 208 made the unpinned
  caret (`FletcherEnabled`: finish a line and you are on the next one, a line the song has left is
  not snatched, a character-distance rush cap replaces the timing lock) the DEFAULT for every play
  and REVERSED the Fletcher mod, which now pins the caret back and takes acronym `FC` at 1.02x. It
  ships one behaviour the old mod never had, `FlexibleLineSnap`: a caret sitting PAST the last
  character of its line is handed to the next line the moment that line starts, so a player who has
  FINISHED is still carried along by the song (an UNFINISHED line is never taken, which is the point
  of the freedom). That pair is CONFIG flags bit 5, and the bit is why the flags word cannot be read
  as a single fact: bit 5 clear means PINNED for a plain old replay but unpinned-without-the-snap for
  one carrying the retired `FT` acronym, so `ReplayEngineFeed.Apply` takes the snap from the bit
  outright and the caret from `bit 5 || TypingEngine.FlexibleCaretFromMod` (set by the two engine
  factories off `TypeBeatModLegacyFletcher`). `FT` is a `ModType.System` mod: unselectable, but still
  resolvable, so its stored rows keep their 0.98x multiplier and 0.90x pp. Bits 5 and 6 landed
  concurrently, which is why the `CreateConfigFrame` parameter order does not track bit order (pass
  the newer three by name).
  Backlog 218 adds `BoundedRush` (a fifth ERA, CONFIG flags bit 7, set for every live stack), which
  makes that caret SYMMETRIC: 208 left rushing time-unbounded while dragging was never, so a finished
  line handed the caret on however many seconds before the next line's cue and, the roll being
  transitive, a fast player could type the whole map at the top of the song. Entry into a line now
  opens exactly `FLETCHER_DRAG_GRACE_MS` (1500) before its `ActivationTime`, the mirror of the 1500 a
  dragging player borrows past `EndTime + SealGraceMs`, so the one constant bounds both directions
  (`entryPermitted` against `sealPermitted`). A refused roll parks the caret past the last cell of its
  line, where keypresses are inert and the WPM clock is stopped, and `snapForwardOnLineStart` performs
  the deferred roll when the bound opens (it is the snap arm, so the snap moved to the same instant).
  The SEAL's hand-overs are never refused: there the song has left the line, so entry is late and not
  early. Backlog 222 corrects what 218 wrote about the clock: the OTHER pre-cue state, a caret rolled
  or snapped ON TO the next line before its cue, is typeable (`ProcessKey` has no time gate), so the
  WPM clock ARMS LAZILY there, on the first press made on that line and from that press's own time.
  Not at `entryOpensAt`, which would pay an idle player for the head start, and nothing is
  back-dated. This is not an era bit: WPM feeds no judgement, so a stored replay re-derives the
  corrected figure. `clockRunsFrom` is the predicate and `typebeat-core.js` mirrors it.
  Backlog 259 adds `BackDatedSealBreak` (a sixth ERA on this file's list, CONFIG flags bit 10 = 1024,
  set for every live stack), which is about WHEN a break lands rather than what a press is worth. A
  line's misses only exist at its SEAL, which under the unpinned caret can be 1500 ms after the song
  left the line and long after the player moved on, so the one break it takes was wiping a run built
  on the NEXT line. It is now back-dated to the line's LAST unforeseen missed cell in (line, cell)
  order: increments earned at or before it die, increments earned strictly past it survive, and
  `TypingEngine.runPositions` (one entry per unit of combo, cleared by every break, restored with a
  redeemed claim's own positions) is the ledger that can answer which is which. The processor half
  mirrors backlog 167's word skip exactly: every seal miss is applied COMBO-NEUTRAL and the one break
  is written by hand first, from `LineSealResult.SurvivingCombo`, at `TypeBeatPlayfield.onLineSealed`
  and its hand-mirror in `TypeBeatReplayScorer`. HP is untouched (same misses, same drain, same
  instant) and neither `MaxCombo` nor an already-judged hit's `ComboAtJudgement` is revisited.
  Backlog 260 adds `LosslessSkipReclaim` (a seventh ERA, CONFIG flags bit 11 = 2048, set for every
  live stack), one law in two places: an accidentally skipped word, typed out in full, costs the run
  NOTHING. A report of 920 cells, 0 misses and a max combo of 919 was the WORD GAP the skipping space
  is judged on, dropped two ways. (1) `skipCurrentWord` moves the caret past the whole word BEFORE
  the same press is judged on the gap, and `rushesPastCap` measures the caret POSITIONALLY, so the
  abandoned tail was charged to the player's rush budget; over the cap the gap earned no combo, and
  SILENTLY, the skip's own break having already zeroed the run. That press is now measured against
  the caret as it stood before the skip (`caretBeforeSkip` in `ProcessKey`), which is what makes
  `rushesPastCap`'s own "a space spends no budget" true; an ordinary press is untouched. (2) The
  passive-claim arm of `snapshotRedeemableBreak` (backlog 243) kept the held claim and dropped its
  own `brokenStreak`/`brokenPositions`, but the call site had already run `breakRun`, so those
  increments died with nothing to redeem them. A passive break now FOLDS its spent run into the claim
  (streak added, positions appended in run order, `positions.Count == streak` preserved), so a full
  correction restores the lot. A THIRD defect from the same report is input-layer only and carries no
  era: `RetypeSelectionAnchor` anchored a wholly abandoned word on its head, which the mass backspace
  cannot stop on (the transparent step-over walks the whole phantom run and erases the gap in FRONT
  of the word), so the Ctrl+A collapse ended up BEHIND its own anchor and the first retyped letter
  landed on that judged gap as a manufactured typo. The selection is widened one step to that gap.
  Widened rather than bounding the backspace, deliberately: playback feeds a recorded `BACKSPACE`
  through the plain `ProcessBackspace`, so a live erase that stopped short would not reproduce.
- **The timing schema** (per-character target times, syllable subdivision, space cells):
  `Gameplay/TypingLine.cs`, `FromLyricLine`.
- **How a judgement becomes a stored osu result**: `Scoring/TypeBeatResultMapping.cs`, which also
  documents why the candidate result set is forced rather than chosen.
- **pp**: `typebeat-web/docs/pp.md` is canonical and every constant here is pinned there.

Most work here is done by an agent spawned into a **git worktree** under `.claude/worktrees/<slug>`
by an orchestrator running in the parent superrepo. The rules below are the ones that get re-typed
into every task brief otherwise, so they live here instead.

## Environment landmine, read this first

**`dotnet` on PATH is a broken Microsoft Store stub** ("No .NET SDKs were found"). Always invoke the
real one:

- PowerShell: `& 'C:\Program Files\dotnet\dotnet.exe' …`
- Bash: `"/c/Program Files/dotnet/dotnet.exe" …`

The .NET 10 SDK builds this repo's `net8.0` target; the 8.0 runtime is present.

## Build and test

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build typebeat.Desktop.slnf -v quiet --nologo

$env:TYPEBEAT_MAPS_DIR     = 'C:\Users\Mingda\Documents\type!beat\maps'
$env:TYPEBEAT_GAP_OSU_DIR  = 'C:\Users\Mingda\Documents\type!beat\gap-fixtures'
& 'C:\Program Files\dotnet\dotnet.exe' test typebeat.Game.Rulesets.TypeBeat.Tests -v minimal
```

Both env vars are required: without them a chunk of the suite silently **skips** rather than fails,
so a "green" run with a much lower test count is not green.

Run: `typebeat.Desktop\bin\Debug\net8.0\type!beat.exe` (AssemblyName is `type!beat`). **Agents must
not run the client.**

### Build warnings

There are **exactly 3 pre-existing warnings**, and a 4th means you introduced a defect:

- `OLOC002` and `OLOC003` in `typebeat.Game/Localisation/FirstRunSetupOverlayStrings.cs`
- `CS0419` in `typebeat.Game.Rulesets.TypeBeat/Edit/TapScope.cs`

Incremental builds hide warnings from unchanged projects, so **confirm the count with
`-t:Rebuild`**. It costs nothing here (measured: cold build 12s, `-t:Rebuild` 12s, warm 1.5s).

## Gate protocol: filter while iterating, full suite once

The full suite is ~22s and the build ~12s, and a non-vacuity protocol that re-runs everything for
each deliberate break burns that many times over for no extra signal.

- **While iterating, and for every break/revert cycle**, run only what your change can affect:
  `& 'C:\Program Files\dotnet\dotnet.exe' test typebeat.Game.Rulesets.TypeBeat.Tests --filter "FullyQualifiedName~<Area>" -v minimal`
- **Run the full suite and a `-t:Rebuild` warning check once, at the end**, and report both.

The orchestrator re-runs the full gate in the main checkout after merging, so your final run is a
check, not the last line of defence. Do not run the full suite a dozen times to prove one thing.

## Rules for agents working here

- **Never `git push`.** Commit on your own worktree branch. Nothing here is ever pushed by an agent;
  the user pushes.
- **Never add `Co-Authored-By`, `Claude-Session`, or any AI-attribution trailer** to a commit
  message, and do not mention AI or agents anywhere in the repo.
- **Never use em dashes** anywhere: code, comments, XMLDoc, markdown, commit messages. Use commas,
  colons or parentheses.
- **Do not run the game client**, and never touch `%APPDATA%\typebeat-development` (the dev data
  dir), `%APPDATA%\osu` (the user's real osu!lazer install) or `%APPDATA%\TypeBeat`.
- Do not edit anything under `.claude/worktrees/` belonging to another branch.

## Landmines that are not obvious from the code

- **The osu to typebeat rename deliberately kept** `osu.Framework` / `osuTK` (external packages) and
  the `Osu*` type identifiers (`OsuGame`, `OsuColour`, …). Do not rename those.
- **Ruleset discovery depends on the dll NAME matching a string literal**: the file must be
  `typebeat.Game.Rulesets.TypeBeat.dll` and the literal is `@"typebeat.Game.Rulesets"`. String
  literals matter here, not just namespaces.
- **`TypeBeatRuleset` must implement `ILegacyRuleset` with `LegacyID = 0`**, or `OnlineID` becomes
  `-1` and score submission plus leaderboards silently no-op.
- **The game's profile fetch has no `Failure` handler**, so any 404ing profile API call spins
  forever. Implement the endpoint before wiring new profile UI.
- `APIRequest.Cancel()` calls `Fail(new OperationCanceledException(...))` and runs the same
  `TriggerFailure` path a 404 does, so response handlers must gate on **request identity**, not on
  exception type.

## Code mirrored in the server repo, which must not drift

These exist twice, here and in `typebeat-web`, and a divergence corrupts shared leaderboards or lies
to the player. `typebeat-web/tests/Typebeat.WireCompat` compiles both repos and pins them.

| here | there |
|---|---|
| `typebeat.Game.Rulesets.TypeBeat/Scoring/PerformancePoints.cs` | `src/Typebeat.Web/Scoring/PerformancePoints.cs` |
| `typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricDifficulty.cs` | `src/Typebeat.Web/Packages/Lyrics/LyricDifficulty.cs` |
| `typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricWpmCurve.cs` | `src/Typebeat.Web/Packages/Lyrics/LyricWpmCurve.cs` |
| `typebeat.Game.Rulesets.TypeBeat/Gameplay/InstrumentalGaps.cs` | `src/Typebeat.Web/Packages/Lyrics/InstrumentalGaps.cs` |
| the `TypingEngine` / score processor | `src/Typebeat.Web/wwwroot/js/typebeat-core.js` (hand-written JS reimplementation) |

`PerformancePoints.VERSION` is **shared** between the two copies and stamps `scores.pp_version`;
bump it in both the moment a change values any stored row differently.

`LyricWpmCurve` (peak WPM, peak CPM and the downsampled WPM curve) is computed locally here for song
select and STORED on the server's beatmap row for the set page's WPM tab, deliberately **not** put on
the wire, so the mirror is the only thing keeping the in-game and on-site figures equal. A change
here also needs the server's `LyricPace.VERSION` bumped so its stored columns recompute.

`docs/pp.md` in `typebeat-web` is the canonical pp spec: every constant in `PerformancePoints.cs` is
pinned there and must not drift from it.

There is a tool for pp changes: `tools/pp.py` in the parent superrepo (`show` / `check` / `set`),
which propagates a constant across both mirrors, the docs and the test expectations. Prefer it over
hand-editing, and if it cannot express your change, say so rather than working around it.
