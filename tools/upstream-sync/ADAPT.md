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
