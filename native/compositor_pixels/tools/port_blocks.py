#!/usr/bin/env python3
"""Port the two Apple-dependent C kernels to standard C -- mechanically, and provably.

Reads the read-only upstream kernels from `src/Compositor/Compositor/Rendering/`,
copies all 18 `.c`/`.h` files verbatim into `csrc/`, then applies seven literal
string replacements to `AdjustPixels.c` and `DitherPixels.c`. Every replacement has
an expected occurrence count and the script fails loudly if the upstream text does
not match, so a future upstream sync cannot silently skip a site.

Nothing here touches pixel arithmetic: the replacements only concern
  * the Apple include,
  * the two `in_bands` definitions (now a macro in dispatch_shim.h),
  * the 22 `in_bands(...)` call openers and the 2 `dispatch_apply(...)` call openers,
  * 3 `__block` qualifiers (a Clang blocks keyword),
  * 3 block `return`s (which abandon one band) -> `continue`.

Usage:  python port_blocks.py [--check]
        upstream dir and output dir are fixed relative to this script.
"""
import hashlib
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                                      # native/compositor_pixels
REPO = os.path.dirname(os.path.dirname(ROOT))                     # repo root
UPSTREAM = os.path.join(REPO, 'src', 'Compositor', 'Compositor', 'Rendering')
CSRC = os.path.join(ROOT, 'csrc')

# ---------------------------------------------------------------- replacements

INCLUDE_OLD = '#include <dispatch/dispatch.h>'
INCLUDE_NEW = ('#include "dispatch_shim.h"   // PORT: standard-C stand-in for '
               '<dispatch/dispatch.h> (no Apple dependency)')

IN_BANDS_DEF_OLD = '''// Runs `body` over `count` items split into a few runs per core, each a (start, end) range, all at once.
static void in_bands(size_t count, void (^body)(size_t start, size_t end)) {
    size_t bands = count < 64 ? 1 : 32, size = (count + bands - 1) / bands;
    dispatch_apply(bands, DISPATCH_APPLY_AUTO, ^(size_t band) {
        size_t start = band * size, end = start + size < count ? start + size : count;
        if (start < end) body(start, end);
    });
}'''

IN_BANDS_DEF_NEW = '''// PORT (compositor_pixels): `in_bands` is the macro of the same name in dispatch_shim.h now.
// Upstream took a Clang block (`^`), which neither MSVC nor GCC compiles; the macro expands
// the same band loop -- same bands, same size, same (first, last) ranges -- at each call site,
// so the loop bodies below stay byte-identical to upstream.'''

DISPATCH_APPLY_OLD = 'dispatch_apply(bands, DISPATCH_APPLY_AUTO, ^(size_t band) {'
DISPATCH_APPLY_NEW = 'dispatch_apply(bands, DISPATCH_APPLY_AUTO, band, {'

OPENERS = [
    (', ^(size_t first, size_t last) {', ', first, last, {'),
    (', ^(size_t firstLine, size_t lastLine) {', ', firstLine, lastLine, {'),
]

BLOCK_QUALIFIER_OLD = '__block int failed = 0;'
BLOCK_QUALIFIER_NEW = 'int failed = 0;   // PORT: dropped Clang\'s __block; plain C has no blocks'

BLOCK_RETURNS = [
    ('if (!copy) { failed = 1; return; }',
     'if (!copy) { failed = 1; continue; }   // PORT: a block\'s `return` abandoned this band'),
    ('if (!cover) { failed = 1; return; }',
     'if (!cover) { failed = 1; continue; }   // PORT: a block\'s `return` abandoned this band'),
]

# file -> [(old, new, expected_count), ...]
PLAN = {
    'AdjustPixels.c': [
        (INCLUDE_OLD, INCLUDE_NEW, 1),
        (IN_BANDS_DEF_OLD, IN_BANDS_DEF_NEW, 1),
        (DISPATCH_APPLY_OLD, DISPATCH_APPLY_NEW, 2),   # camera_raw_statistics, camera_raw_scope
        (OPENERS[0][0], OPENERS[0][1], 13),
    ],
    'DitherPixels.c': [
        (INCLUDE_OLD, INCLUDE_NEW, 1),
        (IN_BANDS_DEF_OLD, IN_BANDS_DEF_NEW, 1),
        (OPENERS[0][0], OPENERS[0][1], 6),
        (OPENERS[1][0], OPENERS[1][1], 3),
        (BLOCK_QUALIFIER_OLD, BLOCK_QUALIFIER_NEW, 3),
        (BLOCK_RETURNS[0][0], BLOCK_RETURNS[0][1], 2),
        (BLOCK_RETURNS[1][0], BLOCK_RETURNS[1][1], 1),
    ],
}


def _eol(text, newline):
    """Upstream is CRLF; the patterns above are written with plain \\n for legibility."""
    return text.replace('\n', newline) if '\n' in text else text


def apply_plan(name, text, newline='\r\n'):
    steps = []
    plan = [(_eol(old, newline), _eol(new, newline), want) for old, new, want in PLAN[name]]
    for old, new, want in plan:
        got = text.count(old)
        if got != want:
            raise SystemExit(f'{name}: expected {want} occurrence(s) of\n  {old!r}\nbut found {got}. '
                             f'Upstream changed; update PLAN before porting.')
        text = text.replace(old, new)
        steps.append((old, want))
    return text, steps


def sha256(path):
    with open(path, 'rb') as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def main():
    check = '--check' in sys.argv
    os.makedirs(CSRC, exist_ok=True)
    names = sorted(n for n in os.listdir(UPSTREAM) if n.endswith(('.c', '.h')))
    if len(names) != 18:
        raise SystemExit(f'expected 18 kernel sources upstream, found {len(names)}')

    verbatim, ported = [], []
    for name in names:
        with open(os.path.join(UPSTREAM, name), encoding='utf-8', newline='') as fh:
            original = fh.read()
        newline = '\r\n' if '\r\n' in original else '\n'
        if name in PLAN:
            out, steps = apply_plan(name, original, newline)
            ported.append((name, steps))
        else:
            out = original
            verbatim.append(name)
        target = os.path.join(CSRC, name)
        if check:
            with open(target, encoding='utf-8', newline='') as fh:
                have = fh.read()
            if have != out:
                raise SystemExit(f'{name}: csrc/ differs from what this script would produce')
        else:
            with open(target, 'w', encoding='utf-8', newline='') as fh:
                fh.write(out)

    print(f'upstream : {UPSTREAM}')
    print(f'csrc     : {CSRC}')
    print(f'verbatim : {len(verbatim)} files (unchanged, SHA256-identical to upstream)')
    print(f'ported   : {len(ported)} files')
    for name, steps in ported:
        total = sum(n for _, n in steps)
        print(f'  {name}: {total} replacements')
        for old, n in steps:
            first = old.strip().split('\n')[0][:72]
            print(f'      x{n}  {first}')
    print('OK' if check else 'WROTE')


if __name__ == '__main__':
    main()
