#!/usr/bin/env python3
"""
port-clang-blocks.py -- mechanical source transform: Clang blocks -> plain C.

Why this exists
---------------
Nine .c files under Compositor/Rendering are pure buffer-in/buffer-out pixel kernels
(no CoreGraphics at all). Seven of them are plain C99/C11 and compile on Windows
verbatim. Two of them -- AdjustPixels.c and DitherPixels.c -- use Apple's Clang
`blocks` extension (`^(...) { ... }`) to feed parallel band loops, via GCD
(`dispatch_apply`) or via the local `in_bands()` helper.

Neither MSVC (cl.exe) nor GCC supports the `^` block literal syntax, and neither
ships `dispatch/dispatch.h`. This script removes exactly that dependency, and
nothing else:

  1. block *parameter declarations*   `void (^name)(A, B)`  ->  `void (*name)(A, B)`
  2. block *literals* used as arguments
        `f(x, ^(A a, B b) { BODY })`
     -> `f(x, ({ void __blkN(A a, B b) { BODY } __blkN; }))`
     i.e. a GCC nested function inside a statement expression -- which keeps the
     lexical capture semantics of a block (it can still read/write the enclosing
     function's locals).
  3. the `__block` storage qualifier -> removed (nested functions access the
     enclosing frame directly, so the qualifier is not needed).

The numeric body of every kernel is left byte-for-byte untouched. Only the 2
files that actually contain blocks are transformed; the other 7 are compiled
verbatim from `csrc/`.

Parallelism note: the generated `dispatch_apply` shim runs bands serially.
That is a *correctness* port, not a performance port. See README.md.

Usage:  python port-clang-blocks.py <csrc-dir> <out-dir>
"""

import os
import re
import sys

# ---------------------------------------------------------------- lexer helpers

def skip_noise(s, i):
    """Return index just past any comment/string/char-literal starting at i (else i)."""
    n = len(s)
    if s.startswith("//", i):
        j = s.find("\n", i)
        return n if j < 0 else j
    if s.startswith("/*", i):
        j = s.find("*/", i + 2)
        return n if j < 0 else j + 2
    if s[i] in "\"'":
        q = s[i]
        j = i + 1
        while j < n:
            if s[j] == "\\":
                j += 2
                continue
            if s[j] == q:
                return j + 1
            j += 1
        return n
    return i


def match_delim(s, i, open_ch, close_ch):
    """Given s[i] == open_ch, return index of the matching close_ch."""
    depth = 0
    n = len(s)
    while i < n:
        nxt = skip_noise(s, i)
        if nxt != i:
            i = nxt
            continue
        c = s[i]
        if c == open_ch:
            depth += 1
        elif c == close_ch:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    raise ValueError("unbalanced %s starting at %d" % (open_ch, i))


# ---------------------------------------------------------------- the transform

DECL_RE = re.compile(r"\(\s*\^\s*([A-Za-z_]\w*)\s*\)")   # void (^body)(...)  -> pointer decl


def transform(src):
    """Transform one translation unit. Returns (new_src, n_decls, n_literals)."""
    out = []
    i = 0
    n = len(src)
    n_decls = 0
    n_literals = 0
    blk_id = 0

    while i < n:
        nxt = skip_noise(src, i)
        if nxt != i:
            out.append(src[i:nxt])
            i = nxt
            continue

        # ---- 1. block parameter declaration:  (^name)  ->  (*name)
        m = DECL_RE.match(src, i)
        if m:
            out.append("(*%s)" % m.group(1))
            n_decls += 1
            i = m.end()
            continue

        # ---- 2. block literal used as an argument:  , ^(params) { BODY }
        if src[i] == "^" and i + 1 < n and src[i + 1] == "(":
            # confirm it is an argument position (previous non-space char is ',' or '(')
            k = len("".join(out)) - 1
            while k >= 0 and "".join(out)[k] in " \t\r\n":
                k -= 1
            prev = "".join(out)[k] if k >= 0 else ""
            if prev in ",(":
                close_paren = match_delim(src, i + 1, "(", ")")
                # find the '{' that opens the body
                j = close_paren + 1
                while j < n and src[j] in " \t\r\n":
                    j += 1
                if j < n and src[j] == "{":
                    close_brace = match_delim(src, j, "{", "}")
                    params = src[i + 2:close_paren]
                    body = src[j + 1:close_brace]
                    blk_id += 1
                    name = "__blk%d" % blk_id
                    out.append("({ void %s(%s) {%s} %s; })" % (name, params, body, name))
                    n_literals += 1
                    i = close_brace + 1
                    continue

        out.append(src[i])
        i += 1

    res = "".join(out)
    # ---- 3. drop the __block qualifier
    res = re.sub(r"\b__block\b\s*", "", res)
    return res, n_decls, n_literals


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    src_dir, out_dir = sys.argv[1], sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)

    only = {f for f in os.listdir(src_dir) if f.endswith(".c")}
    for name in sorted(only):
        raw = open(os.path.join(src_dir, name), "r", encoding="utf-8").read()
        if "^(" not in raw:
            print("%-18s SKIP   (no blocks; compiled verbatim from csrc/)" % name)
            continue
        new, nd, nl = transform(raw)
        if "^(" in new:
            raise SystemExit("ERROR: %s still contains a block literal" % name)
        dst = os.path.join(out_dir, name)
        with open(dst, "w", encoding="utf-8", newline="") as f:
            f.write(new)
        print("%-18s PORTED (%d block declarations, %d block literals rewritten) -> %s"
              % (name, nd, nl, dst))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
