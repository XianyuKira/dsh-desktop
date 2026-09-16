# 端到端验收：发布版程序能否独立拉起 dsh 并把界面渲染进窗口。
# 用法：.\verify-selftest.ps1

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

$proj = $PSScriptRoot
$exe = Join-Path $proj 'app\DeepSeekHarness.exe'
$result = Join-Path $proj 'selftest-report.txt'
Remove-Item "$result*" -ErrorAction SilentlyContinue

if (-not (Test-Path $exe)) { throw "未找到发布版程序：$exe（请先运行 build.ps1）" }

"=== 1. 桌面快捷方式 ==="
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeepSeek Harness.lnk'
if (Test-Path $lnk) {
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk)
    "  快捷方式 : $lnk"
    "  目标     : $($s.TargetPath)"
    "  存在     : $(Test-Path $s.TargetPath)"
} else {
    "  未找到桌面快捷方式"
}

"=== 2. 运行自检（离屏，不打扰用户）==="
$proc = Start-Process -FilePath $exe -ArgumentList @("--selftest=$result") -PassThru -WindowStyle Hidden
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $proc.HasExited -and $sw.Elapsed.TotalSeconds -lt 150) { Start-Sleep -Milliseconds 300 }
try { $proc.WaitForExit(5000) | Out-Null } catch { }
"  退出码   : $($proc.ExitCode)"
"  用时     : $([int]$sw.Elapsed.TotalSeconds) 秒"

"=== 3. 自检报告 ==="
if (Test-Path $result) {
    [System.IO.File]::ReadAllText($result, [System.Text.Encoding]::UTF8)
} else {
    "  没有生成报告"
}

"=== 4. 退出后是否残留 dsh 服务进程 ==="
$leftover = Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
    Where-Object { $_.CommandLine -match 'dsh' -and $_.CommandLine -match 'web' -and $_.CommandLine -match 'no-open' }
if ($leftover) {
    "  残留 $($leftover.Count) 个（不合格）"
    $leftover | Select-Object ProcessId, CommandLine | Format-List | Out-String
} else {
    "  无残留（合格）"
}

"=== 5. WebView2 缓存目录数量 ==="
$wv = Join-Path $env:LOCALAPPDATA 'DshDesktop\webview'
if (Test-Path $wv) {
    $dirs = Get-ChildItem $wv -Directory
    $size = [math]::Round(((Get-ChildItem $wv -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
    "  $($dirs.Count) 个目录，共 $size MB"
}
