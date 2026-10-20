// CapVd2 - 找 XgtVd2 窗口并 PrintWindow 截图验证（画面服务可行性）
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;

class CapVd2
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    static string TargetProc = "XgtVd2";

    static void Main()
    {
        // 找目标进程的窗口
        System.Diagnostics.Process[] procs = System.Diagnostics.Process.GetProcessesByName(TargetProc);
        if (procs.Length == 0) { Console.WriteLine("no XgtVd2 process"); return; }
        uint target = (uint)procs[0].Id;
        Console.WriteLine("target pid=" + target);
        IntPtr found = IntPtr.Zero;
        int fw = 0, fh = 0;
        EnumWindows((h, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target)
            {
                RECT r;
                GetWindowRect(h, out r);
                int w = r.R - r.L, hh = r.B - r.T;
                int len = GetWindowTextLength(h);
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                Console.WriteLine("  win hwnd=0x" + h.ToInt64().ToString("X") + " " + w + "x" + hh + " vis=" + IsWindowVisible(h) + " title='" + sb + "'");
                if (w * hh > fw * fh) { found = h; fw = w; fh = hh; }
            }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) { Console.WriteLine("no window found"); return; }
        Console.WriteLine("capturing hwnd=0x" + found.ToInt64().ToString("X") + " " + fw + "x" + fh);

        // PrintWindow flags=2 (PW_RENDERFULLCONTENT)
        using (Bitmap bmp = new Bitmap(fw, fh))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(found, hdc, 2);
                g.ReleaseHdc(hdc);
                Console.WriteLine("PrintWindow=" + ok);
            }
            bmp.Save("vd2_cap.png", ImageFormat.Png);
            Console.WriteLine("saved vd2_cap.png");
        }

        // 分析像素：是否全黑/全白（判断画面有效性）
        using (Bitmap bmp = new Bitmap("vd2_cap.png"))
        {
            int step = Math.Max(1, bmp.Width / 80);
            int nonBlack = 0, total = 0;
            for (int x = 0; x < bmp.Width; x += step)
                for (int y = 0; y < bmp.Height; y += step)
                {
                    Color c = bmp.GetPixel(x, y);
                    total++;
                    if (c.R > 20 || c.G > 20 || c.B > 20) nonBlack++;
                }
            Console.WriteLine("pixel sample: nonBlack=" + nonBlack + "/" + total + " (" + (100 * nonBlack / total) + "%)");
        }
    }
}
