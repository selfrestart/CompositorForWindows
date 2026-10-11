#!/usr/bin/env python3
"""Compare two parity dumps section by section and report where they differ.

Usage: python compare_parity.py <dumpA.bin> <logA> <dumpB.bin> <logB>
"""
import re
import sys

LINE = re.compile(r'^\s*(.*?)\s+(\d+)\s+B\s+fnv1a=([0-9a-f]+)\s*$')


def sections(path):
    """Parse the per-section hash log. Accepts UTF-8 (with or without BOM), UTF-16
    (Windows PowerShell's `>` redirection) and the local ANSI code page, because these
    logs are written by whichever PowerShell happens to be running."""
    raw = open(path, 'rb').read()
    for encoding in ('utf-8-sig', 'utf-16', 'cp936', 'latin-1'):
        try:
            text = raw.decode(encoding)
            if 'fnv1a=' in text:
                break
        except (UnicodeDecodeError, UnicodeError):
            continue
    else:
        text = raw.decode('utf-8', errors='replace')
    out = []
    for line in text.splitlines():
        m = LINE.match(line)
        if m:
            out.append((m.group(1).strip(), int(m.group(2)), m.group(3)))
    return out


def main():
    dump_a, log_a, dump_b, log_b = sys.argv[1:5]
    limit = int(sys.argv[5]) if len(sys.argv) > 5 else 0
    sec_a, sec_b = sections(log_a), sections(log_b)
    a = open(dump_a, 'rb').read()
    b = open(dump_b, 'rb').read()
    print(f'A: {dump_a}  {len(a)} bytes  {len(sec_a)} sections')
    print(f'B: {dump_b}  {len(b)} bytes  {len(sec_b)} sections')
    if len(sec_a) != len(sec_b):
        print('section counts differ; cannot align')
        return 1
    off = 0
    differing = []
    first = None
    for (name, n, h), (name_b, n_b, h_b) in zip(sec_a, sec_b):
        blk_a = a[off + 8:off + 8 + n]
        blk_b = b[off + 8:off + 8 + n]
        d = [i for i, (x, y) in enumerate(zip(blk_a, blk_b)) if x != y]
        if d:
            differing.append((name, len(d), n, d[0], blk_a[d[0]], blk_b[d[0]], h, h_b))
            if first is None:
                first = off + 8 + d[0]
        off += 8 + n
    print(f'differing sections: {len(differing)}')
    for name, nd, n, at, va, vb, h, h_b in differing[:20]:
        print(f'  {name:58s} {nd:>7}/{n} bytes   first at +{at}: {va} -> {vb}')
        print(f'      fnv1a {h} -> {h_b}')
    if first is not None:
        print(f'first differing byte in the dump: offset {first} of {len(a)}')
    print(f'RESULT: {len(differing)} differing section(s), tolerance {limit} -> '
          f'{"OK" if len(differing) <= limit else "EXCEEDED"}')
    return 0 if len(differing) <= limit else 1


if __name__ == '__main__':
    sys.exit(main())
