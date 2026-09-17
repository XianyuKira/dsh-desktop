# 视觉验证：界面缩放是否为正常大小（含首次运行向导截图）
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
'@ -Name W -Namespace Vis

$script:found = [IntPtr]::Zero
function Find-Window([int]$procId) {
    $script:found = [IntPtr]::Zero
    $cb = [Vis.W+EnumProc]{
        param($h, $p)
        $o = 0
        [void][Vis.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -ne $procId) { return $true }
        if (-not [Vis.W]::IsWindowVisible($h)) { return $true }
        $sb = New-Object System.Text.StringBuilder 256
        [void][Vis.W]::GetWindowText($h, $sb, 256)
        if ($sb.Length -gt 0) { $script:found = $h; return $false }
        return $true
    }
    [void][Vis.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:found
}

function Shot([string]$exe, [string]$label, [string]$out, [string[]]$extraArgs, [hashtable]$env) {
    Write-Host ''
    Write-Host "=== $label ==="
    $args = @('--port', '31420') + $extraArgs
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($args -join ' ')
    $psi.UseShellExecute = $false
    foreach ($k in $env.Keys) { $psi.EnvironmentVariables[$k] = $env[$k] }
    $p = [System.Diagnostics.Process]::Start($psi)
    Write-Host "  启动 pid $($p.Id)"

    $hwnd = [IntPtr]::Zero
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 90) {
        Start-Sleep -Milliseconds 600
        if ($p.HasExited) { break }
        $hwnd = Find-Window $p.Id
        if ($hwnd -ne [IntPtr]::Zero) { break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { Write-Host '  没找到窗口'; return $p.Id }
    Write-Host "  窗口出现于 $([int]$sw.Elapsed.TotalSeconds)s"

    # 等界面渲染完
    Start-Sleep -Seconds 22
    $p.Refresh()
    if ($p.HasExited) { Write-Host "  进程已退出 exit=$($p.ExitCode)"; return $p.Id }

    $r = New-Object Vis.W+RECT
    [void][Vis.W]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    Write-Host "  窗口 ${w}x${h} @ ($($r.Left),$($r.Top))"
    if ($w -lt 100 -or $h -lt 100) { Write-Host '  窗口尺寸异常'; return $p.Id }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  已截图: $out"
    return $p.Id
}

$exe = 'C:\Users\xiany\Desktop\dsh-desktop\app\DeepSeekHarness.exe'
$iso = 'I:\dsh-bundle\vis-iso'
Remove-Item $iso -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory -Force -Path "$iso\data" | Out-Null

# --- 场景 A：正常使用（配置已完成）---
@{
    dataRoot = "$iso\data"; setupComplete = $true; preferredPort = 31420
} | ConvertTo-Json | Set-Content "$iso\settings.json" -Encoding UTF8
$envA = @{
    DSH_DESKTOP_CONFIG = "$iso\settings.json"
    DSH_DESKTOP_DATA_ROOT = "$iso\data"
}
$pidA = Shot $exe '场景 A：正常界面（检查缩放是否 100%）' 'C:\Users\xiany\Desktop\dsh-desktop\ui-normal.png' @() $envA
if ($pidA) { Stop-Process -Id $pidA -Force -EA SilentlyContinue }
Start-Sleep -Seconds 3

# --- 场景 B：首次运行向导（setupComplete=false）---
Remove-Item "$iso\settings.json" -Force -EA SilentlyContinue
@{
    dataRoot = "$iso\data"; setupComplete = $false; preferredPort = 31420
} | ConvertTo-Json | Set-Content "$iso\settings.json" -Encoding UTF8
$pidB = Shot $exe '场景 B：首次运行向导' 'C:\Users\xiany\Desktop\dsh-desktop\ui-wizard.png' @() $envA
if ($pidB) { Stop-Process -Id $pidB -Force -EA SilentlyContinue }

Remove-Item $iso -Recurse -Force -EA SilentlyContinue
Write-Host ''
Write-Host '截图完成。注意：只结束了我自己启动的 pid。'
