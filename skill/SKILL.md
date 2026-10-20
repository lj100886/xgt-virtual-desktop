---
name: 小工作台
description: 虚拟桌面监控中心（豆包同款子会话方案）AI 接入技能。可枚举 Windows 会话、查看虚拟桌面实时画面、注入键鼠/文本输入、取送剪贴板、新建与关闭虚拟桌面。通过 CLI（XiaoGongZuoTai.exe）与本地 HTTP API（XgtVd2 画面服务，端口 17777+会话ID）双通道控制，无需人工操作 GUI。
version: 1.2
---

# 小工作台（XiaoGongZuoTai）

豆包同款"虚拟桌面"的本地监控中心（自研 C#）。能创建**子会话虚拟桌面**（会话名形如 `7A78855482A04FA781DC#0`）、实时监控画面、远程注入输入，专为 AI 直接操控虚拟桌面设计。

## 程序位置

- 主程序：`XiaoGongZuoTai.exe`（可放在任意目录；下文示例用 `%LOCALAPPDATA%\ResGuard\` 作为默认安装位置，调用时填实际完整路径即可）
- 三合一创建/画面助手：同目录 `XgtVd2.exe`（keep 模式＝Connect 创建子会话 + WTS 轮询确认会话 ID + 内建 HTTP 画面服务，端口 `17777+会话ID`，API 与旧 XgtCapture 兼容）
- 旧组件（保留作回退，正常流程不再使用）：`XgtVd3.exe`（旧三进程方案的轮询助手）、`XgtCapture.exe`（旧画面代理）、`XgtHelper.exe`（SYSTEM 计划任务，画面预注入/关闭兜底——画面已内建 XgtVd2，不再依赖）
- 源码与编译：同目录 `xgt\Main.cs` + `build_xgt.bat`（编译前须杀 XiaoGongZuoTai/XgtVd2 进程）

## 接入通道（三选一）

| 通道 | 方式 | 适用 |
|---|---|---|
| **CLI** | 直接运行 `XiaoGongZuoTai.exe --参数` | 会话枚举、截图、新建虚拟桌面（最稳） |
| **HTTP API** | `http://127.0.0.1:17777+<会话ID>/...` | 实时画面帧、输入注入、剪贴板（最快） |
| **封装脚本** | `scripts\xgt.ps1` | 常用操作一行调用（推荐 AI 使用） |

> 说明：旧 Python 版的 stdio MCP（239 tools）随迁移已废弃；C# 版无内置 MCP server，用 CLI/HTTP 等价替代。

## 快速上手（AI 推荐路径）

```powershell
# 1. 查看全部会话（找虚拟桌面，会话名含 #0 的就是）
& "<XiaoGongZuoTai.exe 完整路径>" --status

# 2. 查看某虚拟桌面画面（自动直连 XgtVd2 画面服务，截图保存到 ResGuard\xgt_live.png）
& "<XiaoGongZuoTai.exe 完整路径>" --connect 16

# 3. 向虚拟桌面注入文本/键鼠（XgtVd2 keep 运行中时）
& "<XiaoGongZuoTai.exe 完整路径>" --input 16 text "你好，豆包"
& "<XiaoGongZuoTai.exe 完整路径>" --input 16 key 13        # Enter

# 4. 新建一个虚拟桌面（XgtVd2 三合一：创建→确认→画面服务常驻，全程 3~60 秒）
& "<XiaoGongZuoTai.exe 完整路径>" --newvd
```

## 关键注意事项

1. **关闭是异步的**：`WTSLogoffSession` 返回成功 ≠ 会话立即消失。注销需要数秒~数十秒，期间会话仍显示 Active。关闭后应**轮询等待会话从 `query session` 消失**再判定成功（UI 已内置 30 秒轮询；脚本调用时自行轮询）。
2. **画面服务端口**：由 `XgtVd2.exe`（keep 模式）监听 `17777+会话ID`（如会话 16 → 17778）。`/frame` 取 JPEG 帧、`/input` 注入输入、`/clip` 取/送剪贴板、`/stop` 停止。新建成功后助手常驻提供画面；主程序 GUI 关闭时自动清理助手进程。
3. **画面不依赖 XgtHelper**（2026-09-14 起）：画面服务已内建到 XgtVd2，跨会话画面直连可用，无需安装计划任务。桌面"小工作台-一键安装助手.bat"仅作为关闭兜底的可选增强，非必需。
4. **关闭/新建有破坏性**：关闭虚拟桌面会结束该会话内所有程序；执行前必须向用户确认目标会话。新建时会自动先关闭现有子会话（免确认，Windows 只允许一个子会话）。
5. **新建虚拟桌面架构（2026-09-14 三合一定稿）**：主程序内直接持有 RDP ActiveX 或含 WTS 轮询循环的方法，在 .NET 4.8 下会触发 CLR 内部错误 `0xc0000005`（反复实证）。最终方案：主程序零 ActiveX、零 WTS 轮询——启动 `XgtVd2.exe <密码> keep`（独立进程内 Connect 创建子会话→WTS 轮询（State=0 即 Active，勿误判为 Listen）→写 `C:\Users\Public\Xgt\newvd_result.txt`→启动内建 HTTP 画面服务→常驻）→主程序仅文件轮询读会话 ID→直连画面端口。`--newvd` CLI 完成即 `Environment.Exit(0)` 硬退出（绕过析构，exit=0 实证不崩）；GUI 新建后主窗体常驻，画面由 WebBrowser 内核拉帧显示（浏览器内核处理 HTTP/解码，规避主程序集 JIT 崩溃）。
6. 详细用法见 `references\usage.md`，故障排查见 `references\troubleshooting.md`。
