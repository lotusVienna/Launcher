using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class AppInfo
{
    public const string Version = "1.1.1";
    // 改成你自己的 GitHub 仓库，格式：用户名/仓库名
    public const string Repo = "lotusVienna/Launcher";
}

class Item
{
    public string Name, Path;
    public List<Process> Procs = new List<Process>();   // 同一项可同时运行多个实例
    public Panel Row;
    public Label Dot, NameLbl, PathLbl;
    public Button Start, Stop;

    public int Count
    {
        get
        {
            for (int i = Procs.Count - 1; i >= 0; i--)
            {
                bool dead;
                try { dead = Procs[i].HasExited; } catch { dead = true; }
                if (dead) Procs.RemoveAt(i);
            }
            return Procs.Count;
        }
    }
}

class LauncherForm : Form
{
    // ---------- 单实例：唤出已有窗口 ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string msg);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    static readonly uint WM_SHOWME = RegisterWindowMessage("Lofla.Launcher.ShowMe");

    public static void NotifyExisting()
    {
        PostMessage((IntPtr)0xFFFF, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)WM_SHOWME) { ShowWindow_(); return; }
        base.WndProc(ref m);
    }

    // ---------- 配色 ----------
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color Bg = Color.FromArgb(245, 246, 248);
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

    List<Item> items = new List<Item>();
    Panel list = new Panel();
    CheckBox keep = new CheckBox();
    CheckBox multi = new CheckBox();
    CheckBox trayChk = new CheckBox();
    CheckBox autoUpd = new CheckBox();
    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    NotifyIcon tray;
    ContextMenuStrip trayMenu = new ContextMenuStrip();
    ToolTip tip = new ToolTip();
    Button updBtn;
    bool exiting = false, loading = true, balloonShown = false, updating = false;

    public LauncherForm()
    {
        Text = "启动器";
        Size = new Size(640, 440);
        MinimumSize = new Size(520, 260);
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Bg;
        AllowDrop = true;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ---- 顶部标题栏 ----
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Color.White };
        var title = new Label
        {
            Text = "启动器", Left = 16, Top = 10, AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold)
        };
        var ver = new Label
        {
            Text = "v" + AppInfo.Version, Left = 18, Top = 34, AutoSize = true,
            ForeColor = Muted, Font = new Font("Microsoft YaHei UI", 8f)
        };
        var add = MkBtn("＋ 添加", Accent, Color.White, 84);
        add.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        add.Top = 14; add.Left = header.Width - 84 - 16 - 96 - 8;
        add.Click += delegate { AddDialog(); };
        updBtn = MkBtn("检查更新", OffBg, Color.FromArgb(55, 65, 81), 96);
        updBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        updBtn.Top = 14; updBtn.Left = header.Width - 96 - 16;
        updBtn.Click += delegate { CheckUpdate(false); };
        header.Controls.Add(title); header.Controls.Add(ver);
        header.Controls.Add(add); header.Controls.Add(updBtn);
        header.Paint += delegate (object s, PaintEventArgs e)
        {
            e.Graphics.DrawLine(new Pen(Line), 0, header.Height - 1, header.Width, header.Height - 1);
        };

        // ---- 选项栏 ----
        var opts = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 38, WrapContents = false, BackColor = Color.White,
            Padding = new Padding(12, 8, 6, 0)
        };
        keep.Text = "结束后保持窗口"; keep.AutoSize = true; keep.Checked = true;
        multi.Text = "允许多开"; multi.AutoSize = true; multi.Checked = true;
        trayChk.Text = "关闭/最小化到托盘"; trayChk.AutoSize = true; trayChk.Checked = true;
        autoUpd.Text = "启动时检查更新"; autoUpd.AutoSize = true; autoUpd.Checked = true;
        tip.SetToolTip(keep, "脚本结束后窗口保持打开，报错信息不会一闪而过");
        opts.Controls.Add(keep); opts.Controls.Add(multi);
        opts.Controls.Add(trayChk); opts.Controls.Add(autoUpd);
        opts.Paint += delegate (object s, PaintEventArgs e)
        {
            e.Graphics.DrawLine(new Pen(Line), 0, opts.Height - 1, opts.Width, opts.Height - 1);
        };

        list.Dock = DockStyle.Fill; list.AutoScroll = true; list.BackColor = Bg;
        list.AllowDrop = true;
        Controls.Add(list); Controls.Add(opts); Controls.Add(header);

        DragEventHandler enter = delegate (object s, DragEventArgs e) { e.Effect = DragDropEffects.Copy; };
        DragEventHandler drop = delegate (object s, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null) return;
            foreach (var f in files) AddItem(System.IO.Path.GetFileNameWithoutExtension(f), f);
            Save();
        };
        DragEnter += enter; DragDrop += drop;
        list.DragEnter += enter; list.DragDrop += drop;

        // ---- 托盘 ----
        tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "启动器", Visible = true };
        trayMenu.Opening += delegate { BuildTrayMenu(); };
        tray.ContextMenuStrip = trayMenu;
        tray.MouseDoubleClick += delegate (object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ToggleWindow();
        };

        CleanOld();
        Load_();
        loading = false;
        if (items.Count > 0 && !File.Exists(Cfg)) Save();

        keep.CheckedChanged += delegate { Save(); };
        multi.CheckedChanged += delegate { Save(); Refresh_(); };
        trayChk.CheckedChanged += delegate { Save(); };
        autoUpd.CheckedChanged += delegate { Save(); };

        timer.Interval = 800;
        timer.Tick += delegate { Refresh_(); };
        timer.Start();
        Refresh_();

        // 启动 3 秒后静默检查更新
        if (autoUpd.Checked && !AppInfo.Repo.StartsWith("YOUR_", StringComparison.Ordinal))
        {
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += delegate { t.Stop(); t.Dispose(); CheckUpdate(true); };
            t.Start();
        }
    }

    static Button MkBtn(string text, Color back, Color fore, int w)
    {
        var b = new Button
        {
            Text = text, Width = w, Height = 28, FlatStyle = FlatStyle.Flat,
            BackColor = back, ForeColor = fore, Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
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
                    bool v = kv[1].Trim() == "1";
                    if (kv[0] == "keep") keep.Checked = v;
                    else if (kv[0] == "multi") multi.Checked = v;
                    else if (kv[0] == "tray") trayChk.Checked = v;
                    else if (kv[0] == "autoupdate") autoUpd.Checked = v;
                }
                continue;
            }
            int i = line.IndexOf('|');
            if (i > 0) AddItem(line.Substring(0, i), line.Substring(i + 1));
        }
    }

    void Save()
    {
        if (loading) return;
        var sb = new StringBuilder();
        sb.AppendLine("#keep=" + (keep.Checked ? "1" : "0"));
        sb.AppendLine("#multi=" + (multi.Checked ? "1" : "0"));
        sb.AppendLine("#tray=" + (trayChk.Checked ? "1" : "0"));
        sb.AppendLine("#autoupdate=" + (autoUpd.Checked ? "1" : "0"));
        foreach (var it in items) sb.AppendLine(it.Name + "|" + it.Path);
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Cfg, sb.ToString(), new UTF8Encoding(false));
    }

    // ================= 列表 =================
    void AddItem(string name, string path)
    {
        var it = new Item { Name = name, Path = path };

        it.Row = new Panel { Height = 48, BackColor = Color.White };
        it.Row.Paint += delegate (object s, PaintEventArgs e)
        {
            var r = it.Row;
            e.Graphics.DrawRectangle(new Pen(Line), 0, 0, r.Width - 1, r.Height - 1);
        };
        it.Dot = new Label { Text = "●", ForeColor = OffFg, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
        it.NameLbl = new Label
        {
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
        };
        it.PathLbl = new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted };
        it.Start = MkBtn("启动", Accent, Color.White, 60);
        it.Stop = MkBtn("关闭", DangerBg, Danger, 60);
        it.Start.Click += delegate { Run(it); };
        it.Stop.Click += delegate { Kill(it, false); };

        tip.SetToolTip(it.PathLbl, path);
        tip.SetToolTip(it.NameLbl, path);

        var menu = new ContextMenuStrip();
        menu.Items.Add("重命名").Click += delegate { Rename(it); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关闭最近启动的一个").Click += delegate { Kill(it, true); };
        menu.Items.Add("关闭全部").Click += delegate { Kill(it, false); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("移除此项").Click += delegate
        {
            Kill(it, false);
            list.Controls.Remove(it.Row);
            items.Remove(it); Save(); Layout_();
        };
        it.Row.ContextMenuStrip = menu;
        it.Dot.ContextMenuStrip = menu;
        it.NameLbl.ContextMenuStrip = menu;
        it.PathLbl.ContextMenuStrip = menu;
        it.NameLbl.DoubleClick += delegate { Rename(it); };

        it.Row.Controls.Add(it.Dot); it.Row.Controls.Add(it.NameLbl); it.Row.Controls.Add(it.PathLbl);
        it.Row.Controls.Add(it.Start); it.Row.Controls.Add(it.Stop);
        list.Controls.Add(it.Row);
        items.Add(it);
        Layout_();
        Refresh_();
    }

    void Rename(Item it)
    {
        string nn = Prompt("重命名", "新名称", it.Name);
        if (nn == null) return;
        nn = nn.Trim().Replace("|", " ");
        if (nn.Length == 0 || nn == it.Name) return;
        it.Name = nn;
        Save();
        Refresh_();
    }

    void Layout_()
    {
        int y = 10;
        int w = Math.Max(list.ClientSize.Width - 20, 300);
        foreach (var it in items)
        {
            it.Row.SetBounds(10, y, w, 48);
            it.Dot.SetBounds(6, 10, 24, 28);
            it.NameLbl.SetBounds(32, 4, 150, 40);
            int btnArea = 60 + 60 + 8 + 12 + 8;
            it.PathLbl.SetBounds(188, 4, Math.Max(w - 188 - btnArea, 40), 40);
            it.Start.SetBounds(w - 60 - 60 - 8 - 10, 10, 60, 28);
            it.Stop.SetBounds(w - 60 - 10, 10, 60, 28);
            y += 56;
        }
    }

    void Refresh_()
    {
        int total = 0;
        foreach (var it in items)
        {
            int n = it.Count;
            total += n;
            it.NameLbl.Text = it.Name + (n > 1 ? "  ×" + n : "");
            it.PathLbl.Text = it.Path;
            it.Dot.ForeColor = n > 0 ? Green : OffFg;
            it.NameLbl.ForeColor = n > 0 ? Green : SystemColors.ControlText;
            SetBtn(it.Start, multi.Checked || n == 0, Accent, Color.White);
            SetBtn(it.Stop, n > 0, DangerBg, Danger);
        }
        tray.Text = total > 0 ? "启动器 · 运行中 " + total : "启动器";
    }

    // ================= 启动 / 关闭 =================
    void Run(Item it)
    {
        if (!multi.Checked && it.Count > 0) return;
        try
        {
            var psi = new ProcessStartInfo();
            string ext = System.IO.Path.GetExtension(it.Path).ToLowerInvariant();
            psi.WorkingDirectory = System.IO.Path.GetDirectoryName(it.Path);
            psi.UseShellExecute = true;
            if (ext == ".ps1")
            {
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass " + (keep.Checked ? "-NoExit " : "")
                              + "-File \"" + it.Path + "\"";
            }
            else if (ext == ".bat" || ext == ".cmd")
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = (keep.Checked ? "/k" : "/c") + " \"\"" + it.Path + "\"\"";
            }
            else psi.FileName = it.Path;
            var p = Process.Start(psi);
            if (p != null) it.Procs.Add(p);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动失败"); }
        Refresh_();
    }

    void Kill(Item it, bool lastOnly)
    {
        if (it.Count == 0) return;
        var targets = new List<Process>();
        if (lastOnly) targets.Add(it.Procs[it.Procs.Count - 1]);
        else targets.AddRange(it.Procs);
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
        Refresh_();
    }

    // ================= 托盘 =================
    void ShowWindow_()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    void ToggleWindow()
    {
        if (Visible && WindowState != FormWindowState.Minimized) { Hide(); return; }
        ShowWindow_();
    }

    void BuildTrayMenu()
    {
        trayMenu.Items.Clear();
        trayMenu.Items.Add(Visible ? "隐藏窗口" : "显示窗口").Click += delegate { ToggleWindow(); };
        trayMenu.Items.Add(new ToolStripSeparator());

        bool any = false;
        foreach (var item in items)
        {
            var it = item;
            int n = it.Count;
            if (n > 0) any = true;
            var mi = trayMenu.Items.Add((n > 0 ? "● " : "○ ") + it.Name + (n > 1 ? "  ×" + n : ""));
            mi.ToolTipText = it.Path;
            mi.Enabled = multi.Checked || n == 0;
            mi.Click += delegate { Run(it); };
        }
        if (items.Count > 0) trayMenu.Items.Add(new ToolStripSeparator());

        var closeAll = trayMenu.Items.Add("关闭全部运行项");
        closeAll.Enabled = any;
        closeAll.Click += delegate { foreach (var it in items) Kill(it, false); };

        trayMenu.Items.Add("退出").Click += delegate { ExitApp(); };
    }

    void ExitApp()
    {
        exiting = true;
        tray.Visible = false;
        tray.Dispose();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && trayChk.Checked && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!balloonShown)
            {
                balloonShown = true;
                tray.ShowBalloonTip(2000, "启动器", "已收到托盘，右键托盘图标可退出。", ToolTipIcon.Info);
            }
            return;
        }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && trayChk.Checked && !loading)
        {
            Hide();
            return;
        }
        Layout_();
    }

    // ================= 程序内更新（GitHub Releases） =================
    // 发布时：新建 Release，tag 形如 v1.1.2，并上传编译好的 exe 作为附件
    void CheckUpdate(bool silent)
    {
        if (updating) return;
        if (AppInfo.Repo.StartsWith("YOUR_", StringComparison.Ordinal))
        {
            if (!silent) MessageBox.Show("尚未配置 GitHub 仓库：请修改源码中 AppInfo.Repo 后重新编译。", "检查更新");
            return;
        }
        updBtn.Enabled = false; updBtn.Text = "检查中…";
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
        updBtn.Enabled = true; updBtn.Text = "检查更新";
        if (err != null)
        {
            if (!silent) MessageBox.Show("检查更新失败：" + err, "检查更新");
            return;
        }
        Version cur = new Version(AppInfo.Version), nw;
        try { nw = new Version(latest); } catch { return; }
        if (nw > cur)
        {
            if (MessageBox.Show("发现新版本 v" + latest + "（当前 v" + AppInfo.Version + "），是否立即更新？\n更新后会自动重启。",
                "发现新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                DoUpdate(url);
        }
        else if (!silent)
            MessageBox.Show("当前已是最新版本（v" + AppInfo.Version + "）。", "检查更新");
    }

    void DoUpdate(string url)
    {
        updating = true;
        updBtn.Enabled = false; updBtn.Text = "下载中…";
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
        updBtn.Enabled = true; updBtn.Text = "检查更新";
        if (err != null)
        {
            try { File.Delete(tmp); } catch { }
            MessageBox.Show("更新失败：" + err, "更新");
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
            MessageBox.Show("替换程序失败：" + ex.Message, "更新");
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
    string Prompt(string title, string label, string init)
    {
        var f = new Form
        {
            Text = title, Size = new Size(400, 150), FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false
        };
        f.Font = Font;
        var l = new Label { Text = label, Left = 14, Top = 18, Width = 60 };
        var t = new TextBox { Left = 78, Top = 14, Width = 290, Text = init };
        var ok = new Button { Text = "确定", Left = 208, Top = 58, Width = 76, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 292, Top = 58, Width = 76, DialogResult = DialogResult.Cancel };
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Controls.AddRange(new Control[] { l, t, ok, cancel });
        f.Shown += delegate { t.SelectAll(); t.Focus(); };
        return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
    }

    void AddDialog()
    {
        var f = new Form
        {
            Text = "添加", Size = new Size(460, 160), FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false
        };
        f.Font = Font;
        var l1 = new Label { Text = "名称", Left = 12, Top = 16, Width = 40 };
        var n = new TextBox { Left = 56, Top = 12, Width = 370 };
        var l2 = new Label { Text = "路径", Left = 12, Top = 50, Width = 40 };
        var p = new TextBox { Left = 56, Top = 46, Width = 300 };
        var b = new Button { Text = "浏览", Left = 362, Top = 44, Width = 64 };
        var ok = new Button { Text = "确定", Left = 270, Top = 84, Width = 76, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 350, Top = 84, Width = 76, DialogResult = DialogResult.Cancel };
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
        f.Controls.AddRange(new Control[] { l1, n, l2, p, b, ok, cancel });
        if (f.ShowDialog(this) == DialogResult.OK && p.Text.Trim().Length > 0)
        {
            string name = n.Text.Trim().Length > 0 ? n.Text.Trim().Replace("|", " ")
                        : System.IO.Path.GetFileNameWithoutExtension(p.Text.Trim());
            AddItem(name, p.Text.Trim().Trim(new char[] { '"' }));
            Save();
        }
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
            LauncherForm.NotifyExisting();   // 已有实例：唤出它（包括收在托盘里的）
            return;
        }
        Application.EnableVisualStyles();
        Application.Run(new LauncherForm());
        GC.KeepAlive(mtx);
    }
}
