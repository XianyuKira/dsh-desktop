[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

# 用一个全新的安装目录副本测试"零部署"体验
# 直接在同盘 robocopy，避免重复占用空间
$pkg    = 'I:\dsh-bundle\package'
$install = 'I:\dsh-bundle\test-install'
$data    = 'I:\dsh-bundle\test-data'

Write-Host '=== 1. 准备全新安装目录（模拟安装器释放后的结果）==='
if (Test-Path $install) { Remove-Item $install -Recurse -Force -ErrorAction SilentlyContinue }
robocopy $pkg $install /E /SL /NFL /NDL /NJH /NJS /NP /MT:16 | Out-Null
Write-Host "  robocopy 退出码 $LASTEXITCODE"
Write-Host "  $([math]::Round((Get-ChildItem $install -Recurse -File | Measure-Object Length -Sum).Sum/1MB,1)) MB"
Write-Host "  启动器版本: $((Get-Item "$install\DeepSeekHarness.exe").VersionInfo.FileVersion)"
Write-Host "  随包标记  : $(Test-Path "$install\bundled.marker")"
Write-Host "  随包 node : $(Test-Path "$install\runtime\node\node.exe")"
Write-Host "  随包 dsh  : $(Test-Path "$install\runtime\vendor\node_modules\@deepseek-ai\dsh\lib\bin.js")"
Write-Host "  随包插件  : $(Test-Path "$install\runtime\profile-web\package.json")"

Write-Host ''
Write-Host '=== 2. 写数据目录设置 + 预置 API Key（模拟向导填完的结果）==='
if (Test-Path $data) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $data | Out-Null

$settingsDir = Join-Path $env:LOCALAPPDATA 'DshDesktop'
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
$settingsFile = Join-Path $settingsDir 'settings.json'
$backup = "$settingsFile.bak"
if (Test-Path $settingsFile) { Copy-Item $settingsFile $backup -Force }

@{
    dataRoot      = $data
    setupComplete = $true
    preferredPort = 3080
} | ConvertTo-Json | Set-Content -Path $settingsFile -Encoding UTF8
Write-Host "  dataRoot = $data"
Write-Host "  setupComplete = true（跳过向导，直接验证运行）"

Write-Host ''
Write-Host '=== 3. 直接启动，验证零部署是否成立 ==='
$result = Join-Path $env:TEMP 'zero-deploy.txt'
Remove-Item "$result*" -ErrorAction SilentlyContinue
$exe = Join-Path $install 'DeepSeekHarness.exe'
$p = Start-Process -FilePath $exe -ArgumentList @("--selftest=$result", '--port', '31330') -PassThru -WindowStyle Hidden
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 260) { Start-Sleep -Milliseconds 500 }
try { $p.WaitForExit(8000) | Out-Null } catch { }
Write-Host "  自检 exit=$($p.ExitCode)  用时 $([int]$sw.Elapsed.TotalSeconds)s"

if (Test-Path $result) {
    $t = [System.IO.File]::ReadAllText($result, [System.Text.Encoding]::UTF8)
    $t -split "`r?`n" | Where-Object {
        $_ -match '^(exe|command|market label|dom diagnostics|first navigation|server url|status strip)'
    } | ForEach-Object { Write-Host "  $_" }
} else { Write-Host '  没有报告' }

Write-Host ''
Write-Host '=== 4. 数据是否都写在用户选的数据目录（而非 C 盘）==='
Write-Host "  $data 内容:"
Get-ChildItem $data -Force -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $($_.Name)" }
Write-Host "  dsh-home 是否在其中: $(Test-Path (Join-Path $data 'dsh-home'))"
Write-Host "  profile 是否已部署: $(Test-Path (Join-Path $data 'dsh-home\profiles\web\package.json'))"
Write-Host "  webview 缓存是否在其中: $(Test-Path (Join-Path $data 'webview'))"

# 还原设置
if (Test-Path $backup) { Move-Item $backup $settingsFile -Force } else { Remove-Item $settingsFile -Force -ErrorAction SilentlyContinue }
Remove-Item "$result*" -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host '（已还原原有 settings.json）'
