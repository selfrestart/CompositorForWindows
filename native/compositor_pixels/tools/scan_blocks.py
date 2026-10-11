#!/usr/bin/env python3
"""Scan the upstream C kernels for block-based band loops and report, per call site,
the facts that decide how it can be rewritten in standard C.

Reported per site:
  * line span of the call
  * whether the body contains a `return` (Clang blocks return from the *block*,
    not from the enclosing function -- this is the one non-obvious semantic)
  * whether such a `return` is at band-body top level (only then can it become
    `continue` in a macro-expanded band loop)
  * `__block` declarations (mutated outer locals)
  * `break` / `continue` at band-body top level
  * free variables (captures) that are neither declared in the body nor a
    parameter of the block -- these are what a context struct would have to carry

Usage: python scan_blocks.py <file.c> [<file.c> ...]
"""
import re
import sys

KEYWORDS = {
    'if', 'for', 'while', 'do', 'else', 'switch', 'case', 'return', 'sizeof', 'int', 'long', 'short', 'char',
    'float', 'double', 'unsigned', 'signed', 'void', 'const', 'static', 'struct', 'union', 'enum', 'typedef',
    'uint8_t', 'uint16_t', 'uint32_t', 'uint64_t', 'int8_t', 'int16_t', 'int32_t', 'int64_t', 'size_t',
    'break', 'continue', 'goto', 'default', 'inline', 'volatile', 'restrict', 'bool', 'true', 'false',
    'NULL', 'uintptr_t', 'ptrdiff_t', 'wchar_t', 'auto', 'register', 'extern',
}

IDENT = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')


def strip_noise(text):
    """Replace comments and string/char literals with spaces, keeping offsets."""
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                out[i] = ' '
                i += 1
        elif c == '/' and i + 1 < n and text[i + 1] == '*':
            while i < n and not (text[i] == '*' and i + 1 < n and text[i + 1] == '/'):
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            for k in range(i, min(i + 2, n)):
                out[k] = ' '
            i += 2
        elif c in '"\'':
            q = c
            out[i] = ' '
            i += 1
            while i < n and text[i] != q:
                if text[i] == '\\':
                    out[i] = ' '
                    i += 1
                    if i < n:
                        out[i] = ' '
                        i += 1
                    continue
                if text[i] != '\n':
                    out[i] = ' '
                i += 1
            if i < n:
                out[i] = ' '
                i += 1
        else:
            i += 1
    return ''.join(out)


def match_brace(clean, start):
    """Index just past the '}' matching the '{' at `start`."""
    assert clean[start] == '{', clean[start:start + 20]
    depth, i, n = 0, start, len(clean)
    while i < n:
        if clean[i] == '{':
            depth += 1
        elif clean[i] == '}':
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    raise ValueError('unbalanced braces')


def top_level_returns(clean, body):
    """True/False per `return` occurrence: is it outside every loop in the body?"""
    results = []
    depth_loop = 0
    i = 0
    n = len(body)
    while i < n:
        if body.startswith('for', i) and not is_ident_char(body, i + 3):
            depth_loop += 1
            i += 3
            continue
        if body.startswith('while', i) and not is_ident_char(body, i + 5):
            depth_loop += 1
            i += 5
            continue
        if body.startswith('do', i) and not is_ident_char(body, i + 2):
            depth_loop += 1
            i += 2
            continue
        if body[i] == '{':
            i += 1
            continue
        if body[i] == '}':
            # a '}' may close a loop; approximate by tracking braces per loop opener
            i += 1
            continue
        if body.startswith('return', i) and not is_ident_char(body, i + 6):
            results.append(depth_loop == 0)
            i += 6
            continue
        i += 1
    return results


def is_ident_char(text, i):
    return i < len(text) and (text[i].isalnum() or text[i] == '_')


def scan(path):
    raw = open(path, encoding='utf-8').read()
    clean = strip_noise(raw)
    lines = raw.split('\n')
    line_starts = []
    pos = 0
    for ln in lines:
        line_starts.append(pos)
        pos += len(ln) + 1

    def lineno(idx):
        lo, hi = 0, len(line_starts) - 1
        while lo < hi:
            mid = (lo + hi + 1) // 2
            if line_starts[mid] <= idx:
                lo = mid
            else:
                hi = mid - 1
        return lo + 1

    sites = []
    for m in re.finditer(r'\b(in_bands|dispatch_apply)\s*\(', clean):
        name = m.group(1)
        open_paren = clean.index('(', m.start())
        # find the '^' block literal inside the call
        caret = clean.find('^', open_paren)
        close_paren = clean.find(')', open_paren)
        if caret == -1 or caret > close_paren:
            continue  # the definition itself, not a call site
        # block parameter list
        pstart = clean.index('(', caret)
        pend = clean.index(')', pstart)
        params = [p.strip() for p in clean[pstart + 1:pend].split(',') if p.strip()]
        brace = clean.index('{', pend)
        body_end = match_brace(clean, brace)
        call_end = clean.index(';', body_end)
        body = clean[brace:body_end]
        raw_body = raw[brace + 1:body_end - 1]

        declared = set(IDENT.findall(clean[pstart + 1:pend]))
        for pm in params:
            declared |= set(IDENT.findall(pm)) - KEYWORDS
        for dm in re.finditer(r'\b(?:size_t|int|long|short|char|float|double|unsigned|signed|uint8_t|uint16_t|'
                              r'uint32_t|uint64_t|int32_t|int64_t|const|struct\s+\w+)\s+([A-Za-z_]\w*)\s*[=;,\[]',
                              body):
            declared.add(dm.group(1))
        used = {t for t in IDENT.findall(body)
                if t not in KEYWORDS and not t.isdigit()}
        captures = sorted(used - declared)

        sites.append({
            'file': path,
            'name': name,
            'line': lineno(m.start()),
            'lines': (lineno(m.start()), lineno(call_end)),
            'params': params,
            'returns': top_level_returns(clean, body),
            'has_block': '__block' in raw[brace:call_end],
            'captures': captures,
            'body_len': len(raw_body.split('\n')),
            'top_level_break': bool(re.search(r'(?m)^\s*(break)\s*;', raw_body)),
        })
    return sites


def main():
    total = 0
    for path in sys.argv[1:]:
        print('=' * 100)
        print(path)
        print('=' * 100)
        for s in scan(path):
            total += 1
            lo, hi = s['lines']
            rets = s['returns']
            ret_str = 'none' if not rets else f'{len(rets)} ({sum(rets)} top-level, {len(rets) - sum(rets)} nested)'
            print(f"L{lo:>5}-{hi:<5} {s['name']:<14} params={s['params']} body={s['body_len']:>3} lines")
            print(f"                    return: {ret_str}   __block: {s['has_block']}")
            print(f"                    captures({len(s['captures'])}): {', '.join(s['captures'])}")
    print(f"\nTOTAL CALL SITES: {total}")


if __name__ == '__main__':
    main()
