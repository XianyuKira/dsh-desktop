[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
$work = 'I:\dsh-bundle'; $dist = "$work\dist"; $pkg = "$work\package"

Write-Host '=== 0. 确认 payload 里的启动器是最新构建 ==='
$builtExe = "$pkg\DeepSeekHarness.exe"
$builtDll = "$pkg\DeepSeekHarness.dll"
"  构建产物 exe : $((Get-Item $builtExe).LastWriteTime.ToString('HH:mm:ss'))  v$((Get-Item $builtExe).VersionInfo.FileVersion)"
"  构建产物 dll : $((Get-Item $builtDll).LastWriteTime.ToString('HH:mm:ss'))"

$install = "$work\e2e-install"
$data    = "$work\e2e-data"
foreach ($d in @($install, $data)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force -EA SilentlyContinue } }

Write-Host ''
Write-Host '=== 1. 静默安装（模拟用户双击安装器并选择目录）==='
$installer = "$dist\install.ps1"
$sw = [Diagnostics.Stopwatch]::StartNew()
$installOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer `
    -InstallDir $install -DataDir $data -Yes -NoLaunch 2>&1 | Out-String
"  用时 $([int]$sw.Elapsed.TotalSeconds) 秒"
($installOut -split "`n" | Where-Object { $_ -match '安装到|数据目录|已安装版本|完成|✓|错误|失败|异常' } |
    Select-Object -First 12) | ForEach-Object { "    $($_.TrimEnd())" }

Write-Host ''
Write-Host '=== 2. 安装结果检查 ==='
$checks = [ordered]@{
    '主程序存在'       = Test-Path "$install\DeepSeekHarness.exe"
    '随包标记'         = Test-Path "$install\bundled.marker"
    '随包 node'        = Test-Path "$install\runtime\node\node.exe"
    '随包 dsh'         = Test-Path "$install\runtime\vendor\node_modules\@deepseek-ai\dsh\lib\bin.js"
    '随包插件 profile' = Test-Path "$install\runtime\profile-web\package.json"
    '更新助手'         = Test-Path "$install\DshDesktopUpdater.exe"
    '卸载脚本'         = Test-Path "$install\uninstall.ps1"
}
foreach ($k in $checks.Keys) { "  {0,-18} {1}" -f $k, $(if ($checks[$k]) { '✓' } else { '✗' }) }

"  安装体积: $([math]::Round((Get-ChildItem $install -Recurse -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum/1MB,1)) MB"
"  启动器版本: $((Get-Item "$install\DeepSeekHarness.exe").VersionInfo.FileVersion)"

Write-Host ''
Write-Host '=== 3. 注册项与快捷方式 ==='
$uk = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness'
if (Test-Path $uk) {
    $props = Get-ItemProperty $uk
    "  卸载项 ✓  $($props.DisplayName) $($props.DisplayVersion)  发布者=$($props.Publisher)"
    "  UninstallString: $($props.UninstallString)"
} else { "  卸载项 ✗" }
$desk = [Environment]::GetFolderPath('Desktop')
"  桌面快捷方式: $(Test-Path (Join-Path $desk 'DeepSeek Harness.lnk'))"
$sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeepSeek Harness.lnk'
"  开始菜单    : $(Test-Path $sm)"

Write-Host ''
Write-Host '=== 4. 数据目录设置是否正确写入 ==='
$sf = Join-Path $env:LOCALAPPDATA 'DshDesktop\settings.json'
if (Test-Path $sf) {
    "  $sf"
    Get-Content $sf -Raw | ForEach-Object { "    $_" }
    $j = Get-Content $sf -Raw | ConvertFrom-Json
    "  dataRoot 指向我们的数据目录: $($j.dataRoot -eq $data)"
    "  setupComplete = $($j.setupComplete)  （false 表示首次启动会问 API Key）"
} else { "  settings.json 不存在 ✗" }

Write-Host ''
Write-Host '（安装目录与数据目录保留，供下一步首次运行测试使用）'
