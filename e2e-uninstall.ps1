[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
$work = 'I:\dsh-bundle'
$install = "$work\e2e-install"
$data = "$work\e2e-data"
$sf = Join-Path $env:LOCALAPPDATA 'DshDesktop\settings.json'
$desk = [Environment]::GetFolderPath('Desktop')
$smLink = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DeepSeek Harness.lnk'
$uk = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness'

Write-Host '=== 1. 运行卸载器（回答 n：保留数据目录）==='
$uninstaller = "$install\uninstall.ps1"
if (-not (Test-Path $uninstaller)) { Write-Host '  没有卸载脚本 ✗'; exit 1 }
$out = 'n' | powershell.exe -NoProfile -ExecutionPolicy Bypass -File $uninstaller 2>&1 | Out-String
($out -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 10) | ForEach-Object { "    $($_.TrimEnd())" }

Write-Host ''
Write-Host '=== 2. 卸载结果 ==='
Start-Sleep -Seconds 2
$checks = [ordered]@{
    '程序目录已删除'     = -not (Test-Path $install)
    '桌面快捷方式已删除' = -not (Test-Path (Join-Path $desk 'DeepSeek Harness.lnk'))
    '开始菜单已删除'     = -not (Test-Path $smLink)
    '卸载注册项已删除'   = -not (Test-Path $uk)
    '数据目录已保留'     = Test-Path $data
}
foreach ($k in $checks.Keys) { "  {0,-20} {1}" -f $k, $(if ($checks[$k]) { '✓' } else { '✗' }) }

Write-Host ''
Write-Host '=== 3. 恢复你的设置（指向你的真实数据目录）==='
$yourData = 'C:\Users\xiany\AppData\Local\DshDesktop'
@{
    dataRoot      = $yourData
    setupComplete = $true
    preferredPort = 31301
} | ConvertTo-Json | Set-Content -Path $sf -Encoding UTF8
Get-Content $sf -Raw | ForEach-Object { "    $_" }
Write-Host '  （这是普通安装，dataRoot 指向 %LOCALAPPDATA%\DshDesktop，与之前一致）'

Write-Host ''
Write-Host '=== 4. 清理测试数据目录 ==='
if (Test-Path $data) { Remove-Item $data -Recurse -Force -EA SilentlyContinue; Write-Host '  已删除 e2e-data' }
Write-Host ''
Write-Host "=== 5. 磁盘 ==="
Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Name -in @('C','I') } |
    ForEach-Object { "  $($_.Name): 可用 $([math]::Round($_.Free/1GB,2)) GB" }
