# build.ps1 -- compile the Compositor C pixel kernels into compositor_pixels.dll.
#
#   pwsh -File native/compositor_pixels/build.ps1 [-Toolchain gcc|msvc] [-OpenMp] [-Clean]
#
# Artifact: build/bin/compositor_pixels.dll  (the name src/Compositor.Pixels/*.csproj expects)
#
# Measured facts this script encodes (see docs/port/wave0-contracts.md section 0 and
# spikes/c-kernel/README.md):
#   * 7 of 9 .c files are plain C and compile with no change at all.
#   * AdjustPixels.c and DitherPixels.c were ported by tools/port_blocks.py: Clang
#     blocks + GCD are gone, replaced by dispatch_shim.h (standard C only).
#   * HealPixels.c:168 uses M_PI, which strict -std=c11 hides -> -std=gnu11.
#     MSVC needs _USE_MATH_DEFINES (see the msvc branch).
#   * PowerShell must NOT run with $ErrorActionPreference='Stop' here: gcc writes even
#     plain warnings to stderr and Windows PowerShell turns that into a terminating
#     error. Every native call is checked through $LASTEXITCODE instead.
#   * No file in the kernel uses CoreGraphics/CGImage/CGContext, and nothing outside
#     dispatch_shim.h includes a platform header.

[CmdletBinding()]
param(
    [ValidateSet('gcc', 'msvc')]
    [string]$Toolchain = 'gcc',
    # Opt-in parallel band loops (#pragma omp parallel for). Serial is the default
    # because it is the ordering the macOS build produces; the two agree bit for bit.
    [switch]$OpenMp,
    # Where the DLL lands, relative to this script. Overridden by the verification runs so
    # that the -OpenMp build can be tested without disturbing build/bin/compositor_pixels.dll.
    [string]$OutDir = 'build\bin',
    # Source tree to compile, relative to this script. Only the mutation control changes it:
    # it builds a deliberately wrong copy to prove the parity comparison can fail.
    [string]$SourceDir = 'csrc',
    # C compiler to use for -Toolchain gcc. Default: the first gcc on PATH, else
    # D:\tdm-gcc\bin\gcc.exe. tools\run_openmp_parity.ps1 passes a compiler that ships
    # libgomp, because TDM-GCC does not (so it cannot link an -OpenMp build at all).
    [string]$CC = '',
    [switch]$Clean
)

$ErrorActionPreference = 'Continue'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Csrc = Join-Path $Here $SourceDir
$Obj = Join-Path $Here ('build\obj-' + ($OutDir -replace '[\\/:]', '_'))
$Bin = Join-Path $Here $OutDir
$Logs = Join-Path $Here 'logs'
$Def = Join-Path $Bin 'compositor_pixels.def'

if ($Clean) { Remove-Item -Recurse -Force (Join-Path $Here 'build') -ErrorAction SilentlyContinue }
foreach ($d in @($Obj, $Bin, $Logs)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

# ---------------------------------------------------------------- 1. export table
Write-Host '=== [1/3] export table from the public headers ===' -ForegroundColor Cyan
$exports = Select-String -Path (Join-Path $Csrc '*.h') -Pattern '^\s*(?:void|int|long|float|double|size_t)\s+([a-z_][A-Za-z0-9_]*)\s*\(' |
    ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique
if ($exports.Count -ne 42) {
    throw "expected 42 exported entry points (docs/port/wave0-contracts.md section 3.1), got $($exports.Count)"
}
"EXPORTS" | Set-Content $Def
$exports | ForEach-Object { "    $_" } | Add-Content $Def
Write-Host ("  {0} entry points -> {1}" -f $exports.Count, $Def) -ForegroundColor Green

$Sources = Get-ChildItem $Csrc -Filter *.c | Sort-Object Name
if ($Sources.Count -ne 9) { throw "expected 9 kernel sources, found $($Sources.Count)" }

# ---------------------------------------------------------------- 2. compile
Write-Host "=== [2/3] compile with $Toolchain$(if ($OpenMp) { ' (+OpenMP)' }) ===" -ForegroundColor Cyan
$objects = @()

if ($Toolchain -eq 'gcc') {
    $cc = if ($CC) { $CC } else { (Get-Command gcc -ErrorAction SilentlyContinue).Source }
    if (-not $cc) { $cc = 'D:\tdm-gcc\bin\gcc.exe' }
    if (-not (Test-Path $cc)) { throw "gcc not found (looked on PATH, at the -CC value, and at D:\tdm-gcc\bin\gcc.exe)" }
    Write-Host "compiler: $cc"
    Write-Host ((& $cc --version | Select-Object -First 1))

    $flags = @('-std=gnu11', '-O2', '-Wall', '-c', "-I$Csrc", "-I$Here")
    if ($OpenMp) { $flags += '-fopenmp' }

    foreach ($f in $Sources) {
        $o = Join-Path $Obj ($f.BaseName + '.o')
        $out = & $cc @($flags + @($f.FullName, '-o', $o)) 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            $out | Set-Content (Join-Path $Logs ("compile-fail-" + $f.BaseName + ".log"))
            Write-Host ("  {0,-18} FAILED" -f $f.Name) -ForegroundColor Red
            Write-Host $out
            throw "compile failed for $($f.Name)"
        }
        if ($out.Trim()) { $out | Add-Content (Join-Path $Logs 'warnings.log') }
        Write-Host ("  {0,-18} OK" -f $f.Name) -ForegroundColor Green
        $objects += $o
    }

    # Single interpolated token: PowerShell's comma operator binds tighter than '+',
    # so `@($a,'-Wl,--out-implib,' + $p)` would split into three arguments.
    $dll = Join-Path $Bin 'compositor_pixels.dll'
    $implibArg = "-Wl,--out-implib,$(Join-Path $Bin 'libcompositor_pixels.a')"
    # --no-insert-timestamp zeroes the PE header's Time/Date field, which is otherwise the
    # only thing that changes between two builds of identical sources: without it the DLL's
    # SHA256 is not a usable fingerprint of its contents. (Linux's ld has no such option,
    # so build.sh does not pass it.)
    $link = @('-shared', '-O2', '-o', $dll) + $objects + @($Def, $implibArg, '-static-libgcc', '-Wl,--no-insert-timestamp')
    if ($OpenMp) { $link += '-fopenmp' }
    $out = & $cc @link 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        $out | Set-Content (Join-Path $Logs 'link-fail.log'); Write-Host $out; throw 'link failed'
    }
    if ($out.Trim()) { $out | Add-Content (Join-Path $Logs 'warnings.log') }
}
else {
    # ---- MSVC path. NOT VERIFIED ON THIS MACHINE (no MSVC installed, see
    # ---- docs/port/02-toolchain.md section 3). The flags below are the documented
    # ---- ones; the port itself no longer needs GCC, so this branch is expected to
    # ---- work once Build Tools are present.
    # ----   * /D_USE_MATH_DEFINES  : HealPixels.c:168 uses M_PI
    # ----   * /std:c11             : C99 declarations and variadic macros (VS2019 16.7+)
    # ----   * /openmp              : MSVC's OpenMP 2.0; dispatch_shim.h keeps the band
    # ----                            index signed and never `continue`s out of the
    # ----                            worksharing loop, which OpenMP 2.0 forbids.
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw @'
MSVC toolchain not found: vswhere.exe is absent, i.e. no Visual Studio / Build Tools.
Install (needs an elevated shell, ~4-7 GB, no reboot):
    winget install --exact --id Microsoft.VisualStudio.2022.BuildTools `
      --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
Then re-run:  pwsh -File build.ps1 -Toolchain msvc
'@
    }
    $vsPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if (-not $vsPath) { throw 'vswhere found no VC toolset (Microsoft.VisualStudio.Component.VC.Tools.x86.x64)' }
    $vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found under $vsPath" }
    Write-Host "toolset: $vsPath"

    $dll = Join-Path $Bin 'compositor_pixels.dll'
    $cflags = "/nologo /O2 /W3 /std:c11 /D_USE_MATH_DEFINES /D_CRT_SECURE_NO_WARNINGS /I`"$Csrc`" /I`"$Here`""
    if ($OpenMp) { $cflags += ' /openmp' }
    $clObjs = @()
    foreach ($f in $Sources) {
        $o = Join-Path $Obj ($f.BaseName + '.obj')
        $line = "cl $cflags /c `"$($f.FullName)`" /Fo`"$o`""
        $out = & cmd /c "call `"$vcvars`" >nul 2>&1 && $line" 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            $out | Set-Content (Join-Path $Logs ("compile-fail-" + $f.BaseName + ".log"))
            Write-Host $out; throw "compile failed for $($f.Name)"
        }
        Write-Host ("  {0,-18} OK" -f $f.Name) -ForegroundColor Green
        $clObjs += "`"$o`""
    }
    $linkLine = "link /nologo /DLL /OUT:`"$dll`" /DEF:`"$Def`" " + ($clObjs -join ' ')
    $out = & cmd /c "call `"$vcvars`" >nul 2>&1 && $linkLine" 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        $out | Set-Content (Join-Path $Logs 'link-fail.log'); Write-Host $out; throw 'link failed'
    }
}

# ---------------------------------------------------------------- 3. verify exports
Write-Host '=== [3/3] verify the DLL export table ===' -ForegroundColor Cyan
# $cc is only set by the gcc branch; the MSVC branch leaves it unset, so asking for its directory
# unconditionally raised "Cannot bind argument to parameter 'Path' because it is an empty string" on
# every MSVC build. Absent a compiler path there is simply no objdump to look beside.
$objdump = if ($cc) { Join-Path (Split-Path -Parent $cc) 'objdump.exe' } else { $null }
if (-not $objdump -or -not (Test-Path $objdump)) { $objdump = 'D:\tdm-gcc\bin\objdump.exe' }
$exportLog = Join-Path $Logs 'exports.txt'
if (Test-Path $objdump) {
    # The export table of the linked PE, not the .def we asked for: this is the artifact.
    $dump = & $objdump -p $dll 2>&1 | Out-String
    $dump | Set-Content $exportLog
    $exported = [regex]::Matches($dump, '(?m)^\s+\[\s*\d+\]\s+([A-Za-z_][A-Za-z0-9_]*)\s*$') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    Write-Host ("  DLL exports {0} symbols" -f $exported.Count) -ForegroundColor Green
    "EXPORTED ($($exported.Count)):" | Add-Content $exportLog
    $exported | ForEach-Object { "  $_" } | Add-Content $exportLog
    if ($exported.Count -ne 42) { throw "the linked DLL exports $($exported.Count) symbols, expected 42" }
    $missing = $exports | Where-Object { $exported -notcontains $_ }
    $extra = $exported | Where-Object { $exports -notcontains $_ }
    if ($missing -or $extra) { throw "export mismatch. missing: $missing  extra: $extra" }
    Write-Host '  export set matches the 42 public entry points exactly' -ForegroundColor Green
}
else {
    Write-Host "  objdump not found; skipping symbol table verification (checked the .def instead)" -ForegroundColor Yellow
}

$hash = (Get-FileHash $dll -Algorithm SHA256).Hash
Write-Host ''
Write-Host ("DLL    : {0}" -f $dll)
Write-Host ("size   : {0:N0} bytes" -f (Get-Item $dll).Length)
Write-Host ("sha256 : {0}" -f $hash)
Write-Host ("openmp : {0}" -f [bool]$OpenMp)
