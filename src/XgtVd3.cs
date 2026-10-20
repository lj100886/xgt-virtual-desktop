// XgtVd3 - 虚拟桌面会话轮询助手（独立进程，无 ActiveX）
// 背景：主程序 XiaoGongZuoTai 内部做 WTS 会话轮询时，特定方法 JIT 编译触发
// .NET 4.8 CLR 内部错误 0xc0000005（2026-09-14 21:52-22:00 实证，实例/静态方法链均崩，
// 而独立小程序 TestPoll 同构代码 exit=0 不崩）。因此把轮询移到独立进程：
// 主程序仅做 File.Exists 文件轮询（零 WTS 调用），本进程做 WTS 枚举。
// 行为：轮询 12s 找新出现的子会话（ID>4 且名称形如 7A78855482A0...#0），
// 找到后把会话 ID 写入 C:\Users\Public\Xgt\newvd_result.txt 并退出 0；超时退出 2。
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Threading;

namespace XgtVd3
{
    internal class SessionInfo3 { public int Id; public string Name; public int State; }

    internal static class Native3
    {
        public const int WTS_CURRENT_SERVER_HANDLE = 0;
        public const int WTSWinStationName = 6;
        [DllImport("wtsapi32.dll")] public static extern bool WTSEnumerateSessions(IntPtr h, int v, int f, out IntPtr pp, out int cnt);
        [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] public static extern bool WTSQuerySessionInformationW(IntPtr h, int sid, int cls, out IntPtr pp, out int cnt);
        [StructLayout(LayoutKind.Sequential)] public struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }
    }

    internal static class Program3
    {
        [STAThread]
        static void Main()
        {
            Log("vd3: start");
            int sid = PollNewSessionId(12000);
            if (sid > 0)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(@"C:\Users\Public\Xgt");
                    System.IO.File.WriteAllText(@"C:\Users\Public\Xgt\newvd_result.txt", sid.ToString());
                    Log("vd3: result written id=" + sid);
                }
                catch (Exception ex) { Log("vd3: write err " + ex.Message); }
                Environment.Exit(0);
            }
            Log("vd3: timeout, no new session");
            Environment.Exit(2);
        }

        static int PollNewSessionId(int timeoutMs)
        {
            var before = EnumSessions();
            HashSet<int> beforeIds = new HashSet<int>();
            foreach (var sb in before) beforeIds.Add(sb.Id);
            int wait = 0;
            int round = 0;
            while (wait < timeoutMs)
            {
                Application.DoEvents();
                Thread.Sleep(500);
                wait += 500;
                round++;
                if (round % 4 == 0) Log("vd3: poll round " + (round / 4));
                var sessions = EnumSessions();
                foreach (var s in sessions)
                {
                    if (beforeIds.Contains(s.Id)) continue;
                    if (s.Id <= 4 || s.Name.Length <= 8) continue; // 子会话名形如 7A78855482A0...#0
                    Log("vd3: new session id=" + s.Id + " name=" + s.Name);
                    return s.Id;
                }
            }
            return 0;
        }

        static List<SessionInfo3> EnumSessions()
        {
            List<SessionInfo3> list = new List<SessionInfo3>();
            IntPtr p;
            int count;
            if (!Native3.WTSEnumerateSessions((IntPtr)Native3.WTS_CURRENT_SERVER_HANDLE, 0, 1, out p, out count)) return list;
            try
            {
                int size = Marshal.SizeOf(typeof(Native3.WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    Native3.WTS_SESSION_INFO si = (Native3.WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(p.ToInt64() + i * size), typeof(Native3.WTS_SESSION_INFO));
                    SessionInfo3 s = new SessionInfo3();
                    s.Id = si.SessionId;
                    s.State = si.State;
                    IntPtr pn; int n;
                    Native3.WTSQuerySessionInformationW((IntPtr)0, si.SessionId, Native3.WTSWinStationName, out pn, out n);
                    s.Name = pn == IntPtr.Zero ? "" : Marshal.PtrToStringUni(pn);
                    if (pn != IntPtr.Zero) Native3.WTSFreeMemory(pn);
                    list.Add(s);
                }
            }
            finally { Native3.WTSFreeMemory(p); }
            return list;
        }

        static void Log(string msg)
        {
            try
            {
                string f = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResGuard", "xgt.log");
                System.IO.File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + msg + "\r\n");
            }
            catch { }
        }
    }
}
