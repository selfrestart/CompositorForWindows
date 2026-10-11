# run_mutation.ps1 -- a control for the parity comparison: make the kernel wrong on
# purpose and check that tools/run_parity.ps1 would have caught it.
#
# A green parity run only means something if the comparison can go red. This copies
# csrc/, changes one arithmetic constant inside a function the parity driver exercises
# (`h += tint * 0.06` -> `0.07` in adjust_camera_raw_calibration), builds that copy into
# its own directory, and re-runs the driver. The bytes must differ, and the first
# difference must land in that function's section.
#
# Exit 0 = the control behaved (the wrong kernel WAS detected). Exit 1 = the parity
# comparison is blind and step 6 of run-tests.ps1 proves nothing.

[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$Here = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)   # native/compositor_pixels
$Root = Split-Path -Parent (Split-Path -Parent $Here)
$Spike = Join-Path $Root 'spikes\c-kernel\build\bin'
$Mut = Join-Path $Here 'build\mutation'
$MutSrc = Join-Path $Mut 'csrc'
$MutBin = Join-Path $Mut 'bin'
$MutDrv = Join-Path $Mut 'driver'
$Driver = Join-Path $Here 'tools\parity_driver.c'
$Baseline = Join-Path $Here 'build\parity-bin\pixels.bin'   # from tools/run_parity.ps1
$BaselineLog = Join-Path $Here 'build\parity-bin\pixels.log'

$py = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $py) { $py = 'D:\Python13\python.exe' }
$cc = (Get-Command gcc -ErrorAction SilentlyContinue).Source
if (-not $cc) { $cc = 'D:\tdm-gcc\bin\gcc.exe' }

if (-not (Test-Path $Baseline)) {
    throw "run the parity test first (missing $Baseline): pwsh -File tools\run_parity.ps1"
}

# ---- 1. a wrong copy of the kernel -------------------------------------------------
Remove-Item -Recurse -Force $Mut -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $MutSrc, $MutDrv | Out-Null
Copy-Item (Join-Path $Here 'csrc\*') $MutSrc -Force

$edited = Join-Path $MutSrc 'AdjustPixels.c'
$script = @"
import io, sys
p = r'$edited'
s = open(p, encoding='utf-8', newline='').read()
old, new = 'h += tint * 0.06;', 'h += tint * 0.07;'
n = s.count(old)
if n != 1:
    sys.exit(f'mutation site: expected 1 occurrence, found {n}')
open(p, 'w', encoding='utf-8', newline='').write(s.replace(old, new))
print('mutated:', old, '->', new)
"@
& $py -c $script
if ($LASTEXITCODE -ne 0) { throw 'could not apply the mutation' }

# ---- 2. build it (same flags as build.ps1, separate directories) --------------------
& (Join-Path $Here 'build.ps1') -SourceDir 'build\mutation\csrc' -OutDir 'build\mutation\bin' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'the mutated build failed; the mutation may not have compiled' }
Write-Host "mutated DLL: $(Join-Path $MutBin 'compositor_pixels.dll')"

# ---- 3. run the same driver against it ---------------------------------------------
$flags = @('-std=gnu11', '-O2', '-Wall', "-I$MutSrc", "-I$Here")
$exe = Join-Path $MutDrv 'parity_mutated.exe'
$out = & $cc @($flags + @($Driver, (Join-Path $MutBin 'libcompositor_pixels.a'), '-o', $exe, '-lm')) 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { Write-Host $out; throw 'compiling the mutated driver failed' }
Copy-Item (Join-Path $MutBin 'compositor_pixels.dll') $MutDrv -Force
$mutatedBin = Join-Path $Mut 'mutated.bin'
Push-Location $MutDrv
& $exe $mutatedBin > (Join-Path $Mut 'mutated.log') 2>&1
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { throw "the mutated driver exited $code" }

# ---- 4. the comparison must notice ------------------------------------------------
$a = [System.IO.File]::ReadAllBytes($Baseline)
$b = [System.IO.File]::ReadAllBytes($mutatedBin)
$ha = (Get-FileHash $Baseline -Algorithm SHA256).Hash
$hb = (Get-FileHash $mutatedBin -Algorithm SHA256).Hash
Write-Host "correct kernel : $ha"
Write-Host "mutated kernel : $hb"
if ($a.Length -ne $b.Length) {
    Write-Host 'RESULT: FAIL -- the control changed the dump length, not just its contents' -ForegroundColor Red
    exit 1
}
if ($ha -eq $hb) {
    Write-Host 'RESULT: FAIL -- the parity comparison is blind: a wrong kernel produced identical bytes' -ForegroundColor Red
    exit 1
}
$first = -1
for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { $first = $i; break } }

# which section does that offset fall in? the dump is [8-byte length][bytes] per section,
# and the hash log lists the sections in the same order.
$sectionAt = '(unknown)'
$offset = 0
foreach ($line in (Select-String -Path $BaselineLog -Pattern 'fnv1a=' | ForEach-Object { $_.Line })) {
    if ($offset -gt $first) { break }
    $sectionAt = $line.Trim()
    $offset += 8 + [int]($line.Trim() -replace '.*\s(\d+)\s+B\s+fnv1a=.*', '$1')
}
Write-Host ("first differing byte at offset {0} ({1} of {2})" -f $first, $first, $a.Length)
Write-Host "  in section: $sectionAt"
if ($sectionAt -notmatch 'adjust_camera_raw_calibration') {
    Write-Host 'RESULT: FAIL -- the difference was not where the mutation was planted' -ForegroundColor Red
    exit 1
}
Write-Host 'RESULT: PASS -- a one-constant change was detected, at the mutated function' -ForegroundColor Green
exit 0
