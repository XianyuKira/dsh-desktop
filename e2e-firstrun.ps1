[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
$work = 'I:\dsh-bundle'
$install = "$work\e2e-install"
$data = "$work\e2e-data"
$sf = Join-Path $env:LOCALAPPDATA 'DshDesktop\settings.json'
$backup = "$sf.e2e-backup"

Write-Host '=== 1. 备份你正在用的配置（测完还原）==='
if (Test-Path $sf) { Copy-Item $sf $backup -Force; Write-Host "  已备份 -> $backup" }
Write-Host '  原内容:'
Get-Content $sf -Raw -EA SilentlyContinue | ForEach-Object { "    $_" }

Write-Host ''
Write-Host '=== 2. 模拟"用户已填完 API Key"（setupComplete=true，数据目录不变）==='
@{
    dataRoot      = $data
    setupComplete = $true
    preferredPort = 31340
} | ConvertTo-Json | Set-Content -Path $sf -Encoding UTF8
Write-Host '  已写入'

Write-Host ''
Write-Host '=== 3. 首次运行（会自动部署随包插件到数据目录）==='
$result = Join-Path $env:TEMP 'e2e-first-run.txt'
Remove-Item "$result*" -EA SilentlyContinue
$exe = Join-Path $install 'DeepSeekHarness.exe'
$p = Start-Process -FilePath $exe -ArgumentList @("--selftest=$result", '--port', '31340') -PassThru -WindowStyle Hidden
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 300) { Start-Sleep -Milliseconds 500 }
try { $p.WaitForExit(8000) | Out-Null } catch { }
Write-Host "  自检 exit=$($p.ExitCode)  用时 $([int]$sw.Elapsed.TotalSeconds) 秒"

if (Test-Path $result) {
    $t = [System.IO.File]::ReadAllText($result, [System.Text.Encoding]::UTF8)
    $t -split "`r?`n" | Where-Object {
        $_ -match '^(exe|command|market label|first navigation|server url|status strip)|packaged install|first run|provision|setup complete'
    } | ForEach-Object { Write-Host "  $_" }
} else { Write-Host '  没有报告 ✗' }

Write-Host ''
Write-Host '=== 4. 数据是否落在指定目录 ==='
"  $data"
Get-ChildItem $data -Force -EA SilentlyContinue | ForEach-Object { "    $($_.Name)" }
$checks = [ordered]@{
    'dsh-home 已建立'        = Test-Path "$data\dsh-home"
    '插件 profile 已部署'    = Test-Path "$data\dsh-home\profiles\web\package.json"
    'pnpm 链接结构保留'      = Test-Path "$data\dsh-home\profiles\web\node_modules\.pnpm"
    '插件市场包已到位'       = Test-Path "$data\dsh-home\profiles\web\node_modules\dshmarket"
    '余额鲸鱼包已到位'       = Test-Path "$data\dsh-home\profiles\web\node_modules\dsh-whale-widget"
    'webview 缓存在数据目录' = Test-Path "$data\webview"
    '日志在数据目录'         = Test-Path "$data\logs"
}
foreach ($k in $checks.Keys) { "  {0,-24} {1}" -f $k, $(if ($checks[$k]) { '✓' } else { '✗' }) }

$used = [math]::Round((Get-ChildItem $data -Recurse -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum/1MB,1)
"  数据目录体积: $used MB"

Write-Host ''
Write-Host '=== 5. 还原你的配置 ==='
if (Test-Path $backup) { Move-Item $backup $sf -Force; Write-Host '  已还原' }
Get-Content $sf -Raw -EA SilentlyContinue | ForEach-Object { "    $_" }
Remove-Item "$result*" -Force -EA SilentlyContinue
