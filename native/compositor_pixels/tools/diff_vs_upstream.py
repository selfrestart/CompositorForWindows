#!/usr/bin/env python3
"""Prove the port changed nothing but the band plumbing.

Both checks run against the read-only upstream sources.

1. Line alignment (difflib, autojunk off). Every changed region must decompose
   *exactly* into a sequence of the documented literal replacements from
   port_blocks.py -- same removed lines, same added lines, nothing else. A region
   that does not decompose fails the run, so "only in_bands/dispatch_apply lines
   changed" is enforced rather than asserted. The lines outside the changed regions
   are reported as the untouched set.

2. The 16 files that needed no port at all are checked SHA256-identical to upstream.

Usage:  python diff_vs_upstream.py
"""
import difflib
import hashlib
import os
import sys

# The report contains characters outside the legacy console encodings -- a U+2212 minus, for one.
# A Windows console defaults to a code page that cannot encode them, and printing then raises
# UnicodeEncodeError and fails the run on a passing tree. Forcing UTF-8 on the streams makes the
# tool work wherever it is invoked from, without the caller having to know to set anything.
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from port_blocks import CSRC, UPSTREAM, PLAN, _eol  # noqa: E402  (same directory)

CRLF = '\r\n'

# All comparison happens on '\n', whatever the checkout produced.
#
# This used to assume the upstream sources were CRLF on disk, which was an artefact of
# `core.autocrlf=true` on the machine the port was written on -- git stores them with LF. The
# assumption made the tool fail on any ordinary checkout, including the Linux CI job, where nothing
# converts line endings at all. Normalising here means the check does not care.
EOL = '\n'


def read(path):
    with open(path, encoding='utf-8', newline='') as fh:
        return fh.read().replace('\r\n', EOL).replace('\r', EOL)


def sha256(path):
    with open(path, 'rb') as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def label(old, new):
    """A short human label for a replacement."""
    head = old.split(CRLF)[0].strip()
    if head.startswith('#include'):
        return 'include: <dispatch/dispatch.h> -> "dispatch_shim.h"'
    if 'in_bands(size_t count' in old:
        return 'in_bands definition -> macro (dispatch_shim.h)'
    if 'dispatch_apply' in old:
        return 'dispatch_apply call opener'
    if old.strip() == '__block int failed = 0;':
        return '__block qualifier dropped (3x)'
    if 'failed = 1; return;' in old:
        return "block `return` -> `continue` (abandons one band)"
    if 'first, last' in old or 'firstLine' in old:
        return 'in_bands call opener'
    return head[:60]


def decompose(removed, added, patterns):
    """Check a changed region is exactly what the documented replacements produce.

    `removed`/`added` are whole lines from the alignment. Joining them reproduces the
    region's text, so applying the replacements in plan order to the removed text must
    yield the added text -- nothing more, nothing less. Returns the pattern indices
    that applied, or None when the region cannot be explained.
    """
    text = EOL.join(removed)
    used = []
    for k, (o, n, _) in enumerate(patterns):
        if o in text:
            text = text.replace(o, n)
            used.append(k)
    return used if text == EOL.join(added) and used else None


def main():
    ok = True
    print('=' * 96)
    print('1. every changed line is a documented band-plumbing replacement')
    print('=' * 96)
    for name in sorted(PLAN):
        patterns = [(_eol(o, EOL), _eol(n, EOL), c) for o, n, c in PLAN[name]]
        old = read(os.path.join(UPSTREAM, name)).split(EOL)
        new = read(os.path.join(CSRC, name)).split(EOL)
        sm = difflib.SequenceMatcher(None, old, new, autojunk=False)

        untouched = changed_r = changed_a = 0
        counts = {}
        regions = 0
        for tag, i1, i2, j1, j2 in sm.get_opcodes():
            if tag == 'equal':
                untouched += i2 - i1
                continue
            removed, added = old[i1:i2], new[j1:j2]
            regions += 1
            used = decompose(removed, added, patterns)
            if used is None:
                ok = False
                print(f'  {name}: UNDOCUMENTED CHANGE at upstream line {i1 + 1}')
                for l in removed:
                    print(f'      - {l!r}')
                for l in added:
                    print(f'      + {l!r}')
                continue
            changed_r += len(removed)
            changed_a += len(added)
            for k in used:
                counts[k] = counts.get(k, 0) + 1

        total = len(old)
        print(f'\n{name}  ({total} lines upstream, {len(patterns)} documented replacement kinds)')
        print(f'  changed regions   : {regions}')
        print(f'  changed lines     : -{changed_r} +{changed_a} = {changed_r + changed_a} '
              f'({100.0 * (changed_r + changed_a) / total:.1f}% of the file)')
        print(f'  untouched lines   : {untouched} ({100.0 * untouched / total:.1f}%) -- identical, and in order')
        for k, (o, n, _) in enumerate(patterns):
            if counts.get(k):
                print(f'    {counts[k]:>3} x  {label(o, n)}')

    print()
    print('=' * 96)
    print('2. the files that needed no port')
    print('=' * 96)
    names = sorted(n for n in os.listdir(UPSTREAM) if n.endswith(('.c', '.h')))
    identical = 0
    for name in names:
        if name in PLAN:
            continue
        if sha256(os.path.join(UPSTREAM, name)) == sha256(os.path.join(CSRC, name)):
            identical += 1
        else:
            ok = False
            print(f'  {name}: DIFFERS from upstream')
    print(f'  {identical}/16 files are SHA256-identical to upstream')

    print()
    print('RESULT:', 'PASS -- only band plumbing lines differ' if ok else 'FAIL')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
