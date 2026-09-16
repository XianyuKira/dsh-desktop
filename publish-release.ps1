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

$cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
$token = ($cred | Where-Object { $_ -match '^password=' }) -replace '^password=', ''
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
修掉一个会让「检查更新」莫名失败的缺陷。

## 修了什么

GitHub 对**未认证**的 API 请求按 **IP** 限每小时 60 次，同 IP 所有人共用这一份。
配额一耗尽，「检查更新」就失败，而原来的提示是"连不上 GitHub"——把用户引向完全错误的排查方向。

现在：

- 程序会复用 **Git Credential Manager 里已授权的凭据**（就是 ``git push`` 时授权过的那份），
  配额从 **60 次/小时（按 IP）** 变成 **5000 次/小时（按你的账号）**，不再被同 IP 的其它请求挤掉
- 机器上没有该凭据时自动退回匿名请求，行为与之前一致
- 遇到 403/429 时明确提示"配额已用尽"并给出恢复时间，不再误报成网络问题
- ``--check-update`` 增加 ``auth`` 一行，一眼看出走的是匿名还是带凭据

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
