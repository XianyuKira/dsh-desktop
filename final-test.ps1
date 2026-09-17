# 最终验证：安装 → 重装（应保留数据目录）→ 卸载
# 安全：DSH_DESKTOP_CONFIG 隔离配置、-KeepShortcut 保护快捷方式、只按 pid 结束自己的进程
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

$work = 'I:\dsh-bundle'; $dist = "$work\dist"
$install = "$work\fin-install"
$iso = "$work\fin-iso"
$conf = "$iso\settings.json"
$data1 = "$iso\data1"
$data2 = "$iso\data2"
$desk = [Environment]::GetFolderPath('Desktop')
$linkPath = Join-Path $desk 'DeepSeek Harness.lnk'
$userSettings = Join-Path $env:LOCALAPPDATA 'DshDesktop\settings.json'
$uk = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness'

$fail = 0
function Check([string]$w, [bool]$o) { Write-Host ("  " + $(if ($o) { 'PASS' } else { 'FAIL' }) + "  " + $w); if (-not $o) { $script:fail++ } }
function Title([string]$t) { Write-Host ''; Write-Host "=== $t ===" -ForegroundColor Cyan }

$sh = New-Object -ComObject WScript.Shell
$shortcutBefore = $sh.CreateShortcut($linkPath).TargetPath
$settingsBefore = Get-Content $userSettings -Raw -EA SilentlyContinue
Write-Host "安全快照: 快捷方式=$shortcutBefore  settings长度=$($settingsBefore.Length)"

# 隔离环境（安装器和被测程序都读它）
$env:DSH_DESKTOP_CONFIG = $conf
$env:DSH_DESKTOP_DATA_ROOT = $data1

foreach ($d in @($install, $iso)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force -EA SilentlyContinue } }
New-Item -ItemType Directory -Force -Path $iso | Out-Null

Title '1. 第一次安装（数据目录 = data1）'
$sw = [Diagnostics.Stopwatch]::StartNew()
$o1 = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$dist\install.ps1" `
    -InstallDir $install -DataDir $data1 -Yes -NoLaunch -KeepShortcut 2>&1 | Out-String
Write-Host "  用时 $([int]$sw.Elapsed.TotalSeconds)s"
($o1 -split "`n" | Where-Object { $_ -match '已安装版本|未覆盖|数据将写入|配置写入|注册到' }) | ForEach-Object { Write-Host "    $($_.TrimEnd())" }
Check '主程序就位'       (Test-Path "$install\DeepSeekHarness.exe")
Check '配置写到隔离路径' (Test-Path $conf)
Check '数据目录已建'     (Test-Path $data1)
$c1 = Get-Content $conf -Raw | ConvertFrom-Json
Check 'dataRoot = data1' ($c1.dataRoot -eq $data1)
Check '首次会问 API Key' ($c1.setupComplete -eq $false)

Title '2. 首次运行（隔离，自动部署插件）'
@{
    dataRoot = $data1; setupComplete = $true; preferredPort = 31400
} | ConvertTo-Json | Set-Content $conf -Encoding UTF8
$result = Join-Path $env:TEMP 'fin-firstrun.txt'
Remove-Item "$result*" -EA SilentlyContinue
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "$install\DeepSeekHarness.exe"
$psi.Arguments = "--selftest=`"$result`" --port 31400"
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['DSH_DESKTOP_CONFIG'] = $conf
$psi.EnvironmentVariables['DSH_DESKTOP_DATA_ROOT'] = $data1
$child = [System.Diagnostics.Process]::Start($psi)
$sw2 = [Diagnostics.Stopwatch]::StartNew()
while (-not $child.HasExited -and $sw2.Elapsed.TotalSeconds -lt 280) { Start-Sleep -Milliseconds 500 }
try { $child.WaitForExit(8000) | Out-Null } catch { }
Write-Host "  pid=$($child.Id) exit=$($child.ExitCode) 用时 $([int]$sw2.Elapsed.TotalSeconds)s"
$rep = if (Test-Path $result) { [System.IO.File]::ReadAllText($result, [System.Text.Encoding]::UTF8) } else { '' }
Check '自检成功'         ($child.ExitCode -eq 0)
Check '界面渲染'         ($rep -match 'first navigation True')
Check '插件市场在工作'   ($rep -match 'market label\s+text=\[插件市场')
Check '插件已部署到数据' (Test-Path "$data1\dsh-home\profiles\web\package.json")
Check 'pnpm 链接保留'    (Test-Path "$data1\dsh-home\profiles\web\node_modules\.pnpm")
Check 'webview 在数据目录' (Test-Path "$data1\webview")
($rep -split "`r?`n" | Where-Object { $_ -match 'provisioned profile|packaged install: DSH_HOME' }) | ForEach-Object { Write-Host "    $_" }

Title '3. 再装一次（数据目录选 data2，应保留 data1）'
$o2 = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$dist\install.ps1" `
    -InstallDir $install -DataDir $data2 -Yes -NoLaunch -KeepShortcut 2>&1 | Out-String
$c2 = Get-Content $conf -Raw | ConvertFrom-Json
Check '数据目录仍是 data1（未被覆盖）' ($c2.dataRoot -eq $data1)
Check 'setupComplete 保留为 true'      ($c2.setupComplete -eq $true)
Check '端口选择保留为 31400'            ($c2.preferredPort -eq 31400)
($o2 -split "`n" | Where-Object { $_ -match '沿用原有|已有安装|数据将写入' }) | ForEach-Object { Write-Host "    $($_.TrimEnd())" }

Title '4. 卸载（回答 n：保留数据）'
$o3 = 'n' | powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$install\uninstall.ps1" 2>&1 | Out-String
Start-Sleep -Seconds 2
Check '程序目录已删除'   (-not (Test-Path $install))
Check '注册项已删除'     (-not (Test-Path $uk))
Check '数据目录被保留'   (Test-Path $data1)
Check '快捷方式未被误删' (Test-Path $linkPath)

Title '5. 你的环境未被污染'
Check 'settings.json 未变' ((Get-Content $userSettings -Raw -EA SilentlyContinue) -eq $settingsBefore)
Check '快捷方式未变'        ($sh.CreateShortcut($linkPath).TargetPath -eq $shortcutBefore)

Title '6. 清理'
Remove-Item $iso -Recurse -Force -EA SilentlyContinue
if (Test-Path $uk) { Remove-Item $uk -Recurse -Force -EA SilentlyContinue }
Remove-Item "$result*" -Force -EA SilentlyContinue
Remove-Item Env:\DSH_DESKTOP_CONFIG, Env:\DSH_DESKTOP_DATA_ROOT -EA SilentlyContinue
Write-Host '  已清理隔离目录'

Write-Host ''
if ($fail -eq 0) { Write-Host '=== 全部通过 ===' -ForegroundColor Green } else { Write-Host "=== $fail 项失败 ===" -ForegroundColor Red }
