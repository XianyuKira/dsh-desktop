# DeepSeek Harness 桌面版 —— 零部署安装器
#
# 这个包已经自带 Node 运行时、dsh 和常用插件（插件市场 / 余额鲸鱼 / 手机访问 /
# 背景 / 读图 / 会话管理等），所以装完不需要再装任何东西，也不需要系统里有 Node。
#
# 安装位置和数据目录都可以自己选，默认都不强制放 C 盘。
#
# 静默安装（便于自动化验证）：
#   install.ps1 -InstallDir D:\App -DataDir D:\AppData -Yes

param(
    [string]$InstallDir,
    [string]$DataDir,
    [switch]$Yes,            # 跳过所有交互确认
    [switch]$NoShortcuts,    # 不创建快捷方式（测试用）
    [switch]$NoRegistry,     # 不写卸载注册项（测试用）
    [switch]$NoLaunch,       # 装完不自动启动
    [switch]$KeepShortcut,   # 不覆盖已存在的快捷方式（安全测试用）
    [switch]$ForceDataDir    # 即使已有安装，也强制使用本次选择的数据目录
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms | Out-Null
Add-Type -AssemblyName System.Drawing | Out-Null

$here = $PSScriptRoot
$payloadZip = Get-ChildItem $here -Filter 'payload-*.zip' -ErrorAction SilentlyContinue | Select-Object -First 1
$payloadDir = Join-Path $here 'payload'

Write-Host ''
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host '  DeepSeek Harness 桌面版  ·  零部署安装' -ForegroundColor Cyan
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host '这个包自带 Node 运行时、dsh 与常用插件，装完即可用，'
Write-Host '不需要预先安装 Node.js 或 npm。'
Write-Host ''

# ---------- 1. 选安装目录 ----------
$defaultRoot = if (Test-Path 'D:\') { 'D:\DeepSeekHarness' } else { Join-Path $env:LOCALAPPDATA 'Programs\DeepSeekHarness' }
$silent = $Yes -or -not [string]::IsNullOrEmpty($InstallDir)

if (-not [string]::IsNullOrEmpty($InstallDir)) {
    $installDir = $InstallDir
    Write-Host "  安装到（参数指定）: $installDir" -ForegroundColor Green
} else {
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择安装位置（程序文件和运行时都会放这里，约 600 MB）'
    $dialog.SelectedPath = $defaultRoot
    $dialog.ShowNewFolderButton = $true
    Write-Host '请在弹出的窗口里选择【安装位置】…'
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Host '已取消安装。' -ForegroundColor Yellow
        exit 0
    }
    $installDir = $dialog.SelectedPath
    Write-Host "  安装到: $installDir" -ForegroundColor Green
}
Write-Host ''

# ---------- 2. 选数据目录（可选） ----------
$defaultData = Join-Path $installDir 'data'
if (-not [string]::IsNullOrEmpty($DataDir)) {
    $dataDir = $DataDir
    Write-Host "  数据目录（参数指定）: $dataDir" -ForegroundColor Green
} elseif ($silent) {
    $dataDir = $defaultData
    Write-Host "  数据目录（默认，随程序）: $dataDir" -ForegroundColor Green
} else {
    Write-Host '数据目录用来放会话记录、缓存和日志，会随时间增长。'
    Write-Host "  默认: $defaultData"
    if ((Read-Host '要另选一个位置吗（例如放到别的盘）？(y/N)') -match '^[Yy]') {
        $dialog2 = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog2.Description = '选择数据目录'
        $dialog2.SelectedPath = $defaultData
        $dialog2.ShowNewFolderButton = $true
        Write-Host '请在弹出的窗口里选择【数据目录】…'
        if ($dialog2.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            $dataDir = $dialog2.SelectedPath
        } else {
            $dataDir = $defaultData
        }
    } else {
        $dataDir = $defaultData
    }
    Write-Host "  数据目录: $dataDir" -ForegroundColor Green
}
Write-Host ''

"  程序: $installDir"
"  数据: $dataDir"
Write-Host ''
if (-not $Yes) {
    if ((Read-Host '确认安装吗？(Y/N)') -notmatch '^[Yy]') { Write-Host '已取消。'; exit 0 }
}

# ---------- 3. 释放文件 ----------
function Copy-Payload([string]$from, [string]$to) {
    # robocopy 能处理 .pnpm 里的链接结构，也能并行复制几万个文件
    $args = @($from, $to, '/E', '/SL', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/MT:16')
    $p = Start-Process -FilePath 'robocopy.exe' -ArgumentList $args -PassThru -Wait -WindowStyle Hidden
    # robocopy 退出码 < 8 都算成功
    if ($p.ExitCode -ge 8) { throw "复制失败（robocopy 退出码 $($p.ExitCode)）" }
}

Write-Host ''
Write-Host '正在释放文件（约 600 MB / 4.8 万个文件，需要几分钟）…' -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()

if (Test-Path $payloadDir) {
    Copy-Payload $payloadDir $installDir
    Write-Host "  完成，用时 $([int]$sw.Elapsed.TotalSeconds) 秒" -ForegroundColor Green
} elseif ($payloadZip) {
    # Expand straight into the install folder: staging in %TEMP% would need another ~600 MB on
    # the system drive, which is exactly what a user choosing another drive is avoiding.
    Write-Host '  解压中…'
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Expand-Archive -Path $payloadZip.FullName -DestinationPath $installDir -Force
    Write-Host "  完成，用时 $([int]$sw.Elapsed.TotalSeconds) 秒" -ForegroundColor Green
} else {
    Write-Host "找不到安装数据（既没有 payload\ 目录，也没有 payload-*.zip）" -ForegroundColor Red
    Read-Host '按回车退出'; exit 1
}

$exe = Join-Path $installDir 'DeepSeekHarness.exe'
if (-not (Test-Path $exe)) {
    Write-Host "释放后没有找到 $exe" -ForegroundColor Red
    Read-Host '按回车退出'; exit 1
}
$version = (Get-Item $exe).VersionInfo.FileVersion
Write-Host "  已安装版本: $version" -ForegroundColor Green

# ---------- 4. 写入数据目录设置 ----------
Write-Host ''
Write-Host '正在写入数据目录设置…' -ForegroundColor Cyan
# 配置写到 DSH_DESKTOP_CONFIG 指定的位置（测试用），否则写默认位置
$configPath = $env:DSH_DESKTOP_CONFIG
if ([string]::IsNullOrWhiteSpace($configPath)) {
    $settingsDir = Join-Path $env:LOCALAPPDATA 'DshDesktop'
    New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
    $configPath = Join-Path $settingsDir 'settings.json'
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path $configPath -Parent) | Out-Null
}

# 尊重已有安装：已经存在且设了数据目录时，不覆盖它，除非用户明确要求
$existingConfig = $null
$existingDataRoot = $null
if (Test-Path $configPath) {
    try {
        $existingConfig = Get-Content $configPath -Raw | ConvertFrom-Json
        $existingDataRoot = $existingConfig.dataRoot
    } catch { }
}

if ($existingDataRoot -and $existingDataRoot -ne $dataDir -and -not $ForceDataDir) {
    Write-Host ''
    Write-Host "已有安装的数据目录是：$existingDataRoot" -ForegroundColor Yellow
    Write-Host "这次选择的是：        $dataDir"
    if ((Read-Host '要改成新目录吗？(y/N，直接回车=沿用原有的)') -match '^[Yy]') {
        $dataDir = $dataDir
    } else {
        $dataDir = $existingDataRoot
        Write-Host "  沿用原有数据目录: $dataDir" -ForegroundColor Green
    }
}

$settings = [ordered]@{
    dataRoot      = $dataDir
    # Reinstalling over an existing setup must not send the user back through the wizard or
    # reset their port choice, so those two are only initialised for a fresh install.
    setupComplete = if ($existingConfig) { $existingConfig.setupComplete } else { $false }
    preferredPort = if ($existingConfig -and $existingConfig.preferredPort) { $existingConfig.preferredPort } else { 3080 }
}
$settings | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Write-Host "  数据将写入: $dataDir" -ForegroundColor Green
Write-Host "  配置写入  : $configPath"

# ---------- 5. 快捷方式 ----------
Write-Host ''
Write-Host '正在创建快捷方式…' -ForegroundColor Cyan
$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'

if (-not $NoShortcuts) {
    $deskLink = Join-Path $desktop 'DeepSeek Harness.lnk'
    $menuLink = Join-Path $startMenu 'DeepSeek Harness.lnk'

    # -KeepShortcut exists so a test install cannot hijack the shortcut belonging to a real
    # install — which is exactly how the user's desktop icon once ended up pointing at a test folder.
    if ($KeepShortcut -and (Test-Path $deskLink)) {
        Write-Host '  桌面快捷方式已存在，未覆盖（-KeepShortcut）'
    } else {
        $link = $shell.CreateShortcut($deskLink)
        $link.TargetPath = $exe
        $link.WorkingDirectory = $installDir
        $link.Description = '双击打开 DeepSeek Harness（界面在本窗口内运行）'
        $link.IconLocation = "$exe,0"
        $link.Save()
        Write-Host '  桌面快捷方式 ✓'
    }

    if ($KeepShortcut -and (Test-Path $menuLink)) {
        Write-Host '  开始菜单已存在，未覆盖（-KeepShortcut）'
    } else {
        $link2 = $shell.CreateShortcut($menuLink)
        $link2.TargetPath = $exe
        $link2.WorkingDirectory = $installDir
        $link2.Description = '双击打开 DeepSeek Harness'
        $link2.IconLocation = "$exe,0"
        $link2.Save()
        Write-Host '  开始菜单 ✓'
    }
} else {
    Write-Host '  已跳过（-NoShortcuts）'
}

# ---------- 6. 卸载注册项 ----------
if (-not $NoRegistry) {
    $uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness"
    New-Item -Path $uninstallKey -Force | Out-Null
    Set-ItemProperty -Path $uninstallKey -Name 'DisplayName'     -Value 'DeepSeek Harness 桌面版'
    Set-ItemProperty -Path $uninstallKey -Name 'DisplayVersion'  -Value $version
    Set-ItemProperty -Path $uninstallKey -Name 'Publisher'       -Value '贤余sama'
    Set-ItemProperty -Path $uninstallKey -Name 'InstallLocation' -Value $installDir
    Set-ItemProperty -Path $uninstallKey -Name 'DisplayIcon'     -Value "$exe,0"
    Set-ItemProperty -Path $uninstallKey -Name 'UninstallString' -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installDir 'uninstall.ps1') + '"')
    Write-Host '  已注册到「应用和功能」✓'
} else {
    Write-Host '  已跳过注册（-NoRegistry）'
}

# ---------- 7. 卸载脚本 ----------
# 卸载只允许删掉"确定属于这次安装"的东西。之前它无条件删除桌面上同名的快捷方式、
# 以及整个 %LOCALAPPDATA%\DshDesktop，结果卸载一个测试安装时把用户真实安装的快捷方式
# 和设置一起删了。
$uninstall = @"
# 卸载 DeepSeek Harness 桌面版
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
`$install = '$installDir'
`$data    = '$dataDir'
`$exe     = Join-Path `$install 'DeepSeekHarness.exe'

Write-Host '即将卸载 DeepSeek Harness 桌面版。' -ForegroundColor Cyan
Write-Host ''
Write-Host "  程序目录: `$install"
Write-Host "  数据目录: `$data"
Write-Host ''
`$dropData = Read-Host '是否同时删除数据目录（会话记录、密钥）? (y/N)'

# 只结束从这个安装目录启动的实例，不碰别处的 dsh
Get-Process DeepSeekHarness -ErrorAction SilentlyContinue | Where-Object {
    try { `$_.Path -eq `$exe } catch { `$false }
} | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# 快捷方式：只有指向本次安装时才删
`$shell = New-Object -ComObject WScript.Shell
foreach (`$lnk in @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeepSeek Harness.lnk'),
    (Join-Path `$env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeepSeek Harness.lnk')
)) {
    if (-not (Test-Path `$lnk)) { continue }
    `$points = `$null
    try { `$points = `$shell.CreateShortcut(`$lnk).TargetPath } catch { }
    if (`$points -and (`$points -ieq `$exe -or `$points.StartsWith(`$install, [StringComparison]::OrdinalIgnoreCase))) {
        Remove-Item `$lnk -Force -ErrorAction SilentlyContinue
        Write-Host "  已删除快捷方式: `$lnk"
    } else {
        Write-Host "  保留快捷方式（指向别处）: `$lnk"
    }
}

Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness' -Recurse -Force -ErrorAction SilentlyContinue

# 启动器状态目录：只有本次安装就落在里面时才删，否则会误伤另一个安装
`$state = Join-Path `$env:LOCALAPPDATA 'DshDesktop'
if ((Test-Path `$state) -and (`$install.StartsWith(`$state, [StringComparison]::OrdinalIgnoreCase))) {
    Remove-Item `$state -Recurse -Force -ErrorAction SilentlyContinue
}

if (`$dropData -match '^[Yy]') { Remove-Item `$data -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Item `$install -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host '卸载完成。' -ForegroundColor Green
Read-Host '按回车退出'
"@
[System.IO.File]::WriteAllText((Join-Path $installDir 'uninstall.ps1'), $uninstall, (New-Object System.Text.UTF8Encoding($true)))
Write-Host '  卸载脚本 ✓'

# ---------- 8. 完成 ----------
Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host '  安装完成' -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green
Write-Host ''
Write-Host '第一次启动时会让你填一个 DeepSeek API Key，填完就能用了。'
Write-Host '（在 platform.deepseek.com 创建）'
Write-Host ''

if ($NoLaunch) {
    Write-Host '（已跳过自动启动 -NoLaunch）'
} elseif ($silent) {
    Start-Process -FilePath $exe -WorkingDirectory $installDir
    Write-Host '已启动。' -ForegroundColor Green
} else {
    $run = Read-Host '现在启动吗？(Y/n)'
    if ($run -notmatch '^[Nn]') {
        Start-Process -FilePath $exe -WorkingDirectory $installDir
        Write-Host '已启动。' -ForegroundColor Green
    }
}
Write-Host ''
Write-Host '以后可以从桌面快捷方式启动；卸载请用「设置 → 应用 → DeepSeek Harness 桌面版」。'
if (-not $silent) {
    Write-Host '按任意键关闭本窗口。'
    [void](Read-Host)
}
