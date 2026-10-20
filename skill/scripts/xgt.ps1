# 小工作台 · AI 封装脚本
# 用法（PowerShell）：
#   . .\xgt.ps1                          # 导入函数
#   xgt list                              # 枚举会话（含虚拟桌面识别）
#   xgt view <sid>                        # 查看虚拟桌面画面（截图到 ResGuard\xgt_live.png）
#   xgt shot [path]                       # 监控中心截图
#   xgt new                               # 新建虚拟桌面
#   xgt input <sid> <cmd> [args...]       # 注入输入（text/key/click/dbl/right/move/scroll/combo）
#   xgt close <sid> [-force]              # 关闭虚拟桌面（WTSLogoffSession + 轮询等待消失，最多 30 秒）

# 可通过环境变量 $env:XGT_EXE 覆盖可执行文件位置；缺省取 %LOCALAPPDATA%\ResGuard\
$resDir = Join-Path $env:LOCALAPPDATA "ResGuard"
$script:Exe = if ($env:XGT_EXE) { $env:XGT_EXE } else { Join-Path $resDir "XiaoGongZuoTai.exe" }

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
            & $script:Exe --connect $sid
        }
        "shot" {
            $p = if ($Args2[0]) { $Args2[0] } else { Join-Path $resDir "xgt_ui.png" }
            & $script:Exe --screenshot $p
        }
        "new" {
            & $script:Exe --newvd
        }
        "input" {
            & $script:Exe --input @Args2
        }
        "close" {
            $sid = $Args2[0]
            if (-not $sid) { Write-Host "用法: xgt close <会话ID>"; return }
            if (-not $Args2.Contains("-force")) {
                $ans = Read-Host "确认关闭虚拟桌面会话 $sid？该会话内所有程序都会结束 [y/N]"
                if ($ans -notmatch '^[yY]') { Write-Host "已取消"; return }
            }
            Add-Type @"
using System;
using System.Runtime.InteropServices;
public class XgtWts {
    [DllImport("wtsapi32.dll", SetLastError=true)] public static extern bool WTSLogoffSession(IntPtr hServer, int SessionId, bool bWait);
}
"@
            $ok = [XgtWts]::WTSLogoffSession([IntPtr]::Zero, [int]$sid, $false)
            $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            Write-Host "WTSLogoffSession($sid) => $ok err=$err"
            if (-not $ok) { Write-Host "直接注销失败（err=$err），可能需 XgtHelper 计划任务兜底"; return }
            Write-Host "注销请求已生效，轮询等待会话消失（最长 30 秒）..."
            $deadline = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
                $exists = query session $sid 2>$null
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "会话 $sid 已彻底关闭"
                    return
                }
                Write-Host "  ...会话 $sid 仍在注销中"
            }
            Write-Host "30 秒仍未消失：注销请求已生效，会话在后台退出中，稍后 query session 确认"
        }
        default {
            Write-Host "用法: xgt list|view <sid>|shot [path]|new|input <sid> <cmd>...|close <sid>"
        }
    }
}

# 导入后可直接调用；也可用 -Command 方式： powershell -NoProfile -File xgt.ps1 <action> ...
if ($MyInvocation.InvocationName -ne ".") {
    # 作为脚本直接执行：把参数透传
    xgt @args
}
