# DeepSeek Harness 桌面版

把 dsh 的 Web 界面装进一个**独立窗口**的小程序。双击图标即用，不再经由 Edge/浏览器。

```
桌面双击「DeepSeek Harness」
        │
        ├─ 启动器在后台拉起 dsh web（子进程）
        ├─ 从 dsh 输出里取到带 token 的鉴权地址
        └─ 用内嵌 WebView2 加载该地址 → 界面就在这个窗口里
```

关闭窗口 = 结束本次 dsh 服务（进程树一并回收，不留残留）。

## 安装（给第一次用的人）

本仓库是**源码**，不含编译好的 exe。有两种用法：自己构建，或直接用别人构建好的。

这个启动器**本身不是 Harness** —— 它只是给 dsh 换了个窗口。所以前提是 dsh 能正常跑起来。

### 第 0 步：Windows 版本要求

只支持 **Windows 10 / 11**。程序是 WinForms + 内嵌 WebView2，没有跨平台计划。

### 第 1 步：装 Node.js 和 dsh

```powershell
# 装 Node.js（>= 20）：https://nodejs.org  或  winget install OpenJS.NodeJS.LTS

npm i -g @deepseek-ai/dsh
dsh --version          # 能打印版本号就说明 dsh 可用了
```

### 第 2 步：让 dsh 能连上模型

dsh 首次使用需要配置模型凭据（DeepSeek API Key）。两种方式，选一种：

```powershell
# 方式 A：环境变量
setx DEEPSEEK_API_KEY "sk-你的key"     # 之后重开一个终端

# 方式 B：先直接跑一次 dsh web，在界面里登录/填 key
dsh web
```

> 凭据存在 `%USERPROFILE%\.dsh\.credentials.yaml`，跟本启动器无关，也不会进本仓库。

### 第 3 步：拿到本启动器（两条路，选一条）

**路线 A：直接下载，不想编译 —— 推荐**

到 [Releases](https://github.com/XianyuKira/dsh-desktop/releases/latest) 下载，然后跳到第 5 步：

| 包 | 大小 | 适用 |
| --- | --- | --- |
| `dsh-desktop-<版本>-win-x64.zip` | 约 0.6 MB | 需要 .NET 8 桌面运行时（多数机器已有） |
| `dsh-desktop-<版本>-win-x64-selfcontained.zip` | 约 58 MB | 零依赖，已内嵌运行时，解压双击即可 |

**路线 B：clone 源码自己构建**

```powershell
git clone https://github.com/XianyuKira/dsh-desktop.git
cd dsh-desktop
```

### 第 4 步：构建（走路线 A 的可以跳过）

```powershell
.\build.ps1
```

这个脚本会：生成图标 → 发布程序到 `app\` → 在桌面创建快捷方式。

构建需要 [.NET SDK 8 或更高](https://dotnet.microsoft.com/download)。如果 PowerShell 拒绝执行脚本，先运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
```

### 第 5 步：运行

- 走**路线 B（自己构建）**：双击桌面上的 **「DeepSeek Harness」**，或运行 `app\DeepSeekHarness.exe`。
- 走**路线 A（下载 zip）**：解压到任意目录，双击里面的 `DeepSeekHarness.exe`。

首次运行 Windows 可能弹出 SmartScreen 警告（exe 没有代码签名），点「更多信息」→「仍要运行」即可。

### 前置条件一览

| 组件 | 要求 | 说明 |
| --- | --- | --- |
| 操作系统 | Windows 10 / 11 | 仅此 |
| .NET 桌面运行时 | 8.0 或更高 | 仅框架依赖版需要；选了 selfcontained 包则无需 |
| Edge WebView2 运行时 | 任意较新版本 | Win11 及多数 Win10 已自带，[下载](https://go.microsoft.com/fwlink/p/?LinkId=2124703) |
| Node.js | 20 或更高 | 用来跑 dsh |
| `@deepseek-ai/dsh` | 全局安装 | 启动器会自动定位，无需配置路径 |
| DeepSeek API Key | 自备 | 配在 dsh 里，不是配在本启动器里 |

启动器会在这些位置找 dsh：全局 npm 目录 → `Program Files\nodejs` → `NODE_PATH` → 从程序目录向上找 `node_modules` → PATH 上的 `dsh.cmd`。都找不到时，窗口里会直接告诉你缺什么。

## 界面上的操作

| 位置 | 功能 |
| --- | --- |
| 刷新(R) | 重新加载界面；若服务已停止则重新拉起 |
| 浏览器打开 | 把当前界面交给系统默认浏览器（可选，不是必须） |
| ＋ / － | 界面缩放（Ctrl+= / Ctrl+- 也可，Ctrl+0 复位） |
| 运行日志 | 展开 dsh 的原始输出，便于排查 |
| 菜单 → 更改工作目录 | 换 dsh 的工作目录（= 会话默认工作区），会自动重启服务 |
| 菜单 → 开发者工具 | 打开 WebView2 DevTools（F12） |
| 菜单 → 重启 / 停止 dsh 服务 | 手动控制后台服务 |
| 状态栏 | 服务状态、端口、工作目录、缩放比例 |

## 常用参数

```
DeepSeekHarness.exe [选项]

  --workspace <目录>   指定 dsh 的工作目录（默认：上次使用或用户主目录）
  --port <端口>        指定端口，0 表示由系统随机分配（默认：3080）
  --portable           设置与缓存放在程序目录，而不是 %LOCALAPPDATA%
  --browser            不用内嵌窗口，改为在系统默认浏览器中打开
  --selftest[=<文件>]  自动启动、自检并写报告后退出（开发用）
  -h, --help           帮助
```

## 端口与配置文件

启动时按顺序尝试端口：`settings.json` 里记着的端口（默认 3080）→ 被占用则由系统分配一个空闲端口。

端口只影响界面地址，数据都在 dsh 自己的会话库里，不受影响。

- 默认配置与缓存：`%LOCALAPPDATA%\DshDesktop\`（`settings.json`、`webview\`）
- 每次启动用一个独立的 WebView2 缓存目录，只保留最近两个，旧的自动清理
- `--portable` 时以上内容改为放在程序目录

`settings.json` 示例：

```json
{
  "workspace": "C:\\Users\\you\\Desktop",
  "preferredPort": 3080,
  "portableData": false,
  "strictPort": false
}
```

## 项目结构

```
build.ps1              一键构建：生成图标 → 发布到 app\ → 创建桌面快捷方式
verify-selftest.ps1    离屏端到端自检：真跑一遍 dsh + WebView2 并输出报告
make-release.ps1       打发布包：框架依赖版 + 自包含版 zip，并算 SHA256
src\
  Program.cs           入口、命令行参数、WebView2 运行时检查
  MainForm.cs          窗口：工具条、日志面板、加载态、缩放、菜单
  DshServer.cs         子进程管理、token URL 解析、Job Object 进程看守
  AppConfig.cs         settings.json 读写、端口与工作目录
  app.ico              构建时生成，不纳入版本控制
tools\IconMaker\       用 .NET 现画多尺寸 .ico，仓库里不含二进制美术资源
```

## 排查

| 现象 | 处理 |
| --- | --- |
| 窗口一直停在"正在启动 dsh 服务…" | 展开「运行日志」看 dsh 原始输出 |
| 提示找不到 dsh | 确认 `dsh --version` 在命令行可用，或检查 Node.js 安装 |
| 界面空白 / 加载失败 | 用「刷新」重试；或菜单 → 重启 dsh 服务 |
| 打不开窗口 | 菜单 → 在浏览器中打开，或加 `--browser` 参数启动 |

启动器不会把访问 token 写进日志或自检报告（一律显示为 `token=***`）。

## 许可

[MIT](LICENSE) © 2026 贤余sama
