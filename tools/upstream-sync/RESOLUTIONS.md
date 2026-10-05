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

## Kept type!beat's version

- step 15 `BeatmapManager.cs` (#38332): type!beat already fixed "new difficulty
  loses its storyboard" its own way (copies only the video, so difficulties
  don't share storyboard state).
- step 33 `LocalCachedBeatmapMetadataSource.cs` (#38379): type!beat never
  fetches osu!'s metadata cache, so upstream's test-only guard is moot.
- step 37 `UpdateSettings.cs` (#38430): type!beat has no release-stream picker.
- step 84 `SongSelect.cs` (#38639): type!beat removed beatmap-set scoping.

## Framework

- step 35: `ppy.osu.Framework` 2026.629.0 → 2026.724.0. type!beat keeps its own
  `typebeat.Game.Resources` package. On vendor/osu, upstream's resources
  package shows up as `ppy.typebeat.Game.Resources` (a side effect of the
  rename; the real one is `ppy.osu.Game.Resources`), so that line never applies.
- step 43: 2026.724.0 → 2026.728.1. Built as is.
- step 50: 2026.728.1 → 2026.731.0. Needed the `FormColourSwatch` fix above.
- step 63: 2026.731.0 → 2026.807.0. Built as is.
