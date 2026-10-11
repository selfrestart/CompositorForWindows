# run_parity.ps1 -- prove the standard-C port produces the same pixels as the spike build.
#
#   pwsh -File native/compositor_pixels/tools/run_parity.ps1
#
# Compiles tools/parity_driver.c twice: once against the spike's kernel
# (spikes/c-kernel/build/bin/libcompositor_kernel.a + compositor_kernel.dll, built from
# the same upstream sources with the Clang-blocks/GCC-nested-function transform) and once
# against this port (build/bin/libcompositor_pixels.a + compositor_pixels.dll). The driver
# source is identical, so any difference in the dumped bytes is a difference the port made.
#
# The dump covers every entry point that runs through a band loop (all 22 in_bands sites
# and both direct dispatch_apply sites), across seven geometries that straddle the band
# partition, plus controls from the seven files that needed no port.

[CmdletBinding()]
param(
    # Which port build to compare against the spike. `-PortDir build\bin-openmp` compares
    # the OpenMP build, which is how "parallel and serial agree bit for bit" is checked.
    [string]$PortDir = 'build\bin',
    # Compiler used to build the driver (and whose bin directory supplies libgomp, for an
    # -OpenMp port build). Defaults to the first gcc on PATH.
    [string]$Cc = '',
    # How many differing sections to tolerate. 0 is the rule for a serial build: any
    # difference fails. It is raised only by tools\run_openmp_parity.ps1, because the
    # parallel build does have a small, documented, non-deterministic difference (see
    # README.md "the parallel build").
    [int]$AllowDifferences = 0
)

$ErrorActionPreference = 'Continue'
$Here = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)   # native/compositor_pixels
$Root = Split-Path -Parent (Split-Path -Parent $Here)                          # repo root
$Spike = Join-Path $Root 'spikes\c-kernel\build\bin'
$Mine = if ([System.IO.Path]::IsPathRooted($PortDir)) { $PortDir } else { Join-Path $Here $PortDir }
$Work = Join-Path $Here ('build\parity-' + (Split-Path -Leaf $Mine))
$Driver = Join-Path $Here 'tools\parity_driver.c'

$cc = if ($Cc) { $Cc } else { (Get-Command gcc -ErrorAction SilentlyContinue).Source }
if (-not $cc) { $cc = 'D:\tdm-gcc\bin\gcc.exe' }
if (-not (Test-Path $cc)) { throw 'gcc not found' }
if (-not (Test-Path (Join-Path $Spike 'libcompositor_kernel.a'))) {
    throw "the spike build is missing; run spikes/c-kernel/run-spike.ps1 first (expected $(Join-Path $Spike 'libcompositor_kernel.a'))"
}
if (-not (Test-Path (Join-Path $Mine 'libcompositor_pixels.a'))) {
    throw 'build this port first: pwsh -File native/compositor_pixels/build.ps1'
}

$flags = @('-std=gnu11', '-O2', '-Wall', "-I$(Join-Path $Here 'csrc')", "-I$Here")

# two self-contained directories, each with its own DLL next to its executable
foreach ($side in @('kernel', 'pixels')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Work $side) | Out-Null
}
$jobs = @(
    @{ side = 'kernel'; implib = 'libcompositor_kernel.a'; dll = 'compositor_kernel.dll'; dllname = 'compositor_kernel.dll' },
    @{ side = 'pixels'; implib = 'libcompositor_pixels.a'; dll = 'compositor_pixels.dll'; dllname = 'compositor_pixels.dll' }
)

foreach ($j in $jobs) {
    $dir = Join-Path $Work $j.side
    $exe = Join-Path $dir ("parity_" + $j.side + ".exe")
    $lib = Join-Path ($(if ($j.side -eq 'kernel') { $Spike } else { $Mine })) $j.implib
    $out = & $cc @($flags + @($Driver, $lib, '-o', $exe, '-lm')) 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $out; throw "compiling the $($j.side) driver failed" }
    if ($out.Trim()) { Write-Host $out }
    Copy-Item (Join-Path ($(if ($j.side -eq 'kernel') { $Spike } else { $Mine })) $j.dll) $dir -Force
    if ($j.side -eq 'pixels') {
        # An -OpenMp build links libgomp; put it (and the thread runtime it may need)
        # beside the executable so the comparison works without the compiler's bin
        # directory on PATH.
        $ccDir = Split-Path -Parent $cc
        foreach ($pattern in @('libgomp*.dll', 'libwinpthread*.dll', 'libgcc_s_*.dll')) {
            Get-ChildItem (Join-Path $ccDir $pattern) -ErrorAction SilentlyContinue |
                ForEach-Object { Copy-Item $_.FullName $dir -Force }
        }
    }
    Write-Host "built  : $exe"
}

# ---- run both ----
foreach ($j in $jobs) {
    $dir = Join-Path $Work $j.side
    $exe = Join-Path $dir ("parity_" + $j.side + ".exe")
    $log = Join-Path $Work ($j.side + '.log')
    $bin = Join-Path $Work ($j.side + '.bin')
    Push-Location $dir
    # Explicit UTF-8: Windows PowerShell's `>` redirection writes UTF-16, which the Python
    # helpers that read these logs would then see as garbage.
    & $exe $bin 2>&1 | Out-File -FilePath $log -Encoding utf8
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { throw "parity_$($j.side).exe exited $code (see $log)" }
    Write-Host ("ran    : {0} -> {1} sections, {2:N0} bytes" -f $j.side, (Select-String -Path $log -Pattern 'sections:').Line, (Get-Item $bin).Length)
}

# ---- compare ----
$a = Join-Path $Work 'kernel.bin'
$b = Join-Path $Work 'pixels.bin'
# the per-section hash logs, so a failure can name the section
$na = (Select-String -Path (Join-Path $Work 'kernel.log') -Pattern 'fnv1a=' | ForEach-Object { $_.Line.Trim() })
$nb = (Select-String -Path (Join-Path $Work 'pixels.log') -Pattern 'fnv1a=' | ForEach-Object { $_.Line.Trim() })
$ha = (Get-FileHash $a -Algorithm SHA256).Hash
$hb = (Get-FileHash $b -Algorithm SHA256).Hash
Write-Host ''
Write-Host "kernel.bin sha256 : $ha"
Write-Host "pixels.bin sha256 : $hb"
if ((Get-Item $a).Length -ne (Get-Item $b).Length) {
    Write-Host "RESULT: FAIL -- sizes differ ($((Get-Item $a).Length) vs $((Get-Item $b).Length))" -ForegroundColor Red
    exit 1
}
if ($ha -ne $hb) {
    if ($AllowDifferences -gt 0) {
        Write-Host "the dumps differ; locating the differences (tolerance $AllowDifferences section(s))..." -ForegroundColor Yellow
        $py = (Get-Command python -ErrorAction SilentlyContinue).Source
        if (-not $py) { $py = 'D:\Python13\python.exe' }
        & $py (Join-Path $Here 'tools\compare_parity.py') $a (Join-Path $Work 'kernel.log') $b (Join-Path $Work 'pixels.log') $AllowDifferences
        $cmp = $LASTEXITCODE
        Write-Host ("sections compared : {0}" -f $na.Count)
        if ($cmp -eq 0) {
            Write-Host 'RESULT: PASS WITH KNOWN DIFFERENCES -- within the documented tolerance' -ForegroundColor Yellow
            exit 0
        }
        Write-Host 'RESULT: FAIL -- more differences than the documented tolerance' -ForegroundColor Red
        exit 1
    }
    Write-Host 'RESULT: FAIL -- the two builds disagree' -ForegroundColor Red
    & D:\Python13\python.exe -c @"
import sys
a = open(r'$a','rb').read(); b = open(r'$b','rb').read()
for i,(x,y) in enumerate(zip(a,b)):
    if x != y:
        print(f'first differing byte at {i}: spike={x} port={y}'); break
"@
    exit 1
}
$diff = Compare-Object $na $nb
Write-Host ("sections compared : {0}" -f $na.Count)
if ($diff) { Write-Host 'RESULT: FAIL -- section hashes differ' -ForegroundColor Red; $diff | Format-Table -AutoSize; exit 1 }
Write-Host 'RESULT: PASS -- every dumped byte is identical between the spike build and this port' -ForegroundColor Green
