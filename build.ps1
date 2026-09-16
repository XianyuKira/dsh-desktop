# 构建 DeepSeek Harness 桌面版：生成图标、发布程序、附带更新助手、创建桌面快捷方式。
# 用法：在 PowerShell 里执行 .\build.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\DeepSeekHarness.csproj'
$iconTool = Join-Path $root 'tools\IconMaker\MakeIcon.csproj'
$iconFile = Join-Path $root 'src\app.ico'
$updaterProject = Join-Path $root 'tools\Updater\Updater.csproj'
$publish = Join-Path $root 'app'
$exeName = 'DeepSeekHarness.exe'
$updaterName = 'DshDesktopUpdater.exe'
$shortcutName = 'DeepSeek Harness.lnk'

Write-Host '== 1/5 生成应用图标 ==' -ForegroundColor Cyan
dotnet run --project $iconTool -c Release -- $iconFile
if ($LASTEXITCODE -ne 0) { throw '生成图标失败' }

Write-Host '== 2/5 发布主程序 ==' -ForegroundColor Cyan
dotnet publish $project -c Release -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Write-Host '== 3/5 发布更新助手 ==' -ForegroundColor Cyan
# 自更新需要它：程序退出后由它替换文件并重启，所以必须和主程序放在同一目录
dotnet publish $updaterProject -c Release -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw '发布更新助手失败' }

Write-Host '== 4/5 清理调试残留 ==' -ForegroundColor Cyan
Get-ChildItem $publish -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $publish -Recurse -Directory -Filter 'webview-profile' -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

$exePath = Join-Path $publish $exeName
if (-not (Test-Path $exePath)) { throw "没有找到 $exePath" }
if (-not (Test-Path (Join-Path $publish $updaterName))) { throw "没有找到 $updaterName（自更新需要它）" }

Write-Host '== 5/5 创建桌面快捷方式 ==' -ForegroundColor Cyan
$desktop = [Environment]::GetFolderPath('Desktop')
$linkPath = Join-Path $desktop $shortcutName
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($linkPath)
$link.TargetPath = $exePath
$link.WorkingDirectory = $publish
$link.Description = '双击打开 DeepSeek Harness，界面运行在本窗口内'
$link.IconLocation = "$exePath,0"
$link.Save()

Write-Host ''
Write-Host '完成。' -ForegroundColor Green
Write-Host "  程序目录  : $publish"
Write-Host "  快捷方式  : $linkPath"
Write-Host ''
Write-Host '双击桌面上的「DeepSeek Harness」即可使用。' -ForegroundColor Green
