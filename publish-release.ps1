# Publish a GitHub release for the current csproj version, using the credential
# already stored by Git Credential Manager (git push authorized it once).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$owner = 'XianyuKira'
$repo = 'dsh-desktop'

$csprojText = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'src\DeepSeekHarness.csproj'), [System.Text.Encoding]::UTF8)
$version = [regex]::Match($csprojText, '<Version>([^<]+)</Version>').Groups[1].Value
$tag = "v$version"
Write-Host "version: $version  tag: $tag"

# Git Credential Manager reads its query from stdin and wants a terminating blank line.
# Writing to the child's StandardInput proved unreliable here (git answers "missing protocol
# field"), so the query goes to a temp file and cmd performs the redirection, which works.
$queryFile = Join-Path $env:TEMP ("gcm-query-" + [guid]::NewGuid().ToString('N').Substring(0, 8) + ".txt")
[System.IO.File]::WriteAllText($queryFile, "protocol=https`nhost=github.com`n`n", (New-Object System.Text.UTF8Encoding($false)))

$token = $null
try {
    $out = cmd.exe /c "git credential fill < `"$queryFile`"" 2>&1 | Out-String
    foreach ($line in ($out -split "`n")) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('password=')) { $token = $trimmed.Substring('password='.Length).Trim() }
    }
}
finally {
    Remove-Item $queryFile -Force -ErrorAction SilentlyContinue
}
if (-not $token) { throw 'no GitHub credential available from GCM' }
$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'dsh-desktop-release'
}
function Redact([string]$t) { if ($t) { $t.Replace($token, '***') } else { $t } }

$sums = Get-Content (Join-Path $PSScriptRoot 'artifacts\release\SHA256SUMS.txt') -Raw

# 发布说明从同目录的 release-notes-<version>.txt 读取，避免把长文案写进脚本
$notesPath = Join-Path $PSScriptRoot "release-notes-$version.txt"
if (Test-Path $notesPath) {
    $notes = [System.IO.File]::ReadAllText($notesPath, [System.Text.Encoding]::UTF8)
} else {
    $notes = "DeepSeek Harness 桌面版 $version"
    Write-Host "  提示: 未找到 $notesPath，使用简短说明"
}

# ---------- 1. tag ----------
Write-Host "=== 1. tag $tag ==="
# git writes progress to stderr; under ErrorActionPreference=Stop that aborts the script,
# so the git calls run with a relaxed preference.
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
git rev-parse -q --verify "refs/tags/$tag" | Out-Null
if ($LASTEXITCODE -ne 0) {
    git tag -a $tag -m "Release $tag"
    Write-Host '  created locally'
} else {
    Write-Host '  tag already exists locally'
}
$remoteTag = git ls-remote --tags origin "refs/tags/$tag" 2>$null
if ($remoteTag) {
    Write-Host '  tag already on the remote'
} else {
    git push origin $tag 2>&1 | ForEach-Object { Write-Host "  $_" }
}
$ErrorActionPreference = $previous

# ---------- 2. release ----------
Write-Host '=== 2. release ==='
$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/tags/$tag" -Headers $headers -TimeoutSec 60
    Write-Host "  already exists: $($release.html_url)"
} catch {
    $code = $null; try { $code = $_.Exception.Response.StatusCode.value__ } catch { }
    if ($code -ne 404) { Write-Host "  lookup returned HTTP $code" }
}

if (-not $release) {
    $body = @{
        tag_name         = $tag
        target_commitish = 'main'
        name             = "DeepSeek Harness 桌面版 $tag"
        body             = $notes
        draft            = $false
        prerelease       = $false
    } | ConvertTo-Json -Compress
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases" -Method Post `
            -Headers $headers -ContentType 'application/json' `
            -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 120
        Write-Host "  created: $($release.html_url)"
    } catch {
        $code = $null; try { $code = $_.Exception.Response.StatusCode.value__ } catch { }
        Write-Host "  FAILED HTTP $code : $(Redact $_.Exception.Message)"
        try {
            $rd = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
            Write-Host "  detail: $(Redact $rd.ReadToEnd())"
        } catch { }
        exit 1
    }
}

# ---------- 3. assets ----------
Write-Host '=== 3. upload assets ==='
$assets = @(
    "artifacts\release\dsh-desktop-$version-win-x64.zip",
    "artifacts\release\dsh-desktop-$version-win-x64-selfcontained.zip",
    'artifacts\release\SHA256SUMS.txt'
)
foreach ($rel in $assets) {
    $file = (Resolve-Path $rel).Path
    $name = Split-Path $file -Leaf
    $sizeMb = [math]::Round((Get-Item $file).Length / 1MB, 2)
    $uploadUrl = "$($release.upload_url -replace '\{\?name,label\}', '')?name=$([uri]::EscapeDataString($name))"
    $ctype = if ($name -like '*.zip') { 'application/zip' } else { 'text/plain' }
    try {
        $r = Invoke-RestMethod -Uri $uploadUrl -Method Post -Headers $headers `
            -ContentType $ctype -InFile $file -TimeoutSec 1200
        Write-Host ("  OK   {0} ({1} MB)" -f $name, $sizeMb)
    } catch {
        Write-Host "  FAIL $name : $(Redact $_.Exception.Message)"
    }
}

# ---------- 4. verify ----------
Write-Host '=== 4. final state ==='
$final = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/tags/$tag" -Headers $headers -TimeoutSec 60
Write-Host "  $($final.html_url)"
$final.assets | ForEach-Object { Write-Host ("    {0}  {1:N2} MB" -f $_.name, ($_.size / 1MB)) }
