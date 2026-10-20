// TestPoll - 最小复现：PollNewSession 的 JIT 崩溃是否由基础原语（EnumSessions/HashSet/DoEvents）引起
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Threading;

class SInfo { public int Id; public string Name; public int State; }

class NativeT
{
    public const int WTS_CURRENT_SERVER_HANDLE = 0;
    [DllImport("wtsapi32.dll")] public static extern bool WTSEnumerateSessions(IntPtr h, int v, int f, out IntPtr pp, out int cnt);
    [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] public static extern bool WTSQuerySessionInformationW(IntPtr h, int sid, int cls, out IntPtr pp, out int cnt);
    [StructLayout(LayoutKind.Sequential)] public struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }
}

class TestPoll
{
    [STAThread]
    static void Main()
    {
        Log("TP: poll-enter");
        var before = EnumS();
        Log("TP: before count=" + before.Count);
        HashSet<int> ids = new HashSet<int>();
        foreach (var s in before) ids.Add(s.Id);
        int wait = 0, round = 0;
        while (wait < 10000)
        {
            Application.DoEvents();
            Thread.Sleep(500);
            wait += 500;
            round++;
            Log("TP: round " + round);
            var sessions = EnumS();
            foreach (var s in sessions)
            {
                if (ids.Contains(s.Id)) continue;
                if (s.Id <= 4 || s.Name.Length <= 8) continue;
                Log("TP: new session id=" + s.Id + " name=" + s.Name);
                return;
            }
        }
        Log("TP: done");
    }

    static List<SInfo> EnumS()
    {
        List<SInfo> list = new List<SInfo>();
        IntPtr p;
        int count;
        if (!NativeT.WTSEnumerateSessions((IntPtr)NativeT.WTS_CURRENT_SERVER_HANDLE, 0, 1, out p, out count)) return list;
        try
        {
            int size = Marshal.SizeOf(typeof(NativeT.WTS_SESSION_INFO));
            for (int i = 0; i < count; i++)
            {
                NativeT.WTS_SESSION_INFO si = (NativeT.WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(p.ToInt64() + i * size), typeof(NativeT.WTS_SESSION_INFO));
                SInfo s = new SInfo();
                s.Id = si.SessionId;
                s.State = si.State;
                IntPtr pn; int n;
                NativeT.WTSQuerySessionInformationW((IntPtr)0, si.SessionId, 6, out pn, out n);
                s.Name = pn == IntPtr.Zero ? "" : Marshal.PtrToStringUni(pn);
                if (pn != IntPtr.Zero) NativeT.WTSFreeMemory(pn);
                list.Add(s);
            }
        }
        finally { NativeT.WTSFreeMemory(p); }
        return list;
    }

    static void Log(string m)
    {
        try { System.IO.File.AppendAllText("tpoll.log", DateTime.Now.ToString("HH:mm:ss") + " " + m + "\r\n"); }
        catch { }
    }
}
