#!/usr/bin/env python3
"""Extend the vendor/osu branch with new upstream osu! commits.

vendor/osu holds upstream osu!, reshaped to look like type!beat:
  - osu.Game -> typebeat.Game, osu.Desktop -> typebeat.Desktop
  - the other rulesets, tests, mobile and tournament projects left out
  - `osu.Game` / `osu.Desktop` renamed to `typebeat.*` inside every text file

Each upstream commit on ppy/osu master becomes one vendor/osu commit, with
trailers that merge_steps.sh copies into tools/upstream-sync/UPSTREAM:
  Upstream: ppy/osu@<sha>         the upstream commit
  Upstream-Date: <iso date>       when it landed on osu! master
  Upstream-Release: <tag>         osu! releases this commit completes (if any)
The script picks up after the newest `Upstream:` trailer, so running it again
only adds the commits that are new since.

Usage (from the type!beat repo):
  git fetch https://github.com/ppy/osu master:refs/remotes/ppy/master
  python3 tools/upstream-sync/build_vendor.py ppy/master

Never rebuild vendor/osu from scratch: the new commits would get new hashes
and type!beat would lose its shared history with the old ones.
"""
import os, re, subprocess, sys, tempfile

TIP = sys.argv[1] if len(sys.argv) > 1 else 'ppy/master'
BRANCH = 'refs/heads/vendor/osu'

DIR_MAP = {'osu.Game/': 'typebeat.Game/', 'osu.Desktop/': 'typebeat.Desktop/'}
FILE_MAP = {
    'osu.sln.DotSettings': 'typebeat.sln.DotSettings',
    'osu.TestProject.props': 'typebeat.TestProject.props',
    'osu.licenseheader': 'typebeat.licenseheader',
}
KEEP_ROOT = {'.config/', '.editorconfig', '.gitattributes', '.gitignore', '.run/', '.vscode/',
             'CodeAnalysis/', 'Directory.Build.props', 'FodyWeavers.xml', 'InspectCode.ps1',
             'InspectCode.sh', 'LICENCE', 'UseLocalFramework.ps1', 'UseLocalFramework.sh',
             'UseLocalResources.ps1', 'UseLocalResources.sh', 'app.manifest', 'assets/', 'global.json'}
# Known quirk, kept for stable history: this also turns upstream's `ppy.osu.Game.Resources`
# package into `ppy.typebeat.Game.Resources` on vendor/osu. type!beat uses its own
# `typebeat.Game.Resources` package, so that line never applies anyway.
REPLACE = [(b'osu.Game', b'typebeat.Game'), (b'osu.Desktop', b'typebeat.Desktop')]

SIGN = ['-c', 'user.name=lauriys', '-c', 'user.email=laura@iys.io', '-c', 'gpg.format=ssh',
        '-c', 'user.signingkey=' + os.path.expanduser('~/.ssh/typebeat_signing')]


def git(*args, input=None, env=None):
    return subprocess.run(['git', *args], input=input, env=env, check=True, capture_output=True).stdout


def map_path(p):
    for src, dst in DIR_MAP.items():
        if p.startswith(src):
            new = dst + p[len(src):]
            return new.replace('/osu.Game.csproj', '/typebeat.Game.csproj') \
                      .replace('/osu.Desktop.csproj', '/typebeat.Desktop.csproj')
    if p in FILE_MAP:
        return FILE_MAP[p]
    for k in KEEP_ROOT:
        if p == k or (k.endswith('/') and p.startswith(k)):
            return p
    return None


blob_cache = {}


def transform_blob(sha):
    if sha not in blob_cache:
        data = git('cat-file', 'blob', sha)
        if b'\0' in data[:8000]:
            blob_cache[sha] = sha
        else:
            new = data
            for a, b in REPLACE:
                new = new.replace(a, b)
            blob_cache[sha] = sha if new == data else git('hash-object', '-w', '--stdin', input=new).decode().strip()
    return blob_cache[sha]


parent = git('rev-parse', BRANCH).decode().strip()
# The base commit's `Upstream:` line isn't in its final paragraph, so read the body.
last_upstream = re.findall(r'^Upstream: ppy/osu@([0-9a-f]{40})$', git('log', '-1', '--format=%B', parent).decode(), re.M)[-1]

# osu! release tags, by the commit they point at. ls-remote, so the tags don't
# land in type!beat's own tag list.
release_tags = {}
for line in git('ls-remote', '--tags', 'https://github.com/ppy/osu').decode().splitlines():
    sha, ref = line.split('\t')
    name = ref[len('refs/tags/'):]
    if name.endswith('^{}'):
        release_tags[name[:-3]] = sha  # annotated tag: the commit it points at
    else:
        release_tags.setdefault(name, sha)
tags_at = {}
for name, sha in release_tags.items():
    tags_at.setdefault(sha, []).append(name)
# A release whose commit only touches files type!beat leaves out is credited
# to the next vendor commit.
pending_releases = []

idx = tempfile.mktemp(prefix='vendor-index-')
env = dict(os.environ, GIT_INDEX_FILE=idx)
git('read-tree', parent, env=env)

made = skipped = 0
for c in git('rev-list', '--reverse', '--first-parent', f'{last_upstream}..{TIP}').decode().split():
    pending_releases += sorted(tags_at.get(c, []))
    lines = []
    raw = git('diff-tree', '-r', '-z', '--no-renames', '--no-commit-id', c + '^', c).split(b'\0')
    for i in range(0, len(raw) - 1, 2):
        _, newmode, _, newsha, status = raw[i].decode()[1:].split()
        new = map_path(raw[i + 1].decode())
        if new is None:
            continue
        if status == 'D':
            lines.append(f'0 {"0" * 40}\t{new}\n')
        else:
            lines.append(f'{newmode} {transform_blob(newsha)}\t{new}\n')
    if not lines:
        skipped += 1
        continue
    git('update-index', '--add', '--remove', '--index-info', input=''.join(lines).encode(), env=env)
    tree = git('write-tree', env=env).decode().strip()
    an, ae, ad, cd, body = git('log', '-1', '--format=%an%x00%ae%x00%aI%x00%cI%x00%B', c).decode().split('\0', 4)
    body = body.rstrip() + f'\n\nUpstream: ppy/osu@{c}\nUpstream-Date: {cd}\n'
    body += ''.join(f'Upstream-Release: {t}\n' for t in pending_releases)
    pending_releases = []
    cenv = dict(os.environ, GIT_AUTHOR_NAME=an, GIT_AUTHOR_EMAIL=ae, GIT_AUTHOR_DATE=ad)
    parent = git(*SIGN, 'commit-tree', '-S', tree, '-p', parent, input=body.encode(), env=cenv).decode().strip()
    made += 1

git('update-ref', BRANCH, parent)
os.unlink(idx)
print(f'vendor/osu: added {made}, skipped {skipped} (no type!beat-relevant files), tip {parent[:8]}')
