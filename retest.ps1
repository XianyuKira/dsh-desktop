[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
$work = 'I:\dsh-bundle'; $dist = "$work\dist"
$install = "$work\rt-install"; $data = "$work\rt-data"
$desk = [Environment]::GetFolderPath('Desktop')
$linkPath = Join-Path $desk 'DeepSeek Harness.lnk'
$uk = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness'
$sf = Join-Path $env:LOCALAPPDATA 'DshDesktop\settings.json'

$fail = 0
function Check([string]$what, [bool]$ok) {
    Write-Host ("  " + $(if ($ok) { 'PASS' } else { 'FAIL' }) + "  " + $what)
    if (-not $ok) { $script:fail += 1 }
}

# ---------- 准备 ----------
if (Test-Path $install) { Remove-Item $install -Recurse -Force -EA SilentlyContinue }
if (Test-Path $data) { Remove-Item $data -Recurse -Force -EA SilentlyContinue }
$sh = New-Object -ComObject WScript.Shell
$shortcutBefore = $sh.CreateShortcut($linkPath).TargetPath
$settingsBefore = if (Test-Path $sf) { Get-Content $sf -Raw } else { $null }
$settingsBackup = "$sf.rt-backup"
if (Test-Path $sf) { Copy-Item $sf $settingsBackup -Force }
Write-Host "安装前快捷方式 -> $shortcutBefore"
Write-Host ''

# ---------- 1. 静默安装（带快捷方式保护）----------
Write-Host '=== 1. 静默安装（-KeepShortcut 保护）==='
$sw = [Diagnostics.Stopwatch]::StartNew()
$out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$dist\install.ps1" `
    -InstallDir $install -DataDir $data -Yes -NoLaunch -KeepShortcut 2>&1 | Out-String
Write-Host "  用时 $([int]$sw.Elapsed.TotalSeconds) 秒"
($out -split "`n" | Where-Object { $_ -match '安装到|数据目录|已安装版本|完成|✓|未覆盖' } |
    Select-Object -First 10) | ForEach-Object { Write-Host "    $($_.TrimEnd())" }

Write-Host ''
Write-Host '=== 2. 安装正确性 ==='
Check '主程序存在'      (Test-Path "$install\DeepSeekHarness.exe")
Check '随包 node'       (Test-Path "$install\runtime\node\node.exe")
Check '随包 dsh'        (Test-Path "$install\runtime\vendor\node_modules\@deepseek-ai\dsh\lib\bin.js")
Check '随包插件'        (Test-Path "$install\runtime\profile-web\package.json")
Check '更新助手依赖齐全' (Test-Path "$install\DshDesktopUpdater.Core.dll")
Check '卸载脚本'        (Test-Path "$install\uninstall.ps1")
Check '版本为 1.3.3'    ((Get-Item "$install\DeepSeekHarness.exe").VersionInfo.FileVersion -like '1.3.3*')
Check '快捷方式未被劫持' ($sh.CreateShortcut($linkPath).TargetPath -eq $shortcutBefore)

Write-Host ''
Write-Host '=== 3. 数据目录设置 ==='
$j = Get-Content $sf -Raw | ConvertFrom-Json
Check "dataRoot 指向 $data" ($j.dataRoot -eq $data)
Check 'setupComplete=false（首次会问 API Key）' ($j.setupComplete -eq $false)

# ---------- 4. 首次运行 ----------
Write-Host ''
Write-Host '=== 4. 首次运行（自动部署插件 + 随包运行时）==='
@{
    dataRoot = $data; setupComplete = $true; preferredPort = 31350
} | ConvertTo-Json | Set-Content -Path $sf -Encoding UTF8

$result = Join-Path $env:TEMP 'rt-firstrun.txt'
Remove-Item "$result*" -EA SilentlyContinue
$exe = Join-Path $install 'DeepSeekHarness.exe'
$p = Start-Process -FilePath $exe -ArgumentList @("--selftest=$result", '--port', '31350') -PassThru -WindowStyle Hidden
$sw2 = [Diagnostics.Stopwatch]::StartNew()
while (-not $p.HasExited -and $sw2.Elapsed.TotalSeconds -lt 300) { Start-Sleep -Milliseconds 500 }
try { $p.WaitForExit(8000) | Out-Null } catch { }
Write-Host "  自检 exit=$($p.ExitCode) 用时 $([int]$sw2.Elapsed.TotalSeconds)s"
$report = if (Test-Path $result) { [System.IO.File]::ReadAllText($result, [System.Text.Encoding]::UTF8) } else { '' }
Check '自检退出码为 0' ($p.ExitCode -eq 0)
Check '界面渲染成功'   ($report -match 'first navigation True')
Check '市场标签有内容' ($report -match 'market label\s+text=\[插件市场')
($report -split "`r?`n" | Where-Object { $_ -match '^market label|provisioned profile|packaged install: DSH_HOME' }) |
    ForEach-Object { Write-Host "    $_" }

Write-Host ''
Write-Host '=== 5. 数据落位 ==='
Check 'dsh-home'          (Test-Path "$data\dsh-home")
Check '插件 profile 部署' (Test-Path "$data\dsh-home\profiles\web\package.json")
Check 'pnpm 链接保留'     (Test-Path "$data\dsh-home\profiles\web\node_modules\.pnpm")
Check '插件市场包'        (Test-Path "$data\dsh-home\profiles\web\node_modules\dshmarket")
Check '余额鲸鱼包'        (Test-Path "$data\dsh-home\profiles\web\node_modules\dsh-whale-widget")
Check 'webview 缓存'      (Test-Path "$data\webview")
Write-Host "  数据体积: $([math]::Round((Get-ChildItem $data -Recurse -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum/1MB,1)) MB"

# ---------- 6. 卸载 ----------
Write-Host ''
Write-Host '=== 6. 卸载（回答 n：保留数据）==='
$out2 = 'n' | powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$install\uninstall.ps1" 2>&1 | Out-String
Start-Sleep -Seconds 2
Check '程序目录已删除'   (-not (Test-Path $install))
Check '卸载注册项已删除' (-not (Test-Path $uk))
Check '数据目录被保留'   (Test-Path $data)
Check '快捷方式未被误删' (Test-Path $linkPath)

# ---------- 7. 还原并清理 ----------
Write-Host ''
Write-Host '=== 7. 还原与清理 ==='
if (Test-Path $settingsBackup) { Move-Item $settingsBackup $sf -Force; Write-Host '  已还原 settings.json' }
if (Test-Path $data) { Remove-Item $data -Recurse -Force -EA SilentlyContinue; Write-Host '  已删除测试数据目录' }
if (Test-Path $uk) { Remove-Item $uk -Recurse -Force -EA SilentlyContinue }
Remove-Item "$result*" -Force -EA SilentlyContinue
$sh2 = New-Object -ComObject WScript.Shell
Write-Host "  快捷方式现在 -> $($sh2.CreateShortcut($linkPath).TargetPath)"

Write-Host ''
Write-Host $(if ($fail -eq 0) { '=== 全部通过 ===' } else { "=== $fail 项失败 ===" })
