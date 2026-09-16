$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
Set-Location "C:\Users\xiany\Desktop\dsh-desktop"

Remove-Item "verify-v11.ps1" -Force -ErrorAction SilentlyContinue

Write-Host '=== renormalize with the new .gitattributes ==='
git add --renormalize . 2>&1 | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
git add -A
Write-Host '=== staged ==='
git status --short | ForEach-Object { Write-Host "  $_" }

Write-Host '=== privacy audit ==='
$bad = @(git ls-files) | Where-Object { $_ -match '(^|/)(bin|obj|app|artifacts)/|settings\.json|\.png$' }
if ($bad) { Write-Host "  tracked but should not be: $($bad -join ', ')" } else { Write-Host '  clean' }
$hits = git grep -n -I -e "xiany" -- . 2>&1
if ($hits) { Write-Host '  contains local path:'; $hits | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" } }
else { Write-Host '  no local paths' }

Write-Host '=== commit ==='
$msg = @"
chore: add release scripts, stop ps1 files showing a false diff

- publish-release.ps1: create the tag, publish the GitHub release and upload
  assets, reusing the credential Git Credential Manager already holds
- .gitattributes: mark *.ps1 as -text so git stops normalizing them. These
  scripts need a UTF-8 BOM (PowerShell 5.1 misreads Chinese without one), and
  normalization strips it, which made every edit look like an uncommitted change
- README: document the release script
"@
$msg | Out-File "$env:TEMP\cm.txt" -Encoding UTF8
git commit -F "$env:TEMP\cm.txt" 2>&1 | ForEach-Object { Write-Host "  $_" }
Remove-Item "$env:TEMP\cm.txt" -Force -ErrorAction SilentlyContinue

Write-Host '=== push ==='
git push origin main 2>&1 | Where-Object { $_ -match 'main ->|rejected|error|fatal' } | ForEach-Object { Write-Host "  $($_.Trim())" }

Write-Host '=== state ==='
$st = git status --porcelain
Write-Host "  working tree: $(if ($st) { ($st -join '; ') } else { 'clean' })"
Write-Host "  local : $(git rev-parse main)"
Write-Host "  remote: $((git ls-remote origin refs/heads/main 2>&1) -split '\s+' | Select-Object -First 1)"
