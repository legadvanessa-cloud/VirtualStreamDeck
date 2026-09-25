# Builds the portable single-file Release exe: Release\VirtualStreamDeck.exe
# Usage: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 [-SkipTests] [-SkipVerify]
# ASCII only (Windows PowerShell 5.1 reads BOM-less scripts as ANSI).
param([switch]$SkipTests, [switch]$SkipVerify)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw ".NET 8 SDK not found (dotnet.exe)." }

$proj = Join-Path $root 'src\VirtualStreamDeck\VirtualStreamDeck.csproj'
$tests = Join-Path $root 'tests\VirtualStreamDeck.Tests\VirtualStreamDeck.Tests.csproj'
$staging = Join-Path $root 'build\publish'
$release = Join-Path $root 'Release'

if (-not $SkipTests) {
    Write-Host '== Unit tests'
    & $dotnet test $tests -p:Platform=x64 -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
}

Write-Host '== Publishing Release x64 (self-contained, single file)'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=true -o $staging -nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$files = Get-ChildItem $staging -File
$extra = $files | Where-Object { $_.Name -ne 'VirtualStreamDeck.exe' }
if ($extra) { throw ("Publish produced files besides the exe: " + (($extra | ForEach-Object Name) -join ', ')) }

New-Item -ItemType Directory -Force $release | Out-Null
Copy-Item (Join-Path $staging 'VirtualStreamDeck.exe') (Join-Path $release 'VirtualStreamDeck.exe') -Force
$exe = Join-Path $release 'VirtualStreamDeck.exe'
$mb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
"$hash  VirtualStreamDeck.exe" | Set-Content -Encoding ascii (Join-Path $release 'SHA256SUMS.txt')
Write-Host "== Release\VirtualStreamDeck.exe  ($mb MB)  SHA256 $hash"

if (-not $SkipVerify) {
    Write-Host '== Self-test of the published exe'
    $report = Join-Path $root 'verify\release-report.txt'
    New-Item -ItemType Directory -Force (Split-Path $report) | Out-Null
    $p = Start-Process -FilePath $exe -ArgumentList @('--verify', '--out', "`"$report`"") -PassThru -Wait
    Get-Content $report | Select-String -Pattern '^(FAIL|WARN|RESULT)'
    if ($p.ExitCode -ne 0) { throw "Self-test failed (exit $($p.ExitCode)). See $report" }
}
