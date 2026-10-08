#!/bin/bash
# Merge vendor/osu into the current branch one upstream commit at a time,
# building after each. Stops at the first conflict it can't resolve on its
# own, or the first build failure, so a person can look.
#
# Usage, from the root of a worktree on the sync branch:
#   tools/upstream-sync/merge_steps.sh <branch> [vendor tip, default vendor/osu]
# After fixing a conflict, `git add` the files and run it again: it commits
# the in-progress merge, builds it and carries on.
#
# What it resolves by itself:
# - upstream edited a file type!beat deleted: stays deleted
# - .csproj conflicts that are only PackageReference versions: upstream's versions win
# - anything rerere has seen before (rerere is on for every merge here)
#
# Every merge also rewrites tools/upstream-sync/UPSTREAM, so the version merged so far is
# recorded in each commit.
BRANCH=$1
TIP=${2:-vendor/osu}
HERE=$(cd "$(dirname "$0")" && pwd)
LOG=$(git rev-parse --git-dir)/upstream-sync.log
[ -n "$BRANCH" ] || { echo "usage: $0 <branch> [vendor tip]"; exit 1; }

G=(git -c user.name=lauriys -c user.email=laura@iys.io -c gpg.format=ssh
   -c user.signingkey=$HOME/.ssh/typebeat_signing -c commit.gpgsign=true
   -c rerere.enabled=true -c rerere.autoupdate=true)

guard() { [ "$(git branch --show-current)" = "$BRANCH" ] || { echo "wrong branch: $(git branch --show-current)" | tee -a "$LOG"; exit 1; }; }

build() {
  if ! out=$(dotnet build typebeat.Desktop/typebeat.Desktop.csproj -v q -nologo 2>&1); then
    echo "$out" | grep -E ' error ' | sort -u | tee -a "$LOG"
    echo "  BUILD FAILED. Fix it, then: git add -u && git commit --amend --no-edit (signed), and rerun." | tee -a "$LOG"
    exit 4
  fi
  echo "  ok" | tee -a "$LOG"
}

# Record the merged upstream version and commit the merge in progress.
# $1: the vendor/osu commit being merged; the rest: files resolved by hand.
commit_merge() {
  local c=$1; shift
  python3 "$HERE/upstream_info.py" file "$c" > tools/upstream-sync/UPSTREAM && git add tools/upstream-sync/UPSTREAM || exit 3
  python3 "$HERE/upstream_info.py" message "$c" "$@" | "${G[@]}" commit -q -F - || exit 3
}

guard
if [ -f "$(git rev-parse --git-dir)/MERGE_HEAD" ]; then
  [ -z "$(git diff --name-only --diff-filter=U)" ] || { echo "still conflicted:"; git diff --name-only --diff-filter=U; exit 2; }
  commit_merge "$(git rev-parse MERGE_HEAD)" $(cat "$(git rev-parse --git-dir)/upstream-sync-conflicts" 2>/dev/null)
  rm -f "$(git rev-parse --git-dir)/upstream-sync-conflicts"
  echo "  resolved by hand" | tee -a "$LOG"
  build
fi

for c in $(git rev-list --reverse HEAD.."$TIP"); do
  guard
  subj=$(git log -1 --format=%s "$c")
  echo "merge ${c:0:8} $subj" | tee -a "$LOG"
  if ! "${G[@]}" merge -q --no-ff --no-commit "$c" >/dev/null 2>&1; then
    git status --porcelain | awk '$1=="DU"{print $2}' | xargs -r git rm -q
    for f in $(git diff --name-only --diff-filter=U | grep '\.csproj$'); do
      python3 "$HERE/auto_csproj.py" "$f" && git add "$f" && echo "  package versions: upstream's" | tee -a "$LOG"
    done
    left=$(git diff --name-only --diff-filter=U)
    if [ -n "$left" ]; then
      echo $left > "$(git rev-parse --git-dir)/upstream-sync-conflicts"
      echo "  CONFLICT:" $left | tee -a "$LOG"
      exit 2
    fi
  fi
  commit_merge "$c"
  rm -f "$(git rev-parse --git-dir)/upstream-sync-conflicts"
  build
done
echo "all of $TIP merged" | tee -a "$LOG"
