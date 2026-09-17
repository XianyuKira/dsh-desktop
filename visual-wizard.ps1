# 视觉验证 2：确认首次运行向导出现且可读、界面缩放为 100%
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms

Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr p);
[DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
public delegate bool EnumProc(System.IntPtr h, System.IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
'@ -Name W -Namespace V2

function Get-Windows([int]$procId) {
    $list = New-Object System.Collections.ArrayList
    $script:acc = $list
    $cb = [V2.W+EnumProc]{
        param($h, $p)
        $o = 0
        [void][V2.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -ne $procId) { return $true }
        if (-not [V2.W]::IsWindowVisible($h)) { return $true }
        $sb = New-Object System.Text.StringBuilder 256
        [void][V2.W]::GetWindowText($h, $sb, 256)
        $r = New-Object V2.W+RECT
        [void][V2.W]::GetWindowRect($h, [ref]$r)
        [void]$script:acc.Add([pscustomobject]@{
            Hwnd = $h; Title = $sb.ToString()
            L = $r.Left; T = $r.Top; W = ($r.Right - $r.Left); H = ($r.Bottom - $r.Top)
        })
        return $true
    }
    [void][V2.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $list
}

function Capture([int]$L, [int]$T, [int]$W, [int]$H, [string]$out) {
    if ($W -lt 50 -or $H -lt 50) { return $false }
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($L, $T, 0, 0, (New-Object System.Drawing.Size($W, $H)))
    $g.Dispose()
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $true
}

$exe = 'C:\Users\xiany\Desktop\dsh-desktop\app\DeepSeekHarness.exe'
$iso = 'I:\dsh-bundle\wiz-iso'
Remove-Item $iso -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory -Force -Path "$iso\data" | Out-Null

# 模拟全新安装：没有配置 -> 没有 setupComplete -> 向导必须出现
Remove-Item "$iso\settings.json" -Force -EA SilentlyContinue

Write-Host '=== 启动一个全新安装的实例（无配置）==='
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = '--port 31440'
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['DSH_DESKTOP_CONFIG'] = "$iso\settings.json"
$psi.EnvironmentVariables['DSH_DESKTOP_DATA_ROOT'] = "$iso\data"
$p = [System.Diagnostics.Process]::Start($psi)
Write-Host "  pid $($p.Id)"

Start-Sleep -Seconds 6
$wins = Get-Windows $p.Id
Write-Host "  可见窗口数: $(@($wins).Count)"
foreach ($w in $wins) {
    Write-Host "    [$($w.W)x$($w.H)] 标题='$($w.Title)'"
}

$wizard = $wins | Where-Object { $_.Title -like '*首次*' -or $_.Title -like '*设置*' } | Select-Object -First 1
if ($wizard) {
    Write-Host "  → 找到向导窗口: $($wizard.Title)"
    $ok = Capture $wizard.L $wizard.T $wizard.W $wizard.H 'C:\Users\xiany\Desktop\dsh-desktop\ui-wizard2.png'
    Write-Host "  截图: $ok"
} else {
    Write-Host '  → 没有找到向导窗口 ✗'
    $main = $wins | Select-Object -First 1
    if ($main) { [void](Capture $main.L $main.T $main.W $main.H 'C:\Users\xiany\Desktop\dsh-desktop\ui-nodialog.png') }
}

Write-Host ''
Write-Host '=== 只结束我启动的 pid ==='
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -EA SilentlyContinue }
Start-Sleep -Seconds 2
Remove-Item $iso -Recurse -Force -EA SilentlyContinue
Write-Host '  完成'
