# Build release packages: framework-dependent zip + self-contained zip, plus SHA256SUMS.txt.
# ASCII only on purpose: PowerShell 5.1 misreads UTF-8 without a BOM, and this script
# must survive being edited by tools that drop the BOM.
#
#   .\make-release.ps1                          packages app\ (after build.ps1)
#   .\make-release.ps1 -FrameworkDir <dir>      packages <dir> instead; needed when the
#                                               app is running and app\ cannot be replaced

param(
    [string]$FrameworkDir = 'app',
    [string]$SelfContainedDir = 'artifacts\selfcontained',
    [switch]$SkipPreflight
)

$ErrorActionPreference = 'Stop'
$proj = $PSScriptRoot
Set-Location $proj

# ---------- version comes from the csproj, the single source of truth ----------
$csproj = Join-Path $proj 'src\DeepSeekHarness.csproj'
$csprojText = [System.IO.File]::ReadAllText($csproj, [System.Text.Encoding]::UTF8)
$version = [regex]::Match($csprojText, '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $version) { throw 'cannot read <Version> from csproj' }
Write-Host "version: $version"

# ---------- preflight ----------
Write-Host '=== 0. preflight ==='
if (-not $SkipPreflight) {
    $dirty = git status --porcelain
    if ($dirty) {
        Write-Host 'working tree has uncommitted changes:'
        $dirty | ForEach-Object { Write-Host "  $_" }
        throw 'commit first, so the package matches the tag'
    }
    Write-Host '  working tree clean'

    git rev-parse -q --verify "refs/tags/v$version" | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "tag v$version already exists; bump <Version> first" }
    Write-Host "  tag v$version is free"
} else {
    Write-Host '  preflight skipped'
}

$frameDir = if ([System.IO.Path]::IsPathRooted($FrameworkDir)) { $FrameworkDir } else { Join-Path $proj $FrameworkDir }
$scDir = if ([System.IO.Path]::IsPathRooted($SelfContainedDir)) { $SelfContainedDir } else { Join-Path $proj $SelfContainedDir }

if (-not (Test-Path (Join-Path $frameDir 'DeepSeekHarness.exe'))) {
    throw "framework-dependent build not found in $frameDir; run build.ps1 first"
}
Write-Host "  framework source: $frameDir"

$packagedVersion = (Get-Item (Join-Path $frameDir 'DeepSeekHarness.exe')).VersionInfo.FileVersion
Write-Host "  packaged exe version: $packagedVersion"
if ($packagedVersion -notlike "$version*") {
    throw "packaged exe is $packagedVersion but csproj says $version; rebuild before packaging"
}

# Self-update needs the helper next to the app, so a package without it cannot update itself.
if (-not (Test-Path (Join-Path $frameDir 'DshDesktopUpdater.exe'))) {
    throw "DshDesktopUpdater.exe is missing from $frameDir; run build.ps1 (it publishes the helper)"
}
Write-Host '  update helper present'

# Prove the helper actually runs before anyone downloads it. A lone updater exe once shipped
# without its dependency dll and died at startup with 0x8000809A, which no library-level test
# could catch. The helper's own --selftest launches a real process and swaps a real locked exe.
Write-Host '  running the update helper self-test'
$probeDir = Join-Path $env:TEMP ('updprobe-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
Get-ChildItem $frameDir -File | Copy-Item -Destination $probeDir -Force
try {
    $probe = Start-Process -FilePath (Join-Path $probeDir 'DshDesktopUpdater.exe') -ArgumentList '--selftest' -PassThru -Wait
    if ($probe.ExitCode -ne 0) {
        $report = Join-Path $env:TEMP 'dsh-desktop-updater-selftest.log'
        if (Test-Path $report) { Get-Content $report -Encoding UTF8 | ForEach-Object { Write-Host "    $_" } }
        throw "the update helper self-test failed (exit $($probe.ExitCode)); refusing to package an updater that cannot update"
    }
    Write-Host '    helper self-test passed'
}
finally {
    Remove-Item $probeDir -Recurse -Force -ErrorAction SilentlyContinue
}

$stage = Join-Path $proj 'artifacts\stage'
$outDir = Join-Path $proj 'artifacts\release'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage, $outDir | Out-Null

# ---------- bundled README.txt ----------
$readme = @"
DeepSeek Harness Desktop v$version
=================================

Puts the dsh web UI in its own window. No browser involved.

[This package is not Harness itself]
It only gives dsh a window, so dsh must already work:

  1. Install Node.js (>= 20)        https://nodejs.org
  2. npm i -g @deepseek-ai/dsh      check: dsh --version
  3. Provide a model credential (your own DeepSeek API key):
     setx DEEPSEEK_API_KEY "sk-..."
     or run 'dsh web' once and fill it in there.

[How to run]
Double-click DeepSeekHarness.exe. First start takes about 5-10 seconds
while dsh loads its plugin tree; the window shows progress meanwhile.

To pin it to the desktop: right-click the exe -> Send to -> Desktop (create shortcut).

[What this build needs]
- Windows 10 / 11
- .NET 8 Desktop Runtime (if missing, Windows offers the download)
- Edge WebView2 Runtime (already present on Win11 and most Win10)

[Notes]
- SmartScreen may warn on first run (the exe is unsigned): More info -> Run anyway.
- Closing the window ends this dsh server. Any dsh web you started yourself is unaffected.
- Default port is 3080; if it is taken, a free port is chosen automatically.

Repository: https://github.com/XianyuKira/dsh-desktop
License: MIT
"@
$readme | Out-File (Join-Path $stage 'README.txt') -Encoding UTF8

# ---------- 1. framework-dependent ----------
Write-Host '=== 1. framework-dependent package ==='
$zipA = Join-Path $outDir "dsh-desktop-$version-win-x64.zip"
Copy-Item (Join-Path $frameDir '*') $stage -Recurse -Force
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipA -Force
Write-Host ("  {0:N2} MB -> {1}" -f ((Get-Item $zipA).Length / 1MB), (Split-Path $zipA -Leaf))

# ---------- 2. self-contained ----------
Remove-Item $stage -Recurse -Force
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Write-Host '=== 2. self-contained package ==='
# Copy the whole self-contained tree, not just one file: self-update needs the helper
# next to the app, and a single-file build would hide it inside the bundle.
foreach ($required in @('DeepSeekHarness.exe', 'DshDesktopUpdater.exe')) {
    if (-not (Test-Path (Join-Path $scDir $required))) {
        throw "$required is missing from $scDir; publish the self-contained build with the updater"
    }
}
Copy-Item (Join-Path $scDir '*') $stage -Recurse -Force
$readmeBig = $readme -replace '\[What this build needs\][\s\S]*?\[Notes\]', @"
[What this build needs]
- Windows 10 / 11
- .NET runtime is bundled: nothing else to install
- Edge WebView2 Runtime only (already present on Win11 and most Win10)

[Notes]
"@
$readmeBig | Out-File (Join-Path $stage 'README.txt') -Encoding UTF8
$zipB = Join-Path $outDir "dsh-desktop-$version-win-x64-selfcontained.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipB -Force
Write-Host ("  {0:N2} MB -> {1}" -f ((Get-Item $zipB).Length / 1MB), (Split-Path $zipB -Leaf))

# ---------- 3. checksums ----------
Write-Host '=== 3. SHA256 ==='
$sums = foreach ($z in @($zipA, $zipB)) {
    $hash = (Get-FileHash $z -Algorithm SHA256).Hash.ToLower()
    Write-Host "  $(Split-Path $z -Leaf)"
    Write-Host "    $hash"
    "$hash  $(Split-Path $z -Leaf)"
}
$sums | Out-File (Join-Path $outDir 'SHA256SUMS.txt') -Encoding ASCII

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '=== done: artifacts\release ==='
Get-ChildItem $outDir | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
