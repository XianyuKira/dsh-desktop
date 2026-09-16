[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
# 本脚本位于 tools\ 下，工程根是它的上一级
Set-Location (Split-Path -Parent $PSScriptRoot)

Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr p);
[DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
public delegate bool EnumProc(System.IntPtr h, System.IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
'@ -Name Win -Namespace Cap

$script:found = [IntPtr]::Zero

function Find-Window([int]$procId) {
    $script:found = [IntPtr]::Zero
    $cb = [Cap.Win+EnumProc]{
        param($h, $p)
        $owner = 0
        [void][Cap.Win]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -ne $procId) { return $true }
        if (-not [Cap.Win]::IsWindowVisible($h)) { return $true }
        $sb = New-Object System.Text.StringBuilder 256
        [void][Cap.Win]::GetWindowText($h, $sb, 256)
        if ($sb.Length -gt 0) { $script:found = $h; return $false }
        return $true
    }
    [void][Cap.Win]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:found
}

$stage = Join-Path $PWD 'ui-stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$newDir = Join-Path $stage 'new'
$oldDir = Join-Path $stage 'old'
New-Item -ItemType Directory -Force -Path $newDir, $oldDir | Out-Null

$out = Join-Path $PWD 'src\bin\Release\net8.0-windows'
$files = @('DeepSeekHarness.exe', 'DeepSeekHarness.dll', 'DeepSeekHarness.deps.json', 'DeepSeekHarness.runtimeconfig.json')

"=== 1. 构建正式版 1.1.0 ==="
dotnet build "src\DeepSeekHarness.csproj" -c Release -v q --nologo 2>&1 |
    Select-String -Pattern "error" | ForEach-Object { "  $($_.Line)" }
$files | ForEach-Object { Copy-Item (Join-Path $out $_) $newDir -Force }

"=== 2. 构建模拟旧版 0.9.0 ==="
dotnet build "src\DeepSeekHarness.csproj" -c Release -v q --nologo -p:Version=0.9.0 2>&1 |
    Select-String -Pattern "error" | ForEach-Object { "  $($_.Line)" }
$files | ForEach-Object { Copy-Item (Join-Path $out $_) $oldDir -Force }

"=== 3. 恢复正式版构建 ==="
dotnet build "src\DeepSeekHarness.csproj" -c Release -v q --nologo 2>&1 |
    Select-String -Pattern "error" | ForEach-Object { "  $($_.Line)" }

function Capture-Dialog([string]$label, [string]$exePath, [string]$outPng) {
    "=== $label ==="
    if (-not (Test-Path $exePath)) { "  exe 不存在"; return }
    $stdout = Join-Path $env:TEMP "uidlg-$([guid]::NewGuid().ToString('N').Substring(0,6)).out"
    # 不能用 -WindowStyle Hidden：它会把首个窗口一并隐藏，既看不到也枚举不到。
    $p = Start-Process -FilePath $exePath -ArgumentList @('--check-update-ui') -PassThru `
        -RedirectStandardOutput $stdout

    # 更新检查本身要联网，最坏约 20 秒；轮询等窗口出现。
    $hwnd = [IntPtr]::Zero
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 45) {
        if ($p.HasExited) { break }
        Start-Sleep -Milliseconds 700
        $hwnd = Find-Window $p.Id
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
    "  等待 $([math]::Round($sw.Elapsed.TotalSeconds,1)) 秒后: hasExited=$($p.HasExited) hwnd=$hwnd"
    if ($hwnd -eq [IntPtr]::Zero) {
        "  没找到窗口"
        if (Test-Path $stdout) { "  进程输出: $((Get-Content $stdout -Raw).Trim())" }
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        return
    }

    $sb = New-Object System.Text.StringBuilder 256
    [void][Cap.Win]::GetWindowText($hwnd, $sb, 256)
    $rect = New-Object Cap.Win+RECT
    [void][Cap.Win]::GetWindowRect($hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    "  窗口标题: $($sb.ToString())"
    "  位置 ($($rect.Left),$($rect.Top))  尺寸 ${w}x${h}"

    if ($w -gt 100 -and $h -gt 100) {
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
        $g.Dispose()
        $bmp.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        "  已截图: $(Split-Path $outPng -Leaf)"
    }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    if (Test-Path $stdout) { "  进程输出: $((Get-Content $stdout -Raw).Trim())" ; Remove-Item $stdout -Force -EA SilentlyContinue }
    Start-Sleep -Milliseconds 600
}

Capture-Dialog 'A. 模拟旧版 v0.9.0 -> 期望「有新版本可用」' (Join-Path $oldDir 'DeepSeekHarness.exe') (Join-Path $PWD 'ui-1-update-available.png')
Capture-Dialog 'B. 正式版 v1.1.0 -> 期望「已是最新版本」' (Join-Path $newDir 'DeepSeekHarness.exe') (Join-Path $PWD 'ui-2-up-to-date.png')

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
"=== 截图 ==="
Get-ChildItem (Join-Path $PWD 'ui-*.png') -ErrorAction SilentlyContinue |
    Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB)}} | Format-Table -AutoSize
