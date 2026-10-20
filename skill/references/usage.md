# 小工作台 · 详细用法（usage.md）

> 程序：`%LOCALAPPDATA%\ResGuard\XiaoGongZuoTai.exe`
> 全部命令均可在普通权限下运行。画面服务由 `XgtVd2.exe`（keep 模式）内建提供（端口 `17777+会话ID`），**不依赖 XgtHelper 计划任务**。

## 一、CLI 命令清单

### 1. 枚举会话状态

```powershell
& "...\XiaoGongZuoTai.exe" --status
# 或 -s
```

输出监控中心窗口截图（保存到 `%LOCALAPPDATA%\ResGuard\xgt_ui.png`）并显示会话状态文本。左侧列表中的虚拟桌面会话名含 `#0`（如 `7A78855482A04FA781DC#0`）。

替代系统命令：`query session`（会话名/ID/状态；`>console` 为当前用户会话，`rdp-tcp` 为监听器，均不可关闭）。

### 2. 保存监控中心截图

```powershell
& "...\XiaoGongZuoTai.exe" --screenshot "C:\path\out.png"
# 或 --shot / -p；缺省路径 = ResGuard\xgt_ui.png
```

### 3. 查看虚拟桌面画面（连接 + 截图）

```powershell
& "...\XiaoGongZuoTai.exe" --connect <会话ID>
# 或 -c <sid> [port]
```

- 自动直连该会话的画面服务（新建后由 XgtVd2 常驻提供），等待数秒后保存快照到 `%LOCALAPPDATA%\ResGuard\xgt_live.png` 并退出。
- 带端口时直连指定端口：`-c 16 17778`。
- **注意**：`--connect` 保存的是**监控中心窗口**的截图（含画面预览区），不是纯会话画面。实时帧请直接 HTTP 拉取（见下）。

### 4. 新建虚拟桌面

```powershell
& "...\XiaoGongZuoTai.exe" --newvd
# 或 -n
```

创建新的子会话虚拟桌面（XgtVd2 三合一：Connect 创建 → WTS 轮询确认 → 画面服务常驻）。全程 3~60 秒（含自动预关旧会话与双密码重试），完成后 CLI 硬退出（exit=0），新会话 ID 出现在 `query session` 中（名含 `#0`），结果文件 `C:\Users\Public\Xgt\newvd_result.txt` 记录会话 ID。

### 5. 向虚拟桌面注入输入

```powershell
& "...\XiaoGongZuoTai.exe" --input <会话ID> <cmd> [参数...]
# 或 -i
```

| cmd | 参数 | 示例 |
|---|---|---|
| `text` | 文本内容 | `--input 16 text 你好世界` |
| `key` | 虚拟键码 | `--input 16 key 13`（Enter） |
| `keydown` / `keyup` | 虚拟键码 | `--input 16 keydown 17`（Ctrl 按下） |
| `click` / `dbl` / `right` | x y | `--input 16 click 500 300` |
| `move` | x y | `--input 16 move 400 200` |
| `scroll` | x y d | `--input 16 scroll 500 300 -120` |
| `combo` | 组合名 | `--input 16 combo ctrl+c`（见 SendCombo 支持表） |

前置条件：目标会话的画面代理已在运行（否则端口不通，命令无效果）。代理可通过 `--connect` 拉起。

## 二、HTTP API（XgtVd2 画面服务，兼容 XgtCapture API）

画面服务（XgtVd2 keep 模式）监听 `http://127.0.0.1:17777+<会话ID>/`（会话 16 → 17793）。

| 端点 | 方法 | 说明 |
|---|---|---|
| `/frame` | GET | 取当前画面帧（JPEG 二进制） |
| `/input?cmd=<c>&k=<v>&x=<x>&y=<y>&t=<text>&d=<delta>` | GET | 输入注入（与 CLI `--input` 同构） |
| `/clip` | GET | 取目标会话剪贴板文本 |
| `/clip?set=<urlencoded>` | GET | 向目标会话写入剪贴板（配合 Ctrl+V 粘贴） |
| `/stop` | GET | 停止代理 |

PowerShell 取帧示例：

```powershell
Invoke-WebRequest "http://127.0.0.1:17778/frame" -OutFile frame.jpg
```

## 三、推荐的 AI 调用流程

1. `query session` 或 `--status` → 找到含 `#0` 的虚拟桌面会话 ID
2. `--connect <ID>` → 拉起画面代理并确认目标会话存活
3. `Invoke-WebRequest .../frame` → 观察画面（可循环取样）
4. `--input <ID> text/click/key ...` → 操作虚拟桌面
5. `.../clip` → 读结果 / `.../clip?set=` + Ctrl+V → 送内容进虚拟桌面
6. 需要关闭时：确认 → `WTSLogoffSession`（或 UI 关闭按钮）→ **轮询 `query session` 等会话消失**

## 四、脚本封装

`scripts\xgt.ps1` 提供 `xgt` 函数封装上述流程（`xgt list / view <sid> / input <sid> ... / new / close <sid>`），AI 可直接调用。

## 五、权限说明

- 会话枚举、截图、画面查看、输入注入：普通权限即可。
- 新建/关闭虚拟桌面：同用户会话通常可直接执行（WTSLogoffSession）；关闭兜底走 SYSTEM 助手 XgtHelper（可选安装，不影响画面）。
- XgtHelper 安装：管理员权限一次（桌面"小工作台-一键安装助手.bat"），仅关闭兜底需要。
