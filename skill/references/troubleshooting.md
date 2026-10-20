# 小工作台 · 故障排查（troubleshooting.md）

> 按现象查找；每个条目给出根因与处理。

## 1. 虚拟桌面"关不掉"（关闭后会话仍在列表）

**现象**：点"关闭虚拟桌面"或调用 WTSLogoffSession 返回成功，但会话仍显示 Active，反复操作无效。

**根因**：`WTSLogoffSession` 是**异步注销**——返回 True 只代表请求已被接受，会话从 Active 到真正从 WTS 列表消失需要数秒~数十秒。UI 旧版"关闭后立即刷新列表"会把正在注销的会话又显示回来，造成"没关掉"的假象。

**处理**：
- 关闭后**轮询等待**：每 0.5~1 秒执行 `query session`（或 WTSEnumerateSessions），直到目标会话 ID 消失（上限 30 秒）。
- 2026-09-14 已修复 UI：关闭后状态栏显示"注销请求已生效，等待会话退出..."，轮询消失后才提示"已彻底关闭"并刷新列表。新版无需再手动等。
- 会话内有未响应的程序时注销可能更慢，最多等 30 秒仍存在则属正常后台退出，稍后刷新即可。

## 2. 查看画面失败（view: no port after wait / helper not installed）

**现象**：`--connect <sid>` 后日志出现 `view: no port after wait`，拿不到画面。

**根因（2026-09-14 已消除）**：旧版依赖 XgtHelper（SYSTEM 计划任务）把 XgtCapture 注入目标会话，未安装则失败。**现在画面服务已内建到 XgtVd2.exe**（keep 模式，新建虚拟桌面后常驻，端口 `17777+会话ID`），跨会话画面直连可用，**不再依赖 XgtHelper**。

**处理**：
- 若新建后画面失败：确认 XgtVd2 进程存活（`Get-Process XgtVd2`）、端口可连（`Test-NetConnection 127.0.0.1 -Port 1777X`）。
- 若会话是旧会话（非本次新建，助手已退出）：用 `--connect <sid>` 时主程序会尝试直连；无助手进程时需先重新新建或查看会话是否仍 Active。
- 桌面"小工作台-一键安装助手.bat"现仅作关闭兜底的可选增强，不装不影响画面。

## 3. XgtHelper 计划任务查询报 Access denied

**说明**：计划任务以 SYSTEM 运行，普通权限查询可能被拒。画面功能已不依赖 XgtHelper（见第 2 条），此条仅在需要关闭兜底时参考：安装/删除需管理员（UAC）。

## 4. 关闭失败：助手未响应（计划任务权限不足？）

**现象**：状态栏出现该提示，且日志 `close: helper result=` 无记录。

**根因**：直接 WTSLogoffSession 失败（如权限不足），走 helper 兜底，但 XgtHelper 计划任务不存在/被禁/响应超时。

**处理**：
1. 主路径：确认目标会话确为虚拟桌面（名含 `#0`）；console/rdp-tcp 会话不可关。
2. 兜底路径：装 XgtHelper（见第 3 条），或手动 `schtasks /run /tn XgtHelper` 后查看 `C:\Users\Public\Xgt\result.txt`。

## 5. 新建虚拟桌面后会话未出现

**现象**：`--newvd` 完成后 `query session` 无新 `#0` 会话。

**说明**：子会话创建由三合一助手 `XgtVd2.exe <密码> keep` 完成（独立进程内 Connect→WTS 轮询→写 `C:\Users\Public\Xgt\newvd_result.txt`→画面服务常驻）。排查顺序：
1. 看主日志 `%LOCALAPPDATA%\ResGuard\xgt.log`：应有 `vd2: connected, session created on server` 与 `vd: result file id=N`（主程序收到）。
2. 无 `vd2:` 行 → 助手未启动/崩溃：确认 `XgtVd2.exe` 与主程序同目录。
3. 有 `vd2: connected` 但无 `poll result sid=N` → 轮询未命中：确认日志 `poll enum rN ... <ID>:<NAME>(0)` 中**子会话 State=0 是 Active（WTS 枚举 0=Active，6=Listen，勿把 0 当 Listen 过滤掉）**；子会话创建初期 Name 可能为空（注销/创建过渡期），XgtVd2 自清与轮询已按"Id>4 且 State≠6"判定，不依赖 Name。
4. 有 `vd2: connected` 但主程序 `result file timeout` → 会话创建延迟波动（2~30 秒）或上一轮残留会话未注销干净（XgtVd2 Connect 前会自动注销残留并等待消失，最多 30 秒）；主程序会自动用备用密码重试（双密码 pwd0/pwd1）。
5. 主程序报"两个密码都未成功"：凭据错误（`TERMSRV/localhost` 的 CRED 中主/备密码），去 GUI 设置重新保存。
6. Windows 同时只允许一个子会话：新建前必须已关闭旧 `#0` 会话（XgtVd2 Connect 前自动预关残留）。

## 5b. 新建虚拟桌面时主程序崩溃 0xc0000005（clr.dll）

**现象**：`--newvd` 或 GUI 新建时主程序崩溃，exit code `-1073741819`（0xc0000005），事件查看器 faulting module 为 `clr.dll`（offset 0x114161 附近），日志停在某关键行后无下文。

**根因（2026-09-14 实测，四条独立崩溃通道全部规避）**：
- 主程序内直接持有 RDP ActiveX（AxHost/mstscax）：Connect 返回后 ActiveX 析构触发 CLR 内部错误。→ 已迁移：ActiveX 只存在于独立进程 XgtVd2。
- 主程序集内"含 `HashSet<int>` + WTS 枚举轮询的循环方法"JIT 编译崩溃（实例/静态、optimize±、anycpu/x86 均复现；独立小程序同构 exit=0 不崩）。→ 已迁移：WTS 轮询只在 XgtVd2 独立进程。
- 主程序内 `HttpWebRequest`/`WebClient` 异步拉帧回调（Timer 回调方法体 JIT 编译崩溃）。→ 已迁移：画面改由 GUI 内 **WebBrowser 控件**加载 HTML 拉帧（浏览器内核处理 HTTP/解码，主程序零拉帧代码）；CLI `--newvd` 成功后 `Environment.Exit(0)` 硬退出（实证 exit=0 不崩）。
- GUI 隐藏/关闭期间 `refreshTimer` 触发 `RefreshSessions()` 时 ListView 句柄未就绪 → 内部 `BeginInvoke` 抛 InvalidOperationException（ThreadException，不崩但刷屏）。→ 已修复：刷新前检查 `IsDisposed/IsHandleCreated` 并静默捕获。

**处理**：当前版本（v1.2，2026-09-14）全链路 exit=0 实证通过：新建（CLI exit=0）→ 画面帧可取（HTTP 200）→ GUI 关闭（WM_CLOSE exit=0，XgtVd2 自动清理）。若再次出现崩溃，检查 `%LOCALAPPDATA%\ResGuard\dumps\`（LocalDumps 已配置）与 `XgtVd2.exe` 是否被替换/删除。

## 6. 画面模糊/卡顿

**说明**：画面走 JPEG 帧轮询（Quality 72，约 180ms 间隔，XgtVd2 抓帧线程）。高清需求可用 `--connect` 直连或调整 XgtVd2 参数；CPU 占用高时会掉帧，属预期。GUI 内嵌画面由 WebBrowser 每 500ms 刷新一帧。

## 7. 输入注入无效果

**检查**：
1. 目标会话的 XgtVd2 画面服务是否在运行（`Test-NetConnection 127.0.0.1 -Port 1777X`）。
2. 会话是否为 Active 状态（断开的会话接收不到输入）。
3. `combo` 名称是否在支持列表内（ctrl+c/ctrl+v/alt+tab/win+d 等，见 Main.cs SendCombo）。
4. 中文文本走 `text` 需要 UTF-8 编码（CLI 已处理）；剪贴板粘贴走 `/clip?set=` + Ctrl+V。

## 8. 日志位置

- 主日志：`%LOCALAPPDATA%\ResGuard\xgt.log`（含 `vd:`/`view:`/`close:`/`vd2:` 关键事件，UTF-8）
- 创建结果：`C:\Users\Public\Xgt\newvd_result.txt`（会话 ID）、`newvd_connected.txt`（连接标记）
- 助手：`C:\Users\Public\Xgt\helper.log`（仅关闭兜底路径使用）
- 崩溃 dump：`%LOCALAPPDATA%\ResGuard\dumps\`（LocalDumps 已配置 XiaoGongZuoTai.exe）
