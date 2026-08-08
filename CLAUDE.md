# CLAUDE.md

Guidance for anyone (human or agent) working inside `typebeat-osu`, the **type!beat game client**:
a C#/.NET 8 fork of ppy/osu-framework. `TypeBeat` namespace, `type!beat` product name.

`DESIGN.md` at the repo root is the canonical spec for the timing schema and judgement policy.

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
| `typebeat.Game.Rulesets.TypeBeat/Gameplay/InstrumentalGaps.cs` | `src/Typebeat.Web/Packages/Lyrics/InstrumentalGaps.cs` |
| the `TypingEngine` / score processor | `src/Typebeat.Web/wwwroot/js/typebeat-core.js` (hand-written JS reimplementation) |

`PerformancePoints.VERSION` is **shared** between the two copies and stamps `scores.pp_version`;
bump it in both the moment a change values any stored row differently.

`docs/pp.md` in `typebeat-web` is the canonical pp spec: every constant in `PerformancePoints.cs` is
pinned there and must not drift from it.

There is a tool for pp changes: `tools/pp.py` in the parent superrepo (`show` / `check` / `set`),
which propagates a constant across both mirrors, the docs and the test expectations. Prefer it over
hand-editing, and if it cannot express your change, say so rather than working around it.
