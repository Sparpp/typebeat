#!/usr/bin/env python3
"""Resolve PackageReference-only conflicts in a .csproj: upstream's versions win,
type!beat's own lines stay, and upstream's resources package is never brought in.
Exits 1 (leaving the file untouched) if any hunk has anything else in it."""
import re, sys

SKIP = {'ppy.typebeat.Game.Resources'}
ref = re.compile(r'^\s*<PackageReference Include="([^"]+)" Version="([^"]+)"\s*/>\s*$')
p = sys.argv[1]
s = open(p, newline='').read()
hunk = re.compile(r'<<<<<<< [^\n]*\n(.*?)=======\r?\n(.*?)>>>>>>> [^\n]*\n', re.S)


def resolve(m):
    ours, theirs = m.group(1).splitlines(True), m.group(2).splitlines(True)
    tv = {}
    for l in theirs:
        r = ref.match(l)
        if not r:
            raise ValueError(l)
        tv[r.group(1)] = r.group(2)
    out, seen = [], set()
    for l in ours:
        r = ref.match(l)
        if not r:
            raise ValueError(l)
        seen.add(r.group(1))
        out.append(l.replace(f'Version="{r.group(2)}"', f'Version="{tv[r.group(1)]}"') if r.group(1) in tv else l)
    for l in theirs:
        name = ref.match(l).group(1)
        if name not in seen and name not in SKIP:
            out.append(l)
    return ''.join(out)


try:
    new = hunk.sub(resolve, s)
except ValueError as e:
    print('not a package-only conflict:', e)
    sys.exit(1)
if '<<<<<<<' in new:
    sys.exit(1)
open(p, 'w', newline='').write(new)
