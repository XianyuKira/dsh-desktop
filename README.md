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

## 快速开始

1. 双击桌面上的 **「DeepSeek Harness」**。
2. 首次启动约 5–10 秒（要等 dsh 把插件树加载完），会显示"正在启动 dsh 服务…"。
3. 之后就是完整的 Harness 界面：会话、工作区、设置、插件，全都在窗口内。

保持原有的 `dsh web`（比如你已经在 Edge 里开着的那个）不受影响，两者可以同时存在。

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

## 依赖

| 依赖 | 说明 |
| --- | --- |
| .NET 8 桌面运行时 | 本机已装（`Microsoft.WindowsDesktop.App 8.0.x`） |
| Microsoft Edge WebView2 运行时 | 本机已装（Win10/11 通常自带） |
| Node.js + `@deepseek-ai/dsh` | 即你原本在用的 dsh，启动器会自动定位 |

启动器会依次尝试：全局 npm 目录 → `Program Files\nodejs` → `NODE_PATH` → 从程序目录向上找 `node_modules`；
都找不到才回退到 PATH 上的 `dsh.cmd`。找不到时窗口里会给出明确提示。

## 自行构建

```powershell
cd <本仓库目录>
.\build.ps1          # 生成图标 → 发布到 app\ → 创建桌面快捷方式
```

- 源码：`src\`（`Program.cs` 入口、`MainForm.cs` 窗口、`DshServer.cs` 子进程与进程看守、`AppConfig.cs` 设置）
- 图标：`tools\IconMaker\` 用 .NET 现画的多尺寸 `.ico`，仓库里不含二进制美术资源
- 验收：`.\verify-selftest.ps1` 离屏跑一遍真实流程并输出报告

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
