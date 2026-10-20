// XgtVd2 - 虚拟桌面创建+画面服务助手（独立进程承载 RDP ActiveX，常驻）
// 背景：主程序直接创建 ActiveX 时 Connect 后析构/后台线程触发 CLR 0xc0000005；
// 但独立进程保持 ActiveX 不崩（2026-09-14 22:35 实证 12s+ 存活，会话 38 创建成功，
// PrintWindow 抓 mstscax 画面有效 98% 非黑）。因此本助手常驻：
//   Connect -> 轮询 WTS 找新会话 ID -> 写结果文件 -> HTTP 服务（17777+会话ID，与
//   XgtCapture API 兼容：/frame /input /clip /stop）-> 保持消息循环渲染画面。
// 主程序仅文件轮询 + 复用现有 StartView/StopView（不再需要 XgtCapture 注入/XgtHelper）。
// 用法：XgtVd2.exe <password> [keep] [attach会话ID]
//   keep         = 常驻（默认仅创建后立即退出，用于非画面场景）
//   attach会话ID  = 接回模式：附着到已存在的会话，不新建、不注销残留（助手意外退出后恢复画面用）
// 关闭策略（AI 可设，读 C:\Users\Public\Xgt\xgt.conf）：
//   auto_close = never | idle:<分钟>   —— idle 时，距最后一次 HTTP 访问超过阈值就自动注销会话并退出
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;
using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;

namespace XgtVd2
{
    internal static class Native2
    {
        [DllImport("wtsapi32.dll")]
        public static extern bool WTSEnableChildSessions(bool bEnable);
        [DllImport("wtsapi32.dll")]
        public static extern bool WTSLogoffSession(IntPtr hServer, int SessionId, bool bWait);
        [DllImport("winsta.dll", SetLastError = true)]
        public static extern bool WinStationCreateChildSessionTransport(uint sessionId, out uint pTransportSessionId);
        [DllImport("wtsapi32.dll")] public static extern bool WTSEnumerateSessions(IntPtr h, int v, int f, out IntPtr pp, out int cnt);
        [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] public static extern bool WTSQuerySessionInformationW(IntPtr h, int sid, int cls, out IntPtr pp, out int cnt);
        [StructLayout(LayoutKind.Sequential)] public struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_CHAR = 0x0102;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_MOUSEWHEEL = 0x020A;

        // ---- 输入注入目标窗口定位（2026-10-02 修复）----
        // RDP ActiveX 真正接收键盘/鼠标的窗口是内部的 IHWindowClass（标题 "Input Capture Window"），
        // 而不是 ActiveX 宿主窗口。把 WM_KEYDOWN / WM_LBUTTONDOWN 发给宿主窗口会被 RDP 输入管线
        // 静默丢弃，表现为"命令发出去了、虚拟桌面毫无反应"（极难排查）。这里枚举子窗口定位它。
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        // vtable 布局（comtypes 实测）：槽3=PutProperty, 槽4=GetProperty
        [ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMsRdpExtendedSettings
        {
            void PutProperty([MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [MarshalAs(UnmanagedType.Struct)] object pValue);
            void GetProperty([MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [MarshalAs(UnmanagedType.Struct)] out object pValue);
        }

        // 凭据接口：ClearTextPassword 必须在 ocx 的 NonScriptable 接口上设置（AdvancedSettings 上设置无效，
        // 会导致 Windows 安全中心仍弹"凭据不工作"）。2026-09-17 初版修复；2026-09-28 修正：
        // 1) 旧版 IID 2E59EEF1... 是错的（E_NOINTERFACE），真实 IID 从 MSTSCLib.dll 类型库读取；
        // 2) InvokeMember 走默认 IDispatch 找不到该成员（DISP_E_UNKNOWNNAME）；
        // 3) 改为 IDispatch 接口强转（QI 真实 IID）+ DispId(1) 调用，依次尝试新旧接口。
        [ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IMsTscNonScriptable
        {
            [DispId(1)] void ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string clearTextPassword);
        }
        [ComImport, Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IMsRdpClientNonScriptable
        {
            [DispId(1)] void ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string clearTextPassword);
        }
        [ComImport, Guid("17A5E535-4072-4FA4-AF32-C8D0D47345E9"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IMsRdpClientNonScriptable2
        {
            [DispId(1)] void ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string clearTextPassword);
        }
        [ComImport, Guid("B3378D90-0728-45C7-8ED7-B6159FB92219"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IMsRdpClientNonScriptable3
        {
            [DispId(1)] void ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string clearTextPassword);
        }
        [ComImport, Guid("F50FA8AA-1C7D-4F59-B15C-A90CACAE1FCB"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IMsRdpClientNonScriptable4
        {
            [DispId(1)] void ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string clearTextPassword);
        }
        [ComImport, Guid("4F6996D5-D7B1-412C-B0FF-063718566907"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IMsRdpClientNonScriptable5
        {
            [DispId(1)] void ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string clearTextPassword);
        }
        [ComImport, Guid("05293249-B28B-4BD8-BE64-1B2F496B910E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IMsRdpClientNonScriptable6
        {
            [DispId(1)] void ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string clearTextPassword);
        }
        [ComImport, Guid("71B4A60A-FE21-46D8-A39B-8E32BA0C5ECC"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IMsRdpClientNonScriptable7
        {
            [DispId(1)] void ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string clearTextPassword);
        }

        // 依次尝试 QI 设置；返回是否成功
        public static bool TrySetClearTextPassword(object ocx, string pwd)
        {
            try { ((IMsTscNonScriptable)ocx).ClearTextPassword(pwd); return true; } catch { }
            try { ((IMsRdpClientNonScriptable)ocx).ClearTextPassword(pwd); return true; } catch { }
            try { ((IMsRdpClientNonScriptable2)ocx).ClearTextPassword(pwd); return true; } catch { }
            try { ((IMsRdpClientNonScriptable3)ocx).ClearTextPassword(pwd); return true; } catch { }
            try { ((IMsRdpClientNonScriptable4)ocx).ClearTextPassword(pwd); return true; } catch { }
            try { ((IMsRdpClientNonScriptable5)ocx).ClearTextPassword(pwd); return true; } catch { }
            try { ((IMsRdpClientNonScriptable6)ocx).ClearTextPassword(pwd); return true; } catch { }
            try { ((IMsRdpClientNonScriptable7)ocx).ClearTextPassword(pwd); return true; } catch { }
            return false;
        }
    }

    internal class RdpHost2 : AxHost
    {
        public RdpHost2() : base("{A3BC03A0-041D-42E3-AD22-882B7865C9C5}") { }
        public object Ocx { get { return GetOcx(); } }
    }

    internal static class Program2
    {
        static RdpHost2 _rdp;
        static IntPtr _capHwnd = IntPtr.Zero;
        static IntPtr _inputHwnd = IntPtr.Zero;   // RDP 内部输入窗口 IHWindowClass（输入注入目标）
        static byte[] _lastJpeg = null;
        static object _frameLock = new object();
        static volatile bool _stop = false;
        static string _pwd = "";

        [STAThread]
        static void Main(string[] args)
        {
            _pwd = args.Length > 0 ? args[0] : "";
            bool keep = args.Length > 1 && args[1] == "keep";
            // 2026-10-02 新增：第 3 个参数 = 已存在的会话 ID → “附着/重连”模式。
            // 助手进程意外退出后，子会话会停留在 Disconnected，画面变黑；此时用同样的 sid
            // 重新拉起 XgtVd2 即可接回原会话（不清残留、不新建，会话里跑着的程序都不丢）。
            int attachSid = 0;
            if (args.Length > 2) int.TryParse(args[2], out attachSid);
            _idleCloseSec = ParseAutoCloseSeconds(ReadConfKey("auto_close"));
            Log("vd2: start pwd=" + (string.IsNullOrEmpty(_pwd) ? "none" : "len" + _pwd.Length) + " keep=" + keep + " attach=" + attachSid + " auto_close=" + (ReadConfKey("auto_close") ?? "never") + "(" + _idleCloseSec + "s)");
            try { Native2.WTSEnableChildSessions(true); Log("vd2: child sessions enabled"); }
            catch (Exception ex) { Log("vd2: enable err " + ex.Message); }
            try
            {
                uint cur = (uint)Process.GetCurrentProcess().SessionId;
                uint tsid;
                bool tr = Native2.WinStationCreateChildSessionTransport(cur, out tsid);
                Log("vd2: transport=" + tr + " tsid=" + tsid + " err=" + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Log("vd2: transport err " + ex.Message); }

            Form host = new Form();
            host.ShowInTaskbar = false;
            host.Opacity = 0;
            host.Size = new System.Drawing.Size(1280, 800);
            host.StartPosition = FormStartPosition.Manual;
            host.Location = new System.Drawing.Point(-30000, -30000);
            RdpHost2 rdp = null;
            try
            {
                rdp = new RdpHost2();
                rdp.Dock = DockStyle.Fill;
                host.Controls.Add(rdp);
                host.Show();
                Application.DoEvents();
                DateTime wd = DateTime.Now.AddSeconds(3);
                while (DateTime.Now < wd)
                {
                    Application.DoEvents();
                    Thread.Sleep(100);
                    if (rdp.IsHandleCreated && rdp.Ocx != null && rdp.Width > 100) break;
                }
                Log("vd2: handle=" + rdp.IsHandleCreated + " size=" + rdp.Width + "x" + rdp.Height);
                object ocx = rdp.Ocx;
                Type t = ocx.GetType();
                _rdp = rdp;
                _capHwnd = rdp.Handle;

                Native2.IMsRdpExtendedSettings ext = (Native2.IMsRdpExtendedSettings)ocx;
                ext.PutProperty("ConnectToChildSession", true);
                object v;
                ext.GetProperty("ConnectToChildSession", out v);
                Log("vd2: childSession readback=" + v);

                try { t.InvokeMember("Server", BindingFlags.SetProperty, null, ocx, new object[] { "localhost" }); } catch { }
                try { t.InvokeMember("UserName", BindingFlags.SetProperty, null, ocx, new object[] { Environment.UserName }); } catch { }
                try { t.InvokeMember("DesktopWidth", BindingFlags.SetProperty, null, ocx, new object[] { 1280 }); } catch { }
                try { t.InvokeMember("DesktopHeight", BindingFlags.SetProperty, null, ocx, new object[] { 800 }); } catch { }
                // 凭据：ClearTextPassword 必须在 ocx 本体的 IMsTscNonScriptable 上设置（AdvancedSettings 上设置无效，
                // 会导致 Windows 安全中心仍弹"凭据不工作"）。2026-09-17 初版修复；2026-09-28 修正实现：
                // InvokeMember("ClearTextPassword") 在默认 IDispatch 上找不到该成员（DISP_E_UNKNOWNNAME），
                // 必须强转 IMsTscNonScriptable 后按 vtable 槽7 设置。
                if (!string.IsNullOrEmpty(_pwd))
                {
                    bool pwdOk = false;
                    try
                    {
                        pwdOk = Native2.TrySetClearTextPassword(ocx, _pwd);
                        if (pwdOk) Log("vd2: pwd set on ocx (NonScriptable iface)");
                    }
                    catch (Exception epw) { Log("vd2: pwd ocx fail " + epw.Message); }
                    try
                    {
                        object adv = t.InvokeMember("AdvancedSettings", BindingFlags.GetProperty, null, ocx, null);
                        if (adv != null)
                        {
                            Type ta = adv.GetType();
                            try { ta.InvokeMember("AuthenticationLevel", BindingFlags.SetProperty, null, adv, new object[] { 0 }); } catch { }
                            try { ta.InvokeMember("EnableCredSspSupport", BindingFlags.SetProperty, null, adv, new object[] { true }); } catch { }
                            if (!pwdOk)
                            {
                                try { ta.InvokeMember("ClearTextPassword", BindingFlags.SetProperty, null, adv, new object[] { _pwd }); Log("vd2: password set (adv fallback)"); }
                                catch (Exception epw2) { Log("vd2: pwd adv fail " + epw2.Message); }
                            }
                        }
                    }
                    catch { }
                }

                for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(100); }

                // Connect 前自清残留子会话：注销中/Disconnected 的旧会话会让 Connect 重连而非新建。
                // 判定不依赖 Name（注销过渡期 Name 可能为空）也不假设 ID>4（实测子会话 ID 可从 3 开始）：
                // 排除固定系统会话 ID（0=Services、1=Console、65537=RDP-Tcp）与 WTSListen(6) 后即视为子会话。
                // 2026-10-02：attach 模式（重连已存在会话）必须跳过本段，否则会把要接回的会话注销掉。
                for (int w = 0; attachSid <= 0 && w < 60; w++)
                {
                    bool found = false;
                    foreach (var s in EnumSessions())
                    {
                        if (s.Id == 0 || s.Id == 1 || s.Id == 65537) continue;
                        if (s.State == 6) continue;
                        Log("vd2: closing stale session " + s.Id + " state=" + s.State);
                        try { Native2.WTSLogoffSession((IntPtr)0, s.Id, false); } catch { }
                        found = true;
                    }
                    if (!found) break;
                    Application.DoEvents();
                    Thread.Sleep(500);
                }

                Log("vd2: connecting...");
                t.InvokeMember("Connect", BindingFlags.InvokeMethod, null, ocx, new object[] { });
                Log("vd2: connected, session created on server");
                // 定位 RDP 内部输入窗口（输入注入必须发给它，发给宿主窗口会被丢弃）
                _inputHwnd = FindInputWindow(host.Handle);
                Log("vd2: input window=" + (_inputHwnd == IntPtr.Zero
                    ? "NOT FOUND (fallback to host hwnd)"
                    : "0x" + _inputHwnd.ToInt64().ToString("X")));
                try
                {
                    System.IO.Directory.CreateDirectory(@"C:\Users\Public\Xgt");
                    System.IO.File.WriteAllText(@"C:\Users\Public\Xgt\newvd_connected.txt", "1");
                }
                catch { }

                if (keep)
                {
                    Log("vd2: staying alive (keep mode) attach=" + attachSid);
                    int sid = attachSid > 0 ? attachSid : PollNewSessionId(25000);
                    Log("vd2: poll result sid=" + sid);
                    if (sid > 0)
                    {
                        if (attachSid <= 0)
                        {
                            try { System.IO.File.WriteAllText(@"C:\Users\Public\Xgt\newvd_result.txt", sid.ToString()); Log("vd2: result written id=" + sid); }
                            catch (Exception ex) { Log("vd2: result write err " + ex.Message); }
                        }
                        StartHttp(17777 + sid);
                        // 抓帧线程：持续 PrintWindow -> JPEG 缓存（与 XgtCapture 180ms 轮询一致）
                        Thread cap = new Thread(CapLoop);
                        cap.IsBackground = true;
                        cap.Start();
                        Log("vd2: http on " + (17777 + sid) + " frame loop started");
                    }
                    // 保持消息循环（ActiveX 后台线程与渲染需要）
                    while (!_stop)
                    {
                        Application.DoEvents();
                        Thread.Sleep(300);
                        if (_idleCloseSec > 0)
                        {
                            double idle = (DateTime.Now - _lastAct).TotalSeconds;
                            if (idle >= _idleCloseSec)
                            {
                                Log("vd2: idle " + (int)idle + "s >= " + _idleCloseSec + "s -> auto-close session " + sid);
                                _stop = true;
                                try
                                {
                                    Native2.WTSLogoffSession(IntPtr.Zero, sid, false);
                                    Log("vd2: auto logoff requested sid=" + sid);
                                    // 助手是"生命维持"：先于会话退出会留下一个 Disconnected 残留会话
                                    // （2026-10-02 实测：助手退出后会话仍以"断开"状态滞留 1 分钟以上）。
                                    // 这里等它真的从 WTS 列表消失再退，与 --close 的轮询语义保持一致。
                                    for (int g = 0; g < 20; g++)
                                    {
                                        Application.DoEvents();
                                        Thread.Sleep(500);
                                        bool still = false;
                                        try
                                        {
                                            foreach (var ss in EnumSessions())
                                            {
                                                if (ss.Id == sid) { still = true; break; }
                                            }
                                        }
                                        catch { }
                                        if (!still) { Log("vd2: auto logoff confirmed, session " + sid + " gone"); break; }
                                    }
                                }
                                catch (Exception ex) { Log("vd2: auto logoff err " + ex.Message); }
                                break;
                            }
                        }
                    }
                    Log("vd2: stop requested, exiting");
                    Environment.Exit(0);
                }
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log("vd2: error " + ex);
                try { Environment.Exit(1); } catch { }
            }
            Log("vd2: fallthrough");
        }

        // ---- WTS 轮询：找新出现的子会话（独立进程，TestPoll 同构，安全）----
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
                var sessions = EnumSessions();
                if (round % 2 == 0)
                {
                    string names = "";
                    foreach (var s in sessions) names += s.Id + ":" + s.Name + "(" + s.State + ") ";
                    Log("vd2: poll enum r" + round + " " + names);
                }
                foreach (var s in sessions)
                {
                    if (beforeIds.Contains(s.Id)) continue;
                    // 排除固定系统会话：0=Services、1=Console、65537=RDP-Tcp；6=WTSListen（监听口）
                    if (s.Id == 0 || s.Id == 1 || s.Id == 65537) continue;
                    if (s.State == 6) continue;
                    // 子会话名形如 7A78855482A04FA781DC#0；新会话初期 Name 可能未就绪，
                    // 只要非系统固定 ID 且非 Listen 即视为子会话候选（实测子会话 ID 可从 3 开始，勿按 Id>4 过滤）
                    if (s.Name.Length > 8 || s.Name.IndexOf("#0", StringComparison.OrdinalIgnoreCase) >= 0) return s.Id;
                }
            }
            return 0;
        }

        class SInfo2 { public int Id; public string Name; public int State; }

        static System.Collections.Generic.List<SInfo2> EnumSessions()
        {
            var list = new System.Collections.Generic.List<SInfo2>();
            IntPtr p;
            int count;
            if (!Native2.WTSEnumerateSessions((IntPtr)0, 0, 1, out p, out count)) return list;
            try
            {
                int size = Marshal.SizeOf(typeof(Native2.WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    Native2.WTS_SESSION_INFO si = (Native2.WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(p.ToInt64() + i * size), typeof(Native2.WTS_SESSION_INFO));
                    SInfo2 s = new SInfo2();
                    s.Id = si.SessionId;
                    s.State = si.State;
                    IntPtr pn; int n;
                    Native2.WTSQuerySessionInformationW((IntPtr)0, si.SessionId, 6, out pn, out n);
                    s.Name = pn == IntPtr.Zero ? "" : Marshal.PtrToStringUni(pn);
                    if (pn != IntPtr.Zero) Native2.WTSFreeMemory(pn);
                    list.Add(s);
                }
            }
            finally { Native2.WTSFreeMemory(p); }
            return list;
        }

        // ---- 定位 RDP 内部输入窗口 IHWindowClass（2026-10-02）----
        // 从宿主窗口递归枚举所有后代，取类名为 IHWindowClass 的那个。
        // ---- 关闭策略：由助手自己执行"空闲释放"（它是生命维持进程，最清楚最后一次被访问的时间）----
        public const string ConfPath = @"C:\Users\Public\Xgt\xgt.conf";
        static int _idleCloseSec = 0;              // 0 = 永不自动关闭（默认，保持旧行为）
        static DateTime _lastAct = DateTime.Now;   // 最后一次 HTTP 访问时间

        public static string ReadConfKey(string key)
        {
            try
            {
                if (!File.Exists(ConfPath)) return null;
                foreach (string raw in File.ReadAllLines(ConfPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        // "never" -> 0；"idle:30" -> 1800 秒；非法值一律 0（安全兜底：不乱关）
        static int ParseAutoCloseSeconds(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            string s = v.Trim().ToLowerInvariant();
            if (!s.StartsWith("idle:")) return 0;
            double m;
            if (!double.TryParse(s.Substring(5).Trim(), out m) || m <= 0) return 0;
            return (int)Math.Ceiling(m * 60.0);
        }

        static IntPtr FindInputWindow(IntPtr root)
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                Native2.EnumChildWindows(root, delegate(IntPtr h, IntPtr l)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                    Native2.GetClassName(h, sb, 256);
                    if (sb.ToString() == "IHWindowClass") { found = h; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        // ---- 画面抓取：PrintWindow -> JPEG ----
        static void CapLoop()
        {
            while (!_stop)
            {
                try
                {
                    byte[] jpeg = CaptureFrame();
                    if (jpeg != null)
                    {
                        lock (_frameLock) { _lastJpeg = jpeg; }
                    }
                }
                catch { }
                Thread.Sleep(180);
            }
        }

        static byte[] CaptureFrame()
        {
            IntPtr h = _capHwnd;
            if (h == IntPtr.Zero) return null;
            Native2.RECT r;
            if (!Native2.GetWindowRect(h, out r)) return null;
            int w = r.R - r.L, hh = r.B - r.T;
            if (w <= 0 || hh <= 0) return null;
            using (Bitmap bmp = new Bitmap(w, hh))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    Native2.PrintWindow(h, hdc, 2);
                    g.ReleaseHdc(hdc);
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Jpeg);
                    return ms.ToArray();
                }
            }
        }

        // ---- HTTP 服务（与 XgtCapture API 兼容）----
        static void StartHttp(int port)
        {
            Thread t = new Thread(() =>
            {
                try
                {
                    HttpListener hl = new HttpListener();
                    hl.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    hl.Start();
                    while (!_stop)
                    {
                        try
                        {
                            HttpListenerContext ctx = hl.GetContext();
                            ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
                        }
                        catch { break; }
                    }
                }
                catch (Exception ex) { Log("vd2: http err " + ex.Message); }
            });
            t.IsBackground = true;
            t.Start();
        }

        // Clipboard 读/写必须在 STA 线程上执行（OLE 要求）。
        // 放到临时 STA 线程跑，避免污染 HttpListener 的 MTA 回调线程。
        static string StaClip(string q)
        {
            string res = "ERR: unknown";
            Thread t = new Thread(delegate()
            {
                try
                {
                    if (!string.IsNullOrEmpty(q) && q.StartsWith("?set="))
                    {
                        Clipboard.SetText(Uri.UnescapeDataString(q.Substring(5)));
                        res = "OK";
                    }
                    else
                    {
                        res = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                    }
                }
                catch (Exception ex) { res = "ERR " + ex.Message; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(4000)) return "ERR timeout";
            return res;
        }

        static void Handle(HttpListenerContext ctx)
        {
            try
            {
                _lastAct = DateTime.Now;   // 任何 HTTP 访问都算"有人在用"，重置空闲计时
                string path = ctx.Request.Url.AbsolutePath;
                if (path == "/frame")
                {
                    byte[] jpeg = null;
                    lock (_frameLock) { jpeg = _lastJpeg; }
                    if (jpeg == null) jpeg = CaptureFrame();
                    ctx.Response.ContentType = "image/jpeg";
                    ctx.Response.ContentLength64 = jpeg == null ? 0 : jpeg.Length;
                    if (jpeg != null) ctx.Response.OutputStream.Write(jpeg, 0, jpeg.Length);
                }
                else if (path == "/stop")
                {
                    WriteText(ctx, "OK");
                    _stop = true;
                }
                else if (path == "/clip")
                {
                    // WinForms Clipboard 只能在 STA 线程调用；HttpListener 回调线程是 MTA，
                    // 直接调用会抛 "在可以调用 OLE 之前，必须将当前线程设置为单线程单元(STA)模式"。
                    WriteText(ctx, StaClip(ctx.Request.Url.Query));
                }
                else if (path == "/input")
                {
                    HandleInput(ctx);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    WriteText(ctx, "not found");
                }
            }
            catch { }
            finally { try { ctx.Response.Close(); } catch { } }
        }

        static void HandleInput(HttpListenerContext ctx)
        {
            string q = ctx.Request.Url.Query;
            string cmd = GetParam(q, "cmd");
            // 输入必须发给 RDP 内部的 IHWindowClass；发给宿主窗口会被 RDP 输入管线丢弃（2026-10-02 修复）
            IntPtr h = _inputHwnd != IntPtr.Zero ? _inputHwnd : _capHwnd;
            if (cmd == "text")
            {
                string t = GetParam(q, "t");
                if (!string.IsNullOrEmpty(t))
                {
                    foreach (char c in t)
                    {
                        Native2.PostMessage(h, Native2.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                        Thread.Sleep(5);
                    }
                }
                WriteText(ctx, "OK");
            }
            else if (cmd == "key")
            {
                int k = 0;
                int.TryParse(GetParam(q, "k"), out k);
                if (k > 0)
                {
                    Native2.PostMessage(h, Native2.WM_KEYDOWN, (IntPtr)k, IntPtr.Zero);
                    Native2.PostMessage(h, Native2.WM_KEYUP, (IntPtr)k, IntPtr.Zero);
                }
                WriteText(ctx, "OK");
            }
            else if (cmd == "click")
            {
                int x = 0, y = 0;
                int.TryParse(GetParam(q, "x"), out x);
                int.TryParse(GetParam(q, "y"), out y);
                IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
                Native2.PostMessage(h, Native2.WM_LBUTTONDOWN, (IntPtr)1, lp);
                Native2.PostMessage(h, Native2.WM_LBUTTONUP, IntPtr.Zero, lp);
                WriteText(ctx, "OK");
            }
            else if (cmd == "scroll")
            {
                int d = 0;
                int.TryParse(GetParam(q, "d"), out d);
                IntPtr wp = (IntPtr)(d << 16);
                Native2.PostMessage(h, Native2.WM_MOUSEWHEEL, wp, IntPtr.Zero);
                WriteText(ctx, "OK");
            }
            else WriteText(ctx, "OK unknown");
        }

        static string GetParam(string query, string name)
        {
            if (string.IsNullOrEmpty(query)) return "";
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                int i = part.IndexOf('=');
                if (i > 0 && part.Substring(0, i) == name) return Uri.UnescapeDataString(part.Substring(i + 1));
            }
            return "";
        }

        static void WriteText(HttpListenerContext ctx, string s)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(s);
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            // 必须显式关闭：否则客户端（WebClient）可能等到超时才认为响应结束，
            // 表现为 /stop 之类"请求发了但对面迟迟不动"。
            ctx.Response.Close();
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
