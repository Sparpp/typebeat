# sync/adapt: our decisions on top of the upstream sync

`sync/vendor` merges upstream osu! while keeping type!beat as it is. This
branch sits on top of it and holds the changes we choose to make because of
upstream: adopting an upstream feature type!beat had hidden, undoing an older
type!beat choice, showing something new, or keeping our other branches
building. One commit per decision.

When `sync/vendor` moves on, rebase this branch onto it.

- **`FormColourPalette` keeps no-argument constructors** for `ColourButton`
  and `ColourPickerPopover` (2026-10-05). Upstream (step 50) made a suggestions
  list required. type!beat's own swatches (freestyle colour, timing tint) don't
  offer suggestions; with the overloads, `FormColourSwatch` goes back to
  main's version and `feature/timing-tint` builds unchanged on top.
- **Avatars, profile covers and team flags use the online asset cache**
  (settles the step 82 open decision in RESOLUTIONS.md, 2026-10-05). The
  type!beat server gives each of these a new URL when it changes, so a cached
  file never hides a newer one. type!beat has no teams yet; flags assume
  future ones get new URLs too. Upstream switched the cache itself off in
  step 96, so for now this only downloads; when upstream turns it back on,
  these get it with no further change.
- **The editor's playback keys are listed in key binding settings**
  (2026-10-05). Upstream made them rebindable in step 95 (#38666): toggle
  pause, play from start, seek back/forward, seek to start/end. They work in
  type!beat's editor, so its trimmed Editor list shows them. The two
  timing-point seeks stay hidden, like the other control-point actions.
- **The settings footer shows the merged upstream release** (2026-10-05),
  as a small gray "upstream: <release>" line under the version. It reads the
  embedded tools/upstream-sync/UPSTREAM, which every sync merge rewrites.
- **Skin is its own settings section again, like upstream** (2026-10-05).
  Upstream always had it separate; type!beat folded it into User Interface in
  faf82d3a ("Tidy up the settings overlay", 2026-08-10). This undoes that one
  part: Skin sits after General in the sidebar and in first-run setup.
- **No "take and upload screenshot" key** (2026-10-05). Upstream (step 158,
  ppy/osu#36133) added Shift+F12, which uploads a screenshot to
  `/api/v2/screenshots`. The type!beat server has no screenshot uploads, so
  it could only fail. Its default binding is left out, which also keeps it
  out of the key binding settings; F12 still saves a screenshot locally.
