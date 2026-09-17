# 完整隔离验证：安装 → 首次运行 → 自更新助手自检 → 卸载
# 安全约束（针对之前三次事故）：
#   · 用 DSH_DESKTOP_CONFIG / DSH_DESKTOP_DATA_ROOT 隔离，绝不改用户的 settings.json
#   · -KeepShortcut，绝不劫持用户快捷方式
#   · 只按 pid 结束自己启动的进程，绝不按进程名批量杀
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

$work = 'I:\dsh-bundle'; $dist = "$work\dist"
$install = "$work\iso-install"
$isoRoot = "$work\iso-root"          # 隔离的配置+数据根
$desk = [Environment]::GetFolderPath('Desktop')
$linkPath = Join-Path $desk 'DeepSeek Harness.lnk'
$userSettings = Join-Path $env:LOCALAPPDATA 'DshDesktop\settings.json'
$uk = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness'

$fail = 0
function Check([string]$what, [bool]$ok) {
    Write-Host ("  " + $(if ($ok) { 'PASS' } else { 'FAIL' }) + "  " + $what)
    if (-not $ok) { $script:fail += 1 }
}
function Title([string]$t) { Write-Host ''; Write-Host "=== $t ===" -ForegroundColor Cyan }

# ---------- 安全快照 ----------
$sh = New-Object -ComObject WScript.Shell
$shortcutBefore = $sh.CreateShortcut($linkPath).TargetPath
$settingsBefore = Get-Content $userSettings -Raw -EA SilentlyContinue
Write-Host "安全快照："
Write-Host "  你的快捷方式 -> $shortcutBefore"
Write-Host "  你的 settings.json 长度 -> $($settingsBefore.Length)"

# ---------- 1. 安装 ----------
Title '1. 静默安装（隔离 + 快捷方式保护）'
foreach ($d in @($install, $isoRoot)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force -EA SilentlyContinue } }
$sw = [Diagnostics.Stopwatch]::StartNew()
$out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$dist\install.ps1" `
    -InstallDir $install -DataDir "$isoRoot\data" -Yes -NoLaunch -KeepShortcut 2>&1 | Out-String
Write-Host "  用时 $([int]$sw.Elapsed.TotalSeconds) 秒"
($out -split "`n" | Where-Object { $_ -match '已安装版本|未覆盖|✓|失败|错误' } | Select-Object -First 8) |
    ForEach-Object { Write-Host "    $($_.TrimEnd())" }

Title '2. 安装结果'
Check '主程序'          (Test-Path "$install\DeepSeekHarness.exe")
Check '随包标记'        (Test-Path "$install\bundled.marker")
Check '随包 node'       (Test-Path "$install\runtime\node\node.exe")
Check '随包 dsh'        (Test-Path "$install\runtime\vendor\node_modules\@deepseek-ai\dsh\lib\bin.js")
Check '随包插件 profile' (Test-Path "$install\runtime\profile-web\package.json")
Check '更新助手完整'    ((Test-Path "$install\DshDesktopUpdater.exe") -and (Test-Path "$install\DshDesktopUpdater.Core.dll"))
Check '卸载脚本'        (Test-Path "$install\uninstall.ps1")
$ver = (Get-Item "$install\DeepSeekHarness.exe").VersionInfo.FileVersion
Write-Host "  版本 $ver"
Check '快捷方式未被劫持' ($sh.CreateShortcut($linkPath).TargetPath -eq $shortcutBefore)
Write-Host "  安装体积 $([math]::Round((Get-ChildItem $install -Recurse -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum/1MB,1)) MB"

# ---------- 3. 首次运行（完全隔离）----------
Title '3. 首次运行（隔离配置 + 自动部署插件）'
$env:DSH_DESKTOP_CONFIG = "$isoRoot\settings.json"
$env:DSH_DESKTOP_DATA_ROOT = "$isoRoot\data"
@{
    dataRoot = "$isoRoot\data"; setupComplete = $true; preferredPort = 31370
} | ConvertTo-Json | Set-Content "$isoRoot\settings.json" -Encoding UTF8

$result = Join-Path $env:TEMP 'iso-firstrun.txt'
Remove-Item "$result*" -EA SilentlyContinue
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "$install\DeepSeekHarness.exe"
$psi.Arguments = "--selftest=`"$result`" --port 31370"
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['DSH_DESKTOP_CONFIG'] = "$isoRoot\settings.json"
$psi.EnvironmentVariables['DSH_DESKTOP_DATA_ROOT'] = "$isoRoot\data"
$child = [System.Diagnostics.Process]::Start($psi)      # 只记录我们自己的 pid
$sw2 = [Diagnostics.Stopwatch]::StartNew()
while (-not $child.HasExited -and $sw2.Elapsed.TotalSeconds -lt 300) { Start-Sleep -Milliseconds 500 }
try { $child.WaitForExit(8000) | Out-Null } catch { }
Write-Host "  自检 pid=$($child.Id) exit=$($child.ExitCode) 用时 $([int]$sw2.Elapsed.TotalSeconds)s"

$report = if (Test-Path $result) { [System.IO.File]::ReadAllText($result, [System.Text.Encoding]::UTF8) } else { '' }
Check '自检退出码 0'   ($child.ExitCode -eq 0)
Check '界面渲染成功'   ($report -match 'first navigation True')
Check '市场标签有内容' ($report -match 'market label\s+text=\[插件市场')
($report -split "`r?`n" | Where-Object { $_ -match 'packaged install: DSH_HOME|provisioned profile|^market label' }) |
    ForEach-Object { Write-Host "    $_" }

Title '4. 数据落位（隔离目录内）'
Check 'dsh-home'            (Test-Path "$isoRoot\data\dsh-home")
Check '插件 profile 已部署' (Test-Path "$isoRoot\data\dsh-home\profiles\web\package.json")
Check 'pnpm 链接保留'       (Test-Path "$isoRoot\data\dsh-home\profiles\web\node_modules\.pnpm")
Check '插件市场包'          (Test-Path "$isoRoot\data\dsh-home\profiles\web\node_modules\dshmarket")
Check '余额鲸鱼包'          (Test-Path "$isoRoot\data\dsh-home\profiles\web\node_modules\dsh-whale-widget")
Check 'webview 缓存'        (Test-Path "$isoRoot\data\webview")
Check '日志目录'            (Test-Path "$isoRoot\data\logs")
Write-Host "  数据体积 $([math]::Round((Get-ChildItem "$isoRoot\data" -Recurse -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum/1MB,1)) MB"

Title '5. 更新助手自检（真实进程替换被锁 exe）'
$probe = "$isoRoot\helper-probe"
New-Item -ItemType Directory -Force -Path $probe | Out-Null
Get-ChildItem $install -File | Copy-Item -Destination $probe -Force
$hp = Start-Process -FilePath "$probe\DshDesktopUpdater.exe" -ArgumentList '--selftest' -PassThru -Wait
Check '助手自检通过' ($hp.ExitCode -eq 0)
$hlog = Join-Path $env:TEMP 'dsh-desktop-updater-selftest.log'
if (Test-Path $hlog) {
    Get-Content $hlog -Encoding UTF8 | Where-Object { $_ -match 'PASS|FAIL|all checks' } |
        Select-Object -Last 4 | ForEach-Object { Write-Host "    $_" }
}
Remove-Item $probe -Recurse -Force -EA SilentlyContinue

Title '6. 你的环境是否被污染'
Check '你的 settings.json 未被改动' ((Get-Content $userSettings -Raw -EA SilentlyContinue) -eq $settingsBefore)
Check '你的快捷方式未被改动'        ($sh.CreateShortcut($linkPath).TargetPath -eq $shortcutBefore)

Title '7. 卸载（回答 n：保留数据）'
$out2 = 'n' | powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$install\uninstall.ps1" 2>&1 | Out-String
Start-Sleep -Seconds 2
Check '程序目录已删除'   (-not (Test-Path $install))
Check '卸载注册项已删除' (-not (Test-Path $uk))
Check '数据目录被保留'   (Test-Path "$isoRoot\data")
Check '快捷方式未被误删' (Test-Path $linkPath)

# ---------- 收尾 ----------
Title '8. 清理'
Remove-Item $isoRoot -Recurse -Force -EA SilentlyContinue
if (Test-Path $uk) { Remove-Item $uk -Recurse -Force -EA SilentlyContinue }
Remove-Item "$result*" -Force -EA SilentlyContinue
Remove-Item Env:\DSH_DESKTOP_CONFIG, Env:\DSH_DESKTOP_DATA_ROOT -EA SilentlyContinue
Write-Host "  已清理隔离目录"
Write-Host "  你的快捷方式 -> $($sh.CreateShortcut($linkPath).TargetPath)"

Write-Host ''
if ($fail -eq 0) { Write-Host '=== 全部通过 ===' -ForegroundColor Green }
else { Write-Host "=== $fail 项失败 ===" -ForegroundColor Red }
