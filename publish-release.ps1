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

$notes = @"
新增：**状态栏常驻显示插件市场状态**。不用再进「设置 → 插件市场」才能知道有没有插件该更新。

## 状态栏显示什么

| 显示 | 含义 |
| --- | --- |
| ``插件市场 1.47.0 · 8 个插件`` | 正常，插件都是最新 |
| ``插件市场：2 个插件可更新`` | **有插件可以更新**（橙色） |
| ``插件市场：正在安装 xxx 1/3`` | 市场正在安装/更新中 |
| ``插件市场：未启用`` | 这个 profile 没装 ``dshmarket`` |

- **鼠标悬停**看明细：哪几个插件、从哪个版本升到哪个版本
- **点一下**打开插件市场

## 它怎么拿到这些

读的是市场自带的**本地只读 API**（``/dsh-market/api/v1/capabilities``、``/dsh-market/status``、
``/dsh-market/api/v1/updates?name=…``），不需要会话令牌，也不改动任何东西。

刷新节奏分开：轻量状态每 8 秒读一次；完整更新扫描每 10 分钟一次（每个插件都可能去问一次
仓库，问太勤不礼貌）。dsh 启动期间市场的路由还没挂上，这时会自动重试，不会误报成"未启用"。

## 选哪个包

| 包 | 大小 | 适用 |
| --- | --- | --- |
| ``dsh-desktop-$version-win-x64.zip`` | 0.7 MB | **推荐**。需要 .NET 8 桌面运行时 |
| ``dsh-desktop-$version-win-x64-selfcontained.zip`` | 62 MB | 零依赖，已内嵌运行时 |

已经在用 1.2.x 的话，直接「一键更新」即可，不用下载这两个包。

## 一键更新怎么工作

Windows 不允许覆盖正在运行的 exe，所以流程是：

``````
下载新包 → 启动更新助手 → 主程序退出（顺带关掉它托管的 dsh 服务）
        → 助手把新文件覆盖进原目录 → 自动重新打开程序
``````

**就地替换，同一个文件夹**，不会再堆出新目录。你自己放进该目录的其它文件不会被删。

菜单 → **检查更新** → 有新版本时点「**一键更新**」，带下载进度。

## 其他既有功能

- 菜单 → **检查更新**：已是最新时显示当前版本，有新版本时给出更新入口
- 关于对话框显示当前版本号
- 命令行 ``--check-update``：检查后退出（0=最新，10=有新版，1=失败），方便脚本化

## 选哪个包

| 包 | 大小 | 适用 |
| --- | --- | --- |
| ``dsh-desktop-$version-win-x64.zip`` | 0.7 MB | **推荐**。需要 .NET 8 桌面运行时 |
| ``dsh-desktop-$version-win-x64-selfcontained.zip`` | 62 MB | 零依赖，已内嵌运行时 |

## 这不是 Harness 本身

本程序只是给 dsh 换了个窗口，运行前 dsh 要能用：

``````powershell
winget install OpenJS.NodeJS.LTS      # 或从 nodejs.org 安装
npm i -g @deepseek-ai/dsh
dsh --version                          # 能打印版本号即可

setx DEEPSEEK_API_KEY "sk-你的key"     # 自备 key
``````

## 从旧版更新

如果你在 **1.1.0 或更早**：那一版还没有一键更新的能力，所以需要手动一次 ——
关闭程序、下载上面的 zip、解压覆盖原目录、再双击程序。之后就能一键更新了。

如果已经在 **1.2.0 及以上**：直接用菜单里的「一键更新」，不用下载这里的包。

会话与设置不在程序目录（在 ``%LOCALAPPDATA%\DshDesktop\`` 与 ``%USERPROFILE%\.dsh\``），覆盖不会丢数据。

## 校验

``````
$sums
``````

## 许可

MIT © 2026 贤余sama
"@

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
