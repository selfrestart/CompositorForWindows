# run-spike.ps1 -- end-to-end: compile the C kernel, then run the C# P/Invoke probe.
#
# Prerequisites actually present on this machine:
#   * gcc            D:\tdm-gcc\bin\gcc.exe   (tdm64-1 / GCC 10.3.0, x86_64-w64-mingw32)
#   * .NET SDK       7.0.304
#   * python         D:\Python13\python.exe
# Nothing from src/ is modified: the 9 .c/.h files are copied to csrc/ and hashed.
#
# Usage:  powershell -ExecutionPolicy Bypass -File run-spike.ps1

[CmdletBinding()]
param([switch]$SkipNative, [switch]$SkipDotnet)

$ErrorActionPreference = 'Continue'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host ''
Write-Host '########## 1/3  native kernel -> compositor_kernel.dll ##########' -ForegroundColor Yellow
if (-not $SkipNative) {
    & (Join-Path $Here 'build-kernel.ps1') -Clean
    if ($LASTEXITCODE -ne 0) { throw "build-kernel.ps1 failed ($LASTEXITCODE)" }
}

Write-Host ''
Write-Host '########## 2/3  build the C# probe ##########' -ForegroundColor Yellow
$proj = Join-Path $Here 'KernelProbe\KernelProbe.csproj'
if (-not $SkipDotnet) {
    Push-Location (Join-Path $Here 'KernelProbe')
    dotnet build -v:m
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'dotnet build failed' }
    Pop-Location
}

Write-Host ''
Write-Host '########## 3/3  run the probe ##########' -ForegroundColor Yellow
Push-Location (Join-Path $Here 'KernelProbe')
dotnet run --no-build
$rc = $LASTEXITCODE
Pop-Location

Write-Host ''
if ($rc -eq 0) { Write-Host 'SPIKE RESULT: ALL CHECKS PASSED' -ForegroundColor Green }
else { Write-Host "SPIKE RESULT: FAILURES PRESENT (exit $rc)" -ForegroundColor Red }
exit $rc
