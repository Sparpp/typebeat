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
# PORTABILITY. The dashes are matched as their UTF-8 bytes under LC_ALL=C, so this script itself
# holds no dash and the match does not depend on the runner's locale. Runs in git bash on
# windows-latest (the CI runner) and on any GNU userland.

set -euo pipefail

BASE="${1:?usage: check-dashes.sh <base commit>}"

export LC_ALL=C

status=0

# `|| true` because grep exits 1 for a file with no matches. Binary files have no added text lines.
while IFS= read -r file; do
    hits=$(git diff --unified=0 --no-color "${BASE}...HEAD" -- "${file}" \
        | grep -nP '^\+(?!\+\+ ).*\xe2\x80[\x93\x94]' || true)

    if [ -n "${hits}" ]; then
        echo "::error file=${file}::added line(s) with an em or en dash, use a comma, colon, parentheses or \"to\""
        echo "${hits}" | sed "s|^|${file}: |"
        status=1
    fi
done < <(git diff --name-only --diff-filter=ACMR "${BASE}...HEAD")

exit "${status}"
