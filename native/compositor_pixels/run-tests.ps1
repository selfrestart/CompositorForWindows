# run-tests.ps1 -- reproduce every acceptance check for native/compositor_pixels.
#
#   pwsh -File native/compositor_pixels/run-tests.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File native/compositor_pixels/run-tests.ps1
#
# Steps (all of them read the read-only upstream sources; nothing is written outside
# native/compositor_pixels/):
#   1. csrc/ is exactly what tools/port_blocks.py produces from upstream
#   2. every line that differs from upstream is band plumbing (tools/diff_vs_upstream.py)
#   3. build the DLL and verify the linked PE exports exactly 42 entry points
#   4. shim self-test: the band partition, both forms, serial
#   5. the ten numeric checks against the ported DLL (probe, net8.0)
#   6. bit-for-bit parity with the spike build over every band-loop entry point
#   7. the -OpenMp build: compile-verified, and run-verified on a compiler that ships libgomp
#   8. mutation control: a deliberately wrong kernel must make step 6 fail
#
# Every child script runs as a separate process (`<host> -File ...`), because an in-process
# invocation of a .ps1 does not reliably report its exit code, and a green step must mean
# a green step.
#
# Exit code 0 means every step passed.

[CmdletBinding()]
param([switch]$SkipOpenMp, [switch]$SkipMutation)

$ErrorActionPreference = 'Continue'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path $Here 'tools'

$scriptHost = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $scriptHost) { $scriptHost = (Get-Command powershell -ErrorAction SilentlyContinue).Source }
if (-not $scriptHost) { throw 'neither pwsh nor powershell found' }
Write-Host "script host : $scriptHost"

$py = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $py) { $py = 'D:\Python13\python.exe' }
if (-not (Test-Path $py)) { throw 'python not found (needed by the port and diff scripts)' }

$cc = (Get-Command gcc -ErrorAction SilentlyContinue).Source
if (-not $cc) { $cc = 'D:\tdm-gcc\bin\gcc.exe' }
if (-not (Test-Path $cc)) { throw 'gcc not found' }

# docs/port/wave0-contracts.md section 0: every dotnet command needs DOTNET_ROOT.
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = 'C:\Program Files\dotnet' }

$buildPs1 = Join-Path $Here 'build.ps1'
$probeProject = Join-Path $Here 'probe\KernelProbe.csproj'
$probeExe = Join-Path $Here 'probe\bin\Release\net8.0\KernelProbe.exe'
$dllSerial = Join-Path $Here 'build\bin\compositor_pixels.dll'

$script:Results = New-Object System.Collections.ArrayList
$script:Captured = ''

function Run {
    param([string]$Label, [string]$File, [string[]]$Arguments = @(), [switch]$Capture)
    Write-Host ''
    Write-Host ('=' * 96) -ForegroundColor Cyan
    Write-Host "STEP: $Label" -ForegroundColor Cyan
    Write-Host ('=' * 96) -ForegroundColor Cyan
    if ($Capture) {
        $text = & $File @Arguments 2>&1 | Out-String
        Write-Host $text
        $script:Captured = $text
    }
    else {
        # `| Out-Host` keeps the child's output off this function's return value: a PowerShell
        # function returns everything written to its output stream, which would otherwise turn
        # the caller's exit-code variable into an array of strings.
        & $File @Arguments 2>&1 | Out-Host
    }
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 1 }
    [void]$script:Results.Add([pscustomobject]@{ Step = $Label; Exit = $code })
    Write-Host ("--> {0}: exit {1}" -f $(if ($code -eq 0) { 'PASS' } else { 'FAIL' }), $code) `
        -ForegroundColor $(if ($code -eq 0) { 'Green' } else { 'Red' })
    return $code
}

function Step-Script {
    param([string]$Label, [string]$Script, [string[]]$Arguments = @())
    Run -Label $Label -File $scriptHost -Arguments (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script) + $Arguments)
}

function Fail-IfNotZero {
    param([int]$Code)
    if ($Code -ne 0) {
        Write-Host ''
        Write-Host ('=' * 96)
        Write-Host 'SUMMARY (aborted early)'
        Write-Host ('=' * 96)
        $script:Results | Format-Table -AutoSize
        Write-Host 'RESULT: FAIL' -ForegroundColor Red
        exit 1
    }
}

$code = Run -Label '1. csrc/ is byte-identical to what the port script produces' `
    -File $py -Arguments @((Join-Path $tools 'port_blocks.py'), '--check')
Fail-IfNotZero $code

$code = Run -Label '2. every line that differs from upstream is band plumbing' `
    -File $py -Arguments @((Join-Path $tools 'diff_vs_upstream.py'))
Fail-IfNotZero $code

$code = Step-Script -Label '3. build (serial) and verify the 42 exports of the linked DLL' -Script $buildPs1
Fail-IfNotZero $code

$selftest = Join-Path $tools 'shim_selftest.c'
$exe = Join-Path $Here 'build\shim_selftest_serial.exe'
$code = Run -Label '4a. compile the shim self-test' -File $cc `
    -Arguments @('-std=gnu11', '-O2', '-Wall', "-I$Here", $selftest, '-o', $exe)
Fail-IfNotZero $code
$code = Run -Label '4. shim self-test: band partition, macro and callback forms, serial' -File $exe
Fail-IfNotZero $code

$code = Run -Label '5a. build the probe (net8.0)' -File 'dotnet' `
    -Arguments @('build', $probeProject, '-c', 'Release', '--nologo')
Fail-IfNotZero $code
$env:COMPOSITOR_PIXELS_DLL = $dllSerial
$code = Run -Label '5. the ten numeric checks against the ported DLL' -File $probeExe -Capture
$env:COMPOSITOR_PIXELS_DLL = $null
if ($script:Captured -notmatch '==== 10 passed, 0 failed ====') {
    Write-Host 'the probe did not report "10 passed, 0 failed"' -ForegroundColor Red
    [void]$script:Results.Add([pscustomobject]@{ Step = '5. probe result'; Exit = 1 })
    Fail-IfNotZero 1
}

$code = Step-Script -Label '6. bit-for-bit parity with the spike build' -Script (Join-Path $tools 'run_parity.ps1')
Fail-IfNotZero $code

if (-not $SkipOpenMp) {
    $code = Step-Script -Label '7. the -OpenMp (parallel) build: compile and, where possible, run' `
        -Script (Join-Path $tools 'run_openmp_parity.ps1')
    Fail-IfNotZero $code
}

if (-not $SkipMutation) {
    $code = Step-Script -Label '8. mutation control: a wrong kernel must make step 6 fail' `
        -Script (Join-Path $tools 'run_mutation.ps1')
    Fail-IfNotZero $code
}

Write-Host ''
Write-Host ('=' * 96)
Write-Host 'SUMMARY'
Write-Host ('=' * 96)
$script:Results | Format-Table -AutoSize
$failed = @($script:Results | Where-Object { $_.Exit -ne 0 })
Write-Host ("{0}/{1} steps passed" -f ($script:Results.Count - $failed.Count), $script:Results.Count)
if ($failed.Count) {
    Write-Host 'RESULT: FAIL' -ForegroundColor Red
    exit 1
}
Write-Host 'RESULT: ALL STEPS PASSED' -ForegroundColor Green
exit 0
