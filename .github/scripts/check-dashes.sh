#!/usr/bin/env bash
#
# Fails when a change ADDS an em dash (U+2014) or an en dash (U+2013). Used by
# .github/workflows/ci.yml on pull requests, with the pull request's base commit.
#
# The repos' written rule is no em or en dashes anywhere (code, comments, docs, commit messages):
# use a comma, a colon, parentheses, or "to" for a range. Only lines the change adds are checked,
# so the dashes upstream osu!lazer code already carries never trip it.
#
# USAGE: check-dashes.sh <base commit>
#
# FAILS CLOSED. An unknown base commit, or any git failure while diffing, fails the script rather
# than reading as "no dashes found": the diff is taken once, into a variable, so set -e sees it.
#
# PORTABILITY. The dashes are matched as their UTF-8 bytes under LC_ALL=C, so this script itself
# holds no dash and the match does not depend on the runner's locale. Runs in git bash on
# windows-latest (the CI runner) and on any GNU userland.

set -euo pipefail

BASE="${1:?usage: check-dashes.sh <base commit>}"

export LC_ALL=C

if ! git rev-parse --verify --quiet "${BASE}^{commit}" > /dev/null; then
    echo "::error::base commit ${BASE} is not in this checkout (a shallow clone?), cannot diff against it" >&2
    exit 1
fi

# The added lines of the whole change, taken once so a git failure stops the script.
diff_text=$(git diff --unified=0 --no-color --diff-filter=ACMR "${BASE}...HEAD")

# Walks the diff keeping the current file and new-side line number from the "+++ b/<path>" and
# "@@ -a,b +N,M @@" headers, prints "path<TAB>line<TAB>text" for every added line, and keeps the
# ones with a dash. Binary files have no hunks, so they print nothing. The byte match is left to
# grep -P rather than awk, whose handling of byte escapes differs between implementations.
# `|| true` because grep exits 1 when nothing matches; the diff itself was already checked above.
hits=$(printf '%s\n' "${diff_text}" | awk '
    /^diff --git / { inhunk = 0; next }
    /^\+\+\+ / && !inhunk { file = ($0 == "+++ /dev/null") ? "" : substr($0, 7); next }
    /^@@ /     { split($3, p, ","); line = substr(p[1], 2) + 0; inhunk = 1; next }
    !inhunk    { next }
    /^\+/      { printf "%s\t%d\t%s\n", file, line, substr($0, 2); line++ }
' | grep -P '^[^\t]*\t[^\t]*\t.*\xe2\x80[\x93\x94]' || true)

if [ -z "${hits}" ]; then
    exit 0
fi

while IFS=$'\t' read -r file line text; do
    echo "::error file=${file},line=${line}::added line with an em or en dash, use a comma, colon, parentheses or \"to\""
    echo "${file}:${line}: ${text}"
done <<< "${hits}"

exit 1
