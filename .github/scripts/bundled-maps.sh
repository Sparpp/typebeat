#!/usr/bin/env bash
#
# Populates and verifies typebeat.Desktop/Bundled/, the fresh-install beatmap payload, against
# the committed manifest at typebeat.Desktop/bundled-maps.manifest.
#
# THE BUG THIS EXISTS TO KILL. Bundled/ is gitignored (real, non-redistributable audio), so a
# fresh actions/checkout has no such directory, the csproj glob Content Include="Bundled/*.typb"
# matches nothing, and the publish carries no maps. Nothing failed: OsuGame.importBundledBeatmaps
# returns quietly when the directory is absent, and it only ever runs while first-run setup is
# pending, so a Linux or macOS player got exactly one shot at the bundle and it silently did
# nothing. Every failure path below is therefore fatal and names its fix. A warning here would
# reproduce the defect.
#
# TRANSPORT. Plain HTTPS off the web app's existing anonymous /releases/{file} route, which
# serves flat file names out of /data/downloads/releases/ in the app container. Packages are
# named for their own hash, bundled-<sha256>.typb, so the URL and the checksum are the same fact
# and cannot drift; the route already sends an immutable-ish 24h cache for non-manifest names.
# No SSH, no secret, no server code: a client build does not need the box's deploy key just to
# assemble its own payload, and the expensive macOS runner does not set one up.
#
# MODES
#   fetch      (default) download every manifest entry into Bundled/, verifying as it goes
#   verify     check the existing Bundled/ against the manifest, downloading nothing
#   count      print the number of manifest entries (for a post-publish assertion)
#   manifest   regenerate the manifest from the current Bundled/ and print it to stdout
#
# ADDING OR REPLACING A PACKAGE. Put the .typb in Bundled/, upload it to the box under its
# content-addressed name, then regenerate the manifest and commit it. The upload, from the dev
# box, is the ship-client.ps1 pattern (scp to the host, docker cp + atomic mv into the volume):
#
#   f='typebeat.Desktop/Bundled/<name>.typb'
#   h=$(sha256sum "$f" | cut -d' ' -f1)
#   scp -i ~/.ssh/typebeat_hetzner "$f" "root@46.62.142.5:/tmp/bundled-$h.typb"
#   ssh -i ~/.ssh/typebeat_hetzner root@46.62.142.5 \
#     "docker exec typebeat-web-app-1 mkdir -p /data/downloads/releases \
#      && docker cp /tmp/bundled-$h.typb typebeat-web-app-1:/data/downloads/releases/.incoming-bundled-$h.typb \
#      && docker exec typebeat-web-app-1 mv /data/downloads/releases/.incoming-bundled-$h.typb /data/downloads/releases/bundled-$h.typb \
#      && rm -f /tmp/bundled-$h.typb"
#
# Upload BEFORE the manifest reaches a branch CI builds from, or the next build fails.
#
# PORTABILITY. Runs on ubuntu-latest, on macos-latest (bash 3.2, BSD userland: no sha256sum, no
# GNU stat, no mapfile/readarray/associative arrays) and in git bash on the Windows dev box.

set -euo pipefail

MODE="${1:-fetch}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
MANIFEST="${REPO_ROOT}/typebeat.Desktop/bundled-maps.manifest"
BUNDLED_DIR="${REPO_ROOT}/typebeat.Desktop/Bundled"
BASE_URL="${BUNDLED_MAPS_BASE_URL:-https://typebeat.mingda.sh/releases}"

# ::error:: is a GitHub Actions annotation and plain noise anywhere else, which is the right
# behaviour in both places. Everything diagnostic goes to stderr so `count` and `manifest` stay
# pipeable.
die() {
    echo "::error::$1" >&2
    shift
    for line in "$@"; do echo "    $line" >&2; done
    exit 1
}

# macOS has shasum but not sha256sum; Linux has both, but coreutils' sha256sum is the faster
# path where it exists.
sha256_of() {
    if command -v sha256sum > /dev/null 2>&1; then
        sha256sum "$1" | cut -d' ' -f1
    else
        shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

# `wc -c <file` rather than stat: GNU wants -c%s, BSD wants -f%z, and wc is POSIX on both.
# Redirect rather than pass the name so wc prints the count alone.
size_of() {
    wc -c < "$1" | tr -d '[:space:]'
}

# Feed a manifest line to `read -r sha size name` and `name` is the remainder of the line, spaces
# and all, which is what the entries need. A comment line lands sha='#' and a blank line lands
# sha='', so both filter out on column 1 alone.
require_manifest() {
    [ -f "$MANIFEST" ] || die "bundled beatmap manifest not found at ${MANIFEST}" \
        "This file is committed; a checkout without it is broken, not a configuration problem."
}

# Verify one already-present file. Prints nothing on success; returns 1 with a reason on stderr
# otherwise, so callers can decide between "fetch it" and "abort".
check_file() {
    _path="$1"; _sha="$2"; _size="$3"

    [ -f "$_path" ] || { echo "missing" >&2; return 1; }

    _actual_size="$(size_of "$_path")"
    if [ "$_actual_size" != "$_size" ]; then
        echo "size ${_actual_size}, manifest says ${_size}" >&2
        return 1
    fi

    _actual_sha="$(sha256_of "$_path")"
    if [ "$_actual_sha" != "$_sha" ]; then
        echo "sha256 ${_actual_sha}, manifest says ${_sha}" >&2
        return 1
    fi

    return 0
}

do_count() {
    require_manifest
    _n=0
    while read -r sha size name || [ -n "${sha:-}" ]; do
        case "$sha" in ''|'#'*|$'\r') continue;; esac
        _n=$((_n + 1))
    done < "$MANIFEST"
    echo "$_n"
}

do_manifest() {
    [ -d "$BUNDLED_DIR" ] || die "no ${BUNDLED_DIR} to regenerate a manifest from."
    _n=0
    for f in "$BUNDLED_DIR"/*.typb; do
        [ -e "$f" ] || continue
        printf '%s  %s  %s\n' "$(sha256_of "$f")" "$(size_of "$f")" "$(basename "$f")"
        _n=$((_n + 1))
    done
    [ "$_n" -gt 0 ] || die "no .typb packages in ${BUNDLED_DIR}; refusing to emit an empty manifest." \
        "An empty manifest would make every future build ship an empty bundle without complaint."
    echo "regenerated ${_n} entries; paste them under the header of ${MANIFEST}" >&2
}

# fetch and verify share one loop: the only difference is whether a bad or absent file is
# repairable by downloading it. Keeping them together is what guarantees CI and the dev box
# accept exactly the same bundle.
do_fetch_or_verify() {
    _fetching="$1"
    require_manifest

    mkdir -p "$BUNDLED_DIR"

    _entries=0
    _fetched=0
    _kept=0
    _expected_names=""

    while read -r sha size name || [ -n "${sha:-}" ]; do
        # Strip \r BEFORE the blank/comment test, not after: \r is not in IFS, so on a CRLF
        # checkout a blank line arrives as sha=$'\r' (not empty) and a content line arrives with
        # the \r glued to the file name, and every lookup misses. .gitattributes pins this file to
        # LF, but a stray core.autocrlf or a hand-edited copy should degrade to "works", not to
        # "silently ships no maps".
        sha="${sha%$'\r'}"
        size="${size%$'\r'}"
        name="${name%$'\r'}"

        case "$sha" in ''|'#'*) continue;; esac

        case "$sha" in
            [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]*) ;;
            *) die "malformed manifest line: column 1 is not a lowercase hex sha256 ('${sha}')." ;;
        esac
        [ ${#sha} -eq 64 ] || die "malformed manifest line: sha256 '${sha}' is ${#sha} chars, expected 64."
        [ -n "${size:-}" ] && [ -n "${name:-}" ] \
            || die "malformed manifest line for '${sha}': expected '<sha256>  <bytes>  <file name>'."

        _entries=$((_entries + 1))
        _expected_names="${_expected_names}
${name}"

        dest="${BUNDLED_DIR}/${name}"

        if check_file "$dest" "$sha" "$size" 2>/dev/null; then
            echo "ok       ${name}"
            _kept=$((_kept + 1))
            continue
        fi

        reason="$(check_file "$dest" "$sha" "$size" 2>&1 >/dev/null || true)"

        if [ "$_fetching" != "1" ]; then
            die "bundled beatmap '${name}' does not match the manifest (${reason})." \
                "Fetch the declared bundle with:  .github/scripts/bundled-maps.sh fetch" \
                "If the local file is the one you want to ship, regenerate and commit the manifest" \
                "instead (see the script header), and upload the new package to the box first."
        fi

        url="${BASE_URL}/bundled-${sha}.typb"
        tmp="${BUNDLED_DIR}/.incoming-${sha}.typb"
        rm -f "$tmp"

        echo "fetch    ${name}  (${reason})"
        if ! curl -fsSL --retry 3 --retry-delay 2 --max-time 600 -o "$tmp" "$url"; then
            rm -f "$tmp"
            die "could not download bundled beatmap '${name}' from ${url}" \
                "The bundle is content-addressed, so a 404 means this exact package was never" \
                "uploaded to the box. Upload it under that name (see the script header for the" \
                "scp/docker cp command), or drop the entry from the manifest and commit that." \
                "Refusing to continue: a build that skips this ships an installer whose first-run" \
                "import silently finds nothing, which is the bug this step exists to prevent."
        fi

        if ! reason="$(check_file "$tmp" "$sha" "$size" 2>&1 >/dev/null)"; then
            rm -f "$tmp"
            die "downloaded '${name}' does not match the manifest (${reason})." \
                "The URL is derived from the manifest's own hash, so this means the object stored" \
                "at bundled-${sha}.typb is not the package that hash was computed from. Re-upload it."
        fi

        mv -f "$tmp" "$dest"
        _fetched=$((_fetched + 1))
    done < "$MANIFEST"

    [ "$_entries" -gt 0 ] || die "the manifest declares no bundled beatmaps." \
        "An empty bundle is never intentional here; if it ever is, delete this step and the" \
        "csproj's Bundled glob together so the absence is visible in the diff."

    # An extra .typb is the Windows-only half of this bug: the local pack shipped whatever sat in
    # the working tree, so a stray package became part of a release nobody could reproduce. Reject
    # it on both sides rather than only on the side that fetches.
    _extra=0
    for f in "$BUNDLED_DIR"/*.typb; do
        [ -e "$f" ] || continue
        b="$(basename "$f")"
        case "
${_expected_names}
" in
            *"
${b}
"*) ;;
            *)
                echo "::error::'${b}' is in ${BUNDLED_DIR} but not in the manifest." >&2
                _extra=$((_extra + 1))
                ;;
        esac
    done
    [ "$_extra" -eq 0 ] || die "${_extra} package(s) present but undeclared." \
        "Either delete them, or add them to the manifest AND upload them to the box, so the" \
        "bundle a release ships is the bundle the repo describes."

    echo "bundled beatmaps: ${_entries} declared, ${_fetched} fetched, ${_kept} already correct."
}

case "$MODE" in
    fetch)    do_fetch_or_verify 1 ;;
    verify)   do_fetch_or_verify 0 ;;
    count)    do_count ;;
    manifest) do_manifest ;;
    *) die "unknown mode '${MODE}' (expected: fetch, verify, count, manifest)." ;;
esac
