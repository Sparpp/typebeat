#!/bin/bash
# Which upstream osu! version this checkout has merged, and what's still waiting.
#   tools/upstream-sync/status.sh [branch or commit, default HEAD]
REV=${1:-HEAD}
cd "$(git rev-parse --show-toplevel)" || exit 1

echo "Merged from upstream osu!:"
git show "$REV:tools/upstream-sync/UPSTREAM" | grep -v '^#' | sed '/^$/d; s/^/  /'

if git rev-parse -q --verify vendor/osu >/dev/null; then
  waiting=$(git rev-list --count "$REV"..vendor/osu)
  echo
  echo "Waiting on vendor/osu: $waiting commits"
  releases=$(git log --reverse --format='%(trailers:key=Upstream-Release,valueonly,separator=%x2C )' "$REV"..vendor/osu | sed '/^$/d' | paste -sd' ' | sed 's/ /, /g')
  [ -n "$releases" ] && echo "  releases they complete: $releases"
  next=$(git rev-list --reverse "$REV"..vendor/osu | head -1)
  [ -n "$next" ] && echo "  next: $(git log -1 --format=%s "$next")"
fi

if git rev-parse -q --verify ppy/master >/dev/null && git rev-parse -q --verify vendor/osu >/dev/null; then
  last=$(git log -1 --format=%B vendor/osu | sed -n 's/^Upstream: ppy\/osu@\([0-9a-f]\{40\}\)$/\1/p' | tail -1)
  echo
  echo "On ppy/master but not in vendor/osu yet: $(git rev-list --first-parent --count "$last"..ppy/master) commits (run build_vendor.py)"
fi
