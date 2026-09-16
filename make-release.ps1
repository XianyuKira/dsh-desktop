[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$proj = "C:\Users\xiany\Desktop\dsh-desktop"
Set-Location $proj

$version = '1.0.0'
$stage = Join-Path $proj 'artifacts\stage'
$outDir = Join-Path $proj 'artifacts\release'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage, $outDir | Out-Null

# ---------- 包内说明 ----------
$readme = @"
DeepSeek Harness 桌面版 v$version
================================

把 dsh 的 Web 界面装进一个独立窗口，不经过浏览器。

【这个包本身不含 Harness】
它只是给 dsh 换了个窗口，所以运行前 dsh 要能用：

  1. 装 Node.js（>= 20）           https://nodejs.org
  2. npm i -g @deepseek-ai/dsh     验证：dsh --version
  3. 配模型凭据（自备 DeepSeek API Key）：
     setx DEEPSEEK_API_KEY "sk-..."
     或先跑一次 dsh web，在界面里填

【怎么跑】
双击 DeepSeekHarness.exe 即可。首次启动约 5-10 秒
（要等 dsh 加载插件树），会显示“正在启动 dsh 服务…”。

想放桌面就右键 exe → 发送到 → 桌面快捷方式。

【这个版本需要什么】
- Windows 10 / 11
- .NET 8 桌面运行时（若缺，双击时会提示并给出下载地址）
- Edge WebView2 运行时（Win11 及多数 Win10 已自带）

【小提示】
- 首次运行 Windows 可能弹 SmartScreen 警告（exe 没有代码签名），
  点“更多信息” → “仍要运行”即可。
- 窗口关闭 = 该 dsh 服务结束；你另外开着的 dsh web 不受影响。
- 端口默认 3080，被占用会自动换一个空闲端口。

官网仓库：https://github.com/XianyuKira/dsh-desktop
许可：MIT
"@
$readme | Out-File (Join-Path $stage 'README.txt') -Encoding UTF8

# ---------- 1. 框架依赖包 ----------
"=== 1. 打包框架依赖版 ==="
$zipA = Join-Path $outDir "dsh-desktop-$version-win-x64.zip"
Copy-Item (Join-Path $proj 'app\*') $stage -Recurse -Force
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipA -Force
"  $([math]::Round((Get-Item $zipA).Length/1MB, 2)) MB -> $zipA"

# ---------- 2. 自包含包 ----------
Remove-Item $stage -Recurse -Force
New-Item -ItemType Directory -Force -Path $stage | Out-Null
"=== 2. 打包自包含版 ==="
$scDir = Join-Path $proj 'artifacts\selfcontained'
Copy-Item (Join-Path $scDir 'DeepSeekHarness.exe') $stage -Force
$readmeBig = $readme -replace '【这个版本需要什么】[\s\S]*?【小提示】', @"
【这个版本需要什么】
- Windows 10 / 11
- 已自带 .NET 运行时，不需要另外安装任何东西
- 仅需 Edge WebView2 运行时（Win11 及多数 Win10 已自带）

【小提示】
"@
$readmeBig | Out-File (Join-Path $stage 'README.txt') -Encoding UTF8
$zipB = Join-Path $outDir "dsh-desktop-$version-win-x64-selfcontained.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipB -Force
"  $([math]::Round((Get-Item $zipB).Length/1MB, 2)) MB -> $zipB"

# ---------- 3. 校验和 ----------
"=== 3. 计算 SHA256 ==="
$lines = foreach ($z in @($zipA, $zipB)) {
    $h = (Get-FileHash $z -Algorithm SHA256).Hash.ToLower()
    "  $(Split-Path $z -Leaf)"
    "    $h"
    "$h  $(Split-Path $z -Leaf)"
}
$lines | Out-File (Join-Path $outDir 'SHA256SUMS.txt') -Encoding ASCII
Get-Content (Join-Path $outDir 'SHA256SUMS.txt')

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
"=== 完成，产物在 artifacts\release\ ==="
Get-ChildItem $outDir | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
