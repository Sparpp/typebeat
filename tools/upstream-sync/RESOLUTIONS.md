# Upstream sync: resolutions

What happened to each upstream commit that needed a hand on `sync/vendor`,
so the next sync (and anyone reading `git blame`) knows why type!beat looks
the way it does. Each merge on `sync/vendor` adds its own note here.

Step numbers count vendor/osu commits after the 2026.711.0-lazer base.
Our own decisions made because of upstream are on `sync/adapt`, in ADAPT.md.

## Open decisions (kept type!beat's behaviour for now)

- **step 60, pp on non-best profile scores (#38516).** type!beat still shows a
  dash. This only affects the profile's Recent plays list (Best, Pinned and
  Firsts only hold best plays). The type!beat website also shows best plays
  only, as of 2026-10-05. Port upstream's "show it with a non-best tooltip"
  once the server sends pp for non-best plays, without the "processing"
  spinner: type!beat's server prices a play when it's submitted, so a missing
  pp never arrives later.
- **step 82, online asset disk cache (#38454).** Avatars, profile covers and
  team flags still load uncached. The new cache keys on the URL and keeps an
  image as long as it's viewed at least once a month. That is only safe if
  the type!beat server gives an asset a new URL whenever it changes (a content
  hash or an update time in the URL). If avatars stay at a fixed URL, players
  would never see an avatar change. Once confirmed, switching
  `DrawableAvatar`, `DrawableTeamFlag` and `UserCoverBackground` over is one
  line each.
  Upstream also switched the cache itself off in step 96 (#38668), so for
  now it only downloads either way.
- **step 87, "Synchronise" bookmarks/preview point across difficulties
  (#38381).** Not in the editor menu. It re-saves every sibling difficulty
  through upstream's generic `GetPlayableBeatmap` + `BeatmapManager.Save`
  round trip, and nobody has checked whether that keeps type!beat's
  per-difficulty data (lyrics, fonts, gain, stems). The dialog and strings
  are in; enabling it means adding the menu entry plus the method from
  upstream's commit.

## Changes type!beat had to make

- **step 50, `FormColourSwatch`** (the freestyle colour picker) passes an empty
  suggestions list to `FormColourPalette.ColourButton`, whose constructor
  upstream changed.
- **step 66, hit-error meters.** Upstream moved them into the per-ruleset HUD
  layer, which would have put them back into type!beat's HUD. Removed again
  from Argon and Triangles. Side effect: Argon's key counter now always sits
  36px in from the right edge (upstream's fixed offset).
- **step 73, dropped an orphan file.** `PlacementStateManager.cs` is an
  upstream refactor of hit-object editor code that type!beat deleted.
- **step 79, dropped an orphan file.** `HitObjectComposer.SelectionState.cs`
  is a partial of `HitObjectComposer`, which type!beat deleted.
- **step 81, `Ruleset.AllVariants`** doesn't append upstream's new
  `EDITOR_VARIANT`. `TypeBeatRuleset` ignores the variant in
  `GetDefaultKeyBindings`. Without this, every player would get a duplicate
  set of gameplay bindings stored as "editor" bindings, plus a "Beatmap
  editor" key-binding section for an editor that never reads it.
- **step 82, schema renumbered.** Upstream's Realm version 52 ("Add
  RealmOnlineAsset") is type!beat's **65**, because type!beat had already used
  52-64. No migration body is needed (it's a new table).
- **step 82, `OnlineAssetCachingStore`** builds its `TrustedDomainOnlineStore`
  with type!beat's endpoints and wraps it in `LoopbackAwareStore`, like the
  game's other online stores.
- **step 90, settings layout (#38640).** Adopted upstream's new layout
  (decided 2026-10-05), keeping type!beat's own edits:
  - Main settings: the "type!beat" section becomes upstream's per-ruleset
    section (smaller sidebar button, type!beat's icon, still titled
    "type!beat"). Skin, Online and Debug stay out; Experimental stays in;
    the April Fools toggle still goes last.
  - Key bindings: upstream's split into Global / Gameplay / Song select /
    Editor, plus type!beat's own section. The Editor section keeps type!beat's
    6-action list and leaves out upstream's per-ruleset editor bindings
    (type!beat has none, see step 81).
  - First-run setup keeps the ruleset's settings (upstream dropped them there).
  - `TypeBeatSettingsSubsection` and `TypeBeatExperimentalSettingsSubsection`
    lose their blank-header overrides: upstream's base class now does the same
    thing and doesn't allow overriding it.
- **step 95, rebindable editor playback keys (#38666).** Upstream moved the
  editor's Space / arrows / Z X C V keys from hard-coded handling into
  rebindable global actions. type!beat's key binding settings keep their
  trimmed editor list, so the new actions work on their default keys but
  aren't listed for rebinding. Build fix: `PlaybackControl.cs` keeps
  `using osuTK.Input;`, which type!beat's speed box still needs.
  Checked: Space still taps in tap timing, because that overlay holds focus
  and focus beats global actions in osu-framework's input order.

- **step 103, .NET 10 (#38039).** Upstream moved `typebeat.Game` and
  `typebeat.Desktop` to net10.0. The same merge also moves what upstream's
  commit can't see:
  - `typebeat.Game.Rulesets.TypeBeat` and its tests to net10.0 (a net8.0
    project can't reference a net10.0 one);
  - both workflows (`build-linux.yml`, `build-macos.yml`) to .NET 10.0.x;
  - `VPK_VERSION` to 1.2.0, because upstream bumped the Velopack library from
    0.0.1298 to 1.2.0 and the workflow says the two must match.
  `global.json` takes upstream's 10.0.100 but keeps type!beat's
  `latestMajor` roll-forward.
  Check on the first release: an installed 0.0.1298 client updates to a
  build packed with vpk 1.2.0. osu! made the same jump, so it should.

## Combined both sides

- step 5 `LocalScoreDeleteDialog.cs` (#38227): upstream's
  `LocalisableString.Interpolate` + type!beat's `GetDisplayRank()`.
- step 26 `ResourceManagerLocalisationStore.cs` (#38360): upstream's string
  cache (file now nullable-enabled) + type!beat's `Rebrand`, now typed `string?`.
- step 27 `SubmittingPlayer.cs` (#38230): type!beat's "no spectator server,
  mark the score processed now" path + upstream's new `token` argument.
  If type!beat ever hosts a spectator server, it has to speak the new
  osu-server-spectator API (ppy/osu-server-spectator#523).
- step 31 `OsuGame.cs` (#38378): re-applied upstream's login overlay change
  to type!beat's overlay layout. The overlays type!beat removed stay out.
- step 50 `ResourcesSection.cs` (#38391): upstream's new `BackgroundChanged`
  event fires from type!beat's `refreshBackgroundDisplays()`. The combo colour
  suggestions it feeds live in `ColoursSection`, which type!beat's editor
  doesn't show.
- step 70 `FilterControl.cs` (#38549): type!beat's saved-sort handling (BPM
  falls back to Title) + upstream's remembered collection filter.
- step 75 `OsuGame.cs` (#38604): same as step 31, for the now-playing
  overlay.
- step 77 `OsuTextBox.cs` (#38630): upstream's overridable `Font` is the
  default inside type!beat's `getTextFont()`; a chosen font family still wins.
- step 81 `EditorStrings.cs` (#38608): both new strings. The ruleset API
  bump to 2026.818.0 is fine: `TypeBeatRuleset` reports the current constant.
- step 93 `OnlineAssetCachingStore.cs`, `OsuGameBase.cs` (#38656, dispose the
  cache): upstream's disposal fix and lookup rewrite, plus type!beat's
  endpoints argument.
- step 96 `OnlineAssetCachingStore.cs` (#38668, cache switched off for
  performance): took upstream's switch-off; it adds its own empty-URL check.
  type!beat's trusted-domain setup stays.
- step 115 `.vscode/launch.json` (#38815, VS Code executable path): upstream's
  net10.0 folders, type!beat's `type!beat.dll` file name.

## Kept type!beat's version

- step 15 `BeatmapManager.cs` (#38332): type!beat already fixed "new difficulty
  loses its storyboard" its own way (copies only the video, so difficulties
  don't share storyboard state).
- step 33 `LocalCachedBeatmapMetadataSource.cs` (#38379): type!beat never
  fetches osu!'s metadata cache, so upstream's test-only guard is moot.
- step 37 `UpdateSettings.cs` (#38430): type!beat has no release-stream picker.
- step 84 `SongSelect.cs` (#38639): type!beat removed beatmap-set scoping.
- step 101 `BeatmapSet/Info.cs` (#38733, user tags on first selection): type!beat
  shows no user tags there (its server has none), so upstream's fix doesn't
  apply. Its other changes (the set lookup request, song select) came in.

## Framework

- step 35: `ppy.osu.Framework` 2026.629.0 → 2026.724.0. type!beat keeps its own
  `typebeat.Game.Resources` package. On vendor/osu, upstream's resources
  package shows up as `ppy.typebeat.Game.Resources` (a side effect of the
  rename; the real one is `ppy.osu.Game.Resources`), so that line never applies.
- step 43: 2026.724.0 → 2026.728.1. Built as is.
- step 50: 2026.728.1 → 2026.731.0. Needed the `FormColourSwatch` fix above.
- step 63: 2026.731.0 → 2026.807.0. Built as is.
- step 103: 2026.807.0 → 2026.901.0, with the move to .NET 10 above.
