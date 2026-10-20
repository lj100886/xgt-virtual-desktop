using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Reflection;

namespace XiaoGongZuoTai
{
    // ---- 会话内输入代理路由 ----
    // 背景：RDP ActiveX 宿主窗口在屏外/不可见时，RDP 客户端不向远端转发任何输入，
    // PostMessage 与 SendInput 全部失效（见 docs/输入注入排查-2026-10-02.md）。
    // XgtAgent.exe 运行在【虚拟桌面会话内部】，用 SendInput 直注该会话桌面，完全绕开 RDP 输入管线。
    internal static class InputAgent
    {
        public const int Port = 17790;
        static int _sid = int.MinValue;                        // 缓存的代理所属会话号；-1 = 无代理
        static DateTime _checkedAt = DateTime.MinValue;

        // 代理端口当前所属的会话号（-1 = 不可用）。结果缓存 5 秒。
        // 必须校验会话号：Windows 只允许一个子会话，代理为全局单实例（Global Mutex），
        // 若目标会话 != 代理所在会话，走代理会把输入打进错误的桌面。
        public static int SidOf()
        {
            if (_sid != int.MinValue && (DateTime.Now - _checkedAt).TotalSeconds < 5) return _sid;
            _checkedAt = DateTime.Now;
            _sid = -1;
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.Proxy = null;
                    string info = wc.DownloadString("http://127.0.0.1:" + Port + "/info");  // "sid=4 screen=.. fg=.. title=.."
                    if (info != null && info.StartsWith("sid="))
                    {
                        string rest = info.Substring(4);
                        int sp = rest.IndexOf(' ');
                        string num = sp < 0 ? rest : rest.Substring(0, sp);
                        int n;
                        if (int.TryParse(num.Trim(), out n)) _sid = n;
                    }
                }
            }
            catch { _sid = -1; }
            return _sid;
        }

        // 选输入端口：会话内代理优先（SendInput 直注），否则回退 RDP 助手端口（PostMessage 路径）
        public static int Pick(int sessionId, int fallbackPort)
        {
            return SidOf() == sessionId ? Port : fallbackPort;
        }

        // 滚轮命令名不同：会话内代理叫 wheel，RDP 助手叫 scroll
        public static string WheelCmd(int port)
        {
            return port == Port ? "wheel" : "scroll";
        }
    }

    internal static class Native
    {
        public const int WTS_CURRENT_SERVER_HANDLE = 0;
        public const int WTSActive = 0, WTSConnected = 1, WTSDisconnected = 4, WTSListen = 6;
        public const int WTSWinStationName = 6;
        public const int WTSUserName = 5;

        [StructLayout(LayoutKind.Sequential)]
        public struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr pWinStationName;
            public int State;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSEnumerateSessions(IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);
        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr pMemory);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool WTSQuerySessionInformation(IntPtr hServer, int SessionId, int WTSInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

        [StructLayout(LayoutKind.Sequential)]
        public struct CREDENTIAL
        {
            public int Flags; public int Type;
            public IntPtr TargetName; public IntPtr Comment;
            public long LastWritten; public int CredentialBlobSize;
            public IntPtr CredentialBlob; public int Persist;
            public int AttributeCount; public IntPtr Attributes;
            public IntPtr TargetAlias; public IntPtr UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string TargetName, int Type, int Flags, out IntPtr Credential);
        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr Buffer);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string TargetName, int Type, int Flags);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite(ref CREDENTIAL Credential, int Flags);

        public const int CRED_TYPE_GENERIC = 1;
        public const string CRED_TARGET = "TERMSRV/localhost";

        public static string GetCredUser()
        {
            IntPtr p;
            if (!CredRead(CRED_TARGET, CRED_TYPE_GENERIC, 0, out p)) return null;
            try
            {
                CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
                return c.UserName == IntPtr.Zero ? null : Marshal.PtrToStringUni(c.UserName);
            }
            finally { CredFree(p); }
        }

        // 返回 [主密码, 备用密码]（单个可能为 null）。存储格式：第一行=主，第二行=备（兼容旧版单密码）
        public static string[] GetCredPasswords()
        {
            IntPtr p;
            if (!CredRead(CRED_TARGET, CRED_TYPE_GENERIC, 0, out p)) return new string[] { null, null };
            try
            {
                CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
                if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize <= 0) return new string[] { null, null };
                byte[] b = new byte[c.CredentialBlobSize];
                Marshal.Copy(c.CredentialBlob, b, 0, c.CredentialBlobSize);
                string raw = Encoding.Unicode.GetString(b).TrimEnd('\0');
                int nl = raw.IndexOf('\n');
                if (nl < 0) return new string[] { raw, null };
                return new string[] { raw.Substring(0, nl), raw.Substring(nl + 1) };
            }
            finally { CredFree(p); }
        }

        public static string GetCredPassword() { string[] a = GetCredPasswords(); return a[0]; }
        public static string GetCredPassword2() { string[] a = GetCredPasswords(); return a[1]; }

        public static bool SaveCred(string user, string pass1, string pass2)
        {
            // 同时写入 TERMSRV/localhost 与 TERMSRV/127.0.0.1，兼容两种连接地址；主密码第一行，备用密码第二行
            string combo = string.IsNullOrEmpty(pass2) ? pass1 : pass1 + "\n" + pass2;
            bool all = true;
            foreach (string tgt in new string[] { "TERMSRV/localhost", "TERMSRV/127.0.0.1" })
            {
                CREDENTIAL c = new CREDENTIAL();
                c.Type = CRED_TYPE_GENERIC;
                c.TargetName = Marshal.StringToCoTaskMemUni(tgt);
                c.UserName = Marshal.StringToCoTaskMemUni(user);
                c.Persist = 2;
                byte[] b = Encoding.Unicode.GetBytes(combo + "\0");
                c.CredentialBlobSize = b.Length;
                c.CredentialBlob = Marshal.AllocCoTaskMem(b.Length);
                Marshal.Copy(b, 0, c.CredentialBlob, b.Length);
                bool ok = CredWrite(ref c, 0);
                Marshal.FreeCoTaskMem(c.TargetName);
                Marshal.FreeCoTaskMem(c.UserName);
                Marshal.FreeCoTaskMem(c.CredentialBlob);
                if (!ok) all = false;
            }
            return all;
        }

        public static bool DeleteCred()
        {
            bool all = true;
            foreach (string tgt in new string[] { "TERMSRV/localhost", "TERMSRV/127.0.0.1" })
            {
                if (!CredDelete(tgt, CRED_TYPE_GENERIC, 0)) all = false;
            }
            return all;
        }

        // ---- RDP ActiveX 手动创建（v12 无需 license，直接 CoCreateInstance）----
        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        public static object CreateRdpClient()
        {
            Guid clsid = new Guid("1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8"); // RDP Client Control v12（v13 在本机不可实例化）
            Guid iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
            IntPtr pv;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x5, ref iidUnknown, out pv);
            if (hr != 0) { Log.Write("rdp: CoCreateInstance hr=0x" + hr.ToString("X8")); return null; }
            return Marshal.GetObjectForIUnknown(pv);
        }

        // QI IMsRdpExtendedSettings（ConnectToChildSession 等扩展属性在此接口上）
        // vtable 布局（comtypes 实测）：槽3=PutProperty, 槽4=GetProperty
        [ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMsRdpExtendedSettings
        {
            void PutProperty([MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [MarshalAs(UnmanagedType.Struct)] object pValue);
            void GetProperty([MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [MarshalAs(UnmanagedType.Struct)] out object pValue);
        }

        // v12 客户端 IMsTscAx 双接口：vtable 顺序与系统 mstscax typelib 完全一致
        // （方法顺序取自 comtypes.gen.MSTSCLib，comtypes 强类型调用验证不崩）。
        // 用途：Server/UserName/DesktopWidth/Height/Connect 等走 vtable 直接调用，
        // 规避 IDispatch 晚期绑定在 ConnectToChildSession 组合下触发 CLR 内部错误 0xc0000005。
        [ComImport, Guid("8C11EFAE-92C3-11D1-BC1E-00C04FA31489"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        public interface IMsTscAx
        {
            void set_Server([MarshalAs(UnmanagedType.BStr)] string pServer);
            [return: MarshalAs(UnmanagedType.BStr)] string get_Server();
            void set_Domain([MarshalAs(UnmanagedType.BStr)] string pDomain);
            [return: MarshalAs(UnmanagedType.BStr)] string get_Domain();
            void set_UserName([MarshalAs(UnmanagedType.BStr)] string pUserName);
            [return: MarshalAs(UnmanagedType.BStr)] string get_UserName();
            void set_DisconnectedText([MarshalAs(UnmanagedType.BStr)] string pText);
            [return: MarshalAs(UnmanagedType.BStr)] string get_DisconnectedText();
            void set_ConnectingText([MarshalAs(UnmanagedType.BStr)] string pText);
            [return: MarshalAs(UnmanagedType.BStr)] string get_ConnectingText();
            [return: MarshalAs(UnmanagedType.I2)] bool get_Connected();
            void set_DesktopWidth(int pVal);
            int get_DesktopWidth();
            void set_DesktopHeight(int pVal);
            int get_DesktopHeight();
            void set_StartConnected(int fStartConnected);
            int get_StartConnected();
            int get_HorizontalScrollBarVisible();
            int get_VerticalScrollBarVisible();
            void set_FullScreenTitle([MarshalAs(UnmanagedType.BStr)] string pText);
            int get_CipherStrength();
            [return: MarshalAs(UnmanagedType.BStr)] string get_Version();
            int get_SecuredSettingsEnabled();
            IntPtr get_SecuredSettings();
            IntPtr get_AdvancedSettings();
            IntPtr get_Debugger();
            void Connect();
            void Disconnect();
            void CreateVirtualChannels([MarshalAs(UnmanagedType.BStr)] string Name);
            void SendOnVirtualChannel([MarshalAs(UnmanagedType.BStr)] string chanName, [MarshalAs(UnmanagedType.BStr)] string data);
        }

        [DllImport("wtsapi32.dll")]
        public static extern bool WTSEnableChildSessions(bool bEnable);
        [DllImport("wtsapi32.dll")]
        public static extern bool WTSIsChildSessionsEnabled();
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int WTSGetChildSessionId();
        [DllImport("winsta.dll", SetLastError = true)]
        public static extern bool WinStationCreateChildSessionTransport(uint sessionId, out uint pTransportSessionId);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSLogoffSession(IntPtr hServer, int SessionId, bool bWait);

        public static object QueryRdpExtended(object ocx)
        {
            try
            {
                IntPtr pUnk = Marshal.GetIUnknownForObject(ocx);
                Guid iidExt = new Guid("302D8188-0052-4807-806A-362B628F9AC5");
                IntPtr pExt;
                int hr = Marshal.QueryInterface(pUnk, ref iidExt, out pExt);
                Marshal.Release(pUnk);
                if (hr != 0) { Log.Write("rdp: QI Extended hr=0x" + hr.ToString("X8")); return null; }
                return Marshal.GetObjectForIUnknown(pExt);
            }
            catch (Exception ex) { Log.Write("rdp: QI ext error " + ex.Message); return null; }
        }
    }

    public static class Log
    {
        private static readonly string _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResGuard");
        private static readonly string _file = Path.Combine(_dir, "xgt.log");
        public static string LogPath { get { return _file; } }
        public static string SnapPath { get { return Path.Combine(_dir, "ui.png"); } }

        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(_file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + msg + "\r\n");
            }
            catch { }
        }

        public static void InstallGlobalHandlers()
        {
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Write("ThreadException: " + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Write("Unhandled: " + e.ExceptionObject);
            };
            Write("app started (pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + ")");
        }
    }

    public class SessionInfo
    {
        public int Id;
        public string Name;
        public string User;
        public int State;
        public bool IsVirtual
        {
            get { return Name != null && Name.IndexOf("#0", StringComparison.Ordinal) > 0 && State != Native.WTSListen; }
        }
        public string StateText
        {
            get
            {
                if (State == Native.WTSActive) return "Active";
                if (State == Native.WTSDisconnected) return "断开";
                if (State == Native.WTSListen) return "监听";
                return "其他";
            }
        }
    }

    // ===== 深色主题（2026-09-15 UI 美化；纯视觉，不影响逻辑）=====
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(20, 22, 28);       // 窗体背景
        public static readonly Color Panel = Color.FromArgb(24, 26, 33);    // 面板
        public static readonly Color Bar = Color.FromArgb(30, 33, 41);      // 工具栏/状态栏
        public static readonly Color Btn = Color.FromArgb(46, 51, 63);      // 按钮
        public static readonly Color BtnHover = Color.FromArgb(60, 67, 82);
        public static readonly Color BtnText = Color.FromArgb(232, 234, 238);
        public static readonly Color Accent = Color.FromArgb(43, 106, 255); // 主操作蓝
        public static readonly Color AccentHover = Color.FromArgb(66, 128, 255);
        public static readonly Color Text = Color.FromArgb(213, 217, 224);
        public static readonly Color Dim = Color.FromArgb(138, 145, 159);
        public static readonly Color Border = Color.FromArgb(48, 52, 64);
        public static readonly Color ListBg = Color.FromArgb(22, 24, 30);
        public static readonly Color ListSel = Color.FromArgb(38, 78, 160);
        public static readonly Color VdRow = Color.FromArgb(45, 41, 25);    // 虚拟桌面行（深色下金黄微光）
        public static readonly Color Ok = Color.FromArgb(94, 200, 110);
        public static readonly Color Bad = Color.FromArgb(235, 105, 95);
        public static readonly Color InputBg = Color.FromArgb(30, 33, 41);

        public static void Flat(Button b, bool accent = false)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = accent ? Accent : Btn;
            b.ForeColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = accent ? AccentHover : BtnHover;
            b.FlatAppearance.MouseDownBackColor = accent ? AccentHover : BtnHover;
            b.Padding = new Padding(7, 3, 7, 3);
            b.Margin = new Padding(3, 6, 0, 0);
            b.Cursor = Cursors.Hand;
        }

        public static void StyleDialog(Form f)
        {
            f.BackColor = Bg;
            f.ForeColor = Text;
            foreach (Control c in f.Controls)
            {
                if (c is Label) { c.ForeColor = Text; c.BackColor = Color.Transparent; }
                else if (c is TextBox)
                {
                    c.BackColor = InputBg;
                    c.ForeColor = Text;
                    ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is ListBox)
                {
                    c.BackColor = InputBg;
                    c.ForeColor = Text;
                    ((ListBox)c).BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is Button) { Flat((Button)c); }
            }
        }
    }

    public class MainForm : Form
    {
        private ListView lv;
        private Panel pnlRdp;
        private PictureBox picView;
        private System.Windows.Forms.Timer refreshTimer;
        private int viewPort = 0;
        private int viewSession = -1;
        private int viewW = 0, viewH = 0;
        private System.Windows.Forms.WebBrowser webView = null;
        private bool _suppressAuto = false;
        private Button btnRefresh, btnNew, btnShot, btnSettings, btnClose, btnClipIn, btnClipOut, btnText, btnKey;
        private RdpHost rdpHost = null; // AxHost 承载系统 mstscax（A3BC03A0，9/10 备份验证组合）
        private object _vdOcx = null;   // 兼容旧引用（不再使用）
        private Label lblCred, lblHint;
        private StatusStrip status;
        private ToolStripStatusLabel st;
        private ToolStripStatusLabel stCred;

        public MainForm()
        {
            Text = "小工作台 - 虚拟桌面监控中心";
            Font = new Font("Microsoft YaHei UI", 9.5F);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            ClientSize = new Size(1024, 660);
            MinimumSize = new Size(840, 500);
            BuildUI();
            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 5000;
            // ListView 句柄未就绪/窗体销毁期间刷新会触发内部 BeginInvoke → InvalidOperationException
            // （ThreadException 23:24 实证）；跳过未就绪状态并静默捕获，不冒泡不中断。
            refreshTimer.Tick += delegate
            {
                try
                {
                    if (lv == null || lv.IsDisposed) return;
                    if (!lv.IsHandleCreated) return;
                    RefreshSessions();
                }
                catch { }
            };
            refreshTimer.Start();
            RefreshSessions();
            UpdateCredLabel();
            // 启动后自动选中第一个可查看会话（虚拟桌面优先），免手动点击；CLI 模式不干扰
            if (!CliMode)
            {
                System.Windows.Forms.Timer auto = new System.Windows.Forms.Timer();
                auto.Interval = 1200;
                auto.Tick += delegate { auto.Stop(); AutoSelectFirst(); };
                auto.Start();
            }
        }

        internal static bool CliMode = false;

        private void AutoSelectFirst()
        {
            try
            {
                List<SessionInfo> all = EnumSessions();
                if (all == null) return;
                List<SessionInfo> list = all.FindAll(x => x != null && x.State != Native.WTSListen &&
                    !string.Equals(x.Name, "services", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(x.Name, "rdp-tcp", StringComparison.OrdinalIgnoreCase));
                if (list.Count == 0) return;
                SessionInfo pick = null;
                foreach (SessionInfo s in list) { if (s != null && s.IsVirtual) { pick = s; break; } }
                if (pick == null) pick = list[0];
                if (pick == null) return;
                for (int i = 0; i < lv.Items.Count; i++)
                {
                    ListViewItem it = lv.Items[i];
                    if (it == null) continue;
                    SessionInfo si = it.Tag as SessionInfo;
                    if (si != null && si.Id == pick.Id)
                    {
                        it.Selected = true;
                        lv.EnsureVisible(it.Index);
                        break;
                    }
                }
            }
            catch (Exception ex) { Log.Write("auto: " + ex.Message); }
        }

        public static bool IsPortOpen(int port)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                    {
                        IAsyncResult ar = c.BeginConnect("127.0.0.1", port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(1500, false)) continue;
                        c.EndConnect(ar);
                        return true;
                    }
                }
                catch { }
                System.Threading.Thread.Sleep(300);
            }
            return false;
        }

        private void BuildUI()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.RowCount = 3;
            root.ColumnCount = 1;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.Padding = new Padding(0);
            root.BackColor = Theme.Panel;

            // 顶部工具栏
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.FlowDirection = FlowDirection.LeftToRight;
            top.WrapContents = false;
            top.Padding = new Padding(10, 7, 10, 6);
            top.BackColor = Theme.Bar;

            btnRefresh = new Button(); btnRefresh.Text = "刷新会话"; btnRefresh.AutoSize = true; btnRefresh.Click += delegate { RefreshSessions(); };
            btnNew = new Button(); btnNew.Text = "新建虚拟桌面"; btnNew.AutoSize = true; btnNew.Click += delegate { NewVirtualDesktop(); };
            btnShot = new Button(); btnShot.Text = "截图"; btnShot.AutoSize = true; btnShot.Click += delegate { SaveScreenshot(Log.SnapPath); MessageBox.Show("已保存到:\n" + Log.SnapPath); };
            btnSettings = new Button(); btnSettings.Text = "设置"; btnSettings.AutoSize = true; btnSettings.Click += delegate { SettingsDialog(); };
            btnClose = new Button(); btnClose.Text = "关闭虚拟桌面"; btnClose.AutoSize = true; btnClose.Click += delegate { CloseVirtualDesktop(); };
            btnClipIn = new Button(); btnClipIn.Text = "取剪贴板"; btnClipIn.AutoSize = true; btnClipIn.Click += delegate { CopyFromVd(); };
            btnClipOut = new Button(); btnClipOut.Text = "送剪贴板"; btnClipOut.AutoSize = true; btnClipOut.Click += delegate { PasteToVd(); };
            btnText = new Button(); btnText.Text = "输入文本"; btnText.AutoSize = true; btnText.Click += delegate { InputTextDialog(); };
            btnKey = new Button(); btnKey.Text = "按键"; btnKey.AutoSize = true; btnKey.Click += delegate { KeyDialog(); };
            lblCred = new Label(); lblCred.AutoSize = true; lblCred.Visible = false; // 凭据状态改放状态栏右侧

            Theme.Flat(btnRefresh); Theme.Flat(btnShot); Theme.Flat(btnSettings);
            Theme.Flat(btnClose); Theme.Flat(btnClipIn); Theme.Flat(btnClipOut);
            Theme.Flat(btnText); Theme.Flat(btnKey);
            Theme.Flat(btnNew, true); // 主操作按钮用强调色

            top.Controls.Add(btnRefresh);
            top.Controls.Add(btnNew);
            top.Controls.Add(btnShot);
            top.Controls.Add(btnClose);
            top.Controls.Add(btnClipIn);
            top.Controls.Add(btnClipOut);
            top.Controls.Add(btnText);
            top.Controls.Add(btnKey);
            top.Controls.Add(btnSettings);

            // 中部：左列表 + 右画中画
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            // 注意：SplitterDistance 必须在 split 加入布局（获得真实宽度）后设置，
            // 否则会被默认 150px 宽度 clamp 到 ~125px，导致左栏被压缩只显示 2 列。
            split.FixedPanel = FixedPanel.Panel1;
            split.BackColor = Theme.Border;
            split.Panel1.BackColor = Theme.ListBg;
            split.Panel2.BackColor = Color.Black;

            lv = new ListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.BackColor = Theme.ListBg;
            lv.ForeColor = Theme.Text;
            lv.BorderStyle = BorderStyle.None;
            lv.GridLines = false;
            lv.HeaderStyle = ColumnHeaderStyle.None; // 表头用自绘深色栏（ListView 列头无法自定义颜色）
            lv.Columns.Add("会话", 88);
            lv.Columns.Add("ID", 42);
            lv.Columns.Add("用户", 56);
            lv.Columns.Add("状态", 56);
            lv.Columns.Add("类型", 66);
            // 自定义深色表头栏（GDI+ 绘制，避免 Label 布局异常）
            Panel head = new Panel();
            head.Dock = DockStyle.Fill;
            head.BackColor = Theme.Bar;
            string[] heads = { "会话", "ID", "用户", "状态", "类型" };
            int[] widths = { 88, 42, 56, 56, 66 };
            head.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Font hf = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold))
                using (Brush tb = new SolidBrush(Theme.Text))
                {
                    int x = 10;
                    for (int i = 0; i < heads.Length; i++)
                    {
                        e.Graphics.DrawString(heads[i], hf, tb, x, 5);
                        x += widths[i];
                    }
                }
            };
            // 用透明小图标撑高行，视觉更舒展
            try
            {
                ImageList il = new ImageList();
                il.ImageSize = new Size(1, 24);
                Bitmap spacer = new Bitmap(1, 24);
                il.Images.Add(spacer);
                lv.SmallImageList = il;
                lv.Items.Clear();
                for (int i = 0; i < lv.Columns.Count; i++) { }
            }
            catch { }
            lv.SelectedIndexChanged += delegate { LvSelected(); };
            lv.DoubleClick += delegate { ConnectRdp(); };

            pnlRdp = new Panel();
            pnlRdp.Dock = DockStyle.Fill;
            pnlRdp.BackColor = Color.Black;
            lblHint = new Label();
            lblHint.Text = "单击左侧会话自动显示画面\n点\"新建虚拟桌面\"创建豆包式虚拟桌面";
            lblHint.ForeColor = Theme.Dim;
            lblHint.BackColor = Color.Transparent;
            lblHint.TextAlign = ContentAlignment.MiddleCenter;
            lblHint.Dock = DockStyle.Fill;
            pnlRdp.Controls.Add(lblHint);
            picView = new PictureBox();
            picView.Dock = DockStyle.Fill;
            picView.SizeMode = PictureBoxSizeMode.Zoom;
            picView.BackColor = Color.Black;
            picView.Visible = false;
            picView.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (viewPort == 0) return;
                Point dp = ClientToDesktop(e.Location);
                if (e.Button == MouseButtons.Left) SendInputCmd("down", dp.X, dp.Y);
                else if (e.Button == MouseButtons.Right) SendInputCmd("right", dp.X, dp.Y);
            };
            picView.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (viewPort == 0 || e.Button != MouseButtons.Left) return;
                Point dp = ClientToDesktop(e.Location);
                SendInputCmd("move", dp.X, dp.Y);
            };
            picView.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (viewPort == 0 || e.Button != MouseButtons.Left) return;
                SendInputCmd("up", 0, 0);
            };
            picView.MouseWheel += delegate(object s, MouseEventArgs e)
            {
                if (viewPort == 0) return;
                Point dp = ClientToDesktop(e.Location);
                try
                {
                    int wport = InputAgent.Pick(viewSession, viewPort);
                    string u = string.Format("http://127.0.0.1:{0}/input?cmd={1}&x={2}&y={3}&d={4}", wport, InputAgent.WheelCmd(wport), dp.X, dp.Y, e.Delta);
                    HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(u);
                    rq.Timeout = 600;
                    rq.BeginGetResponse(delegate(IAsyncResult ar) { try { using (WebResponse r = rq.EndGetResponse(ar)) { } } catch { } }, null);
                }
                catch { }
            };
            pnlRdp.Controls.Add(picView);

            // 左栏：表头 + 列表用 TableLayoutPanel 固定两行，避免 Dock 覆盖
            TableLayoutPanel leftCol = new TableLayoutPanel();
            leftCol.Dock = DockStyle.Fill;
            leftCol.RowCount = 2;
            leftCol.ColumnCount = 1;
            leftCol.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            leftCol.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftCol.BackColor = Theme.ListBg;
            leftCol.Controls.Add(head, 0, 0);
            leftCol.Controls.Add(lv, 0, 1);
            split.Panel1.Controls.Add(leftCol);
            split.Panel2.Controls.Add(pnlRdp);

            // 底部状态栏
            status = new StatusStrip();
            status.BackColor = Theme.Bar;
            status.ForeColor = Theme.Dim;
            status.GripStyle = ToolStripGripStyle.Hidden;
            status.Padding = new Padding(10, 0, 10, 0);
            st = new ToolStripStatusLabel();
            st.Text = "就绪";
            st.ForeColor = Theme.Dim;
            stCred = new ToolStripStatusLabel();
            stCred.Text = "";
            stCred.ForeColor = Theme.Dim;
            stCred.Spring = true;
            stCred.TextAlign = ContentAlignment.MiddleRight;
            status.Items.Add(st);
            status.Items.Add(stCred);

            root.Controls.Add(top, 0, 0);
            root.Controls.Add(split, 0, 1);
            root.Controls.Add(status, 0, 2);
            Controls.Add(root);
            // split 已加入布局获得真实宽度，此时设置左栏宽度才生效
            split.SplitterDistance = 320;
        }

        private List<SessionInfo> EnumSessions()
        {
            List<SessionInfo> list = new List<SessionInfo>();
            IntPtr p;
            int count;
            if (!Native.WTSEnumerateSessions((IntPtr)Native.WTS_CURRENT_SERVER_HANDLE, 0, 1, out p, out count)) return list;
            try
            {
                int size = Marshal.SizeOf(typeof(Native.WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    Native.WTS_SESSION_INFO si = (Native.WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(p.ToInt64() + i * size), typeof(Native.WTS_SESSION_INFO));
                    SessionInfo s = new SessionInfo();
                    s.Id = si.SessionId;
                    s.State = si.State;
                    s.Name = QueryStr(si.SessionId, Native.WTSWinStationName);
                    s.User = QueryStr(si.SessionId, Native.WTSUserName);
                    list.Add(s);
                }
            }
            finally { Native.WTSFreeMemory(p); }
            return list;
        }

        private string QueryStr(int sessionId, int infoClass)
        {
            IntPtr buf;
            int bytes;
            if (!Native.WTSQuerySessionInformation((IntPtr)Native.WTS_CURRENT_SERVER_HANDLE, sessionId, infoClass, out buf, out bytes)) return "";
            try { return Marshal.PtrToStringUni(buf); }
            finally { Native.WTSFreeMemory(buf); }
        }

        private void RefreshSessions()
        {
            // CLI 无头模式：无 GUI 会话列表需求，跳过刷新（避免退出期 ListView 消息处理
            // BeginInvoke 抛 ThreadException，00:53 实测）
            if (CliMode) return;
            if (lv == null || lv.IsDisposed || !lv.IsHandleCreated) return;
            int keepId = -1;
            if (lv.SelectedItems.Count > 0 && lv.SelectedItems[0].Tag is SessionInfo)
                keepId = ((SessionInfo)lv.SelectedItems[0].Tag).Id;
            _suppressAuto = true;
            lv.Items.Clear();
            foreach (SessionInfo s in EnumSessions())
            {
                if (s.State == Native.WTSListen) continue;
                if (string.Equals(s.Name, "services", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(s.Name, "rdp-tcp", StringComparison.OrdinalIgnoreCase)) continue;
                ListViewItem it = new ListViewItem(s.Name);
                it.SubItems.Add(s.Id.ToString());
                it.SubItems.Add(s.User);
                it.SubItems.Add(s.StateText);
                it.SubItems.Add(s.IsVirtual ? "虚拟桌面" : "系统");
                it.Tag = s;
                if (s.IsVirtual) it.BackColor = Theme.VdRow;
                try { it.ImageIndex = 0; } catch { }
                lv.Items.Add(it);
                if (s.Id == keepId) it.Selected = true;
            }
            _suppressAuto = false;
            st.Text = "会话已刷新 " + DateTime.Now.ToString("HH:mm:ss") + "（单击会话自动查看画面）";
        }

        private void LvSelected()
        {
            if (_suppressAuto) return;
            if (lv.SelectedItems.Count == 0) return;
            SessionInfo si = lv.SelectedItems[0].Tag as SessionInfo;
            if (si == null) return;
            // 已在查看该会话：不重复 StopView（避免杀掉截屏代理导致画面中断）
            if (si.Id == viewSession && viewPort != 0) return;
            ConnectRdp();
        }

        private void UpdateCredLabel()
        {
            string u = Native.GetCredUser();
            if (u == null) { stCred.Text = "凭据：未配置（点\"设置\"）"; stCred.ForeColor = Theme.Bad; }
            else { stCred.Text = "凭据：已配置 (" + u + ")"; stCred.ForeColor = Theme.Ok; }
        }

        private void SetCred()
        {
            Form dlg = new Form();
            dlg.Text = "配置 RDP 凭据（用于连接 localhost）";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(360, 160);
            Label l1 = new Label(); l1.Text = "用户名："; l1.Location = new Point(16, 18); l1.AutoSize = true;
            TextBox t1 = new TextBox(); t1.Location = new Point(96, 15); t1.Width = 240;
            Label l2 = new Label(); l2.Text = "密码："; l2.Location = new Point(16, 58); l2.AutoSize = true;
            TextBox t2 = new TextBox(); t2.Location = new Point(96, 55); t2.Width = 240; t2.UseSystemPasswordChar = true;
            Button ok = new Button(); ok.Text = "保存"; ok.DialogResult = DialogResult.OK; ok.Location = new Point(120, 110);
            Button cancel = new Button(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(220, 110);
            dlg.Controls.Add(l1); dlg.Controls.Add(t1); dlg.Controls.Add(l2); dlg.Controls.Add(t2);
            dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok; dlg.CancelButton = cancel;
            Theme.StyleDialog(dlg);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                if (string.IsNullOrEmpty(t1.Text) || string.IsNullOrEmpty(t2.Text)) { MessageBox.Show("用户名和密码不能为空"); return; }
                if (Native.SaveCred(t1.Text, t2.Text, null)) { MessageBox.Show("凭据已保存"); UpdateCredLabel(); }
                else MessageBox.Show("保存失败（可能无权限）");
            }
        }

        private void DelCred()
        {
            if (MessageBox.Show("确定删除已保存的 RDP 凭据？", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                Native.DeleteCred();
                UpdateCredLabel();
            }
        }

        // ---- 设置对话框（凭据管理集中于此）----
        private void SettingsDialog()
        {
            Form dlg = new Form();
            dlg.Text = "设置";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(400, 300);
            Label l1 = new Label(); l1.Text = "RDP 凭据（仅\"新建虚拟桌面\"方案需要；\n画面监控/剪贴板/关闭虚拟桌面都不需要）"; l1.Location = new Point(16, 10); l1.AutoSize = true;
            Label l2 = new Label(); l2.Text = "用户名："; l2.Location = new Point(16, 44); l2.AutoSize = true;
            TextBox t1 = new TextBox(); t1.Location = new Point(96, 41); t1.Width = 260;
            Label l3 = new Label(); l3.Text = "主密码："; l3.Location = new Point(16, 74); l3.AutoSize = true;
            TextBox t2 = new TextBox(); t2.Location = new Point(96, 71); t2.Width = 260; t2.UseSystemPasswordChar = true;
            Label l4 = new Label(); l4.Text = "备用密码："; l4.Location = new Point(16, 104); l4.AutoSize = true;
            TextBox t3 = new TextBox(); t3.Location = new Point(96, 101); t3.Width = 260; t3.UseSystemPasswordChar = true;
            string cu = Native.GetCredUser(); string[] cp = Native.GetCredPasswords();
            if (cu != null) t1.Text = cu;
            if (cp[0] != null) t2.Text = cp[0];
            if (cp[1] != null) t3.Text = cp[1];
            Button btnSave = new Button(); btnSave.Text = "保存凭据"; btnSave.Location = new Point(16, 141); btnSave.Width = 110;
            Button btnDel = new Button(); btnDel.Text = "删除凭据"; btnDel.Location = new Point(136, 141); btnDel.Width = 110;
            Button btnDaemon = new Button(); btnDaemon.Text = "启动监控助手"; btnDaemon.Location = new Point(16, 176); btnDaemon.Width = 120;
            Label lDm = new Label(); lDm.Text = "监控助手（XgtHelper 常驻）负责注入/关闭的提权操作；\n非管理员无法直接触发计划任务，点此拉起（UAC 点是）。"; lDm.Location = new Point(146, 176); lDm.AutoSize = true;
            Button btnClose = new Button(); btnClose.Text = "关闭"; btnClose.DialogResult = DialogResult.OK; btnClose.Location = new Point(290, 260);
            btnSave.Click += delegate
            {
                if (string.IsNullOrEmpty(t1.Text) || string.IsNullOrEmpty(t2.Text)) { MessageBox.Show("用户名和主密码不能为空（备用密码可留空）"); return; }
                if (Native.SaveCred(t1.Text, t2.Text, t3.Text)) { MessageBox.Show("凭据已保存（连接时主密码失败会自动改用备用密码重试）"); UpdateCredLabel(); }
                else MessageBox.Show("保存失败（可能无权限）");
            };
            btnDel.Click += delegate
            {
                if (MessageBox.Show("确定删除已保存的 RDP 凭据？", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes)
                { Native.DeleteCred(); t1.Text = ""; t2.Text = ""; t3.Text = ""; UpdateCredLabel(); }
            };
            btnDaemon.Click += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo("schtasks", "/run /tn XgtHelper") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                    MessageBox.Show("已请求启动监控助手（请在 UAC 弹窗点\"是\"）");
                }
                catch (Exception ex) { MessageBox.Show("启动失败: " + ex.Message); }
            };
            dlg.Controls.Add(l1); dlg.Controls.Add(l2); dlg.Controls.Add(t1); dlg.Controls.Add(l3); dlg.Controls.Add(t2); dlg.Controls.Add(l4); dlg.Controls.Add(t3);
            dlg.Controls.Add(btnSave); dlg.Controls.Add(btnDel); dlg.Controls.Add(btnDaemon); dlg.Controls.Add(lDm); dlg.Controls.Add(btnClose);
            dlg.ClientSize = new Size(440, 320);
            Theme.StyleDialog(dlg);
            dlg.ShowDialog(this);
        }

        // 按候选名依次尝试设置 OCX 的 IDispatch 属性（不同 MsRdpClient 版本属性名可能有差异）
        private static bool TrySetOcxProp(Type t, object ocx, string[] names, object value)
        {
            foreach (string n in names)
            {
                try
                {
                    t.InvokeMember(n, BindingFlags.SetProperty, null, ocx, new object[] { value });
                    return true;
                }
                catch (Exception e)
                {
                    Log.Write("vd: set " + n + " fail " + e.Message);
                }
            }
            return false;
        }

        // 读取 OCX 的 IDispatch 属性，失败时返回异常类型名而不是抛错
        private static object TryGetOcxProp(Type t, object ocx, string name)
        {
            try { return t.InvokeMember(name, BindingFlags.GetProperty, null, ocx, null); }
            catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }

        // ---- 新建虚拟桌面（豆包同款：WTSEnableChildSessions + RDP ActiveX ConnectToChildSession）----
        // 2026-09-14 重大重构：ActiveX 由独立助手进程 XgtVd2.exe 承载（Connect 后立即硬退出，
        // 会话在服务端创建完成）；主程序只轮询 WTS 检测新会话，不碰 ActiveX，彻底消除
        // Connect 返回后 ActiveX 后台线程触发 CLR 内部错误 0xc0000005 的崩溃（21:06-21:46 多方案实证）。
        public void NewVirtualDesktop()
        {
            Log.Write("vd: new virtual desktop requested");
            st.Text = "正在创建虚拟桌面...";
            try
            {
                // 用户权限可直接启用子会话（无需提权计划任务）
                bool en = Native.WTSEnableChildSessions(true);
                bool en2 = Native.WTSIsChildSessionsEnabled();
                Log.Write("vd: WTSEnableChildSessions=" + en + " isEnabled=" + en2);
            }
            catch (Exception ex) { Log.Write("vd: enable error " + ex.Message); }

            if (viewPort != 0) StopView();

            // Windows 同时仅允许一个子会话（"Shadow Session Exist Or Creating!"，21:38 实证），
            // 新建前自动关闭现有子会话（免确认直接注销——CLI 模式无用户点击，GUI 模式已有确认路径）
            // 注意：此处不得引入 HashSet+WTS 枚举循环（主程序集 JIT 编译崩溃，clr.dll 0xc0000005，已证伪）；
            // 残留清理统一交给 XgtVd2 进程内自清（Connect 前注销并等待消失）。
            try
            {
                var cur = EnumSessions();
                foreach (var s in cur)
                {
                    // 排除固定系统会话：0=Services、1=Console、65537=RDP-Tcp；6=WTSListen（实测子会话 ID 可从 3 开始，勿按 Id>4 过滤）
                    if (s.Id == 0 || s.Id == 1 || s.Id == 65537) continue;
                    if (s.State == Native.WTSListen) continue;
                    // 子会话判定不依赖 Name（注销/创建过渡期 Name 可能为空）
                    if (s.Name.Length > 8 || s.Name.IndexOf("#0", StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.Length == 0)
                    {
                        Log.Write("vd: closing existing child session " + s.Id + " before new");
                        st.Text = "先关闭现有虚拟桌面（会话 " + s.Id + "）...";
                        ForceCloseSession(s.Id);
                        break;
                    }
                }
            }
            catch (Exception ex) { Log.Write("vd: pre-close error " + ex.Message); }

            Log.Write("vd: stepA reading credentials...");
            string[] pwds = Native.GetCredPasswords();
            Log.Write("vd: stepB creds read pwd0=" + (pwds[0] == null ? "null" : "len" + pwds[0].Length) + " pwd1=" + (pwds[1] == null ? "null" : "len" + pwds[1].Length));
            if (string.IsNullOrEmpty(pwds[0]))
            {
                st.Text = "未配置凭据——点\"设置\"保存主密码（可另存备用密码，失败自动重试）";
                MessageBox.Show("请先在设置中保存 RDP 凭据（可同时填主密码和备用密码，连接时逐个重试）");
                return;
            }

            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XgtVd2.exe");
            if (!File.Exists(exe))
            {
                st.Text = "缺少创建助手 XgtVd2.exe（与主程序同目录）";
                Log.Write("vd: XgtVd2.exe missing at " + exe);
                return;
            }

            Log.Write("vd: stepC launching helper with pwd0...");
            string resultFile = @"C:\Users\Public\Xgt\newvd_result.txt";
            try { File.Delete(resultFile); } catch { }
            try { File.Delete(@"C:\Users\Public\Xgt\newvd_connected.txt"); } catch { }
            // 杀残留 XgtVd2（上一轮 keep 常驻进程；避免端口/ActiveX 冲突）
            try
            {
                foreach (var p in Process.GetProcessesByName("XgtVd2")) { try { p.Kill(); } catch { } }
            }
            catch { }
            try { Process.Start(new ProcessStartInfo(exe, "\"" + pwds[0] + "\" keep") { UseShellExecute = false }); }
            catch (Exception ex) { Log.Write("vd: launch helper err " + ex.Message); st.Text = "启动创建助手失败: " + ex.Message; return; }
            // XgtVd2 keep 模式自行轮询 WTS 并写结果文件（独立进程，规避主程序集 WTS 轮询 JIT 崩溃），
            // 同时提供画面服务（17777+会话ID，API 与 XgtCapture 兼容）——不再需要 XgtVd3/XgtHelper。

            bool ok = WaitNewSessionFile(resultFile, 30000);
            if (!ok && !string.IsNullOrEmpty(pwds[1]))
            {
                Log.Write("vd: pwd1 no new session, retry with pwd2");
                st.Text = "主密码未创建出虚拟桌面，正在用备用密码重试...";
                RefreshSessions();
                try { File.Delete(resultFile); } catch { }
                // 杀上一轮 keep 进程（避免双 XgtVd2 并存争抢端口/会话）
                try { foreach (var p in Process.GetProcessesByName("XgtVd2")) { try { p.Kill(); } catch { } } } catch { }
                try { Process.Start(new ProcessStartInfo(exe, "\"" + pwds[1] + "\" keep") { UseShellExecute = false }); }
                catch (Exception ex) { Log.Write("vd: launch helper pwd2 err " + ex.Message); }
                ok = WaitNewSessionFile(resultFile, 30000);
            }
            if (!ok)
            {
                st.Text = "未检测到新子会话：两个密码都未成功，请核对凭据（设置→保存）";
                Log.Write("vd: both passwords failed to create session");
            }
            else
            {
                int sid = 0;
                try { int.TryParse(File.ReadAllText(resultFile).Trim(), out sid); } catch { }
                if (sid > 0)
                {
                    st.Text = "虚拟桌面创建成功（会话 " + sid + "），正在接入画面...";
                    Log.Write("vd: new virtual desktop OK id=" + sid);
                    // 直连 XgtVd2 的画面服务（17777+会话ID），不启动 XgtCapture/XgtHelper
                    try { StartViewWithPort(sid, 17777 + sid); }
                    catch (Exception ev) { Log.Write("vd: start view err " + ev.Message); }
                    Log.Write("vd: bisect-startview-called");
                }
                else
                {
                    st.Text = "虚拟桌面已创建，但结果文件异常（会话 ID 未写入）";
                    Log.Write("vd: result file invalid");
                }
            }
            RefreshSessions();
        }

        // 用指定密码连接一次子会话并轮询检测；返回是否创建出新会话（供双密码逐个重试）
        // 注：2026-09-14 21:03 实测——原单方法（160+ 行、30+ try/catch）在调用入口触发
        // .NET 4.8 JIT 内部错误 0xc0000005（clr.dll），因此拆分为 6 个小方法。
        bool TryConnectWithPassword(string pwd)
        {
            object ocx;
            if (!PrepareRdpHost(out ocx)) return false;
            Type t = ocx.GetType();
            Log.Write("vd: ocx type=" + t.FullName);

            bool childSet = ConfigureChildSession(ocx, t);
            Log.Write("vd: childSession=" + childSet);

            SetBasicProperties(ocx, t);
            SetAdvancedSettings(ocx, t, pwd);
            Log.Write("vd: cred user=" + (Native.GetCredUser() ?? "<none>"));

            if (!DoConnect(ocx, t)) return false;

            // 轮询检测新会话（最多 40 秒）：ConnectToChildSession 创建的子会话登录后会出现在 WTS 列表
            return PollNewSession();
        }

        // 准备 rdpHost ActiveX 并返回 OCX 对象（9/10 备份验证组合：AxHost + 挂载 panel + 断开前一轮 + Visible）
        bool PrepareRdpHost(out object ocx)
        {
            ocx = null;
            Log.Write("vd: step0 hide rdpHost (null=" + (rdpHost == null) + ")");
            try { if (rdpHost != null) { rdpHost.Visible = false; } } catch { }
            try { picView.Visible = true; } catch { }
            try
            {
                if (rdpHost == null)
                {
                    Log.Write("vd: step1 creating RdpHost ActiveX...");
                    rdpHost = new RdpHost();
                    Log.Write("vd: step2 RdpHost created, adding to panel...");
                    rdpHost.Dock = DockStyle.Fill;
                    pnlRdp.Controls.Add(rdpHost);
                    Log.Write("vd: step3 added, SetChildIndex...");
                    pnlRdp.Controls.SetChildIndex(rdpHost, 0);
                    Log.Write("vd: step4 panel add done");
                }
                else if (rdpHost.Parent == null && !pnlRdp.Controls.Contains(rdpHost))
                {
                    // 上一轮已摘除：重新挂回再连接
                    Log.Write("vd: step1b re-adding detached rdpHost...");
                    pnlRdp.Controls.Add(rdpHost);
                    pnlRdp.Controls.SetChildIndex(rdpHost, 0);
                    Log.Write("vd: step2b re-add done");
                }
                // 先断开上一轮的连接，避免状态残留
                try
                {
                    Log.Write("vd: step5 disconnect previous (ocx null=" + (rdpHost.Ocx == null) + ")");
                    if (rdpHost.Ocx != null)
                    {
                        Type t0 = rdpHost.Ocx.GetType();
                        t0.InvokeMember("Disconnect", BindingFlags.InvokeMethod, null, rdpHost.Ocx, new object[] { });
                    }
                    Log.Write("vd: step6 disconnect done");
                }
                catch (Exception ed) { Log.Write("vd: step6 disconnect ex " + ed.Message); }
                Log.Write("vd: step7 Visible=true...");
                rdpHost.Visible = true;
                picView.Visible = false;
                lblHint.Visible = false;
                Log.Write("vd: step8 visible set, waiting handle...");

                DateTime wd = DateTime.Now.AddSeconds(3);
                while (DateTime.Now < wd)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(100);
                    if (rdpHost.IsHandleCreated && rdpHost.Ocx != null && rdpHost.Width > 100) break;
                }
                Log.Write("vd: rdpHost handle=" + rdpHost.IsHandleCreated + " size=" + rdpHost.Width + "x" + rdpHost.Height);

                ocx = rdpHost.Ocx;
                if (ocx == null) { st.Text = "RDP 组件不可用"; return false; }
                return true;
            }
            catch (Exception ex) { Log.Write("vd: prepare error " + ex); st.Text = "虚拟桌面创建异常"; return false; }
        }

        // ConnectToChildSession：多路尝试（强类型接口 → IDispatch 晚期绑定）
        bool ConfigureChildSession(object ocx, Type t)
        {
            try
            {
                Native.IMsRdpExtendedSettings ext = (Native.IMsRdpExtendedSettings)ocx;
                ext.PutProperty("ConnectToChildSession", true);
                object v;
                ext.GetProperty("ConnectToChildSession", out v);
                bool childSet = (v is bool && (bool)v) || string.Equals(Convert.ToString(v), "True", StringComparison.OrdinalIgnoreCase);
                Log.Write("vd: childSession iface readback=" + v);
                return childSet;
            }
            catch (Exception e1)
            {
                Log.Write("vd: childSession iface fail " + e1.Message);
                try
                {
                    object v2 = t.InvokeMember("PutProperty", BindingFlags.InvokeMethod, null, ocx, new object[] { "ConnectToChildSession", true });
                    object v3 = t.InvokeMember("GetProperty", BindingFlags.InvokeMethod, null, ocx, new object[] { "ConnectToChildSession" });
                    bool childSet = Convert.ToString(v3) == "True";
                    Log.Write("vd: childSession idispatch readback=" + v3);
                    return childSet;
                }
                catch (Exception e2) { Log.Write("vd: childSession idispatch fail " + e2.Message); return false; }
            }
        }

        // 常规属性（Server 必须用 localhost 匹配 TERMSRV/localhost 凭据）——IDispatch 晚期绑定
        // （9/10 备份验证组合，与 AxHost+Remove+DoEvents 轮询配合不崩溃）
        void SetBasicProperties(object ocx, Type t)
        {
            try { t.InvokeMember("Server", BindingFlags.SetProperty, null, ocx, new object[] { "localhost" }); } catch { }
            try { t.InvokeMember("UserName", BindingFlags.SetProperty, null, ocx, new object[] { Environment.UserName }); } catch { }
            try { t.InvokeMember("DesktopWidth", BindingFlags.SetProperty, null, ocx, new object[] { 1280 }); } catch (Exception esw) { Log.Write("vd: width fail " + esw.Message); }
            try { t.InvokeMember("DesktopHeight", BindingFlags.SetProperty, null, ocx, new object[] { 800 }); } catch (Exception esh) { Log.Write("vd: height fail " + esh.Message); }
        }

        // AdvancedSettings：认证/密码/禁止提示（IDispatch 晚期绑定——实测此调用路径不触发崩溃，
        // 仅 Server/Connect 在 ConnectToChildSession 组合下需要 v12 vtable 强类型）
        void SetAdvancedSettings(object ocx, Type t, string pwd)
        {
            try
            {
                object adv = t.InvokeMember("AdvancedSettings", BindingFlags.GetProperty, null, ocx, null);
                if (adv != null)
                {
                    Type ta = adv.GetType();
                    try { ta.InvokeMember("AuthenticationLevel", BindingFlags.SetProperty, null, adv, new object[] { 0 }); } catch { }
                    try { ta.InvokeMember("EnableCredSspSupport", BindingFlags.SetProperty, null, adv, new object[] { true }); } catch { }
                    try { ta.InvokeMember("NegotiateSecurityLayer", BindingFlags.SetProperty, null, adv, new object[] { true }); } catch { }
                    // 显式传本次尝试的密码，避免 Windows 安全窗口弹窗
                    if (!string.IsNullOrEmpty(pwd))
                    {
                        try { ta.InvokeMember("ClearTextPassword", BindingFlags.SetProperty, null, adv, new object[] { pwd }); Log.Write("vd: password set from vault"); } catch (Exception epw) { Log.Write("vd: password set fail " + epw.Message); }
                    }
                    Log.Write("vd: adv set");
                }
            }
            catch (Exception ea) { Log.Write("vd: adv error " + ea.Message); }
        }

        // Connect（主线程）+ 返回后摘除 rdpHost；返回是否连接成功
        bool DoConnect(object ocx, Type t)
        {
            for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(100); }
            Log.Write("vd: calling Connect (main thread, returns in ~1-2s)");
            try
            {
                t.InvokeMember("Connect", BindingFlags.InvokeMethod, null, ocx, new object[] { });
                Log.Write("vd: Connect returned");
            }
            catch (Exception e3)
            {
                string detail = e3.Message;
                Exception inner = e3.InnerException;
                while (inner != null) { detail += " << " + inner.Message; inner = inner.InnerException; }
                Log.Write("vd: connect fail " + detail);
                st.Text = "连接失败: " + detail;
                return false;
            }

            // Connect 返回后子会话已在服务端创建（21:36-21:44 多次实证：返回后 1-2 秒即 Active）。
            // 保持 rdpHost 留在窗体中显示子会话画面；不 Remove/不 Dispose/不主动 Exit——
            // 实测 Remove（ActiveX 后台线程存活）与 Dispose/窗体关闭（ActiveX 析构）均触发
            // CLR 内部错误 0xc0000005（21:40-21:42 实证）；进程自然退出/硬退出不析构 → 不崩
            // （21:44 实证 Environment.Exit(0) 后 exit=0 且会话 22 创建成功）。
            // GUI 模式关闭程序时 OnFormClosing 走 Environment.Exit(0) 硬退出绕过析构。
            try { picView.Visible = false; } catch { }
            st.Text = "虚拟桌面创建中（画面预览中，检测子会话）...";
            return true;
        }

        // 等待轮询助手 XgtVd3 写入结果文件（主程序零 WTS 调用，规避主程序集 JIT 崩溃；
        // 仅 File.Exists/ReadAllText/int.TryParse + DoEvents/Sleep 基础原语）
        static bool WaitNewSessionFile(string resultFile, int timeoutMs)
        {
            Log.Write("vd: wait-result enter");
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(500);
                try
                {
                    if (File.Exists(resultFile))
                    {
                        string txt = File.ReadAllText(resultFile).Trim();
                        int id;
                        if (int.TryParse(txt, out id) && id > 0)
                        {
                            Log.Write("vd: result file id=" + id);
                            return true;
                        }
                    }
                }
                catch { }
            }
            Log.Write("vd: result file timeout");
            return false;
        }

        // 轮询检测新会话：ConnectToChildSession 创建的子会话 1-2 秒内出现在 WTS 列表
        // （2026-09-14 21:36 实证：Connect 返回后子会话立即创建成功）。
        // 注：实例方法链 JIT 编译触发 .NET 4.8 CLR 内部错误 0xc0000005（21:52-21:58 实证：
        // poll-enter 可写但 PollLoop 实例方法 JIT 崩）；改为静态方法链（同构独立复现
        // TestPoll 纯静态不崩，21:55 实证）。所有底层调用换用静态 EnumSessionsStatic/QueryStrStatic
        // （--status CLI 路径长期使用，从未崩溃）。
        static bool PollNewSession()
        {
            Log.Write("vd: poll-enter");
            return PollLoop(EnumSessionsStatic(), 10000);
        }

        static bool PollLoop(List<SessionInfo> before, int timeoutMs)
        {
            HashSet<int> beforeIds = new HashSet<int>();
            foreach (var sb in before) beforeIds.Add(sb.Id);
            int pollWait = 0;
            int round = 0;
            while (pollWait < timeoutMs)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(500);
                pollWait += 500;
                round++;
                if (round % 4 == 0) Log.Write("vd: poll round " + (round / 4));
                SessionInfo found = FindNewSession(beforeIds);
                if (found != null)
                {
                    Log.Write("vd: new session detected id=" + found.Id + " name=" + found.Name);
                    TryInjectSession(found.Id);
                    return true;
                }
            }
            Log.Write("vd: no new session within 10s");
            return false;
        }

        static SessionInfo FindNewSession(HashSet<int> beforeIds)
        {
            var sessions = EnumSessionsStatic();
            foreach (var s in sessions)
            {
                if (beforeIds.Contains(s.Id)) continue;
                if (s.Id <= 4 || s.Name.Length <= 8) continue; // 子会话名形如 7A78855482A0...
                return s;
            }
            return null;
        }

        // 尝试通过 XgtHelper 计划任务注入 XgtCapture 到指定会话（普通权限触发，失败则由用户手动提权）
        static void TryInjectSession(int sid)
        {
            try
            {
                string dir = @"C:\Users\Public\Xgt";
                Directory.CreateDirectory(dir);
                File.WriteAllText(dir + @"\request.txt", sid.ToString());
                try { File.Delete(dir + @"\result.txt"); } catch { }
                Process.Start(new ProcessStartInfo("schtasks", "/run /tn XgtHelper") { WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = true });
                Log.Write("vd: helper inject requested for session " + sid);
                DateTime deadline = DateTime.Now.AddSeconds(10);
                while (DateTime.Now < deadline)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(300);
                    if (File.Exists(dir + @"\result.txt"))
                    {
                        string r = File.ReadAllText(dir + @"\result.txt").Trim();
                        Log.Write("vd: inject result=" + r);
                        if (r.StartsWith("OK:")) { Log.Write("vd: inject confirmed for session " + sid); return; }
                        break;
                    }
                }
                Log.Write("vd: inject not confirmed for session " + sid);
            }
            catch (Exception ex) { Log.Write("vd: inject error " + ex.Message); }
        }

        // ---- 关闭策略配置（供 AI 自行决定"什么时候关"）----
        // 文件：C:\Users\Public\Xgt\xgt.conf    格式：key = value（# 为注释）
        //   auto_close    = never | idle:<分钟> | onappexit
        //                   never     : 永不自动关闭（默认，只有显式 --close 才关）
        //                   idle:<分钟>: 助手进程空闲超过该时长 → 自动注销会话并退出
        //                   onappexit : 图形界面主程序退出时，连同虚拟桌面一起关掉
        //   close_confirm = on | off   关闭是否要人工确认（AI 自动化建议 off）
        public const string ConfPath = @"C:\Users\Public\Xgt\xgt.conf";

        public static string GetConf(string key, string def)
        {
            try
            {
                if (!File.Exists(ConfPath)) return def;
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
            return def;
        }

        public static void SetConf(string key, string value)
        {
            var lines = new List<string>();
            try { if (File.Exists(ConfPath)) lines.AddRange(File.ReadAllLines(ConfPath)); } catch { }
            if (lines.Count == 0)
            {
                lines.Add("# 小工作台配置（AI 可用 XiaoGongZuoTai.exe --set <键> <值> 修改）");
                lines.Add("# auto_close    = never | idle:<分钟> | onappexit");
                lines.Add("# close_confirm = on | off");
            }
            bool done = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + " = " + value;
                    done = true;
                    break;
                }
            }
            if (!done) lines.Add(key + " = " + value);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfPath));
                File.WriteAllLines(ConfPath, lines.ToArray());
            }
            catch (Exception ex) { Log.Write("conf write err " + ex.Message); }
        }

        // 免确认注销指定会话并轮询等待其从 WTS 列表消失（供 NewVirtualDesktop 预关使用；
        // 延迟注销：WTSLogoffSession 异步，须等消失否则新建可能撞"已有子会话"限制）
        public static bool ForceCloseSession(int sid)
        {
            Log.Write("vd: force-close session " + sid);
            bool directOk = false;
            try
            {
                directOk = Native.WTSLogoffSession((IntPtr)Native.WTS_CURRENT_SERVER_HANDLE, sid, false);
                Log.Write("vd: force logoff=" + directOk + " err=" + (directOk ? 0 : Marshal.GetLastWin32Error()));
            }
            catch (Exception ex) { Log.Write("vd: force close error " + ex.Message); }
            if (!directOk) return false;
            for (int i = 0; i < 30; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(500);
                bool gone = true;
                try
                {
                    foreach (var s in EnumSessionsStatic())
                        if (s.Id == sid) { gone = false; break; }
                }
                catch { gone = false; }
                if (gone) { Log.Write("vd: session " + sid + " gone after " + (i + 1) + " rounds"); return true; }
            }
            Log.Write("vd: session " + sid + " still present after 15s");
            return false;
        }

        // ---- 关闭虚拟桌面（注销目标会话，SYSTEM 助手代行提权操作）----
        public void CloseVirtualDesktop()
        {
            if (lv.SelectedItems.Count == 0) { MessageBox.Show("请先选中要关闭的虚拟桌面会话"); return; }
            SessionInfo si = lv.SelectedItems[0].Tag as SessionInfo;
            if (si == null) return;
            if (!si.IsVirtual) { MessageBox.Show("当前选中的不是虚拟桌面会话（只有带 #0 的会话可关闭）"); return; }
            if (MessageBox.Show("确定关闭虚拟桌面 [" + si.Name + "] (ID " + si.Id + ")？\n该会话内运行的所有程序都会结束。", "确认关闭", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log.Write("close: request session " + si.Id + " name=" + si.Name);
            st.Text = "正在关闭会话 " + si.Id + "...";
            if (viewPort != 0) StopView();

            // 1) 先以普通权限直接尝试（同用户会话通常允许）
            bool directOk = false;
            try
            {
                directOk = Native.WTSLogoffSession((IntPtr)Native.WTS_CURRENT_SERVER_HANDLE, si.Id, false);
                Log.Write("close: direct logoff=" + directOk + " err=" + (directOk ? 0 : Marshal.GetLastWin32Error()));
            }
            catch (Exception ex) { Log.Write("close: direct error " + ex.Message); }

            // 2) 失败则走 SYSTEM 计划任务（XgtHelper 支持 logoff:<sid>）
            bool closed = directOk;
            if (!directOk)
            {
                string dir = @"C:\Users\Public\Xgt";
                Directory.CreateDirectory(dir);
                File.WriteAllText(dir + @"\request.txt", "logoff:" + si.Id);
                try { File.Delete(dir + @"\result.txt"); } catch { }
                Process.Start(new ProcessStartInfo("schtasks", "/run /tn XgtHelper") { WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = true });
                Log.Write("close: helper requested logoff:" + si.Id);
                DateTime deadline = DateTime.Now.AddSeconds(8);
                while (DateTime.Now < deadline)
                {
                    if (File.Exists(dir + @"\result.txt"))
                    {
                        string r = File.ReadAllText(dir + @"\result.txt").Trim();
                        Log.Write("close: helper result=" + r);
                        if (r.StartsWith("LOGOFF_OK"))
                            closed = true;
                        else
                            st.Text = "关闭失败: " + r;
                        break;
                    }
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(150);
                }
                if (!closed && st.Text == "正在关闭会话 " + si.Id + "...")
                    st.Text = "关闭失败: 助手未响应（计划任务权限不足？）";
            }

            if (closed)
            {
                Log.Write("close: done, session " + si.Id + " logged off");
                // WTSLogoffSession 是异步注销：返回 True 只代表注销请求已被接受，
                // 会话从 Active 到真正从 WTS 列表消失需要数秒~数十秒（实测会话 16 关闭后
                // 仍显示 Active 一段时间）。此前"关闭后立即刷新列表"会把正在注销的会话
                // 又显示回来，用户以为没关掉、反复点关闭。这里改为轮询等待会话真正消失
                // （最长 30 秒），消失后才刷新并提示成功。
                st.Text = "会话 " + si.Id + " 注销请求已生效，等待会话退出...";
                DateTime goneDeadline = DateTime.Now.AddSeconds(30);
                bool gone = false;
                while (DateTime.Now < goneDeadline)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(500);
                    bool stillExists = false;
                    try { stillExists = EnumSessions().Exists(x => x != null && x.Id == si.Id); } catch { }
                    if (!stillExists) { gone = true; break; }
                }
                if (gone)
                {
                    st.Text = "会话 " + si.Id + " 已彻底关闭（会话已注销）";
                }
                else
                {
                    st.Text = "会话 " + si.Id + " 注销中（请求已生效，后台退出中）；若仍显示请稍候再刷新";
                    Log.Write("close: session " + si.Id + " still present after 30s wait");
                }
                RefreshSessions();
            }
        }

        // AxHost 承载系统 mstscax（v12 客户端；豆包 comtypes 方案同款 CLSID，
        // 实测 v12 在 ConnectToChildSession 上兼容 Win11 26100，旧 CLSID A3BC03A0 连接后崩溃）
        // AxHost 承载系统 mstscax（无需 license；9/10 备份验证组合：A3BC03A0 + IDispatch + Remove + DoEvents 轮询）
        public class RdpHost : AxHost
        {
            public RdpHost() : base("{A3BC03A0-041D-42E3-AD22-882B7865C9C5}") { }
            public object Ocx { get { return GetOcx(); } }
        }

        public void ConnectRdp()
        {
            if (lv.SelectedItems.Count == 0) return;
            SessionInfo si = lv.SelectedItems[0].Tag as SessionInfo;
            if (si == null) return;
            if (viewPort != 0) StopView();
            RequestView(si.Id);
        }

        public void RequestView(int sessionId)
        {
            try
            {
                // 防御：目标会话必须是有桌面的会话（排除监听器/服务会话）
                SessionInfo target = EnumSessions().Find(x => x.Id == sessionId);
                if (target != null && (target.State == Native.WTSListen ||
                    string.Equals(target.Name, "services", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target.Name, "rdp-tcp", StringComparison.OrdinalIgnoreCase)))
                {
                    st.Text = "会话 " + sessionId + " 无可查看画面（监听器/服务会话）";
                    Log.Write("view: rejected non-desktop session " + sessionId);
                    return;
                }
                // 目标会话 == 当前进程所在会话：直接启动截屏代理（无需 SYSTEM 助手）
                int cur = Process.GetCurrentProcess().SessionId;
                if (sessionId == cur)
                {
                    int port = 17777 + sessionId;
                    string exe = System.IO.Path.Combine(Application.StartupPath, "XgtCapture.exe");
                    if (!System.IO.File.Exists(exe))
                    {
                        st.Text = "缺少 XgtCapture.exe（应在程序同目录）";
                        Log.Write("view: XgtCapture.exe missing at " + exe);
                        return;
                    }
                    Process.Start(new ProcessStartInfo(exe, sessionId + " " + port) { WindowStyle = ProcessWindowStyle.Hidden });
                    viewPort = port;
                    viewSession = sessionId;
                    picView.Visible = true;
                    lblHint.Visible = false;
                    StartFrameThread();
                    st.Text = "画面: 会话 " + sessionId + " (端口 " + port + "，本会话直连)";
                    Log.Write("view: started direct session=" + sessionId + " port=" + port);
                    return;
                }

                // 端口已就绪（预注入）：直接直连，跳过 schtasks 提权依赖
                int prePort = 17777 + sessionId;
                if (IsPortOpen(prePort))
                {
                    viewPort = prePort; viewSession = sessionId;
                    picView.Visible = true; lblHint.Visible = false;
                    StartFrameThread();
                    st.Text = "画面: 会话 " + sessionId + " (端口 " + prePort + "，预注入直连)";
                    Log.Write("view: started pre-injected session=" + sessionId + " port=" + prePort);
                    return;
                }
                string dir = @"C:\Users\Public\Xgt";
                Directory.CreateDirectory(dir);
                File.WriteAllText(dir + @"\request.txt", sessionId.ToString());
                try { File.Delete(dir + @"\result.txt"); } catch { }
                Process.Start(new ProcessStartInfo("schtasks", "/run /tn XgtHelper") { WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = true });
                Log.Write("view: requested session " + sessionId);
                int hport = 0;
                DateTime deadline = DateTime.Now.AddSeconds(6);
                while (DateTime.Now < deadline)
                {
                    if (File.Exists(dir + @"\result.txt"))
                    {
                        string r = File.ReadAllText(dir + @"\result.txt").Trim();
                        string want = "OK:" + sessionId + ":";
                        if (r.StartsWith(want))
                        {
                            string[] parts = r.Split(':');
                            if (parts.Length >= 4) int.TryParse(parts[3], out hport);
                        }
                        else if (r.StartsWith("FAIL:" + sessionId + ":"))
                        {
                            Log.Write("view: helper result=" + r);
                            st.Text = "画面请求失败: " + r;
                        }
                        break;
                    }
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(150);
                }
                if (hport == 0)
                {
                    st.Text = "画面失败: 会话 " + sessionId + " 需要 SYSTEM 助手——双击桌面『小工作台-一键安装助手.bat』装一次（UAC 点是）";
                    Log.Write("view: no port after wait (helper not installed?)");
                    return;
                }
                viewPort = hport;
                viewSession = sessionId;
                picView.Visible = true;
                lblHint.Visible = false;
                StartFrameThread();
                st.Text = "画面: 会话 " + sessionId + " (端口 " + hport + ")";
                Log.Write("view: started session=" + sessionId + " port=" + hport);
            }
            catch (Exception ex) { Log.Write("view: error " + ex.Message); st.Text = "画面请求异常"; }
        }

        private Rectangle GetZoomRect(Image img, Size client)
        {
            if (img == null || client.Width <= 0 || client.Height <= 0) return Rectangle.Empty;
            float r1 = (float)img.Width / img.Height;
            float r2 = (float)client.Width / client.Height;
            int w, h;
            if (r1 > r2) { w = client.Width; h = (int)(client.Width / r1); }
            else { h = client.Height; w = (int)(client.Height * r1); }
            return new Rectangle((client.Width - w) / 2, (client.Height - h) / 2, w, h);
        }

        private Point ClientToDesktop(Point p)
        {
            Rectangle r = GetZoomRect(picView.Image, picView.ClientSize);
            if (r.IsEmpty || viewW <= 0 || viewH <= 0) return new Point(0, 0);
            int x = (int)((p.X - r.X) * viewW / (float)r.Width);
            int y = (int)((p.Y - r.Y) * viewH / (float)r.Height);
            if (x < 0) x = 0; if (y < 0) y = 0;
            if (x >= viewW) x = viewW - 1; if (y >= viewH) y = viewH - 1;
            return new Point(x, y);
        }

        private void SendInputCmd(string cmd, int x, int y)
        {
            if (viewPort == 0) return;
            try
            {
                int port = InputAgent.Pick(viewSession, viewPort);   // 会话内代理优先
                string u = string.Format("http://127.0.0.1:{0}/input?cmd={1}&x={2}&y={3}", port, cmd, x, y);
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(u);
                rq.Timeout = 600;
                rq.BeginGetResponse(delegate(IAsyncResult ar) { try { using (WebResponse r = rq.EndGetResponse(ar)) { } } catch { } }, null);
            }
            catch { }
        }

        private void SendKeyCmd(string cmd, int k)
        {
            if (viewPort == 0) return;
            try
            {
                int port = InputAgent.Pick(viewSession, viewPort);   // 会话内代理优先
                string u = string.Format("http://127.0.0.1:{0}/input?cmd={1}&k={2}", port, cmd, k);
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(u);
                rq.Timeout = 600;
                rq.GetResponse().Close();
            }
            catch { }
        }

        // ---- 输入文本（Unicode 逐字注入，支持中文）----
        private void InputTextDialog()
        {
            if (viewPort == 0) { MessageBox.Show("请先查看一个虚拟桌面会话"); return; }
            Form dlg = new Form();
            dlg.Text = "输入文本到虚拟桌面（会话 " + viewSession + "）";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(420, 130);
            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.Location = new Point(12, 12);
            tb.Size = new Size(396, 60);
            tb.ScrollBars = ScrollBars.Vertical;
            Button ok = new Button(); ok.Text = "输入"; ok.DialogResult = DialogResult.OK; ok.Location = new Point(160, 84);
            Button cancel = new Button(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(260, 84);
            dlg.Controls.Add(tb); dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok; dlg.CancelButton = cancel;
            Theme.StyleDialog(dlg);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string t = tb.Text;
            if (string.IsNullOrEmpty(t)) return;
            try
            {
                int port = InputAgent.Pick(viewSession, viewPort);   // 会话内代理优先
                string u = string.Format("http://127.0.0.1:{0}/input?cmd=text&t={1}", port, Uri.EscapeDataString(t));
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(u);
                rq.Timeout = 5000;
                rq.GetResponse().Close();
                st.Text = "已向会话 " + viewSession + " 输入 " + t.Length + " 字符";
                Log.Write("input: text " + t.Length + " chars to session " + viewSession);
            }
            catch (Exception ex) { MessageBox.Show("输入失败: " + ex.Message); Log.Write("input: text error " + ex.Message); }
        }

        // ---- 按键 / 快捷键组合 ----
        private void KeyDialog()
        {
            if (viewPort == 0) { MessageBox.Show("请先查看一个虚拟桌面会话"); return; }
            string[] items = new string[]
            {
                "Enter", "Esc", "Tab", "Backspace", "Delete", "空格",
                "方向键 上/下/左/右", "F5 刷新", "F2 重命名",
                "Ctrl+C 复制", "Ctrl+V 粘贴", "Ctrl+X 剪切", "Ctrl+Z 撤销", "Ctrl+A 全选", "Ctrl+S 保存",
                "Alt+Tab 切换窗口", "Win+D 显示桌面", "Win+R 运行", "Win+E 资源管理器",
                "Ctrl+Shift+Esc 任务管理器", "Alt+F4 关闭窗口", "Ctrl+Alt+Del"
            };
            Form dlg = new Form();
            dlg.Text = "按键到虚拟桌面（会话 " + viewSession + "）";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(320, 240);
            ListBox lb = new ListBox();
            lb.Dock = DockStyle.Top;
            lb.Height = 200;
            foreach (string it in items) lb.Items.Add(it);
            Button ok = new Button(); ok.Text = "发送"; ok.DialogResult = DialogResult.OK; ok.Location = new Point(70, 208);
            Button cancel = new Button(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(170, 208);
            dlg.Controls.Add(lb); dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok; dlg.CancelButton = cancel;
            Theme.StyleDialog(dlg);
            if (dlg.ShowDialog(this) != DialogResult.OK || lb.SelectedIndex < 0) return;
            string sel = lb.SelectedItem.ToString();
            try
            {
                SendCombo(sel);
                st.Text = "已发送按键: " + sel + " → 会话 " + viewSession;
                Log.Write("input: combo " + sel + " to session " + viewSession);
            }
            catch (Exception ex) { MessageBox.Show("按键失败: " + ex.Message); Log.Write("input: combo error " + ex.Message); }
        }

        public static int[] ComboVks(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            // 宽松匹配：允许 "Ctrl+V" / "ctrl+v" / "Ctrl+V 粘贴" 三种写法（用户与 CLI 都常省略中文描述）
            string low = name.Trim().ToLowerInvariant().Replace(" ", "").Replace("\u3000", "");
            if (low == "enter" || low == "回车") return new int[] { 13 };
            if (low == "esc" || low == "escape") return new int[] { 27 };
            if (low == "tab") return new int[] { 9 };
            if (low == "backspace") return new int[] { 8 };
            if (low == "delete" || low == "del") return new int[] { 46 };
            if (low == "空格" || low == "space") return new int[] { 32 };
            // 注意：长组合必须先判，否则 "ctrl+shift+esc" 会被 "ctrl+s" 抢先命中
            if (low.StartsWith("ctrl+shift+esc")) return new int[] { 17, 16, 27 };
            if (low.StartsWith("ctrl+alt+del")) return new int[] { 17, 18, 46 };
            if (low.StartsWith("ctrl+c")) return new int[] { 17, 67 };
            if (low.StartsWith("ctrl+v")) return new int[] { 17, 86 };
            if (low.StartsWith("ctrl+x")) return new int[] { 17, 88 };
            if (low.StartsWith("ctrl+z")) return new int[] { 17, 90 };
            if (low.StartsWith("ctrl+a")) return new int[] { 17, 65 };
            if (low.StartsWith("ctrl+s")) return new int[] { 17, 83 };
            if (low.StartsWith("alt+tab")) return new int[] { 18, 9 };
            if (low.StartsWith("alt+f4")) return new int[] { 18, 115 };
            if (low.StartsWith("win+d")) return new int[] { 91, 68 };
            if (low.StartsWith("win+r")) return new int[] { 91, 82 };
            if (low.StartsWith("win+e")) return new int[] { 91, 69 };
            return null;
        }

        public void SendCombo(string name)
        {
            int[] vks = ComboVks(name);
            if (vks == null) return;
            for (int i = 0; i < vks.Length - 1; i++) SendKeyCmd("keydown", vks[i]);
            SendKeyCmd("key", vks[vks.Length - 1]);
            for (int i = vks.Length - 2; i >= 0; i--) SendKeyCmd("keyup", vks[i]);
        }

        // 剪贴板：从虚拟桌面取文本到本机
        private void CopyFromVd()
        {
            if (viewPort == 0) { MessageBox.Show("请先查看一个虚拟桌面会话"); return; }
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + viewPort + "/clip");
                req.Timeout = 2000;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string t = sr.ReadToEnd();
                    if (string.IsNullOrEmpty(t)) { MessageBox.Show("虚拟桌面剪贴板为空"); return; }
                    Clipboard.SetText(t);
                    st.Text = "已从虚拟桌面复制 " + t.Length + " 字符到本机剪贴板";
                    Log.Write("clip: pulled " + t.Length + " chars from session " + viewSession);
                }
            }
            catch (Exception ex) { MessageBox.Show("复制失败: " + ex.Message); Log.Write("clip: pull error " + ex.Message); }
        }

        // 剪贴板：本机文本发到虚拟桌面并触发粘贴
        private void PasteToVd()
        {
            if (viewPort == 0) { MessageBox.Show("请先查看一个虚拟桌面会话"); return; }
            if (!Clipboard.ContainsText()) { MessageBox.Show("本机剪贴板没有文本"); return; }
            string t = Clipboard.GetText();
            try
            {
                string enc = Uri.EscapeDataString(t);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + viewPort + "/clip?set=" + enc);
                req.Timeout = 2000;
                req.GetResponse().Close();
                SendKeyCmd("keydown", 17);   // Ctrl
                SendKeyCmd("key", 86);       // V
                SendKeyCmd("keyup", 17);     // Ctrl
                st.Text = "已发送 " + t.Length + " 字符到虚拟桌面并粘贴";
                Log.Write("clip: pushed " + t.Length + " chars to session " + viewSession + " + ctrl+v");
            }
            catch (Exception ex) { MessageBox.Show("发送失败: " + ex.Message); Log.Write("clip: push error " + ex.Message); }
        }

        private void StartFrameThread()
        {
            // 已弃用：主程序集 JIT 编译 FrameLoop/UpdateViewFrame（含 WebClient/Image/匿名委托/线程循环）
            // 触发 clr.dll 0xc0000005（23:13-23:15 二分实证）。画面改由 WebBrowser 内核拉取（见 StartViewWithPort）。
        }

        public void StartViewWithPort(int sessionId, int port)
        {
            // CLI 模式无窗体：跳过 GUI 画面接入（画面由 XgtVd2 的 HTTP 画面服务独立提供，
            // GUI 打开后自动接入）；避免无句柄创建 WebBrowser 抛 ThreadException（00:49 实测）。
            if (CliMode)
            {
                Log.Write("view: cli mode skip webview session=" + sessionId + " port=" + port);
                st.Text = "画面: 会话 " + sessionId + " (端口 " + port + ")";
                return;
            }
            viewPort = port;
            viewSession = sessionId;
            picView.Visible = false;
            if (webView == null)
            {
                webView = new System.Windows.Forms.WebBrowser();
                webView.Dock = DockStyle.Fill;
                webView.ScriptErrorsSuppressed = true;
                pnlRdp.Controls.Add(webView);
            }
            webView.Visible = true;
            // HTML 由浏览器内核拉帧刷新（每 500ms 换 img.src），主程序零拉帧代码，规避 JIT 崩溃
            string html = "<html><body style='margin:0;background:#000'><img id='f' src='http://127.0.0.1:" + port +
                          "/frame' style='width:100%;height:100%'><script>setInterval(function(){var i=document.getElementById('f');i.src='http://127.0.0.1:" + port +
                          "/frame?t='+Date.now();},500);</script></body></html>";
            try { webView.DocumentText = html; } catch { }
            st.Text = "画面: 会话 " + sessionId + " (端口 " + port + ")";
            Log.Write("view: started webview session=" + sessionId + " port=" + port);
        }

        public void StopView()
        {
            if (webView != null) { try { webView.Navigate("about:blank"); webView.Visible = false; } catch { } }
            if (viewPort != 0)
            {
                // 异步通知代理退出：同步 HttpWebRequest 在目标端口已死（连接被拒/重置）时
                // 会触发 .NET Framework 4.8 的 CLR 内部错误（0xc0000005，实测 21:16 崩溃根因）
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + viewPort + "/stop");
                    req.Timeout = 500;
                    req.BeginGetResponse(delegate(IAsyncResult ar) { try { using (WebResponse r = req.EndGetResponse(ar)) { } } catch { } }, null);
                }
                catch { }
            }
            viewPort = 0;
            viewSession = -1;
            picView.Visible = true;
            lblHint.Visible = true;
            st.Text = "已停止画面";
            Log.Write("view: stopped");
        }

        private void DisconnectRdp() { StopView(); }

        public void SaveScreenshot(string path)
        {
            try
            {
                using (Bitmap bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
                {
                    DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
                    bmp.Save(path, ImageFormat.Png);
                }
                Log.Write("screenshot saved: " + path);
                st.Text = "截图已保存: " + path;
            }
            catch (Exception ex)
            {
                Log.Write("screenshot failed: " + ex.Message);
            }
        }

        public static void PrintStatus()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== XiaoGongZuoTai status ===");
            sb.AppendLine("time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            string user = Native.GetCredUser();
            sb.AppendLine("cred: " + (user == null ? "NOT_SET" : "SET(" + user + ")"));
            sb.AppendLine("log: " + Log.LogPath);
            // 当前虚拟桌面（非系统会话）的助手状态，便于判断"能不能接回"
            try
            {
                foreach (SessionInfo s in EnumSessionsStatic())
                {
                    if (s.Id == 0 || s.Id == 1 || s.Id == 65537) continue;
                    if (s.State == Native.WTSListen) continue;
                    bool hb = IsPortOpen(17777 + s.Id);
                    sb.AppendLine("virtual: sid=" + s.Id + " state=" + s.StateText
                        + " frame=" + (hb ? "UP  http://127.0.0.1:" + (17777 + s.Id) : "DOWN")
                        + (s.State == Native.WTSDisconnected ? "   （画面丢失，可用 --reconnect " + s.Id + " 接回）" : ""));
                }
            }
            catch { }
            sb.AppendLine("--- config (" + ConfPath + ") ---");
            sb.AppendLine("auto_close    = " + GetConf("auto_close", "never") + "     (never | idle:<分钟> | onappexit)");
            sb.AppendLine("close_confirm = " + GetConf("close_confirm", "on") + "     (on | off)");
            sb.AppendLine("--- sessions ---");
            foreach (SessionInfo s in EnumSessionsStatic())
            {
                sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}", s.Name, s.Id, string.IsNullOrEmpty(s.User) ? "-" : s.User, s.StateText, s.IsVirtual ? "virtual" : "system"));
            }
            Console.WriteLine(sb.ToString());
        }

        public static List<SessionInfo> EnumSessionsStatic()
        {
            List<SessionInfo> list = new List<SessionInfo>();
            IntPtr p;
            int count;
            if (!Native.WTSEnumerateSessions((IntPtr)Native.WTS_CURRENT_SERVER_HANDLE, 0, 1, out p, out count)) { Log.Write("WTSEnumerateSessions failed: " + Marshal.GetLastWin32Error()); return list; }
            try
            {
                int size = Marshal.SizeOf(typeof(Native.WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    Native.WTS_SESSION_INFO si = (Native.WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(p.ToInt64() + i * size), typeof(Native.WTS_SESSION_INFO));
                    SessionInfo s = new SessionInfo();
                    s.Id = si.SessionId;
                    s.State = si.State;
                    s.Name = QueryStrStatic(si.SessionId, Native.WTSWinStationName);
                    s.User = QueryStrStatic(si.SessionId, Native.WTSUserName);
                    list.Add(s);
                }
            }
            finally { Native.WTSFreeMemory(p); }
            return list;
        }

        private static string QueryStrStatic(int sessionId, int infoClass)
        {
            IntPtr buf;
            int bytes;
            if (!Native.WTSQuerySessionInformation((IntPtr)Native.WTS_CURRENT_SERVER_HANDLE, sessionId, infoClass, out buf, out bytes)) return "";
            try { return Marshal.PtrToStringUni(buf); }
            finally { Native.WTSFreeMemory(buf); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 清理常驻画面助手 XgtVd2（keep 模式持有 ActiveX；主程序退出时一并结束，
            // 服务端子会话保留，由用户决定是否关闭）
            try
            {
                foreach (var p in Process.GetProcessesByName("XgtVd2")) { try { p.Kill(); } catch { } }
            }
            catch { }
            // AI 可设的退出策略：auto_close=onappexit 时，主程序退出连同虚拟桌面一起注销
            // （默认 never = 只结束助手，会话保留、可由 --reconnect 接回）
            try
            {
                if (GetConf("auto_close", "never").Equals("onappexit", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (SessionInfo s in EnumSessionsStatic())
                    {
                        if (s.Id == 0 || s.Id == 1 || s.Id == 65537) continue;
                        if (s.State == Native.WTSListen) continue;
                        if (s.Name.Length > 8 || s.Name.IndexOf("#0", StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.Length == 0)
                        {
                            Log.Write("vd: onappexit closing session " + s.Id);
                            ForceCloseSession(s.Id);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("vd: onappexit err " + ex.Message); }
            // 若创建过虚拟桌面（rdpHost 持有 ActiveX 连接），关闭窗体将触发 ActiveX 析构
            // → CLR 内部错误 0xc0000005（21:42 实证）。用 Environment.Exit 硬退出绕过析构，
            // 子会话由服务端管理（注销后消失），进程终止由 OS 回收 COM 对象（不析构、不崩）。
            if (rdpHost != null)
            {
                try { Log.Write("vd: hard exit (skip AxHost dispose)"); } catch { }
                Environment.Exit(0);
            }
            try { StopView(); } catch { }
            try
            {
                // 兜底断开 rdpHost（不 Dispose：Dispose 触发 ActiveX 析构崩溃 0xc0000005，21:40 实证）
                if (rdpHost != null && rdpHost.Ocx != null)
                {
                    try
                    {
                        Type t = rdpHost.Ocx.GetType();
                        t.InvokeMember("Disconnect", BindingFlags.InvokeMethod, null, rdpHost.Ocx, new object[] { });
                    }
                    catch { }
                }
                if (_vdOcx != null)
                {
                    try
                    {
                        Type t = _vdOcx.GetType();
                        t.InvokeMember("Disconnect", BindingFlags.InvokeMethod, null, _vdOcx, new object[] { });
                    }
                    catch { }
                    try { Marshal.FinalReleaseComObject(_vdOcx); } catch { }
                    _vdOcx = null;
                }
            }
            catch { }
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Log.InstallGlobalHandlers();

            if (args.Length > 0)
            {
                string a = args[0].ToLowerInvariant();
                MainForm.CliMode = true;
                if (a == "--status" || a == "-s")
                {
                    MainForm.PrintStatus();
                    return;
                }
                if (a == "--set")
                {
                    // XiaoGongZuoTai.exe --set <键> <值>
                    // 让 AI 自己决定"什么时候关虚拟桌面"以及"关闭要不要人工确认"。
                    if (args.Length < 3)
                    {
                        Console.WriteLine("用法: --set <键> <值>");
                        Console.WriteLine("  --set auto_close never            永不自动关闭（默认）");
                        Console.WriteLine("  --set auto_close idle:30          空闲 30 分钟自动注销虚拟桌面");
                        Console.WriteLine("  --set auto_close onappexit        图形界面主程序退出时一并关闭虚拟桌面");
                        Console.WriteLine("  --set close_confirm off           关闭时不再要求人工确认（AI 自动化用）");
                        return;
                    }
                    string k = args[1].ToLowerInvariant();
                    if (k != "auto_close" && k != "close_confirm")
                    {
                        Console.WriteLine("未知配置键: " + args[1] + "（支持 auto_close / close_confirm）");
                        return;
                    }
                    string v = string.Join(" ", args, 2, args.Length - 2).Trim();
                    string lo = v.ToLowerInvariant();
                    if (k == "auto_close" && lo != "never" && lo != "onappexit" && !lo.StartsWith("idle:"))
                    {
                        Console.WriteLine("auto_close 取值无效: " + v + "（应为 never | idle:<分钟> | onappexit）");
                        return;
                    }
                    if (k == "close_confirm" && lo != "on" && lo != "off")
                    {
                        Console.WriteLine("close_confirm 取值无效: " + v + "（应为 on | off）");
                        return;
                    }
                    MainForm.SetConf(k, v);
                    Console.WriteLine("已设置 " + k + " = " + v);
                    Console.WriteLine("配置生效时机：auto_close 由画面助手启动时读取，改完请 --reconnect <会话ID> 让它重新加载。");
                    Log.Write("cli set: " + k + " = " + v);
                    return;
                }
                if (a == "--config" || a == "--conf")
                {
                    Console.WriteLine("=== 小工作台配置 ===");
                    Console.WriteLine("file: " + MainForm.ConfPath);
                    Console.WriteLine("auto_close    = " + MainForm.GetConf("auto_close", "never")
                        + "     (never | idle:<分钟> | onappexit)");
                    Console.WriteLine("close_confirm = " + MainForm.GetConf("close_confirm", "on")
                        + "     (on | off)");
                    return;
                }
                if (a == "--screenshot" || a == "--shot" || a == "-p")
                {
                    string outPath = args.Length > 1 ? args[1] : Log.SnapPath;
                    MainForm f = new MainForm();
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-3000, -3000);
                    f.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(600);
                    f.SaveScreenshot(outPath);
                    f.Close();
                    return;
                }
                if (a == "--connect" || a == "-c")
                {
                    int sid = 1;
                    int port = 0;
                    if (args.Length >= 2) int.TryParse(args[1], out sid);
                    if (args.Length >= 3) int.TryParse(args[2], out port);
                    MainForm f = new MainForm();
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-3000, -3000);
                    f.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(400);
                    if (port == 0)
                    {
                        Log.Write("cli connect: requesting view for session " + sid);
                        f.RequestView(sid);
                        DateTime deadline = DateTime.Now.AddSeconds(10);
                        while (DateTime.Now < deadline) { Application.DoEvents(); System.Threading.Thread.Sleep(100); }
                    }
                    else
                    {
                        Log.Write("cli connect: direct view session " + sid + " port " + port);
                        f.StartViewWithPort(sid, port);
                    }
                    DateTime waitUntil = DateTime.Now.AddSeconds(2);
                    while (DateTime.Now < waitUntil) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                    f.SaveScreenshot(Log.SnapPath);
                    f.StopView();
                    f.Close();
                    Log.Write("cli connect: done, snapshot at " + Log.SnapPath);
                    return;
                }
                if (a == "--newvd" || a == "-n")
                {
                    MainForm f = new MainForm();
                    f.Show();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(600);
                    f.NewVirtualDesktop();
                    // CLI 无头路径：不碰 StopView/SaveScreenshot/Close（WebBrowser 与 DrawToBitmap 在快速
                    // 退出下会触发 clr.dll 0xc0000005，23:21 实证）。结果文件已由 NewVirtualDesktop 内部
                    // 写入；Environment.Exit(0) 硬退出绕过析构（21:44 实证 exit=0 不崩）。
                    // 画面服务 XgtVd2 保持常驻（下次新建/关闭 GUI 时自动清理）。
                    try { System.Threading.Thread.Sleep(800); } catch { }
                    Environment.Exit(0);
                }
                if (a == "--reconnect" || a == "--attach")
                {
                    // XiaoGongZuoTai.exe --reconnect <sid>
                    // 画面助手（XgtVd2）意外退出后，子会话会停在 Disconnected、画面变黑（“僵尸会话”）。
                    // 本命令按原会话 ID 重新拉起助手（XgtVd2 attach 模式：不新建、不注销残留会话），
                    // 会话里跑着的程序不丢。2026-10-02 新增。
                    int rsid = 0;
                    if (args.Length >= 2) int.TryParse(args[1], out rsid);
                    if (rsid <= 1)
                    {
                        Console.WriteLine("用法: --reconnect <会话ID>   (会话ID 必须 > 1)");
                        return;
                    }
                    // 2026-10-02 修复：必须先确认会话真的存在。
                    // 否则「接回不存在的会话」会走到助手 attach 模式里的 Connect()，
                    // 而 ConnectToChildSession=true 在没有子会话时会**新建**一个，
                    // 于是：--reconnect 99 会凭空造出一个虚拟桌面，还必然等不到 99 变 Active 而报失败。
                    bool rexists = false;
                    try
                    {
                        foreach (SessionInfo s in MainForm.EnumSessionsStatic())
                        {
                            if (s != null && s.Id == rsid) { rexists = true; break; }
                        }
                    }
                    catch (Exception ex) { Log.Write("cli reconnect: enum err " + ex.Message); }
                    if (!rexists)
                    {
                        Console.WriteLine("会话 " + rsid + " 不存在，拒绝接回（否则会凭空新建一个虚拟桌面）。");
                        Console.WriteLine("当前会话：");
                        foreach (SessionInfo s in MainForm.EnumSessionsStatic())
                        {
                            if (s.Id == 0 || s.Id == 1 || s.Id == 65537) continue;
                            Console.WriteLine("  " + s.Name + "\tsid=" + s.Id + "\t" + s.StateText);
                        }
                        Log.Write("cli reconnect: session " + rsid + " not found, refused");
                        return;
                    }
                    string[] rpw = Native.GetCredPasswords();
                    if (string.IsNullOrEmpty(rpw[0]))
                    {
                        Console.WriteLine("未配置凭据：请先在图形界面「设置」中保存主密码");
                        return;
                    }
                    string rvd2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XgtVd2.exe");
                    if (!File.Exists(rvd2)) { Console.WriteLine("缺少创建助手 XgtVd2.exe"); return; }

                    Console.WriteLine("reconnecting session " + rsid + " ...");
                    // 结束残留助手（Windows 只允许一个子会话，故至多一个 XgtVd2）
                    try { foreach (var p in Process.GetProcessesByName("XgtVd2")) { try { if (!p.HasExited) p.Kill(); } catch { } } } catch { }
                    System.Threading.Thread.Sleep(500);

                    string[] rcand = { rpw[0], rpw[1] };
                    int rport = 17777 + rsid;
                    bool rok = false;
                    int rused = -1;
                    for (int ci = 0; ci < rcand.Length && !rok; ci++)
                    {
                        if (string.IsNullOrEmpty(rcand[ci])) continue;
                        try { Process.Start(new ProcessStartInfo(rvd2, "\"" + rcand[ci] + "\" keep " + rsid) { UseShellExecute = false }); }
                        catch (Exception ex) { Log.Write("cli reconnect launch err " + ex.Message); continue; }
                        // 判据必须是“会话在 WTS 里回到 Active”——曾用 HTTP /clip 能否应答当判据，
                        // 结果助手进程活着、HTTP 应答了，但密码错时 RDP 根本没接上，会话仍是 Disconnected，
                        // 误报成功（2026-10-02 实测踩过）。
                        for (int i = 0; i < 60; i++)
                        {
                            System.Threading.Thread.Sleep(500);
                            try
                            {
                                foreach (var s in MainForm.EnumSessionsStatic())
                                {
                                    if (s != null && s.Id == rsid && s.State == Native.WTSActive) { rok = true; break; }
                                }
                            }
                            catch { }
                            if (rok) break;
                        }
                        if (rok) { rused = ci; }
                        else
                        {
                            Log.Write("cli reconnect: pwd idx " + ci + " 未接回会话 " + rsid + "，换下一个密码");
                            try { foreach (var p in Process.GetProcessesByName("XgtVd2")) { try { if (!p.HasExited) p.Kill(); } catch { } } } catch { }
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                    Console.WriteLine(rok
                        ? ("session " + rsid + " 画面已接回（服务端口 " + rport + "，密码序号 " + rused + "）")
                        : ("接回失败：会话 " + rsid + " 可能已被注销（用 --status 确认；不存在则 --newvd 新建）"));
                    if (rok)
                    {
                        // 会话变 Active 后，RDP 画面还要重绘几秒：此时 /frame 仍返回 ~20KB 的旧壁纸。
                        // 等帧尺寸回到正常量级（连续两次 >= 30KB）再报就绪，避免调用方立刻取到陈旧画面。
                        bool painted = false;
                        for (int i = 0; i < 20 && !painted; i++)
                        {
                            System.Threading.Thread.Sleep(1000);
                            try
                            {
                                using (System.Net.WebClient wc = new System.Net.WebClient())
                                {
                                    wc.Proxy = null;
                                    byte[] b1 = wc.DownloadData("http://127.0.0.1:" + rport + "/frame");
                                    System.Threading.Thread.Sleep(600);
                                    byte[] b2 = wc.DownloadData("http://127.0.0.1:" + rport + "/frame");
                                    if (b1.Length >= 30000 && b2.Length >= 30000) painted = true;
                                }
                            }
                            catch { }
                        }
                        Console.WriteLine(painted ? " 画面已就绪" : " 已接回，但画面仍在重绘（多等几秒再取帧）");
                        Console.WriteLine(" 取帧: http://127.0.0.1:" + rport + "/frame   注入: XiaoGongZuoTai.exe --input " + rsid + " key 91");
                    }
                    Log.Write("cli reconnect: session " + rsid + " -> " + rok + " pwdIdx=" + rused);
                    return;
                }
                if (a == "--close")
                {
                    // XiaoGongZuoTai.exe --close <sid>
                    // 免确认注销虚拟桌面并轮询确认消失（与 GUI「关闭虚拟桌面」共用 ForceCloseSession）。
                    // 注意 WTSLogoffSession 是异步的：返回 True 只代表请求被接受，必须轮询 WTS 列表确认消失。
                    int csid = 0;
                    if (args.Length >= 2) int.TryParse(args[1], out csid);
                    if (csid <= 1)
                    {
                        Console.WriteLine("用法: --close <会话ID>   (会话ID 必须 > 1；系统会话 0/1/65537 不可关闭)");
                        return;
                    }
                    Console.WriteLine("closing session " + csid + " ...");
                    // 先请该虚拟桌面的 RDP 助手优雅退出，再注销会话。
                    // XgtVd2 是 keep 常驻的"生命维持"进程，只注销会话它会 keep 空转变成孤儿（实测残留 278MB）。
                    try
                    {
                        using (System.Net.WebClient wc = new System.Net.WebClient())
                        {
                            wc.Proxy = null;
                            wc.DownloadString("http://127.0.0.1:" + (17777 + csid) + "/stop");
                        }
                        System.Threading.Thread.Sleep(600);
                    }
                    catch { }
                    System.Threading.Thread.Sleep(400);
                    // 兜底：/stop 依赖 XgtVd2 的 HTTP 响应流及时关闭，未必立即生效（实测会延迟退出）。
                    // Windows 只允许一个子会话，故至多存在一个 XgtVd2，直接结束最稳。
                    try
                    {
                        foreach (var p in Process.GetProcessesByName("XgtVd2"))
                        {
                            try { if (!p.HasExited) p.Kill(); } catch { }
                        }
                    }
                    catch { }
                    bool cok = MainForm.ForceCloseSession(csid);
                    Console.WriteLine("session " + csid + " closed: " + cok);
                    Log.Write("cli close: session " + csid + " -> " + cok);
                    return;
                }
                if (a == "--input" || a == "-i")
                {
                    // XiaoGongZuoTai.exe --input <sid> <cmd> [args...]
                    //   cmd: text <内容> | key <vk> | keydown <vk> | keyup <vk> | click <x> <y> |
                    //        dbl <x> <y> | right <x> <y> | move <x> <y> | scroll <x> <y> <d> |
                    //        combo <name>  (见 SendCombo 支持的名称)
                    int sid = 1;
                    if (args.Length >= 2) int.TryParse(args[1], out sid);
                    string cmd = args.Length >= 3 ? args[2].ToLowerInvariant() : "click";
                    int port = InputAgent.Pick(sid, 17777 + sid);   // 会话内代理优先（见 src/XgtAgent.cs 方案）
                    string u = "";
                    if (cmd == "text" && args.Length >= 4)
                        u = string.Format("http://127.0.0.1:{0}/input?cmd=text&t={1}", port, Uri.EscapeDataString(string.Join(" ", args, 3, args.Length - 3)));
                    else if (cmd == "key" && args.Length >= 4)
                        u = string.Format("http://127.0.0.1:{0}/input?cmd=key&k={1}", port, args[3]);
                    else if ((cmd == "keydown" || cmd == "keyup") && args.Length >= 4)
                        u = string.Format("http://127.0.0.1:{0}/input?cmd={1}&k={2}", port, cmd, args[3]);
                    else if ((cmd == "click" || cmd == "dbl" || cmd == "right" || cmd == "move") && args.Length >= 5)
                        u = string.Format("http://127.0.0.1:{0}/input?cmd={1}&x={2}&y={3}", port, cmd, args[3], args[4]);
                    else if (cmd == "scroll" && args.Length >= 6)
                        u = string.Format("http://127.0.0.1:{0}/input?cmd={1}&x={2}&y={3}&d={4}", port, InputAgent.WheelCmd(port), args[3], args[4], args[5]);
                    else if (cmd == "combo" && args.Length >= 4)
                    {
                        // 纯 HTTP 序列实现组合键，不启动窗口（避免 Close 触发 StopView 杀掉截屏代理）
                        string comboname = string.Join(" ", args, 3, args.Length - 3);
                        int[] vks = MainForm.ComboVks(comboname);
                        if (vks == null) { Console.WriteLine("未知组合键: " + comboname); return; }
                        using (System.Net.WebClient wc = new System.Net.WebClient())
                        {
                            for (int i = 0; i < vks.Length - 1; i++)
                                wc.DownloadString(string.Format("http://127.0.0.1:{0}/input?cmd=keydown&k={1}", port, vks[i]));
                            wc.DownloadString(string.Format("http://127.0.0.1:{0}/input?cmd=key&k={1}", port, vks[vks.Length - 1]));
                            for (int i = vks.Length - 2; i >= 0; i--)
                                wc.DownloadString(string.Format("http://127.0.0.1:{0}/input?cmd=keyup&k={1}", port, vks[i]));
                        }
                        Log.Write("cli input: combo " + comboname + " to session " + sid);
                        Console.WriteLine("combo sent: " + comboname);
                        return;
                    }
                    else
                    {
                        Console.WriteLine("用法: --input <sid> text <内容> | key <vk> | click <x> <y> | combo <名称>");
                        Console.WriteLine("示例: --input 6 text 你好世界 / --input 6 click 640 400 / --input 6 combo Ctrl+C 复制");
                        return;
                    }
                    using (System.Net.WebClient wc = new System.Net.WebClient())
                    {
                        string resp = wc.DownloadString(u);
                        Console.WriteLine("session " + sid + " -> " + cmd + " : " + resp);
                    }
                    Log.Write("cli input: " + cmd + " to session " + sid);
                    return;
                }
                if (a == "--help" || a == "-h" || a == "/?")
                {
                    Console.WriteLine("XiaoGongZuoTai - 小工作台 虚拟桌面监控中心");
                    Console.WriteLine("  (无参数)        启动图形界面");
                    Console.WriteLine("  --status        输出会话/凭据状态");
                    Console.WriteLine("  --screenshot P  截取自身UI保存到 P（默认 ui.png）");
                    Console.WriteLine("  --connect [会话ID]  查看指定会话画面并截图（默认会话1；本会话免安装，跨会话需装助手）");
                    Console.WriteLine("  --input <sid> <cmd> [args]  向虚拟桌面注入操作");
                    Console.WriteLine("      text <内容>        输入文本（支持中文）");
                    Console.WriteLine("      key <vk>           按键（虚拟键码）");
                    Console.WriteLine("      click|dbl|right|move <x> <y>   鼠标操作");
                    Console.WriteLine("      scroll <x> <y> <d>  滚轮");
                    Console.WriteLine("      combo <名称>        快捷键（如: Ctrl+C 复制 / Win+R 运行）");
                    Console.WriteLine("  --reconnect <会话ID>  助手进程意外退出后，把画面接回原会话（不新建、不丢程序）");
                    Console.WriteLine("  --close <会话ID>  关闭指定虚拟桌面（注销会话）");
                    Console.WriteLine("  --config          查看关闭策略等配置");
                    Console.WriteLine("  --set <键> <值>    设置配置（auto_close / close_confirm），AI 可自行决定何时关闭");
                    Console.WriteLine("  --help          显示本帮助");
                    return;
                }
            }

            Application.Run(new MainForm());
        }
    }
}
