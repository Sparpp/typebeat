# Upstream sync: resolutions

What happened to each upstream commit that needed a hand on `sync/vendor`,
so the next sync (and anyone reading `git blame`) knows why type!beat looks
the way it does. Each merge on `sync/vendor` adds its own note here.

Step numbers count vendor/osu commits after the 2026.711.0-lazer base.
Our own decisions made because of upstream are on `sync/adapt`, in ADAPT.md.

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

## Kept type!beat's version

- step 15 `BeatmapManager.cs` (#38332): type!beat already fixed "new difficulty
  loses its storyboard" its own way (copies only the video, so difficulties
  don't share storyboard state).
