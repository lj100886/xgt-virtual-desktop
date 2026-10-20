# 小工作台（XiaoGongZuoTai）

豆包同款「虚拟桌面」的本地监控中心（自研 C#）。能创建**子会话虚拟桌面**（会话名形如
`7A78855482A04FA781DC#0`）、实时监控画面、远程注入键鼠输入，专为 AI 直接操控隔离
虚拟桌面而设计。

## 组成

| 程序 | 作用 |
|---|---|
| `XiaoGongZuoTai.exe` | 主界面：会话列表 + 画中画 + 输入注入（源码 `src/Main.cs`） |
| `XgtAgent.exe` | **会话内输入 / 抓屏代理** —— 运行在虚拟桌面会话**内部**，用 `SendInput` 直达该会话（源码 `src/XgtAgent.cs`，部署到 `C:\Users\Public\Xgt\`，用户级自启动） |
| `XgtVd2.exe` | **三合一助手**：创建子会话 + WTS 轮询确认 ID + 内建 HTTP 画面服务（源码 `src/XgtVd2.cs`） |
| `XgtCapture.exe` | 旧版会话内截屏代理（保留作回退，源码 `src/XgtCapture.cs`） |
| `XgtHelper.exe` | SYSTEM 提权助手（计划任务，仅关闭兜底用，源码 `src/XgtHelper.cs`） |
| `XgtVd3.exe` / `TestPoll.exe` / `CapVd2.exe` | 旧方案组件与调试/验证小程序（源码均在 `src/`，非运行必需） |

## 架构

```
console 会话                                  子会话虚拟桌面（隔离）
┌────────────────────────────┐                ┌──────────────────────────┐
│ XiaoGongZuoTai.exe (GUI)   │                │  完整用户桌面环境          │
│   └── 启动 XgtVd2.exe keep │                │                          │
│ XgtVd2.exe                 │                │  XgtAgent.exe            │
│   ├─ RDP ActiveX           │─ConnectToChild─▶│   ├─ /input  SendInput   │
│   ├─ /frame  PrintWindow   │  → JPEG 画面帧  │   ├─ /frame  会话内抓屏   │
│   └─ /stop                 │                │   └─ /info               │
└────────────┬───────────────┘                └────────────▲─────────────┘
             │                                              │
             │   输入优先走会话内代理（17790，loopback 跨会话共享）
             └──────────────────────────────────────────────┘
                探测不到代理时回退 XgtVd2 的 /input（17777+会话ID）

画面服务端口 = 17777 + 会话ID（会话 2 → 17779）
输入代理端口 = 17790（固定；console 会话会主动让位，见下）
```

**输入为什么必须走会话内代理**（2026-10-02 实证，详见 `docs/输入注入排查-2026-10-02.md`）：

> RDP ActiveX 宿主窗口一旦不可见（被移到屏外 / `WS_EX_LAYERED` 未设 alpha），
> RDP 客户端就**不再向远端转发任何输入** —— `PostMessage`（发宿主窗口或内部
> `IHWindowClass`）与 `SendInput` 全部失效。

唯一可靠的做法是**在目标会话内部注入**：`XgtAgent.exe` 随会话自启动，其 `SendInput`
直接进本会话输入队列，与 RDP 输入管线无关。主程序按 `代理优先 / 助手回退` 自动选择。

## 虚拟桌面机制

- `WTSEnableChildSessions(true)` — 启用子会话（用户权限即可）
- `WinStationCreateChildSessionTransport()` — 创建子会话传输
- RDP ActiveX（`mstscax`）+ `IMsRdpExtendedSettings` 设 `ConnectToChildSession=true`
- 凭据：ClearTextPassword 必须设在 ocx 本体的 `IMsTscNonScriptable` 上，
  设在 AdvancedSettings 无效（会导致系统仍提示"凭据不工作"）

**为什么这些逻辑要放在独立的 XgtVd2 进程里**（2026-09-14 实测，四条独立崩溃通道全部规避）：

- 主程序内直接持有 RDP ActiveX：Connect 后析构触发 CLR 内部错误 `0xc0000005`
- 主程序内「含 `HashSet<int>` + WTS 枚举轮询的循环方法」JIT 编译崩溃
- 主程序内 `HttpWebRequest`/`WebClient` 异步拉帧回调崩溃 → 画面改由 GUI 的 WebBrowser 控件拉帧
- GUI 隐藏期间 `refreshTimer` 在 ListView 句柄未就绪时 `BeginInvoke` 抛异常

## 子会话识别规则

排除固定系统会话 `0=Services`、`1=Console`、`65537=RDP-Tcp`，以及 `WTSListen(6)` 之后即视为子会话。
**勿按 `Id>4` 过滤**（实测子会话 ID 可从 3 开始），**勿把 `State=0` 当 Listen**（0=Active，6=Listen）。

## 首次部署（会话内输入代理）

输入能力依赖跑在虚拟桌面**会话内部**的 `XgtAgent.exe`，它必须随会话自启动：

```bat
:: 1) 编译并部署（build.bat 第 4 步会输出到 C:\Users\Public\Xgt\）
cd src
build.bat

:: 2) 注册用户级自启动（零提权，HKCU 可写、会话共享）
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v XgtAgent ^
    /d "\"C:\Users\Public\Xgt\XgtAgent.exe\"" /f
```

之后**重新登录虚拟桌面会话**（注销后重新创建）即自动生效。

自启动项在 console 与子会话之间共享，代理内部自行避让：
`SessionId <= 1` 时直接退出、端口被占时也退出，因此不会与 console 抢 17790 端口。

验证：

```bat
curl http://127.0.0.1:17790/ping     :: → pong sid=<会话ID>
curl http://127.0.0.1:17790/info     :: → sid=2 screen=1280x800 fg=0x... title=...
```

## 编译

前置：Windows + .NET Framework 4.x（自带 `csc.exe`，无需 SDK）。

```bat
cd src
build.bat        :: 一次编译主程序 / XgtCapture / XgtHelper / XgtAgent（XgtAgent 输出到 C:\Users\Public\Xgt\）
```

`.bat` 必须保持 **纯 ASCII**：cmd.exe 按 GBK 读取，写进中文会搅乱命令行。

## CLI

```
--status / -s              枚举会话
--screenshot [path]        保存监控中心截图
--connect <会话ID>         连接并查看画面
--newvd                    新建虚拟桌面
--input <sid> text|key|click|combo ...   注入输入
```

## 已知问题（2026-09-25 实测，重要）

| 问题 | 根因 | 处理 |
|---|---|---|
| **键鼠注入完全无反应** | XgtVd2 把 `WM_KEYDOWN`/`WM_LBUTTONDOWN` PostMessage 给了 RDP ActiveX 的**外层宿主窗口**（`_capHwnd`，ATL 容器）。RDP 真正的输入入口是内部的 **`IHWindowClass`（"Input Capture Window"）**，发给外层的消息被输入管线丢弃 | 把注入目标改为 `IHWindowClass`。可用 `EnumWindows` + `GetWindowThreadProcessId` 按 PID 定位（类名 `IHWindowClass`，标题 "Input Capture Window"） |
| **Win 组合键无效**（Win+R 等） | VK 91 经 RDP 转发不生效 | 用 `Ctrl+Esc` 打开开始菜单作为等效替代 |
| **`/clip?set=` 永远失败** | HTTP 处理跑在 MTA 线程池线程，而 `Clipboard.SetText`（OLE）要求 STA，必抛「在可以调用 OLE 之前，必须将当前线程设置为单线程单元(STA)模式」 | 从 console 侧 `Set-Clipboard` 设置，RDP 剪贴板重定向会自动同步到子会话 |
| **`/input move` 无效** | XgtVd2 的 HTTP 层未实现 `move`，返回 "OK unknown" | 无（文档与实现不符） |
| **WM_CHAR 注入 UWP（开始菜单）被吞** | IME 拦截字符消息 | 改用 `WM_KEYDOWN` 虚拟键码 |

## 历史探索记录（早期版本）

- 早期窗口化 `Connect()` 曾抛 `E_INVALIDARG (0x80070057)` —— 已由 2026-09-14 三合一方案
  （ActiveX 移入独立进程 XgtVd2）解决。
- 备用路线 `pip_sdk.dll`（豆包自带）`CreateShadowSession`：`Initialize` 参数未逆向出，
  返回错误码 4，**已放弃**。

## 配套技能

`skill/` 目录是对应的 AI 接入 Skill（SKILL.md + usage.md + troubleshooting.md + xgt.ps1），
可直接放进 agent 的 skills 目录使用。RDP 凭据存于 Windows 凭据管理器。

## 相关项目

- [`xgt-mcp-bridge`](https://github.com/lj100886/xgt-mcp-bridge) —— 把 Windows-MCP 的
  UIA 语义控件树带进本项目的隔离虚拟桌面

## 目录结构

```
├─ src/                 全部 C# 源码与 build.bat
│   ├─ Main.cs          主程序（GUI + CLI）
│   ├─ XgtVd2.cs        三合一创建/画面助手
│   ├─ XgtAgent.cs      会话内输入/抓屏代理
│   ├─ XgtCapture.cs    旧版会话内截屏代理（回退）
│   ├─ XgtHelper.cs     SYSTEM 关闭兜底助手（可选）
│   ├─ XgtVd3.cs / TestPoll.cs / CapVd2.cs   旧方案与调试小程序
│   └─ xgt.ps1          AI 封装脚本（环境变量 XGT_EXE/XGT_CLI 可覆盖路径）
├─ docs/                技术排查报告（输入注入失效根因实证）
├─ skill/               AI 接入 Skill（SKILL.md + references/ + scripts/）
└─ 验收-2026-10-02/      稳定性采样记录
```

## 许可证

本项目以 [MIT License](LICENSE) 发布，可自由使用、修改与再分发，保留版权声明即可。

> 说明：RDP 凭据由使用者自己保存在 Windows 凭据管理器中，仓库内不含任何账号、密码或令牌。
