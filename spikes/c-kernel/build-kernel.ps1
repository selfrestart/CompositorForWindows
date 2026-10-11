# build-kernel.ps1 -- compile the 9 Compositor C pixel kernels into a Windows DLL.
#
# Measured facts this script encodes (see ../02-toolchain.md for evidence):
#   * 7 of 9 .c files are plain C and compile VERBATIM (BrushPixels, ContentFill,
#     HealPixels, LensPixels, LevelsPixels, NoisePixels, WandPixels).
#   * HealPixels.c needs -std=gnu11 (or -D_USE_MATH_DEFINES) because it uses M_PI,
#     which strict -std=c11 hides.
#   * AdjustPixels.c and DitherPixels.c use Clang blocks + GCD and must be
#     transformed first by port-clang-blocks.py.
#   * No file in the kernel uses CoreGraphics/CGImage/CGContext.
#
# Usage:  pwsh -File build-kernel.ps1 [-Toolchain gcc|msvc] [-Clean]

[CmdletBinding()]
param(
    [ValidateSet('gcc', 'msvc')]
    [string]$Toolchain = 'gcc',
    [switch]$Clean
)

# NOTE: must stay 'Continue'. Under 'Stop', Windows PowerShell turns a native
# compiler's *stderr* (gcc prints even plain warnings there) into a terminating
# ErrorRecord, which aborts the build on a warning that is not an error.
# Every step below therefore checks $LASTEXITCODE and throws explicitly instead.
$ErrorActionPreference = 'Continue'
$Here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$Csrc   = Join-Path $Here 'csrc'
$Ported = Join-Path $Here 'build\ported'
$Obj    = Join-Path $Here 'build\obj'
$Bin    = Join-Path $Here 'build\bin'
$Shim   = Join-Path $Here 'shim'
$Logs   = Join-Path $Here 'logs'

if ($Clean) { Remove-Item -Recurse -Force (Join-Path $Here 'build') -ErrorAction SilentlyContinue }
foreach ($d in @($Ported, $Obj, $Bin, $Logs)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

# ---------------------------------------------------------------- 1. transform
Write-Host '=== [1/3] port Clang blocks -> plain C (AdjustPixels.c, DitherPixels.c) ===' -ForegroundColor Cyan
$py = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $py) { throw 'python not found; needed for port-clang-blocks.py' }
& $py (Join-Path $Here 'port-clang-blocks.py') $Csrc $Ported
if ($LASTEXITCODE -ne 0) { throw "port-clang-blocks.py failed ($LASTEXITCODE)" }

# ---------------------------------------------------------------- 2. compile
Write-Host "=== [2/3] compile with $Toolchain ===" -ForegroundColor Cyan

$Sources = Get-ChildItem $Csrc -Filter *.c | Sort-Object Name
$objects = @()

if ($Toolchain -eq 'gcc') {
    $cc = (Get-Command gcc -ErrorAction SilentlyContinue).Source
    if (-not $cc) { $cc = 'D:\tdm-gcc\bin\gcc.exe' }
    if (-not (Test-Path $cc)) { throw "gcc not found at $cc" }
    Write-Host "compiler: $cc"
    Write-Host ((& $cc --version | Select-Object -First 1))

    # The transform output shadows the original only for the 2 block-using files.
    $flags = @('-std=gnu11', '-O2', '-Wall', '-c', "-I$Csrc", "-I$Ported", "-I$Shim")

    foreach ($f in $Sources) {
        $inFile = if (Test-Path (Join-Path $Ported $f.Name)) { Join-Path $Ported $f.Name } else { $f.FullName }
        $verbatim = -not (Test-Path (Join-Path $Ported $f.Name))
        $o = Join-Path $Obj ($f.BaseName + '.o')
        $ccArgs = $flags + @($inFile, '-o', $o)
        $out = & $cc @ccArgs 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            $out | Set-Content (Join-Path $Logs ("compile-fail-" + $f.BaseName + ".log"))
            Write-Host ("  {0,-18} FAILED" -f $f.Name) -ForegroundColor Red
            Write-Host $out
            throw "compile failed for $($f.Name)"
        }
        $tag = if ($verbatim) { 'verbatim' } else { 'ported  ' }
        Write-Host ("  {0,-18} OK  ({1})" -f $f.Name, $tag) -ForegroundColor Green
        $objects += $o
    }

    # Export the whole public surface with a stable name, and link a DLL.
    $dll = Join-Path $Bin 'compositor_kernel.dll'
    $def = Join-Path $Bin 'compositor_kernel.def'
    $exports = Select-String -Path (Join-Path $Csrc '*.h') -Pattern '^\s*(?:void|int|long|float|double|size_t)\s+([a-z_][A-Za-z0-9_]*)\s*\(' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique
    "EXPORTS" | Set-Content $def
    $exports | ForEach-Object { "    $_" } | Add-Content $def
    Write-Host ("  exporting {0} kernel entry points" -f $exports.Count)

    # Single interpolated token: PowerShell's comma operator binds tighter than
    # '+', so `@($a,'-Wl,--out-implib,' + $p)` would split into three arguments.
    $implibArg = "-Wl,--out-implib,$(Join-Path $Bin 'libcompositor_kernel.a')"
    $link = @('-shared', '-O2', '-o', $dll) + $objects + @($def, $implibArg)
    $out = & $cc @link 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { $out | Set-Content (Join-Path $Logs 'link-fail.log'); Write-Host $out; throw 'link failed' }
    Write-Host ("  -> {0}  ({1:N0} bytes)" -f $dll, (Get-Item $dll).Length) -ForegroundColor Green
}
else {
    # ---- MSVC path. Kept so the script documents the real command line, but the
    # ---- transform output uses GCC nested functions, which cl.exe cannot compile.
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw @'
MSVC toolchain not found: vswhere.exe is absent, i.e. no Visual Studio / Build Tools install.
Install (needs an elevated shell, ~4-7 GB, reboot not required):
    winget install --exact --id Microsoft.VisualStudio.2022.BuildTools `
      --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
Then re-run:  pwsh -File build-kernel.ps1 -Toolchain msvc
NOTE: cl.exe does not support Clang blocks; this path additionally requires
      rewriting the 2 block-using files with real functions + context structs
      instead of GCC nested functions.
'@
    }
    throw 'MSVC path is a documented placeholder: the GCC nested-function transform is not cl.exe-compatible. See 02-toolchain.md.'
}

Write-Host '=== [3/3] done ===' -ForegroundColor Cyan
Write-Host "DLL: $Bin\compositor_kernel.dll"
