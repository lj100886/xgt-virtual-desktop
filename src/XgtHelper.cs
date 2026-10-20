using System;
using System.IO;
using System.Runtime.InteropServices;

// XgtHelper - SYSTEM elevation helper (runs via scheduled task as SYSTEM).
// Reads C:\Users\Public\Xgt\request.txt (session id),
// duplicates the target session's user token and launches XgtCapture.exe
// inside that session via CreateProcessAsUser.
// Writes result to C:\Users\Public\Xgt\result.txt as "OK:<pid>:<port>" or error.
class XgtHelper
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetTokenInformation(IntPtr hToken, int TokenInformationClass, IntPtr pTokenInformation, uint TokenInformationLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSEnableChildSessions(bool fEnable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSIsChildSessionsEnabled(out bool pfEnable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSLogoffSession(IntPtr hServer, int SessionId, bool bWait);

    const uint MAXIMUM_ALLOWED = 0x02000000;
    const int SecurityImpersonation = 2;
    const int TokenPrimary = 1;
    const int TokenSessionId = 12;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    static string Dir = @"C:\Users\Public\Xgt";

    static void Main(string[] args)
    {
        Directory.CreateDirectory(Dir);
        // -daemon：常驻守护模式——轮询 request.txt，只要有请求且 result 未写就处理（幂等）。
        // 小工作台只需写 request.txt + 删 result.txt，无需任何提权触发。
        if (args.Length > 0 && args[0].ToLowerInvariant() == "-daemon")
        {
            try
            {
                File.AppendAllText(Dir + @"\helper.log", DateTime.Now.ToString("HH:mm:ss") + " daemon start (pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + ")\r\n");
                while (true)
                {
                    try
                    {
                        string reqFile = Dir + @"\request.txt";
                        string resFile = Dir + @"\result.txt";
                        if (File.Exists(reqFile) && !File.Exists(resFile))
                        {
                            string req = File.ReadAllText(reqFile).Trim();
                            if (req.Length > 0) Handle(req);
                        }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(400);
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Dir + @"\helper.log", "daemon fatal: " + ex.Message + "\r\n"); } catch { }
            }
            return;
        }
        try
        {
            string reqFile = Dir + @"\request.txt";
            string resFile = Dir + @"\result.txt";

            if (!File.Exists(reqFile)) { File.WriteAllText(resFile, "NO_REQ"); return; }
            string req = File.ReadAllText(reqFile).Trim();
            Handle(req);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Dir + @"\result.txt", "EX:" + ex.Message); } catch { }
        }
    }

    static void Handle(string req)
    {
        try
        {
            string resFile = Dir + @"\result.txt";
            // 特殊命令：启用/禁用子会话（虚拟桌面机制）
            if (req == "enable_child")
            {
                bool okEnable = WTSEnableChildSessions(true);
                bool chk;
                bool okChk = WTSIsChildSessionsEnabled(out chk);
                File.WriteAllText(resFile, (okEnable && okChk && chk) ? "CHILD_OK" : "CHILD_FAIL:" + Marshal.GetLastWin32Error() + ":enabled=" + (okChk ? chk.ToString() : "?"));
                return;
            }
            if (req == "disable_child")
            {
                bool okDisable = WTSEnableChildSessions(false);
                File.WriteAllText(resFile, okDisable ? "CHILD_DISABLED" : "CHILD_FAIL:" + Marshal.GetLastWin32Error());
                return;
            }
            // 特殊命令：关闭（注销）虚拟桌面会话 logoff:<sid>
            if (req.StartsWith("logoff:"))
            {
                int lsid;
                if (!int.TryParse(req.Substring(7), out lsid)) { File.WriteAllText(resFile, "BAD_SESSION:" + req); return; }
                bool okLogoff = WTSLogoffSession((IntPtr)0, lsid, false);
                File.WriteAllText(resFile, okLogoff ? "LOGOFF_OK:" + lsid : "LOGOFF_FAIL:" + lsid + ":" + Marshal.GetLastWin32Error());
                return;
            }

            uint sid;
            if (!uint.TryParse(req, out sid)) { File.WriteAllText(resFile, "BAD_SESSION:" + req); return; }

            IntPtr token;
            if (!WTSQueryUserToken(sid, out token))
            { File.WriteAllText(resFile, "WTS_FAIL:" + Marshal.GetLastWin32Error()); return; }

            IntPtr dupToken;
            if (!DuplicateTokenEx(token, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dupToken))
            { File.WriteAllText(resFile, "DUP_FAIL:" + Marshal.GetLastWin32Error()); CloseHandle(token); return; }

            IntPtr pSid = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(pSid, (int)sid);
            SetTokenInformation(dupToken, TokenSessionId, pSid, 4);
            Marshal.FreeHGlobal(pSid);

            IntPtr env = IntPtr.Zero;
            CreateEnvironmentBlock(out env, dupToken, false);

            int port = 17777 + (int)sid;
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string capPath = Path.Combine(exeDir, "XgtCapture.exe");
            string cmdLine = "\"" + capPath + "\" " + sid + " " + port;

            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpDesktop = "winsta0\\default";

            PROCESS_INFORMATION pi;
            bool ok = CreateProcessAsUser(dupToken, capPath, cmdLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT, env, @"C:\", ref si, out pi);
            if (!ok)
            { File.WriteAllText(resFile, "FAIL:" + sid + ":" + Marshal.GetLastWin32Error()); }
            else
            {
                File.WriteAllText(resFile, "OK:" + sid + ":" + pi.dwProcessId + ":" + port);
                CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
            }

            CloseHandle(dupToken);
            CloseHandle(token);
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Dir + @"\result.txt", "EX:" + ex.Message); } catch { }
        }
    }
}