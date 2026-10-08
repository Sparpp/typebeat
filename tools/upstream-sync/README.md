# Syncing with upstream osu!

type!beat started as a copy of osu! 2026.711.0-lazer with no shared git
history. These tools give git that missing history, so upstream changes can
be merged instead of copied by hand.

## How it fits together

- **`vendor/osu`**: upstream osu!, reshaped to look like type!beat.
  - `osu.Game` → `typebeat.Game`, `osu.Desktop` → `typebeat.Desktop`
  - The other rulesets, tests, mobile and tournament projects are left out
  - `osu.Game` / `osu.Desktop` are renamed inside every text file
  - One commit per upstream commit. Trailers say which upstream commit it is,
    when it landed, and which osu! releases it completes:
    `Upstream: ppy/osu@<sha>`, `Upstream-Date:`, `Upstream-Release:`
- **The link**: one "ours" merge records vendor/osu's first commit (2026.711.0-lazer)
  as an ancestor of type!beat, without changing any type!beat file.
- **After that**: merging vendor/osu is a normal git merge. It only conflicts
  where type!beat and osu! changed the same lines.

## Which osu! version are we on?

`tools/upstream-sync/UPSTREAM` says, in every commit, which upstream commit type!beat has
merged up to, its date, and the newest osu! release that is fully merged.
`merge_steps.sh` rewrites it with each merge, so it's never stale.

```
tools/upstream-sync/status.sh                 # this checkout: merged version, what's waiting
git log -p -- tools/upstream-sync/UPSTREAM    # the version history
```

## Three branches: sync/tools, sync/vendor and sync/adapt

```
main <- sync/tools <- sync/vendor <- sync/adapt
```

- **`sync/tools`**: these scripts and notes files. Nothing else.
- **`sync/vendor`**: the link merge, then one merge per upstream commit, and
  nothing else. Each merge keeps type!beat behaving as it does on `main`,
  plus whatever upstream changed in code type!beat never customised.
  Conflicts keep type!beat's behaviour; build fixes and schema renumbering are
  fine. Each merge updates UPSTREAM, and adds a note to `RESOLUTIONS.md` if it
  needed a hand.
- **`sync/adapt`**: our decisions made because of upstream: adopting an
  upstream feature type!beat had hidden, undoing an older type!beat choice,
  showing something new, or keeping our other branches building. One commit
  per decision, each recorded in `ADAPT.md`.

When a lower branch moves on, rebase the ones above it:

```
git rebase --onto sync/vendor <old sync/vendor tip> sync/adapt
```

They land in that order, and integrate.ps1 merges them in that order too.

## Doing a sync

```
git fetch https://github.com/ppy/osu master:refs/remotes/ppy/master   # big the first time: all of osu!'s history
python3 tools/upstream-sync/build_vendor.py ppy/master
tools/upstream-sync/merge_steps.sh sync/vendor
```

`merge_steps.sh` merges one upstream commit at a time and builds after each.
It stops on a real conflict or a build failure. Fix it, `git add` the files,
and run it again.

It handles these on its own:
- Upstream edited a file type!beat deleted: the file stays deleted
- Framework/package version bumps in a `.csproj`: upstream's versions win,
  type!beat's own `typebeat.Game.Resources` stays
- Anything git's rerere has seen resolved before

The log is at `.git/upstream-sync.log`.

## Things to watch every time

- **Database schema numbers.** type!beat and osu! number Realm migrations
  separately. An upstream schema bump has to be renumbered after type!beat's
  latest (see `schema_version` in `RealmAccess.cs`). Its `case` block in the
  migration switch needs renumbering too.
- **Files that only make sense with deleted code.** Upstream sometimes adds new
  files next to code type!beat removed (multiplayer, the hit-object editor).
  These fail the build. If nothing in type!beat uses them, delete them.
- **Never rebuild vendor/osu from scratch.** `build_vendor.py` only extends it.
  A rebuild would give every commit a new hash and lose the shared history.

`RESOLUTIONS.md` lists every conflict resolved so far, and the open decisions.
