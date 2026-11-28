using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class AppInfo
{
    public const string Version = "1.2.5";
    // GitHub 仓库，格式：用户名/仓库名
    public const string Repo = "lotusVienna/Launcher";
}

enum Edge { Left, Right, Top, Bottom }

class Item
{
    public string Name, Path;
    public bool Admin;                                   // 以管理员身份运行
    public List<Process> Procs = new List<Process>();   // 同一项可同时运行多个实例
    public Panel Row;
    public Label Dot, NameLbl;
    public Button Start, Stop;

    // 提权进程的句柄有时读不到退出状态，改按进程号确认是否还在
    static bool Alive(Process p)
    {
        try { Process.GetProcessById(p.Id); return true; } catch { return false; }
    }

    public int Count
    {
        get
        {
            for (int i = Procs.Count - 1; i >= 0; i--)
            {
                bool dead;
                try { dead = Procs[i].HasExited; } catch { dead = !Alive(Procs[i]); }
                if (dead) Procs.RemoveAt(i);
            }
            return Procs.Count;
        }
    }
}

// ================= 弹出的小列表 =================
class ListForm : Form
{
    public Panel Host = new Panel();
    public Label Empty = new Label();

    public ListForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        Padding = new Padding(1);
        Font = new Font("Microsoft YaHei UI", 9f);
        Size = new Size(260, 80);

        Host.Dock = DockStyle.Fill;
        Host.AutoScroll = true;
        Host.BackColor = Color.White;
        Controls.Add(Host);

        Empty.Text = "还没有项目\n把文件拖到小方块上，\n或在托盘菜单里选“添加项目”";
        Empty.TextAlign = ContentAlignment.MiddleCenter;
        Empty.ForeColor = Color.FromArgb(110, 118, 129);
        Empty.Dock = DockStyle.Fill;
        Empty.Font = new Font("Microsoft YaHei UI", 8.5f);
        Host.Controls.Add(Empty);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;   // 阴影
            cp.ExStyle |= 0x80;         // 不出现在 Alt+Tab
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Color.FromArgb(214, 218, 224)))
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

// ================= 桌面常驻小方块（主窗体） =================
class LauncherForm : Form
{
    // ---------- 单实例：唤出列表 ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string msg);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    static readonly uint WM_SHOWME = RegisterWindowMessage("Lofla.Launcher.ShowMe");

    // ---------- 全屏检测 ----------
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr h, int idx);

    // ---------- 逐像素透明窗口（圆角无锯齿） ----------
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObj);

    public static void NotifyExisting()
    {
        PostMessage((IntPtr)0xFFFF, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)WM_SHOWME)
        {
            if (!Visible) { Show(); }
            shown = true; leaveTick = 0;
            ShowList();
            return;
        }
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;          // TOOLWINDOW：不进 Alt+Tab
            cp.ExStyle |= 0x08000000;    // NOACTIVATE：点击不抢焦点
            cp.ExStyle |= 0x80000;       // LAYERED：逐像素透明，边缘抗锯齿
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    // ---------- 配色 ----------
    static readonly Color Brand = Color.FromArgb(22, 163, 74);
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color Line = Color.FromArgb(226, 229, 234);
    static readonly Color Muted = Color.FromArgb(110, 118, 129);
    static readonly Color Green = Color.FromArgb(22, 163, 74);
    static readonly Color Danger = Color.FromArgb(220, 38, 38);
    static readonly Color DangerBg = Color.FromArgb(254, 226, 226);
    static readonly Color OffBg = Color.FromArgb(238, 240, 243);
    static readonly Color OffFg = Color.FromArgb(160, 166, 175);

    static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Launcher");
    static readonly string Cfg = System.IO.Path.Combine(Dir, "launcher.cfg");
    // 旧版配置（exe 同目录），首次运行时自动迁移
    static readonly string OldCfg = System.IO.Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "launcher.cfg");

    const int S = 44;      // 小方块边长
    const int PEEK = 6;    // 收起后露出的像素

    List<Item> items = new List<Item>();
    ListForm popup = new ListForm();
    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    NotifyIcon tray;
    ContextMenuStrip trayMenu = new ContextMenuStrip();
    ToolTip tip = new ToolTip();

    bool keep = true, multi = true, autoUpdate = true;
    bool exiting = false, loading = true, updating = false, busy = false;
    Edge edge = Edge.Right;
    int ax, ay;                           // 小方块中心的记忆位置
    bool shown = true, pressed = false, dragging = false;
    int enterTick = 0, leaveTick = -30, statusTick = 0, menuOpen = 0, running = 0;
    Point downCursor, downLoc;

    public LauncherForm()
    {
        Text = "启动器";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(S, S);
        BackColor = Brand;
        Cursor = Cursors.Hand;
        AllowDrop = true;
        DoubleBuffered = true;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ---- 列表弹层 ----
        popup.Host.ClientSizeChanged += delegate { Layout_(); };
        popup.Deactivate += delegate { if (!busy && menuOpen == 0) popup.Hide(); };
        DragEventHandler enter = delegate (object s, DragEventArgs e) { e.Effect = DragDropEffects.Copy; };
        DragEventHandler drop = delegate (object s, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null) return;
            foreach (var f in files) AddItem(System.IO.Path.GetFileNameWithoutExtension(f), f);
            Save();
            ResizePopup();
        };
        DragEnter += enter; DragDrop += drop;
        popup.DragEnter += enter; popup.DragDrop += drop;
        popup.AllowDrop = true; popup.Host.AllowDrop = true;
        popup.Host.DragEnter += enter; popup.Host.DragDrop += drop;

        // ---- 托盘 ----
        tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "启动器", Visible = true };
        trayMenu.Opening += delegate { BuildTrayMenu(); };
        tray.ContextMenuStrip = trayMenu;
        tray.MouseDoubleClick += delegate (object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { shown = true; leaveTick = 0; ShowList(); }
        };

        CleanOld();
        if (IsAutoStart()) SetAutoStart(true);   // 程序被移动过位置时，自动修正开机启动路径

        // 默认位置：主屏右侧偏上
        var wa0 = Screen.PrimaryScreen.WorkingArea;
        ax = wa0.Right - S / 2; ay = wa0.Top + wa0.Height / 3;

        Load_();
        loading = false;
        if (items.Count > 0 && !File.Exists(Cfg)) Save();

        Location = Calc(true).Location;      // 启动时先露出，稍后自动收起
        ResizePopup();
        Refresh_();

        timer.Interval = 25;
        timer.Tick += delegate { Tick_(); };
        timer.Start();

        // 启动 3 秒后静默检查更新
        if (autoUpdate && !AppInfo.Repo.StartsWith("YOUR_", StringComparison.Ordinal))
        {
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += delegate { t.Stop(); t.Dispose(); CheckUpdate(true); };
            t.Start();
        }
    }

    // ================= 绘制 =================
    static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateSkin();
    }

    // 把小方块画成带透明通道的位图，交给系统逐像素合成
    void UpdateSkin()
    {
        if (!IsHandleCreated) return;
        using (var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                DrawSkin(g);
            }
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, oldObj = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                oldObj = SelectObject(memDc, hBmp);
                var size = new SIZE(); size.cx = S; size.cy = S;
                var src = new POINT();
                var bf = new BLENDFUNCTION();
                bf.BlendOp = 0; bf.BlendFlags = 0; bf.SourceConstantAlpha = 255; bf.AlphaFormat = 1;
                UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref bf, 2);
            }
            finally
            {
                if (hBmp != IntPtr.Zero) { SelectObject(memDc, oldObj); DeleteObject(hBmp); }
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    void DrawSkin(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float k = S / 96f;
        using (var gp = Round(new Rectangle(0, 0, S - 1, S - 1), 11))
        using (var br = new SolidBrush(Brand))
            g.FillPath(br, gp);
        using (var pen = new Pen(Color.White, 10 * k))
        {
            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, new PointF[] {
                new PointF(32 * k, 24 * k), new PointF(32 * k, 68 * k), new PointF(66 * k, 68 * k) });
        }
        g.FillEllipse(Brushes.White, 62 * k, 24 * k, 12 * k, 12 * k);
        if (running > 0)
        {
            // 有项目在运行：右上角小圆点
            g.FillEllipse(Brushes.White, S - 13, 2, 11, 11);
            using (var br = new SolidBrush(Color.FromArgb(245, 158, 11)))
                g.FillEllipse(br, S - 11, 4, 7, 7);
        }
    }

    // ================= 贴边 / 收起 / 滑出 =================
    static int Clamp(int v, int lo, int hi)
    {
        if (hi < lo) hi = lo;
        return Math.Max(lo, Math.Min(hi, v));
    }

    Rectangle Calc(bool shownState)
    {
        var wa = Screen.FromPoint(new Point(ax, ay)).WorkingArea;
        int x, y;
        if (edge == Edge.Left || edge == Edge.Right)
        {
            y = Clamp(ay - S / 2, wa.Top, wa.Bottom - S);
            if (edge == Edge.Left) x = shownState ? wa.Left : wa.Left + PEEK - S;
            else x = shownState ? wa.Right - S : wa.Right - PEEK;
        }
        else
        {
            x = Clamp(ax - S / 2, wa.Left, wa.Right - S);
            if (edge == Edge.Top) y = shownState ? wa.Top : wa.Top + PEEK - S;
            else y = shownState ? wa.Bottom - S : wa.Bottom - PEEK;
        }
        return new Rectangle(x, y, S, S);
    }

    bool InHot(Point c)
    {
        var d = Calc(true);
        var wa = Screen.FromPoint(new Point(ax, ay)).WorkingArea;
        int t = 6, m = 14;
        Rectangle r;
        switch (edge)
        {
            case Edge.Left: r = new Rectangle(wa.Left, d.Top - m, t, S + 2 * m); break;
            case Edge.Right: r = new Rectangle(wa.Right - t, d.Top - m, t, S + 2 * m); break;
            case Edge.Top: r = new Rectangle(d.Left - m, wa.Top, S + 2 * m, t); break;
            default: r = new Rectangle(d.Left - m, wa.Bottom - t, S + 2 * m, t); break;
        }
        return r.Contains(c);
    }

    bool IsFullscreen()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero || h == Handle) return false;
            if (popup.IsHandleCreated && h == popup.Handle) return false;
            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            var sb = new StringBuilder(64);
            GetClassName(h, sb, 64);
            string cn = sb.ToString();
            if (cn == "Progman" || cn == "WorkerW" || cn == "Shell_TrayWnd") return false;
            if ((GetWindowLong(h, -16) & 0xC00000) == 0xC00000) return false;   // 有标题栏，不是全屏
            var b = Screen.FromHandle(h).Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }
        catch { return false; }
    }

    static int Step(int d)
    {
        if (Math.Abs(d) <= 2) return d;
        return (int)Math.Round(d * 0.35);
    }

    void Animate()
    {
        Point t = Calc(shown).Location;
        if (t == Location) return;
        Location = new Point(Left + Step(t.X - Left), Top + Step(t.Y - Top));
    }

    void Tick_()
    {
        if (++statusTick >= 32) { statusTick = 0; Refresh_(); }
        Point c = Cursor.Position;

        // 右键菜单：小方块不抢焦点，系统不会替我们处理“点空白处关闭”，这里自己判断
        if (trayMenu.Visible && Control.MouseButtons != MouseButtons.None && !trayMenu.Bounds.Contains(c))
            trayMenu.Close();

        if (!Visible || dragging) return;

        // 点击列表和小方块之外：收起列表
        if (popup.Visible && !busy && menuOpen == 0 && !trayMenu.Visible && Control.MouseButtons != MouseButtons.None)
        {
            if (!popup.Bounds.Contains(c) && !Bounds.Contains(c)) popup.Hide();
        }

        if (shown)
        {
            Rectangle near = Rectangle.Inflate(Calc(true), 24, 24);
            bool keepOpen = popup.Visible || busy || trayMenu.Visible || menuOpen > 0 || pressed || near.Contains(c);
            if (keepOpen) leaveTick = 0;
            else if (++leaveTick >= 28) { shown = false; leaveTick = 0; }   // 约 0.7 秒后收起
        }
        else
        {
            if (InHot(c) && !IsFullscreen())
            {
                if (++enterTick >= 5) { shown = true; enterTick = 0; leaveTick = 0; }   // 停留约 0.12 秒滑出
            }
            else enterTick = 0;
        }
        Animate();
    }

    // ---------- 拖动与点击 ----------
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pressed = true; dragging = false;
        downCursor = Cursor.Position; downLoc = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!pressed) return;
        Point c = Cursor.Position;
        if (!dragging && (Math.Abs(c.X - downCursor.X) > 4 || Math.Abs(c.Y - downCursor.Y) > 4))
        {
            dragging = true;
            popup.Hide();
        }
        if (dragging)
            Location = new Point(downLoc.X + c.X - downCursor.X, downLoc.Y + c.Y - downCursor.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) { ShowDockMenu(); return; }
        if (!pressed || e.Button != MouseButtons.Left) return;
        pressed = false;
        if (dragging) { dragging = false; SnapToEdge(); }
        else ToggleList();
    }

    // 右键菜单：和托盘同一份，但摆在小方块朝向屏幕内侧的一边，不盖住小方块
    void ShowDockMenu()
    {
        popup.Hide();
        BuildTrayMenu();
        Size sz = trayMenu.GetPreferredSize(Size.Empty);
        int w = sz.Width + 2, h = sz.Height + 2;
        Rectangle d = Bounds;
        var wa = Screen.FromPoint(new Point(d.Left + S / 2, d.Top + S / 2)).WorkingArea;
        int x, y;
        switch (edge)
        {
            case Edge.Left:
                x = d.Right + 6; y = Clamp(d.Top, wa.Top, wa.Bottom - h); break;
            case Edge.Right:
                x = d.Left - w - 6; y = Clamp(d.Top, wa.Top, wa.Bottom - h); break;
            case Edge.Top:
                y = d.Bottom + 6; x = Clamp(d.Left, wa.Left, wa.Right - w); break;
            default:
                y = d.Top - h - 6; x = Clamp(d.Left, wa.Left, wa.Right - w); break;
        }
        shown = true; leaveTick = 0;
        trayMenu.Show(new Point(x, y));
    }

    void SnapToEdge()
    {
        var c = new Point(Left + S / 2, Top + S / 2);
        var wa = Screen.FromPoint(c).WorkingArea;
        int dl = c.X - wa.Left, dr = wa.Right - c.X, dt = c.Y - wa.Top, db = wa.Bottom - c.Y;
        int m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        edge = m == dl ? Edge.Left : (m == dr ? Edge.Right : (m == dt ? Edge.Top : Edge.Bottom));
        ax = c.X; ay = c.Y;
        shown = true; leaveTick = 0;
        var b = Calc(true);
        ax = b.X + S / 2; ay = b.Y + S / 2;
        Save();
    }

    // ================= 列表弹层 =================
    void ResizePopup()
    {
        int n = items.Count;
        int h = n == 0 ? 80 : Math.Min(n * 34 + 12, 340);
        popup.Size = new Size(260, h);
        Layout_();
        if (popup.Visible) PlacePopup();
    }

    void PlacePopup()
    {
        var d = Calc(true);
        var wa = Screen.FromPoint(new Point(ax, ay)).WorkingArea;
        int w = popup.Width, h = popup.Height, x, y;
        switch (edge)
        {
            case Edge.Left:
                x = d.Right + 6; y = Clamp(d.Top, wa.Top, wa.Bottom - h); break;
            case Edge.Right:
                x = d.Left - w - 6; y = Clamp(d.Top, wa.Top, wa.Bottom - h); break;
            case Edge.Top:
                y = d.Bottom + 6; x = Clamp(d.Left, wa.Left, wa.Right - w); break;
            default:
                y = d.Top - h - 6; x = Clamp(d.Left, wa.Left, wa.Right - w); break;
        }
        popup.Location = new Point(x, y);
    }

    void ShowList()
    {
        if (!Visible) Show();
        ResizePopup();
        PlacePopup();
        popup.Show();
        popup.BringToFront();
        popup.Activate();
    }

    void ToggleList()
    {
        if (popup.Visible) { popup.Hide(); return; }
        shown = true; leaveTick = 0;
        ShowList();
    }

    // ================= 配置 =================
    void Load_()
    {
        string src = File.Exists(Cfg) ? Cfg : (File.Exists(OldCfg) ? OldCfg : null);
        if (src == null) return;
        foreach (var line in File.ReadAllLines(src, Encoding.UTF8))
        {
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                var kv = line.Substring(1).Split(new char[] { '=' });
                if (kv.Length == 2)
                {
                    string key = kv[0], val = kv[1].Trim();
                    if (key == "keep") keep = val == "1";
                    else if (key == "multi") multi = val == "1";
                    else if (key == "autoupdate") autoUpdate = val == "1";
                    else if (key == "edge")
                    {
                        if (val == "left") edge = Edge.Left;
                        else if (val == "top") edge = Edge.Top;
                        else if (val == "bottom") edge = Edge.Bottom;
                        else edge = Edge.Right;
                    }
                    else if (key == "pos")
                    {
                        var ps = val.Split(new char[] { ',' });
                        int px, py;
                        if (ps.Length == 2 && int.TryParse(ps[0], out px) && int.TryParse(ps[1], out py))
                        { ax = px; ay = py; }
                    }
                }
                continue;
            }
            int i = line.IndexOf('|');
            if (i > 0)
            {
                string rest = line.Substring(i + 1);
                bool adm = false;
                if (rest.EndsWith("|admin", StringComparison.Ordinal))
                { adm = true; rest = rest.Substring(0, rest.Length - 6); }
                AddItem(line.Substring(0, i), rest, adm);
            }
        }
    }

    void Save()
    {
        if (loading) return;
        var sb = new StringBuilder();
        sb.AppendLine("#keep=" + (keep ? "1" : "0"));
        sb.AppendLine("#multi=" + (multi ? "1" : "0"));
        sb.AppendLine("#autoupdate=" + (autoUpdate ? "1" : "0"));
        sb.AppendLine("#edge=" + edge.ToString().ToLowerInvariant());
        sb.AppendLine("#pos=" + ax + "," + ay);
        foreach (var it in items) sb.AppendLine(it.Name + "|" + it.Path + (it.Admin ? "|admin" : ""));
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Cfg, sb.ToString(), new UTF8Encoding(false));
    }

    // ================= 列表项 =================
    static Button MkBtn(string text, Color back, Color fore, int w, int h)
    {
        var b = new Button
        {
            Text = text, Width = w, Height = h, FlatStyle = FlatStyle.Flat,
            BackColor = back, ForeColor = fore, Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false, Font = new Font("Microsoft YaHei UI", 8f)
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    static void SetBtn(Button b, bool on, Color backOn, Color foreOn)
    {
        b.Enabled = on;
        b.BackColor = on ? backOn : OffBg;
        b.ForeColor = on ? foreOn : OffFg;
        b.Cursor = on ? Cursors.Hand : Cursors.Default;
    }

    void AddItem(string name, string path, bool admin = false)
    {
        var it = new Item { Name = name, Path = path, Admin = admin };

        it.Row = new Panel { Height = 32, BackColor = Color.White };
        it.Dot = new Label
        {
            Text = "●", ForeColor = OffFg, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 8f)
        };
        it.NameLbl = new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand };
        it.Start = MkBtn("▶", Accent, Color.White, 30, 24);
        it.Stop = MkBtn("■", DangerBg, Danger, 30, 24);
        it.Start.Click += delegate { Run(it); };
        it.Stop.Click += delegate { Kill(it, false); };
        it.NameLbl.DoubleClick += delegate { Run(it); };

        tip.SetToolTip(it.NameLbl, path + "\n（双击启动，右键更多）");
        tip.SetToolTip(it.Start, "启动");
        tip.SetToolTip(it.Stop, "关闭");

        var menu = new ContextMenuStrip();
        menu.Opened += delegate { menuOpen++; };
        menu.Closed += delegate { if (menuOpen > 0) menuOpen--; };
        menu.Items.Add("编辑（名称 / 路径）…").Click += delegate { EditItem(it); };
        var adminMi = new ToolStripMenuItem("以管理员身份运行");
        adminMi.Click += delegate { it.Admin = !it.Admin; Save(); Refresh_(); };
        menu.Opening += delegate { adminMi.Checked = it.Admin; };
        menu.Items.Add(adminMi);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关闭最近启动的一个").Click += delegate { Kill(it, true); };
        menu.Items.Add("关闭全部").Click += delegate { Kill(it, false); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("移除此项").Click += delegate
        {
            Kill(it, false);
            popup.Host.Controls.Remove(it.Row);
            items.Remove(it); Save(); ResizePopup();
        };
        it.Row.ContextMenuStrip = menu;
        it.Dot.ContextMenuStrip = menu;
        it.NameLbl.ContextMenuStrip = menu;

        it.Row.Controls.Add(it.Dot); it.Row.Controls.Add(it.NameLbl);
        it.Row.Controls.Add(it.Start); it.Row.Controls.Add(it.Stop);
        popup.Host.Controls.Add(it.Row);
        items.Add(it);
        Layout_();
        Refresh_();
    }

    // 编辑：名称、路径、是否管理员，一个对话框里改完
    void EditItem(Item it)
    {
        busy = true;
        try
        {
            var f = new Form
            {
                Text = "编辑", Size = new Size(460, 200), FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, TopMost = true
            };
            f.Font = new Font("Microsoft YaHei UI", 9f);
            var l1 = new Label { Text = "名称", Left = 12, Top = 16, Width = 40 };
            var n = new TextBox { Left = 56, Top = 12, Width = 370, Text = it.Name };
            var l2 = new Label { Text = "路径", Left = 12, Top = 50, Width = 40 };
            var p = new TextBox { Left = 56, Top = 46, Width = 300, Text = it.Path };
            var b = new Button { Text = "浏览", Left = 362, Top = 44, Width = 64 };
            var adm = new CheckBox { Text = "以管理员身份运行（启动时会弹出 UAC 确认）", Left = 56, Top = 80, Width = 370, Checked = it.Admin };
            var ok = new Button { Text = "确定", Left = 270, Top = 122, Width = 76, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 350, Top = 122, Width = 76, DialogResult = DialogResult.Cancel };
            b.Click += delegate
            {
                var d = new OpenFileDialog { Filter = "脚本/程序|*.ps1;*.bat;*.cmd;*.exe|所有文件|*.*" };
                try { string cur = System.IO.Path.GetDirectoryName(p.Text.Trim().Trim(new char[] { '"' })); if (Directory.Exists(cur)) d.InitialDirectory = cur; } catch { }
                if (d.ShowDialog() == DialogResult.OK) p.Text = d.FileName;
            };
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.Controls.AddRange(new Control[] { l1, n, l2, p, b, adm, ok, cancel });
            if (f.ShowDialog() != DialogResult.OK) return;

            string path = p.Text.Trim().Trim(new char[] { '"' });
            if (path.Length == 0) return;
            string name = n.Text.Trim().Replace("|", " ");
            if (name.Length == 0) name = System.IO.Path.GetFileNameWithoutExtension(path);
            it.Name = name; it.Path = path; it.Admin = adm.Checked;
            tip.SetToolTip(it.NameLbl, path + "\n（双击启动，右键更多）");
            Save();
            Refresh_();
        }
        finally { busy = false; }
    }

    void Layout_()
    {
        popup.Empty.Visible = items.Count == 0;
        int y = 6;
        int w = Math.Max(popup.Host.ClientSize.Width - 8, 200);
        foreach (var it in items)
        {
            it.Row.SetBounds(4, y, w, 32);
            it.Dot.SetBounds(2, 4, 20, 24);
            it.NameLbl.SetBounds(24, 4, Math.Max(w - 24 - 30 - 30 - 12, 40), 24);
            it.Start.SetBounds(w - 30 - 4 - 30 - 4, 4, 30, 24);
            it.Stop.SetBounds(w - 30 - 4, 4, 30, 24);
            y += 34;
        }
    }

    void Refresh_()
    {
        int total = 0;
        foreach (var it in items)
        {
            int n = it.Count;
            total += n;
            it.NameLbl.Text = it.Name + (it.Admin ? "（管理员）" : "") + (n > 1 ? "  ×" + n : "");
            it.Dot.ForeColor = n > 0 ? Green : OffFg;
            it.NameLbl.ForeColor = n > 0 ? Green : SystemColors.ControlText;
            SetBtn(it.Start, multi || n == 0, Accent, Color.White);
            SetBtn(it.Stop, n > 0, DangerBg, Danger);
        }
        if (total != running) { running = total; UpdateSkin(); }
        tray.Text = total > 0 ? "启动器 · 运行中 " + total : "启动器";
    }

    // ================= 启动 / 关闭 =================
    void Run(Item it)
    {
        if (!multi && it.Count > 0) return;
        try
        {
            var psi = new ProcessStartInfo();
            string ext = System.IO.Path.GetExtension(it.Path).ToLowerInvariant();
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(it.Path);
            psi.UseShellExecute = true;
            if (it.Admin) psi.Verb = "runas";      // 弹出 UAC 确认
            if (ext == ".ps1")
            {
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass " + (keep ? "-NoExit " : "")
                              + "-File \"" + it.Path + "\"";
            }
            else if (ext == ".bat" || ext == ".cmd")
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = (keep ? "/k" : "/c") + " \"\"" + it.Path + "\"\"";
            }
            else psi.FileName = it.Path;
            var p = Process.Start(psi);
            if (p != null) it.Procs.Add(p);
        }
        catch (System.ComponentModel.Win32Exception wex)
        {
            if (wex.NativeErrorCode != 1223)      // 1223 = 用户在 UAC 里点了“否”，不用提示
                Msg(wex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { Msg(ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        Refresh_();
    }

    void Kill(Item it, bool lastOnly)
    {
        if (it.Count == 0) return;
        var targets = new List<Process>();
        if (lastOnly) targets.Add(it.Procs[it.Procs.Count - 1]);
        else targets.AddRange(it.Procs);
        if (it.Admin)
        {
            // 提权进程只有提权的 taskkill 才结束得了：把所有进程号合并成一次，只弹一次 UAC
            var args = new StringBuilder();
            foreach (var p in targets) args.Append("/PID " + p.Id + " ");
            args.Append("/T /F");
            try
            {
                var k = Process.Start(new ProcessStartInfo("taskkill", args.ToString())
                    { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                if (k != null) k.WaitForExit(5000);
            }
            catch (System.ComponentModel.Win32Exception) { }   // 用户取消了 UAC，保持运行状态
            catch { }
        }
        else
        {
            foreach (var p in targets)
            {
                try
                {
                    // 连同子进程（如 ssh）一起结束
                    var k = Process.Start(new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F")
                        { CreateNoWindow = true, UseShellExecute = false });
                    k.WaitForExit(3000);
                }
                catch { try { p.Kill(); } catch { } }
            }
        }
        Refresh_();
    }

    // ================= 托盘菜单（其他功能都在这里） =================
    void BuildTrayMenu()
    {
        trayMenu.Items.Clear();
        trayMenu.Items.Add("打开列表").Click += delegate { shown = true; leaveTick = 0; ShowList(); };
        trayMenu.Items.Add(Visible ? "隐藏小方块" : "显示小方块").Click += delegate
        {
            if (Visible) { popup.Hide(); Hide(); }
            else { Show(); shown = true; leaveTick = -30; Location = Calc(true).Location; }
        };
        trayMenu.Items.Add(new ToolStripSeparator());

        bool any = false;
        foreach (var item in items)
        {
            var it = item;
            int n = it.Count;
            if (n > 0) any = true;
            var mi = trayMenu.Items.Add((n > 0 ? "● " : "○ ") + it.Name + (it.Admin ? "（管理员）" : "") + (n > 1 ? "  ×" + n : ""));
            mi.ToolTipText = it.Path;
            mi.Enabled = multi || n == 0;
            mi.Click += delegate { Run(it); };
        }
        if (items.Count > 0) trayMenu.Items.Add(new ToolStripSeparator());

        trayMenu.Items.Add("＋ 添加项目…").Click += delegate { AddDialog(); };
        trayMenu.Items.Add(new ToolStripSeparator());

        var o1 = new ToolStripMenuItem("结束后保持窗口（防闪退）") { Checked = keep };
        o1.Click += delegate { keep = !keep; Save(); };
        var o2 = new ToolStripMenuItem("允许多开") { Checked = multi };
        o2.Click += delegate { multi = !multi; Save(); Refresh_(); };
        var o3 = new ToolStripMenuItem("启动时检查更新") { Checked = autoUpdate };
        o3.Click += delegate { autoUpdate = !autoUpdate; Save(); };
        var o4 = new ToolStripMenuItem("开机自启动") { Checked = IsAutoStart() };
        o4.Click += delegate { SetAutoStart(!IsAutoStart()); };
        trayMenu.Items.Add(o1); trayMenu.Items.Add(o2); trayMenu.Items.Add(o3); trayMenu.Items.Add(o4);
        trayMenu.Items.Add(new ToolStripSeparator());

        trayMenu.Items.Add("检查更新").Click += delegate { CheckUpdate(false); };
        var ver = trayMenu.Items.Add("版本 v" + AppInfo.Version);
        ver.Enabled = false;
        var closeAll = trayMenu.Items.Add("关闭全部运行项");
        closeAll.Enabled = any;
        closeAll.Click += delegate { foreach (var it in items) Kill(it, false); };
        trayMenu.Items.Add("退出").Click += delegate { ExitApp(); };
    }

    // ================= 开机自启动（当前用户，写入注册表 Run） =================
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "LoflaLauncher";

    static bool IsAutoStart()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }
        catch { return false; }
    }

    void SetAutoStart(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunName, false);
            }
        }
        catch (Exception ex)
        {
            Msg("设置开机自启动失败：" + ex.Message, "开机自启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void ExitApp()
    {
        exiting = true;
        timer.Stop();
        popup.Hide();
        tray.Visible = false;
        tray.Dispose();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    // ================= 程序内更新（GitHub Releases） =================
    // 发布时：新建 Release，tag 形如 v1.2.1，并上传编译好的 exe 作为附件
    void CheckUpdate(bool silent)
    {
        if (updating) return;
        if (AppInfo.Repo.StartsWith("YOUR_", StringComparison.Ordinal))
        {
            if (!silent) Msg("更新功能暂不可用：程序未设置更新来源。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!silent) tray.ShowBalloonTip(1500, "启动器", "正在检查更新…", ToolTipIcon.Info);
        ThreadPool.QueueUserWorkItem(delegate
        {
            string latest = null, url = null, err = null;
            try
            {
                var wc = NewClient();
                string json = wc.DownloadString("https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
                var tm = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9][0-9.]*)\"");
                var um = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.exe)\"");
                if (tm.Success) latest = tm.Groups[1].Value;
                if (um.Success) url = um.Groups[1].Value;
                if (latest == null || url == null) err = "最新 Release 里没有找到版本号或 exe 附件。";
            }
            catch (Exception ex) { err = ex.Message; }
            try { BeginInvoke(new Action(delegate { OnCheckDone(silent, latest, url, err); })); } catch { }
        });
    }

    void OnCheckDone(bool silent, string latest, string url, string err)
    {
        if (err != null)
        {
            if (!silent) Msg("检查更新失败：" + err, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Version cur = new Version(AppInfo.Version), nw;
        try { nw = new Version(latest); } catch { return; }
        if (nw > cur)
        {
            if (Msg("发现新版本 v" + latest + "（当前 v" + AppInfo.Version + "），是否立即更新？\n更新后会自动重启。",
                "发现新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                DoUpdate(url);
        }
        else if (!silent)
            Msg("当前已是最新版本（v" + AppInfo.Version + "）。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void DoUpdate(string url)
    {
        updating = true;
        tray.ShowBalloonTip(2000, "启动器", "正在下载更新…", ToolTipIcon.Info);
        string cur = Application.ExecutablePath;
        string tmp = cur + ".new";
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = null;
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                NewClient().DownloadFile(url, tmp);
                var fi = new FileInfo(tmp);
                bool ok = fi.Length > 10240;
                if (ok)
                {
                    using (var fs = File.OpenRead(tmp)) ok = fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
                }
                if (!ok) err = "下载的文件不是有效的程序。";
            }
            catch (Exception ex) { err = ex.Message; }
            try { BeginInvoke(new Action(delegate { ApplyUpdate(tmp, err); })); } catch { }
        });
    }

    void ApplyUpdate(string tmp, string err)
    {
        updating = false;
        if (err != null)
        {
            try { File.Delete(tmp); } catch { }
            Msg("更新失败：" + err, "更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string cur = Application.ExecutablePath;
        string old = cur + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(cur, old);          // 运行中的 exe 可以改名，但不能覆盖
            File.Move(tmp, cur);
            Process.Start(new ProcessStartInfo(cur, "--restart") { UseShellExecute = false });
            ExitApp();
        }
        catch (Exception ex)
        {
            try { if (!File.Exists(cur) && File.Exists(old)) File.Move(old, cur); } catch { }
            Msg("替换程序失败：" + ex.Message, "更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static WebClient NewClient()
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
        var wc = new WebClient { Encoding = Encoding.UTF8 };
        wc.Headers["User-Agent"] = "Lofla-Launcher/" + AppInfo.Version;
        return wc;
    }

    static void CleanOld()
    {
        try
        {
            string old = Application.ExecutablePath + ".old";
            if (File.Exists(old)) File.Delete(old);
            string nw = Application.ExecutablePath + ".new";
            if (File.Exists(nw)) File.Delete(nw);
        }
        catch { }
    }

    // ================= 对话框 =================
    // 小方块贴在屏幕边缘，对话框统一居中、置顶显示
    DialogResult Msg(string text, string caption, MessageBoxButtons b, MessageBoxIcon i)
    {
        busy = true;
        try
        {
            using (var o = new Form
            {
                TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(1, 1), Opacity = 0, FormBorderStyle = FormBorderStyle.None
            })
            {
                o.Show();
                return MessageBox.Show(o, text, caption, b, i);
            }
        }
        finally { busy = false; }
    }

    string Prompt(string title, string label, string init)
    {
        busy = true;
        try
        {
            var f = new Form
            {
                Text = title, Size = new Size(400, 150), FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, TopMost = true
            };
            f.Font = new Font("Microsoft YaHei UI", 9f);
            var l = new Label { Text = label, Left = 14, Top = 18, Width = 60 };
            var t = new TextBox { Left = 78, Top = 14, Width = 290, Text = init };
            var ok = new Button { Text = "确定", Left = 208, Top = 58, Width = 76, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 292, Top = 58, Width = 76, DialogResult = DialogResult.Cancel };
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.Controls.AddRange(new Control[] { l, t, ok, cancel });
            f.Shown += delegate { t.SelectAll(); t.Focus(); };
            return f.ShowDialog() == DialogResult.OK ? t.Text : null;
        }
        finally { busy = false; }
    }

    void AddDialog()
    {
        busy = true;
        try
        {
            var f = new Form
            {
                Text = "添加", Size = new Size(460, 200), FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, TopMost = true
            };
            f.Font = new Font("Microsoft YaHei UI", 9f);
            var l1 = new Label { Text = "名称", Left = 12, Top = 16, Width = 40 };
            var n = new TextBox { Left = 56, Top = 12, Width = 370 };
            var l2 = new Label { Text = "路径", Left = 12, Top = 50, Width = 40 };
            var p = new TextBox { Left = 56, Top = 46, Width = 300 };
            var b = new Button { Text = "浏览", Left = 362, Top = 44, Width = 64 };
            var adm = new CheckBox { Text = "以管理员身份运行（启动时会弹出 UAC 确认）", Left = 56, Top = 80, Width = 370 };
            var ok = new Button { Text = "确定", Left = 270, Top = 122, Width = 76, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 350, Top = 122, Width = 76, DialogResult = DialogResult.Cancel };
            b.Click += delegate
            {
                var d = new OpenFileDialog { Filter = "脚本/程序|*.ps1;*.bat;*.cmd;*.exe|所有文件|*.*" };
                if (d.ShowDialog() == DialogResult.OK)
                {
                    p.Text = d.FileName;
                    if (n.Text.Length == 0) n.Text = System.IO.Path.GetFileNameWithoutExtension(d.FileName);
                }
            };
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.Controls.AddRange(new Control[] { l1, n, l2, p, b, adm, ok, cancel });
            if (f.ShowDialog() == DialogResult.OK && p.Text.Trim().Length > 0)
            {
                string name = n.Text.Trim().Length > 0 ? n.Text.Trim().Replace("|", " ")
                            : System.IO.Path.GetFileNameWithoutExtension(p.Text.Trim());
                AddItem(name, p.Text.Trim().Trim(new char[] { '"' }), adm.Checked);
                Save();
                ResizePopup();
            }
        }
        finally { busy = false; }
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // --restart：更新后由旧进程拉起，稍等旧实例退出
        bool restart = Array.IndexOf(args, "--restart") >= 0;
        bool created = false;
        Mutex mtx = null;
        for (int i = 0; i < 40; i++)
        {
            mtx = new Mutex(true, "Local\\Lofla.Launcher.SingleInstance", out created);
            if (created || !restart) break;
            mtx.Close();
            Thread.Sleep(200);
        }
        if (!created)
        {
            LauncherForm.NotifyExisting();   // 已有实例：唤出它的列表（即使小方块已隐藏）
            return;
        }
        Application.EnableVisualStyles();
        Application.Run(new LauncherForm());
        GC.KeepAlive(mtx);
    }
}
