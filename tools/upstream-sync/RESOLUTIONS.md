# Upstream sync: resolutions

What happened to each upstream commit that needed a hand on `sync/vendor`,
so the next sync (and anyone reading `git blame`) knows why type!beat looks
the way it does. Each merge on `sync/vendor` adds its own note here.

Step numbers count vendor/osu commits after the 2026.711.0-lazer base.
Our own decisions made because of upstream are on `sync/adapt`, in ADAPT.md.

## Combined both sides

- step 5 `LocalScoreDeleteDialog.cs` (#38227): upstream's
  `LocalisableString.Interpolate` + type!beat's `GetDisplayRank()`.
