// XgtAgent.cs — 会话内输入 / 抓屏代理（虚拟桌面 = RDP 子会话专用）
//
// 为什么需要它：
//   RDP ActiveX 宿主窗口一旦不可见（屏外 / LAYERED 无 alpha），RDP 客户端
//   就不再向远端转发任何输入 —— PostMessage、SendInput 全部失效。
//   见 docs/输入注入排查-2026-10-02.md。
//
// 本代理运行在【目标会话内部】，直接用 SendInput 注入到自己的桌面，
//   完全绕开 RDP 输入管线；顺带在会话内直接抓屏（比 PrintWindow 抓 RDP 窗口更清晰）。
//
// 部署：
//   1) 编译出 XgtAgent.exe，放到 C:\Users\Public\Xgt\
//   2) 加【用户自启动】（HKCU\...\Run 或 启动文件夹）—— 子会话登录时自动运行
//   3) 控制侧经 127.0.0.1:17790 调用（loopback 跨会话共享，无需转发）
//
// 为什么不自启在 console 会话：console 与子会话共用同一个 loopback 端口，
//   谁先占谁得。本代理默认在 SessionId<=1 时直接退出，避免抢占。
//
// 接口（HTTP/1.0，响应完即关闭连接；body 为 UTF-8 文本或 image/jpeg）：
//   GET /ping                                    -> "pong sid=<n>"
//   GET /info                                    -> "sid=<n> screen=<w>x<h> fg=<hwnd> title=<t>"
//   GET /frame                                   -> JPEG 本会话桌面全屏
//   GET /shot?x=&y=&w=&h=                        -> JPEG 区域
//   GET /input?cmd=click&x=&y=                   -> 左键单击
//   GET /input?cmd=dbl&x=&y=                     -> 双击
//   GET /input?cmd=right&x=&y=                   -> 右键单击
//   GET /input?cmd=down|up&x=&y=                 -> 左键按下 / 抬起
//   GET /input?cmd=rightdown|rightup&x=&y=
//   GET /input?cmd=move&x=&y=                    -> 移动光标
//   GET /input?cmd=wheel&d=                      -> 滚轮（正数向上）
//   GET /input?cmd=key&k=13                      -> 单键（按下+抬起）
//   GET /input?cmd=keydown|keyup&k=
//   GET /input?cmd=combo&k=17,16,27              -> 组合键（顺序按下，逆序抬起）
//   GET /input?cmd=text&t=<urlencoded>           -> Unicode 文本（KEYEVENTF_UNICODE）
//   任意 input 可附加 &pm=1                      -> 额外向当前前台窗口 PostMessage（兼容 Raw Input 应用）
//
// 用法：XgtAgent.exe [-port N] [-force]
//   -port N   监听端口（默认 17790）
//   -force    即使运行在 console 会话也启动（默认 SessionId<=1 直接退出）
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public class XgtAgent
{
    const int DEFAULT_PORT = 17790;
    const string LOGPATH = @"C:\Users\Public\Xgt\agent.log";

    // ---------------- Win32 ----------------
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int nMax);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdcDst, int x, int y, int cx, int cy,
                                                        IntPtr hdcSrc, int sx, int sy, uint rop);

    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_WHEEL = 0x0800;
    const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    static int _port = DEFAULT_PORT;
    static bool _extraPost = false;   // 本次请求是否附加 PostMessage

    // ---------------- 日志 ----------------
    static readonly object _logLock = new object();
    static void Log(string m)
    {
        try
        {
            lock (_logLock)
            {
                if (File.Exists(LOGPATH) && new FileInfo(LOGPATH).Length > 2 * 1024 * 1024)
                    File.Move(LOGPATH, LOGPATH + ".old");
                File.AppendAllText(LOGPATH,
                    DateTime.Now.ToString("MM-dd HH:mm:ss") + " " + m + "\r\n");
            }
        }
        catch { }
    }

    // ---------------- 输入注入 ----------------
    static void SendMouse(uint flags, int dx, int dy, uint mouseData)
    {
        INPUT i = new INPUT();
        i.type = INPUT_MOUSE;
        i.u.mi.dx = dx; i.u.mi.dy = dy;
        i.u.mi.mouseData = mouseData;
        i.u.mi.dwFlags = flags;
        i.u.mi.time = 0;
        i.u.mi.dwExtraInfo = GetMessageExtraInfo();
        SendInput(1, new INPUT[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }

    static void SendKey(ushort vk, bool up)
    {
        INPUT i = new INPUT();
        i.type = INPUT_KEYBOARD;
        i.u.ki.wVk = vk;
        i.u.ki.wScan = 0;
        i.u.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
        i.u.ki.time = 0;
        i.u.ki.dwExtraInfo = IntPtr.Zero;
        SendInput(1, new INPUT[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }

    static void SendUnicodeChar(char ch)
    {
        INPUT i = new INPUT();
        i.type = INPUT_KEYBOARD;
        i.u.ki.wVk = 0;
        i.u.ki.wScan = (ushort)ch;
        i.u.ki.dwFlags = KEYEVENTF_UNICODE;
        i.u.ki.time = 0;
        i.u.ki.dwExtraInfo = IntPtr.Zero;
        SendInput(1, new INPUT[] { i }, Marshal.SizeOf(typeof(INPUT)));
        i.u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(1, new INPUT[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }

    // 兜底：直接给当前前台窗口发窗口消息（部分 Raw Input 应用只认这个）
    static void PostKey(ushort vk, bool up)
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return;
        PostMessage(h, up ? WM_KEYUP : WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
    }

    // ---------------- 抓屏（会话内直抓） ----------------
    static byte[] Capture(int x, int y, int w, int h)
    {
        try
        {
            if (w <= 0 || h <= 0) { w = GetSystemMetrics(0); h = GetSystemMetrics(1); x = 0; y = 0; }
            using (Bitmap bmp = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdcDst = g.GetHdc();
                    IntPtr hdcSrc = GetDC(IntPtr.Zero);
                    try { BitBlt(hdcDst, 0, 0, w, h, hdcSrc, x, y, SRCCOPY | CAPTUREBLT); }
                    finally { ReleaseDC(IntPtr.Zero, hdcSrc); g.ReleaseHdc(hdcDst); }
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Jpeg);
                    return ms.ToArray();
                }
            }
        }
        catch (Exception ex) { Log("capture err: " + ex.Message); return null; }
    }

    // ---------------- query 解析 ----------------
    static string GetQ(string q, string key)
    {
        if (string.IsNullOrEmpty(q)) return "";
        foreach (string part in q.TrimStart('?').Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part.Substring(0, eq) == key)
            {
                try { return Uri.UnescapeDataString(part.Substring(eq + 1)); }
                catch { return part.Substring(eq + 1); }
            }
        }
        return "";
    }

    static int GetI(string q, string key, int dflt)
    {
        int v;
        return int.TryParse(GetQ(q, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : dflt;
    }

    static string FgTitle()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "";
        StringBuilder sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---------------- 请求处理 ----------------
    static string HandleInput(string q)
    {
        string cmd = GetQ(q, "cmd");
        _extraPost = GetQ(q, "pm") == "1";
        int x = GetI(q, "x", 0), y = GetI(q, "y", 0);
        int vk;

        switch (cmd)
        {
            case "click":
                SetCursorPos(x, y); SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0); SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
                break;
            case "dbl":
                SetCursorPos(x, y);
                for (int i = 0; i < 2; i++) { SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0); SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0); Thread.Sleep(40); }
                break;
            case "right":
                SetCursorPos(x, y); SendMouse(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0); SendMouse(MOUSEEVENTF_RIGHTUP, 0, 0, 0);
                break;
            case "down":
                SetCursorPos(x, y); SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0); break;
            case "up":
                SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0); break;
            case "rightdown":
                SetCursorPos(x, y); SendMouse(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0); break;
            case "rightup":
                SendMouse(MOUSEEVENTF_RIGHTUP, 0, 0, 0); break;
            case "move":
                SetCursorPos(x, y); SendMouse(MOUSEEVENTF_MOVE, 0, 0, 0); break;
            case "wheel":
                SendMouse(MOUSEEVENTF_WHEEL, 0, 0, (uint)GetI(q, "d", 120)); break;
            case "key":
                vk = GetI(q, "k", 0);
                if (vk > 0) { SendKey((ushort)vk, false); if (_extraPost) PostKey((ushort)vk, false);
                              SendKey((ushort)vk, true);  if (_extraPost) PostKey((ushort)vk, true); }
                break;
            case "keydown":
                vk = GetI(q, "k", 0);
                if (vk > 0) { SendKey((ushort)vk, false); if (_extraPost) PostKey((ushort)vk, false); }
                break;
            case "keyup":
                vk = GetI(q, "k", 0);
                if (vk > 0) { SendKey((ushort)vk, true); if (_extraPost) PostKey((ushort)vk, true); }
                break;
            case "combo":
                // 顺序按下，逆序抬起
                string[] parts = GetQ(q, "k").Split(',');
                int[] keys = new int[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    int.TryParse(parts[i].Trim(), out keys[i]);
                for (int i = 0; i < keys.Length; i++)
                    if (keys[i] > 0) { SendKey((ushort)keys[i], false); if (_extraPost) PostKey((ushort)keys[i], false); }
                for (int i = keys.Length - 1; i >= 0; i--)
                    if (keys[i] > 0) { SendKey((ushort)keys[i], true); if (_extraPost) PostKey((ushort)keys[i], true); }
                break;
            case "text":
                foreach (char ch in GetQ(q, "t")) { SendUnicodeChar(ch); Thread.Sleep(6); }
                break;
            default:
                return "unknown cmd: " + cmd;
        }
        return "OK cmd=" + cmd + " fg=" + FgTitle();
    }

    // ---------------- 剪贴板（会话内直读/直写本会话剪贴板） ----------------
    // Clipboard 需 STA 线程；HTTP 回调线程是 MTA，必须在临时 STA 线程上调用。
    // 注意：这里操作的是【本会话自己的】剪贴板，不依赖 RDP 剪贴板重定向。
    static string StaClip(string q)
    {
        string res = "ERR: unknown";
        Thread t = new Thread(delegate()
        {
            try
            {
                if (q != null && q.IndexOf("set=", StringComparison.Ordinal) >= 0)
                {
                    Clipboard.SetText(GetQ(q, "set"));
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

    // ---------------- HTTP 响应 ----------------
    static void WriteResp(Stream s, string status, string ctype, byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes(
            "HTTP/1.0 " + status + "\r\n" +
            "Content-Type: " + ctype + "\r\n" +
            "Content-Length: " + (body == null ? 0 : body.Length) + "\r\n" +
            "Connection: close\r\n\r\n");
        s.Write(head, 0, head.Length);
        if (body != null && body.Length > 0) s.Write(body, 0, body.Length);
        s.Flush();
    }

    static void Serve(TcpClient c)
    {
        try
        {
            c.ReceiveTimeout = 5000;
            c.SendTimeout = 15000;
            NetworkStream ns = c.GetStream();

            // 读请求行（够用即可，不解析多余头部）
            byte[] buf = new byte[4096];
            int n = 0;
            try { n = ns.Read(buf, 0, buf.Length); } catch { }
            string raw = Encoding.ASCII.GetString(buf, 0, n);
            string line = raw.Split('\n')[0].Trim();
            if (line.Length == 0) return;

            string[] sp = line.Split(' ');
            if (sp.Length < 2) { WriteResp(ns, "400 Bad Request", "text/plain", Encoding.UTF8.GetBytes("bad request")); return; }
            string target = sp[1];

            string path = target, q = "";
            int qi = target.IndexOf('?');
            if (qi >= 0) { path = target.Substring(0, qi); q = target.Substring(qi + 1); }

            int sid = Process.GetCurrentProcess().SessionId;

            if (path == "/ping")
            {
                WriteResp(ns, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("pong sid=" + sid));
            }
            else if (path == "/info")
            {
                string info = "sid=" + sid +
                              " screen=" + GetSystemMetrics(0) + "x" + GetSystemMetrics(1) +
                              " fg=0x" + GetForegroundWindow().ToInt64().ToString("X") +
                              " title=" + FgTitle();
                WriteResp(ns, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(info));
            }
            else if (path == "/frame")
            {
                byte[] jpeg = Capture(0, 0, 0, 0);
                if (jpeg == null) WriteResp(ns, "500 Internal Server Error", "text/plain", Encoding.UTF8.GetBytes("capture failed"));
                else WriteResp(ns, "200 OK", "image/jpeg", jpeg);
            }
            else if (path == "/shot")
            {
                byte[] jpeg = Capture(GetI(q, "x", 0), GetI(q, "y", 0), GetI(q, "w", 0), GetI(q, "h", 0));
                if (jpeg == null) WriteResp(ns, "500 Internal Server Error", "text/plain", Encoding.UTF8.GetBytes("capture failed"));
                else WriteResp(ns, "200 OK", "image/jpeg", jpeg);
            }
            else if (path == "/clip")
            {
                WriteResp(ns, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(StaClip(q)));
            }
            else if (path == "/input")
            {
                string r = HandleInput(q);
                if (Environment.GetEnvironmentVariable("XGTAGENT_TRACE") == "1") Log("input " + q + " -> " + r);
                WriteResp(ns, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(r));
            }
            else
            {
                WriteResp(ns, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("not found: " + path));
            }
        }
        catch (Exception ex) { Log("serve err: " + ex.Message); }
        finally { try { c.Close(); } catch { } }
    }

    // ---------------- 入口 ----------------
    public static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-port" && i + 1 < args.Length) int.TryParse(args[i + 1], out _port);
            if (args[i] == "-force") _force = true;
        }
        try { SetProcessDPIAware(); } catch { }

        int sid = Process.GetCurrentProcess().SessionId;
        Directory.CreateDirectory(@"C:\Users\Public\Xgt");
        Log("agent start sid=" + sid + " port=" + _port + " force=" + _force);

        if (sid <= 1 && !_force)
        {
            Log("console session -> exit (by design)");
            return 0;
        }

        // 单实例（按端口隔离；Global\ 前缀跨会话唯一）
        bool created;
        Mutex mutex = new Mutex(true, "Global\\XgtAgent_" + _port, out created);
        if (!created)
        {
            Log("another instance on port " + _port + " -> exit");
            return 0;
        }

        TcpListener ls;
        try
        {
            ls = new TcpListener(IPAddress.Loopback, _port);
            ls.Start();
        }
        catch (Exception ex)
        {
            Log("listen failed on " + _port + ": " + ex.Message + " -> exit");
            return 1;
        }
        Log("tcp listen 127.0.0.1:" + _port + " sid=" + sid);

        while (true)
        {
            try
            {
                TcpClient c = ls.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(delegate(object o) { Serve((TcpClient)o); }, c);
            }
            catch (Exception ex) { Log("accept err: " + ex.Message); Thread.Sleep(300); }
        }
    }

    static bool _force = false;
}
