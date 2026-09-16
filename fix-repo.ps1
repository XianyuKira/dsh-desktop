[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
Set-Location "C:\Users\xiany\Desktop\dsh-desktop"

Write-Host '=== remove the one-off helper from the repo ==='
git rm --cached -q commit-final.ps1
Remove-Item "commit-final.ps1" -Force -ErrorAction SilentlyContinue

# 一次性脚本以后也别再混进来
$ignore = [System.IO.File]::ReadAllText((Join-Path $PWD '.gitignore'), [System.Text.Encoding]::UTF8)
if ($ignore -notmatch 'commit-final') {
    $ignore = $ignore.TrimEnd() + "`n`n# 一次性操作脚本（含本机绝对路径，不公开）`ncommit-final.ps1`n"
    [System.IO.File]::WriteAllText((Join-Path $PWD '.gitignore'), $ignore, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host '  .gitignore updated'
}

git add -A
Write-Host '=== staged ==='
git status --short | ForEach-Object { Write-Host "  $_" }

Write-Host '=== privacy audit ==='
$hits = git grep -n -I -e "xiany" -- . 2>&1
if ($hits) { Write-Host '  still contains local paths:'; $hits | ForEach-Object { Write-Host "    $_" } }
else { Write-Host '  no local paths anywhere' }
$tracked = @(git ls-files)
Write-Host "  tracked files: $($tracked.Count)"
$bad = $tracked | Where-Object { $_ -match '(^|/)(bin|obj|app|artifacts)/|settings\.json|\.png$|commit-final' }
if ($bad) { Write-Host "  should not be tracked: $($bad -join ', ')" } else { Write-Host '  no build output, no png, no one-off scripts' }

Write-Host '=== commit ==='
git commit -m "chore: drop accidental one-off script and ignore it" 2>&1 | ForEach-Object { Write-Host "  $_" }

Write-Host '=== push ==='
git push origin main 2>&1 | Out-String | ForEach-Object { if ($_ -match 'main ->') { Write-Host '  pushed' } }

Write-Host '=== final ==='
Write-Host "  local : $(git rev-parse main)"
Write-Host "  remote: $((git ls-remote origin refs/heads/main 2>&1) -split '\s+' | Select-Object -First 1)"
Write-Host "  tree  : $(if (git status --porcelain) { 'dirty' } else { 'clean' })"
Write-Host "  files : $($tracked.Count)"
