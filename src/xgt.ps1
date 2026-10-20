# 小工作台 · AI 封装脚本
# 用法（PowerShell）：
#   . .\xgt.ps1                          # 导入函数
#   xgt list                              # 枚举会话（含虚拟桌面识别）
#   xgt view <sid>                        # 查看虚拟桌面画面（截图到 ResGuard\xgt_live.png）
#   xgt shot [path]                       # 监控中心截图
#   xgt new                               # 新建虚拟桌面
#   xgt input <sid> <cmd> [args...]       # 注入输入（text/key/click/dbl/right/move/scroll/combo）
#   xgt reconnect <sid>                   # 画面助手意外退出（画面全黑）后，把画面接回原会话
#   xgt config [<键> <值>]                # 查看/设置关闭策略（auto_close / close_confirm）
#   xgt close <sid> [-force]              # 关闭虚拟桌面（WTSLogoffSession + 轮询等待消失，最多 30 秒）
#                                          #   close_confirm=on 时需 -force 或交互确认；=off 时直接关

# 可通过环境变量覆盖可执行文件位置：$env:XGT_EXE / $env:XGT_CLI
# 缺省取 %LOCALAPPDATA%\ResGuard\（与程序默认日志目录一致）
$resDir = Join-Path $env:LOCALAPPDATA "ResGuard"
$script:Exe = if ($env:XGT_EXE) { $env:XGT_EXE } else { Join-Path $resDir "XiaoGongZuoTai.exe" }
# 命令行一律走控制台版（/target:exe）。
# 原因（2026-10-02 实测）：GUI 版是 winexe，从 PowerShell 调用时
#   ① Console.WriteLine 的内容完全不输出  ② 调用方也不等它结束
# 而 & .\xgt.ps1 正是 PowerShell 场景。两者同源编译，build_main_ascii.bat 一次出两个。
$script:Cli = if ($env:XGT_CLI) { $env:XGT_CLI } else { Join-Path $resDir "XiaoGongZuoTaiCli.exe" }
if (-not (Test-Path $script:Cli)) { $script:Cli = $script:Exe }

function xgt {
    param(
        [Parameter(Position = 0)] [string] $Action,
        [Parameter(ValueFromRemainingArguments = $true)] [string[]] $Args2
    )
    switch ($Action) {
        "list" {
            query session
        }
        "view" {
            $sid = $Args2[0]
            & $script:Cli --connect $sid
        }
        "shot" {
            $p = if ($Args2[0]) { $Args2[0] } else { Join-Path $resDir "xgt_ui.png" }
            & $script:Cli --screenshot $p
        }
        "new" {
            & $script:Cli --newvd
        }
        "input" {
            & $script:Cli --input @Args2
        }
        "reconnect" {
            $sid = $Args2[0]
            if (-not $sid) { Write-Host "用法: xgt reconnect <会话ID>"; return }
            # 助手进程（XgtVd2）是虚拟桌面的"生命维持"：它一退出，子会话立刻变 Disconnected、画面全黑。
            # --reconnect 用原会话 ID 重新拉起助手（attach 模式：不新建、不注销），会话内程序不丢。
            # 判据是 WTS 里会话回到 Active（HTTP 起来不算数），失败会自动换备用密码重试。
            & $script:Cli --reconnect $sid
        }
        "config" {
            # 关闭策略交给 AI 自己设：xgt config                  → 查看
            #                    xgt config auto_close idle:30   → 空闲 30 分钟自动注销
            #                    xgt config auto_close never     → 永不自动关闭（默认）
            #                    xgt config close_confirm off    → 关闭不再要人工确认
            if ($Args2.Count -ge 2) { & $script:Cli --set $Args2[0] $Args2[1] }
            else { & $script:Cli --config }
        }
        "close" {
            $sid = $Args2[0]
            if (-not $sid) { Write-Host "用法: xgt close <会话ID>"; return }
            # close_confirm=off 时跳过交互确认（AI 自动化场景）；设置见 xgt config
            $confirm = "on"
            $conf = "C:\Users\Public\Xgt\xgt.conf"
            if (Test-Path $conf) {
                foreach ($ln in Get-Content $conf) {
                    $t = $ln.Trim()
                    if ($t -match '^close_confirm\s*=\s*(\S+)') { $confirm = $Matches[1].ToLower() }
                }
            }
            if ($confirm -ne "off" -and -not $Args2.Contains("-force")) {
                $ans = Read-Host "确认关闭虚拟桌面会话 $sid？该会话内所有程序都会结束 [y/N]"
                if ($ans -notmatch '^[yY]') { Write-Host "已取消"; return }
            }
            # 交给 CLI --close 统一处理：先请画面助手 XgtVd2 退出、注销会话、轮询确认消失，
            # 并兜底清理 keep 常驻的助手进程。
            # （此处原先的独立实现只调 WTSLogoffSession，不会结束 XgtVd2，
            #   实测会留下 200MB+ 的空转孤儿进程。）
            & $script:Cli --close $sid
        }
        default {
            Write-Host "用法: xgt list|view <sid>|shot [path]|new|reconnect <sid>|config [<键> <值>]|input <sid> <cmd>...|close <sid> [-force]"
        }
    }
}

# 导入后可直接调用；也可用 -Command 方式： powershell -NoProfile -File xgt.ps1 <action> ...
if ($MyInvocation.InvocationName -ne ".") {
    # 作为脚本直接执行：把参数透传
    xgt @args
}
