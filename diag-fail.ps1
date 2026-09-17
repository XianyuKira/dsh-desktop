[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
$exe = 'C:\Users\xiany\Desktop\dsh-desktop\app\DeepSeekHarness.exe'
$iso = 'I:\dsh-bundle\diag-iso'
Remove-Item $iso -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory -Force -Path "$iso\data" | Out-Null
@{
    dataRoot = "$iso\data"; setupComplete = $true; preferredPort = 31430
} | ConvertTo-Json | Set-Content "$iso\settings.json" -Encoding UTF8

Write-Host '=== 复现启动失败（隔离环境，看启动日志）==='
$result = Join-Path $env:TEMP 'diag-selftest.txt'
Remove-Item "$result*" -EA SilentlyContinue
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = "--selftest=`"$result`" --port 31430"
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['DSH_DESKTOP_CONFIG'] = "$iso\settings.json"
$psi.EnvironmentVariables['DSH_DESKTOP_DATA_ROOT'] = "$iso\data"
$p = [System.Diagnostics.Process]::Start($psi)
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 200) { Start-Sleep -Milliseconds 500 }
try { $p.WaitForExit(8000) | Out-Null } catch { }
Write-Host "  exit=$($p.ExitCode) 用时 $([int]$sw.Elapsed.TotalSeconds)s"

if (Test-Path $result) {
    $t = [System.IO.File]::ReadAllText($result, [System.Text.Encoding]::UTF8)
    Write-Host '  --- 报告 ---'
    ($t -split "`r?`n" | Select-Object -First 45) | ForEach-Object { Write-Host "    $_" }
} else { Write-Host '  没有报告' }

Write-Host ''
Write-Host '=== 当前端口占用 ==='
foreach ($port in @(3080,31420,31430)) {
    $l = Get-NetTCPConnection -LocalPort $port -State Listen -EA SilentlyContinue
    $owner = if ($l) { ($l | Select-Object -First 1).OwningProcess } else { $null }
    $name = if ($owner) { (Get-CimInstance Win32_Process -Filter "ProcessId=$owner" -EA SilentlyContinue).Name } else { '' }
    Write-Host "  $port : $(if ($l) { "占用 pid $owner ($name)" } else { '空闲' })"
}
Write-Host ''
Write-Host '=== 系统 Node 是否可用（非打包版要用它）==='
Write-Host "  node: $((Get-Command node -EA SilentlyContinue).Source)"
Write-Host "  全局 dsh bin.js 存在: $(Test-Path "$env:APPDATA\npm\node_modules\@deepseek-ai\dsh\lib\bin.js")"
Remove-Item $iso -Recurse -Force -EA SilentlyContinue
Remove-Item "$result*" -Force -EA SilentlyContinue
