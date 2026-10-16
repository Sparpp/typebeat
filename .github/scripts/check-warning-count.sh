#!/usr/bin/env bash
#
# Fails the build when a clean rebuild prints more compiler and analyzer warnings than the known
# baseline. Used by .github/workflows/ci.yml on the log of
#
#   dotnet build typebeat.Desktop.slnf -t:Rebuild
#
# THE BASELINE. EXPECTED_WARNINGS below is the one place the number lives. It is 26, the count the
# maintainers' build notes record for a clean -t:Rebuild ("Build warnings", which also breaks it
# down per warning code). A 27th warning means a change introduced a defect,
# so the job goes red. The number is changed deliberately and only in the same commit that clears
# or knowingly accepts a warning, with the notes updated to match. A count BELOW the baseline
# passes, but prints a notice asking for the number to be lowered, so the slack cannot quietly
# absorb a new warning later.
#
# WHAT COUNTS AS ONE WARNING. MSBuild prints every warning where it happens and again in the
# summary at the end, and a parallel build can report the same line more than once. So each
# "<origin>: warning <CODE>: <message> [<project>]" line is normalised (CR, leading whitespace and
# any "N>" node prefix removed) and counted once. The same warning raised in two different
# projects stays two warnings, which is how the per-project MSBuild summary counts it. An
# incremental build skips unchanged projects and hides their warnings, which is why the workflow
# rebuilds; feeding this script an incremental log under-counts.
#
# USAGE: check-warning-count.sh <build log>
#
# PORTABILITY. Runs in git bash on windows-latest (the CI runner) and on any GNU or BSD userland.

set -euo pipefail

EXPECTED_WARNINGS=26

LOG="${1:?usage: check-warning-count.sh <build log>}"

if [ ! -s "${LOG}" ]; then
    echo "::error::build log ${LOG} is missing or empty, nothing to count" >&2
    exit 1
fi

# One line per distinct warning. `|| true` because grep exits 1 on a clean build with no matches.
distinct=$(tr -d '\r' < "${LOG}" \
    | sed -e 's/^[[:space:]]*//' -e 's/^[0-9][0-9]*>//' \
    | grep -E ': warning [A-Za-z]+[0-9]+:' \
    | sort -u || true)

if [ -z "${distinct}" ]; then
    count=0
else
    count=$(printf '%s\n' "${distinct}" | wc -l | tr -d ' ')
fi

echo "distinct warnings: ${count} (baseline ${EXPECTED_WARNINGS})"

if [ "${count}" -gt 0 ]; then
    echo "-- per code:"
    printf '%s\n' "${distinct}" | sed -E 's/.*: warning ([A-Za-z]+[0-9]+):.*/\1/' | sort | uniq -c | sort -rn
    echo "-- every warning:"
    printf '%s\n' "${distinct}"
fi

if [ "${count}" -gt "${EXPECTED_WARNINGS}" ]; then
    echo "::error::${count} build warnings, the baseline is ${EXPECTED_WARNINGS}. Fix the new warning, or raise EXPECTED_WARNINGS in .github/scripts/check-warning-count.sh deliberately."
    exit 1
fi

if [ "${count}" -lt "${EXPECTED_WARNINGS}" ]; then
    echo "::notice::${count} build warnings, below the baseline of ${EXPECTED_WARNINGS}. Lower EXPECTED_WARNINGS in .github/scripts/check-warning-count.sh to ${count} so the slack cannot hide a new one."
fi
