#!/usr/bin/env bash
#
# build.sh -- build libcompositor_pixels.so, the Linux artifact of the C pixel kernels.
#
#   ./build.sh [--openmp] [--clean]
#
# This mirrors build.ps1: same sources, same flags, same 42 exported entry points. CI
# (ubuntu-latest) uses it so that Compositor.Core / Compositor.Pixels can be built and
# tested on Linux against a real native kernel; `DllImport("compositor_pixels")` resolves
# to "compositor_pixels.so" / "libcompositor_pixels.so" there, so the file name matters.
#
# No Apple frameworks and no libdispatch: the band loops are standard C (dispatch_shim.h).
# Uses only a C11 compiler and the C library; -lm is needed for math.h.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
csrc="$here/csrc"
obj="$here/build/obj"
bin="$here/build/bin"
logs="$here/logs"

openmp=0
for arg in "$@"; do
  case "$arg" in
    --openmp) openmp=1 ;;
    --clean)  rm -rf "$here/build" ;;
    *) echo "build.sh: unknown argument: $arg" >&2; exit 2 ;;
  esac
done

mkdir -p "$obj" "$bin" "$logs"

# ---------------------------------------------------------------- 1. export table
expected="$bin/compositor_pixels.exports"
grep -hoE '^[[:space:]]*(void|int|long|float|double|size_t)[[:space:]]+[a-z_][A-Za-z0-9_]*[[:space:]]*\(' "$csrc"/*.h \
  | sed -E 's/^[[:space:]]*(void|int|long|float|double|size_t)[[:space:]]+//; s/[[:space:]]*\($//' \
  | sort -u > "$expected"
count="$(wc -l < "$expected" | tr -d ' ')"
echo "=== [1/3] export table: $count entry points ==="
if [ "$count" -ne 42 ]; then
  echo "expected 42 entry points (docs/port/wave0-contracts.md section 3.1), got $count" >&2
  exit 1
fi

# ---------------------------------------------------------------- 2. compile and link
cc_bin="${CC:-cc}"
flags=(-std=gnu11 -O2 -Wall -fPIC -I"$csrc" -I"$here")
if [ "$openmp" -eq 1 ]; then flags+=(-fopenmp); fi

echo "=== [2/3] compile with $cc_bin $( [ "$openmp" -eq 1 ] && echo '(+OpenMP)' ) ==="
"$cc_bin" --version | head -1
for src in "$csrc"/*.c; do
  base="$(basename "$src" .c)"
  "$cc_bin" "${flags[@]}" -c "$src" -o "$obj/$base.o"
  echo "  $base.c OK"
done

link=(-shared -O2 -o "$bin/libcompositor_pixels.so" "$obj"/*.o -lm)
if [ "$openmp" -eq 1 ]; then link+=(-fopenmp); fi
"$cc_bin" "${link[@]}"

# ---------------------------------------------------------------- 3. verify exports
echo "=== [3/3] verify the shared library's exports ==="
if command -v nm >/dev/null 2>&1; then
  nm -D --defined-only "$bin/libcompositor_pixels.so" | awk '$2 == "T" { print $3 }' | sort -u \
    > "$bin/compositor_pixels.linux-exports"
  got="$(wc -l < "$bin/compositor_pixels.linux-exports" | tr -d ' ')"
  echo "  libcompositor_pixels.so exports $got symbols"
  if ! diff -u "$expected" "$bin/compositor_pixels.linux-exports"; then
    echo "export set does not match the 42 public entry points" >&2
    exit 1
  fi
  echo "  export set matches the 42 public entry points exactly"
else
  echo "  nm not found; skipping the export check (the .def equivalent is in $expected)" >&2
fi

if command -v sha256sum >/dev/null 2>&1; then
  sha256sum "$bin/libcompositor_pixels.so"
fi
echo "artifact: $bin/libcompositor_pixels.so"
