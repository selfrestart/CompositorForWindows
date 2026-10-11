#!/usr/bin/env python3
"""
verify-transform-roundtrip.py -- prove port-clang-blocks.py changed ONLY the
block syntax and not one byte of pixel math.

Method: the transform is designed to be exactly invertible. This script walks the
generated file, finds every

    ({ void __blkN(<params>) {<body>} __blkN; })

and rewrites it back to the Clang block literal

    ^(<params>) {<body>}

then compares the result to the pristine original in csrc/. If the two are byte
identical, the transform provably preserved every arithmetic expression, every
loop bound and every comment -- only the closure annotation moved.

Exit code 0 = byte-identical round trip for every transformed file.

Usage:  python verify-transform-roundtrip.py <csrc-dir> <ported-dir>
"""

import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import import_module

_port = import_module("port-clang-blocks".replace("-", "_")) if False else None

# Re-implement the two tiny lexer helpers locally so this script has no import
# dependency on a module whose filename is not a valid identifier.
def skip_noise(s, i):
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
    depth = 0
    n = len(s)
    while i < n:
        nxt = skip_noise(s, i)
        if nxt != i:
            i = nxt
            continue
        if s[i] == open_ch:
            depth += 1
        elif s[i] == close_ch:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    raise ValueError("unbalanced")


LIT_RE = re.compile(r"\(\{\s*void\s+(__blk\d+)\s*\(")

# The transform also rewrites block *parameter declarations*:
#     void (^body)(size_t, size_t)  ->  void (*body)(size_t, size_t)
# These are inverted by name, and the names are read from the ORIGINAL file (not
# guessed by pattern), so a genuine function pointer in the source can never be
# mistaken for a converted block declaration.
DECL_NAME_RE = re.compile(r"\(\s*\^\s*([A-Za-z_]\w*)\s*\)")


def invert_decls(ported, original):
    names = sorted(set(DECL_NAME_RE.findall(original)))
    for nm in names:
        ported = ported.replace("(*%s)" % nm, "(^%s)" % nm)
    return ported, names


def invert(ported):
    out = []
    i = 0
    n = len(ported)
    count = 0
    while i < n:
        nxt = skip_noise(ported, i)
        if nxt != i:
            out.append(ported[i:nxt])
            i = nxt
            continue

        # "( { void __blkN(" -- back to a block literal
        if ported.startswith("({", i) or (ported[i] == "(" and ported.startswith("({", i)):
            pass
        if ported[i] == "(" and ported.startswith("({", i):
            # locate  void __blkN(
            j = i + 2
            while j < n and ported[j] in " \t":
                j += 1
            if ported.startswith("void", j):
                k = j + 4
                while k < n and ported[k] in " \t":
                    k += 1
                m = re.match(r"__blk\d+", ported[k:])
                if m:
                    name = m.group(0)
                    lp = k + len(name)
                    while lp < n and ported[lp] in " \t":
                        lp += 1
                    if lp < n and ported[lp] == "(":
                        rp = match_delim(ported, lp, "(", ")")
                        params = ported[lp + 1:rp]
                        b = rp + 1
                        while b < n and ported[b] in " \t\r\n":
                            b += 1
                        if b < n and ported[b] == "{":
                            rb = match_delim(ported, b, "{", "}")
                            body = ported[b + 1:rb]
                            # expect the trailing  <name>; })
                            tail = ported[rb + 1:]
                            tm = re.match(r"\s*" + re.escape(name) + r"\s*;\s*\}\)", tail)
                            if not tm:
                                raise SystemExit("malformed tail after %s" % name)
                            out.append("^(%s) {%s}" % (params, body))
                            count += 1
                            i = rb + 1 + tm.end()
                            continue
        out.append(ported[i])
        i += 1
    return "".join(out), count


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    csrc, ported = sys.argv[1], sys.argv[2]
    ok = True
    for name in sorted(os.listdir(ported)):
        if not name.endswith(".c"):
            continue
        orig = open(os.path.join(csrc, name), encoding="utf-8").read()
        gen = open(os.path.join(ported, name), encoding="utf-8").read()
        back, n = invert(gen)
        back, decl_names = invert_decls(back, orig)
        # the only deliberate normalisation is dropping the __block qualifier
        orig_norm = re.sub(r"\b__block\b\s*", "", orig)
        same = back == orig_norm
        ok &= same
        print("%-18s %d block literals + %d block declarations inverted -> %s"
              % (name, n, len(decl_names), "BYTE-IDENTICAL to original" if same else "DIFFERS"))
        if not same:
            for idx, (a, b) in enumerate(zip(back, orig_norm)):
                if a != b:
                    print("   first difference at offset %d:" % idx)
                    print("     round-trip: %r" % back[max(0, idx - 60):idx + 60])
                    print("     original  : %r" % orig_norm[max(0, idx - 60):idx + 60])
                    break
    print()
    print("ROUND TRIP: %s" % ("PASS -- pixel math unchanged" if ok else "FAIL"))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
