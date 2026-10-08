#!/usr/bin/env python3
"""Describe a vendor/osu commit, for the sync merge that brings it in.

  upstream_info.py file <vendor commit>
      prints the tools/upstream-sync/UPSTREAM file for "merged up to this commit"
  upstream_info.py message <vendor commit> [file resolved by hand ...]
      prints the merge commit message
"""
import re, subprocess, sys

FILE_HEADER = """\
# The upstream osu! code type!beat has merged, up to and including this commit.
# tools/upstream-sync/merge_steps.sh rewrites this file in every sync merge; don't edit it by hand.
# tools/upstream-sync/status.sh shows it, plus what's still waiting.
"""


def git(*a):
    return subprocess.run(['git', *a], check=True, capture_output=True).stdout.decode()


def version_key(tag):
    return [int(x) for x in re.findall(r'\d+', tag)]


def describe(c):
    body = git('log', '-1', '--format=%B', c)
    subject = body.split('\n', 1)[0]
    up = re.findall(r'^Upstream: ppy/osu@([0-9a-f]{40})$', body, re.M)[-1]
    date = re.findall(r'^Upstream-Date: (\S+)$', body, re.M)
    base = re.match(r'Add osu! (\S+) as the vendor base', subject)

    # Newest release completed by this commit or any vendor commit before it.
    release = None
    for entry in git('log', '--first-parent', '--format=%B%x00', c).split('\0'):
        tags = re.findall(r'^Upstream-Release: (\S+)$', entry, re.M)
        start = re.match(r'\s*Add osu! (\S+) as the vendor base', entry)
        if tags:
            release = max(tags, key=version_key)
            break
        if start:
            release = start.group(1)
            break

    return {
        'sha': up,
        'title': f'osu! {base.group(1)}' if base else subject,
        'date': (date[-1] if date else git('log', '-1', '--format=%aI', c).strip())[:10],
        'release': release,
    }


def upstream_file(c):
    d = describe(c)
    return (FILE_HEADER + '\n'
            f"commit   ppy/osu@{d['sha']}\n"
            f"title    {d['title']}\n"
            f"date     {d['date']}\n"
            f"release  {d['release']} (the newest osu! release that is fully merged)\n")


def message(c, by_hand):
    d = describe(c)
    pr = re.search(r'\(#(\d+)\)$', d['title'])
    title = re.sub(r'\s*\(#\d+\)$', '', d['title'])
    ref = f"ppy/osu#{pr.group(1)}" if pr else f"ppy/osu@{d['sha'][:10]}"
    head = f"Merge osu! #{pr.group(1)}: {title}" if pr else f"Merge osu! {d['sha'][:10]}: {title}"
    names = ', '.join(f.rsplit('/', 1)[-1] for f in by_hand)
    bullets = ([f'- Resolved by hand: {names}', '  (what was done goes in tools/upstream-sync/RESOLUTIONS.md)']
               if by_hand else ['- Merged cleanly'])
    new_release = git('log', '-1', '--format=%(trailers:key=Upstream-Release,valueonly,separator=%x2C )', c).strip()
    if new_release:
        bullets.append(f'- Completes osu! release {new_release}')
    return (f"{head}\n\nOne upstream osu! commit, merged on its own so it can be reviewed by\nitself.\n\n"
            + '\n'.join(bullets) +
            f"\n\nBefore: type!beat without {ref}.\nAfter: type!beat has it.\n\n"
            f"Upstream: ppy/osu@{d['sha']}\n")


if __name__ == '__main__':
    mode, commit = sys.argv[1], sys.argv[2]
    sys.stdout.write(upstream_file(commit) if mode == 'file' else message(commit, sys.argv[3:]))
