using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// XgtCapture - session capture + input agent.
// Runs INSIDE a target session (launched by XgtHelper via CreateProcessAsUser).
//   GET /frame         -> latest JPEG frame (+ header X-Desktop: "w h")
//   GET /info          -> "w h"
//   GET /input?cmd=..  -> inject mouse/keyboard into THIS session
//   GET /stop          -> terminate
class XgtCapture
{
    static object _lock = new object();
    static byte[] _latest = null;
    static int _port = 17777;
    static string _sessionId = "1";
    static int _deskW = 1920, _deskH = 1080;

    [DllImport("user32.dll")]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")]
    static extern IntPtr GetMessageExtraInfo();

    const uint INPUT_MOUSE = 0;
    const uint INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint MOUSEEVENTF_WHEEL = 0x0800;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }

    static void Log(string m)
    {
        try { File.AppendAllText(@"C:\Users\Public\Xgt\capture.log", DateTime.Now.ToString("HH:mm:ss") + " " + m + "\r\n"); } catch { }
    }

    static void Main(string[] args)
    {
        try
        {
            if (args.Length > 0) _sessionId = args[0];
            if (args.Length > 1) _port = int.Parse(args[1]);

            Log("capture started session=" + _sessionId + " port=" + _port);

            Thread cap = new Thread(CaptureLoop);
            cap.IsBackground = true;
            cap.Start();

            HttpListener hl = new HttpListener();
            hl.Prefixes.Add("http://127.0.0.1:" + _port + "/");
            hl.Start();
            Log("http listening on " + _port);

            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = hl.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(HandleRequest, ctx);
            }
        }
        catch (Exception ex)
        {
            Log("capture FATAL: " + ex);
        }
    }

    static void HandleRequest(object o)
    {
        HttpListenerContext ctx = (HttpListenerContext)o;
        try
        {
            string p = ctx.Request.Url.AbsolutePath;
            if (p == "/frame")
            {
                byte[] data;
                lock (_lock) data = _latest;
                if (data != null)
                {
                    ctx.Response.ContentType = "image/jpeg";
                    ctx.Response.ContentLength64 = data.Length;
                    ctx.Response.Headers["X-Desktop"] = _deskW + " " + _deskH;
                    ctx.Response.OutputStream.Write(data, 0, data.Length);
                }
                else ctx.Response.StatusCode = 503;
            }
            else if (p == "/info")
            {
                byte[] b = Encoding.ASCII.GetBytes(_deskW + " " + _deskH);
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength64 = b.Length;
                ctx.Response.OutputStream.Write(b, 0, b.Length);
            }
            else if (p == "/input")
            {
                HandleInput(ctx);
            }
            else if (p == "/stop")
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
                Log("capture stop requested");
                Environment.Exit(0);
            }
            else ctx.Response.StatusCode = 404;
        }
        catch { }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    static void HandleInput(HttpListenerContext ctx)
    {
        string cmd = ctx.Request.QueryString["cmd"];
        int x = 0, y = 0, d = 120, k = 0;
        int.TryParse(ctx.Request.QueryString["x"], out x);
        int.TryParse(ctx.Request.QueryString["y"], out y);
        int.TryParse(ctx.Request.QueryString["d"], out d);
        int.TryParse(ctx.Request.QueryString["k"], out k);
        if (string.IsNullOrEmpty(cmd))
        {
            ctx.Response.StatusCode = 400;
            byte[] b = Encoding.UTF8.GetBytes("cmd required");
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            return;
        }

        switch (cmd)
        {
            case "move":
                SetCursorPos(x, y);
                SendMouse(MOUSEEVENTF_MOVE, 0, 0, 0);
                break;
            case "down":
                SetCursorPos(x, y);
                SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0);
                break;
            case "up":
                SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
                break;
            case "click":
                SetCursorPos(x, y);
                SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0);
                SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
                break;
            case "dbl":
                SetCursorPos(x, y);
                for (int i = 0; i < 2; i++)
                {
                    SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0, 0);
                    SendMouse(MOUSEEVENTF_LEFTUP, 0, 0, 0);
                    Thread.Sleep(30);
                }
                break;
            case "right":
                SetCursorPos(x, y);
                SendMouse(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0);
                SendMouse(MOUSEEVENTF_RIGHTUP, 0, 0, 0);
                break;
            case "scroll":
                SetCursorPos(x, y);
                SendMouse(MOUSEEVENTF_WHEEL, (uint)d, 0, 0);
                break;
            case "key":
                SendKey((ushort)k, false);
                SendKey((ushort)k, true);
                break;
            case "keydown":
                SendKey((ushort)k, false);
                break;
            case "keyup":
                SendKey((ushort)k, true);
                break;
            case "text":
                string t = ctx.Request.QueryString["t"];
                if (!string.IsNullOrEmpty(t))
                {
                    t = Uri.UnescapeDataString(t);
                    foreach (char ch in t)
                    {
                        SendUnicodeChar(ch);
                        Thread.Sleep(5);
                    }
                }
                break;
            default:
                ctx.Response.StatusCode = 400;
                break;
        }
        ctx.Response.StatusCode = 200;
        byte[] ok = Encoding.ASCII.GetBytes("ok");
        ctx.Response.OutputStream.Write(ok, 0, ok.Length);
    }

    static void SendMouse(uint flags, uint mouseData, int dx, int dy)
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

    static void CaptureLoop()
    {
        while (true)
        {
            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                _deskW = bounds.Width;
                _deskH = bounds.Height;
                using (Bitmap bmp = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
                    }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        ImageCodecInfo jpeg = null;
                        foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                            if (c.FormatID == ImageFormat.Jpeg.Guid) { jpeg = c; break; }
                        EncoderParameters ep = new EncoderParameters(1);
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 55L);
                        bmp.Save(ms, jpeg, ep);
                        lock (_lock) _latest = ms.ToArray();
                    }
                }
            }
            catch { }
            Thread.Sleep(300);
        }
    }
}