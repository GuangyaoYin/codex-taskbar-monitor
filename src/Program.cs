using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexTaskbarMonitor
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            TaskbarEnvironment.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--render-preview") return RenderPreview(args[1]);
            if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
            bool owner;
            using (Mutex mutex = new Mutex(true, @"Local\CodexTaskbarMonitor.1", out owner))
            {
                if (!owner) return 0;
                Application.Run(new MonitorForm());
            }
            return 0;
        }

        private static int SelfTest()
        {
            if (DisplayText.Percent(null) != "--" || DisplayText.Percent(43) != "43%") return 1;
            if (DisplayText.WindowLabel(10080) != "周" || DisplayText.WindowLabel(300) != "5h") return 2;
            CodexSnapshot quota = new CodexSnapshot { Status = CodexStatus.Ready };
            QuotaBucket bucket = new QuotaBucket { Id = "codex" };
            bucket.Windows.Add(new QuotaWindow { UsedPercent = 43, WindowDurationMins = 10080 });
            quota.Buckets.Add(bucket);
            if (DisplayText.CompactQuota(quota) != "Codex  周 43%") return 3;
            quota.IsStale = true;
            if (!DisplayText.CompactQuota(quota).EndsWith("旧")) return 4;
            return 0;
        }

        private static int RenderPreview(string directory)
        {
            Directory.CreateDirectory(directory);
            using (HardwareSampler hardware = new HardwareSampler())
            using (NetworkSampler net = new NetworkSampler())
            using (CodexQuotaClient quota = new CodexQuotaClient())
            using (MonitorForm form = new MonitorForm(false))
            {
                hardware.Sample();
                net.Sample();
                Thread.Sleep(1050);
                HardwareSnapshot h = hardware.Sample();
                NetworkSnapshot n = net.Sample();
                form.SetPreviewData(h, quota.Read(null), n);
                form.SavePreview(directory);
            }
            return 0;
        }
    }

    public sealed class MonitorForm : Form
    {
        private readonly AppSettings settings;
        private readonly HardwareSampler sampler;
        private readonly NetworkSampler networkSampler;
        private readonly CodexQuotaClient quotaClient;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly DetailsWindow details;
        private readonly System.Windows.Forms.Timer timer;
        private HardwareSnapshot hardware;
        private NetworkSnapshot network;
        private CodexSnapshot quota;
        private TaskbarPlacement placement;
        private int hardwareBusy;
        private int quotaBusy;
        private int placementBusy;
        private DateTime nextHardware = DateTime.MinValue;
        private DateTime nextPlacement = DateTime.MinValue;
        private DateTime nextQuota = DateTime.MinValue;
        private DateTime detailsUntil;
        private DateTime noticeUntil;
        private Point detailsAnchor;
        private bool detailsFromTray;
        private int quotaFailures;
        private bool shuttingDown;
        private bool allowDisplay;
        private bool dark;
        private float scale = 1;
        private bool dragging;
        private bool moved;
        private bool hovered;
        private Point dragOffset;
        private string positionState = "等待任务栏";
        private readonly bool interactive;

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000080; return p; }
        }
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(value && allowDisplay);
        }

        public MonitorForm() : this(true) { }
        public MonitorForm(bool runInteractive)
        {
            interactive = runInteractive;
            settings = AppSettings.Load();
            sampler = new HardwareSampler();
            networkSampler = new NetworkSampler();
            quotaClient = new CodexQuotaClient();
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Width = CompactPainter.PreferredWidthDip; Height = CompactPainter.PreferredHeightDip;
            Text = "任务栏状态条";
            Icon = SystemIcons.Application;
            dark = AppSettings.DarkTheme();
            details = new DetailsWindow();
            menu = new ContextMenuStrip();
            menu.Font = new Font("SimSun", 10);
            menu.Renderer = new QuietMenuRenderer(delegate { return dark; });
            menu.Opening += delegate { RebuildMenu(); };
            ContextMenuStrip = menu;
            tray = new NotifyIcon();
            tray.Icon = CreateTrayIcon();
            tray.Text = "任务栏状态条：正在启动";
            tray.ContextMenuStrip = menu;
            tray.Visible = runInteractive;
            tray.MouseMove += delegate { if (!menu.Visible) ShowDetails(Cursor.Position, true); };
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowDetails(Cursor.Position, true);
            };
            MouseEnter += delegate { hovered = true; Invalidate(); if (!dragging && !menu.Visible) ShowDetails(Location, false); };
            MouseLeave += delegate { hovered = false; Invalidate(); detailsUntil = DateTime.UtcNow.AddMilliseconds(400); };
            menu.Opened += delegate { details.Hide(); };
            MouseDown += BeginDrag;
            MouseMove += ContinueDrag;
            MouseUp += EndDrag;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += Tick;
            if (runInteractive)
            {
                // Ensure an HWND exists without displaying an incorrectly positioned startup window.
                IntPtr unused = Handle;
                timer.Start();
                Tick(this, EventArgs.Empty);
            }
        }

        private Icon CreateTrayIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(Color.FromArgb(45, 112, 180))) g.FillEllipse(b, 1, 1, 30, 30);
                using (Pen p = new Pen(Color.White, 2.6f))
                    g.DrawLines(p, new PointF[] { new PointF(6, 20), new PointF(11, 20), new PointF(14, 10), new PointF(18, 24), new PointF(21, 15), new PointF(26, 15) });
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        private void Tick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            if (now >= nextHardware && Interlocked.CompareExchange(ref hardwareBusy, 1, 0) == 0)
            {
                nextHardware = now.AddSeconds(1);
                Task.Factory.StartNew(delegate
                {
                    HardwareSnapshot result;
                    try { result = sampler.Sample(); }
                    catch { result = new HardwareSnapshot { TimestampUtc = DateTime.UtcNow, Error = "硬件采集暂不可用" }; }
                    NetworkSnapshot networkResult;
                    try { networkResult = networkSampler.Sample(); }
                    catch { networkResult = new NetworkSnapshot { TimestampUtc = DateTime.UtcNow, StatusMessage = "网速暂不可用" }; }
                    Post(delegate { hardware = result; network = networkResult; UpdateView(); });
                    Interlocked.Exchange(ref hardwareBusy, 0);
                    if (shuttingDown) { sampler.Dispose(); networkSampler.Dispose(); }
                });
            }
            if (now >= nextPlacement && Interlocked.CompareExchange(ref placementBusy, 1, 0) == 0)
            {
                nextPlacement = now.AddSeconds(2);
                int desiredWidth = Width, desiredHeight = Height;
                Task.Factory.StartNew(delegate
                {
                    TaskbarPlacement result;
                    try { result = TaskbarEnvironment.Inspect(desiredWidth, desiredHeight); }
                    catch { result = new TaskbarPlacement { ShouldHide = true, Reason = "任务栏位置暂不可用", Dpi = 96 }; }
                    Post(delegate { ApplyPlacement(result); });
                    Interlocked.Exchange(ref placementBusy, 0);
                });
            }
            if (now >= nextQuota) RefreshQuota();
            if (details.Visible && now > detailsUntil && !details.Bounds.Contains(Cursor.Position) && (!Visible || !Bounds.Contains(Cursor.Position))) details.Hide();
        }

        private void Post(Action action)
        {
            if (shuttingDown || IsDisposed) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!shuttingDown) action(); }); }
            catch (InvalidOperationException) { }
        }

        private void RefreshQuota()
        {
            if (Interlocked.CompareExchange(ref quotaBusy, 1, 0) != 0) return;
            nextQuota = DateTime.UtcNow.AddMinutes(2);
            string path = settings.CodexPath;
            Task.Factory.StartNew(delegate
            {
                CodexSnapshot result;
                try { result = quotaClient.Read(path); }
                catch { result = new CodexSnapshot { Status = CodexStatus.Error, StatusMessage = "额度查询失败，请重试", AttemptUtc = DateTime.UtcNow, IsStale = true }; }
                Post(delegate
                {
                    quota = result;
                    quotaFailures = result.Status == CodexStatus.Error ? Math.Min(4, quotaFailures + 1) : 0;
                    // Recover promptly from a transient timeout instead of leaving
                    // a stale snapshot untouched for the previous 2/4/8 minutes.
                    nextQuota = result.Status == CodexStatus.Error
                        ? DateTime.UtcNow.AddSeconds(Math.Min(120, 15 * Math.Pow(2, quotaFailures - 1)))
                        : DateTime.UtcNow.AddMinutes(2);
                    UpdateView();
                });
                Interlocked.Exchange(ref quotaBusy, 0);
            });
        }

        private GpuSnapshot SelectedGpu()
        {
            if (hardware == null || hardware.Gpus.Count == 0) return null;
            foreach (GpuSnapshot gpu in hardware.Gpus)
                if (gpu.Id == settings.SelectedGpuId) return gpu;
            foreach (GpuSnapshot gpu in hardware.Gpus)
                if (gpu.Name != null && gpu.Name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) return gpu;
            return hardware.Gpus[0];
        }

        private void UpdateView()
        {
            GpuSnapshot gpu = SelectedGpu();
            string status = "CPU " + DisplayText.Percent(hardware == null ? null : hardware.CpuPercent) + "  RAM " + DisplayText.Percent(hardware == null ? null : hardware.MemoryPercent) + "  GPU " + DisplayText.Percent(gpu == null ? null : gpu.UtilizationPercent) + " | ↓ " + DisplayText.Speed(network == null ? null : network.DownloadBytesPerSecond) + " ↑ " + DisplayText.Speed(network == null ? null : network.UploadBytesPerSecond);
            tray.Text = status.Length > 63 ? status.Substring(0, 63) : status;
            if (details.Visible && DateTime.UtcNow >= noticeUntil)
            {
                details.UpdateContent(BuildDetails(), scale, dark);
                PositionDetails();
            }
            Invalidate();
        }

        private void ApplyPlacement(TaskbarPlacement result)
        {
            placement = result;
            scale = Math.Max(1, result.Dpi / 96.0f);
            dark = AppSettings.DarkTheme();
            Size target = new Size((int)(CompactPainter.PreferredWidthDip * scale), (int)(CompactPainter.PreferredHeightDip * scale));
            Size = target;
            positionState = result.Reason;
            if (result.ShouldHide || result.SafeArea.Width < Width || result.SafeArea.Height < Height)
            {
                allowDisplay = false;
                Hide();
                if (!detailsFromTray) details.Hide();
                return;
            }
            if (!dragging)
            {
                Rectangle safe = result.SafeArea;
                int x = safe.Right - Width;
                int y = safe.Top + (safe.Height - Height) / 2;
                if (settings.ManualX.HasValue) x = settings.ManualX.Value;
                if (settings.ManualY.HasValue) y = settings.ManualY.Value;
                Location = Clamp(new Point(x, y), safe);
            }
            allowDisplay = true;
            if (!Visible) Show();
            if (!menu.Visible)
            {
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
                if (details.Visible) SetWindowPos(details.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
            }
            UpdateView();
        }

        private Point Clamp(Point point, Rectangle area)
        {
            return new Point(Math.Max(area.Left, Math.Min(area.Right - Width, point.X)), Math.Max(area.Top, Math.Min(area.Bottom - Height, point.Y)));
        }

        private void BeginDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = true; moved = false; dragOffset = e.Location; Capture = true; details.Hide();
        }
        private void ContinueDrag(object sender, MouseEventArgs e)
        {
            if (!dragging || placement == null) return;
            Point point = Cursor.Position;
            Point desired = new Point(point.X - dragOffset.X, point.Y - dragOffset.Y);
            Point next = Clamp(desired, placement.SafeArea);
            if (next != Location) { moved = true; Location = next; }
        }
        private void EndDrag(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false; Capture = false;
            if (moved)
            {
                settings.ManualX = Left; settings.ManualY = Top; SaveSettings();
            }
            else ShowDetails(Location, false);
        }

        private void ShowDetails(Point anchor, bool fromTray)
        {
            if (shuttingDown) return;
            noticeUntil = DateTime.MinValue;
            detailsAnchor = anchor;
            detailsFromTray = fromTray;
            details.UpdateContent(BuildDetails(), scale, dark);
            PositionDetails();
            detailsUntil = DateTime.UtcNow.AddSeconds(fromTray ? 5 : 1);
            if (!details.Visible) details.Show();
        }

        private void PositionDetails()
        {
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            int x = detailsFromTray ? detailsAnchor.X - details.Width : Left;
            int y = detailsFromTray ? detailsAnchor.Y - details.Height - (int)(12 * scale) : Top - details.Height - (int)(6 * scale);
            if (y < screen.Top) y = (detailsFromTray ? detailsAnchor.Y : Bottom) + (int)(6 * scale);
            details.Location = new Point(Math.Max(screen.Left, Math.Min(screen.Right - details.Width, x)), Math.Max(screen.Top, Math.Min(screen.Bottom - details.Height, y)));
        }

        private List<string> BuildDetails()
        {
            List<string> content = DisplayText.Details(hardware, SelectedGpu(), quota, positionState, network);
            if (quota != null && quota.Status == CodexStatus.Error && nextQuota != DateTime.MinValue)
            {
                int insert = content.FindIndex(delegate(string line) { return line.StartsWith("额度更新：", StringComparison.Ordinal); });
                content.Insert(insert < 0 ? Math.Max(1, content.Count - 2) : insert + 1,
                    Interlocked.CompareExchange(ref quotaBusy, 0, 0) != 0 ? "额度查询：正在重新查询"
                    : "自动重试：" + nextQuota.ToLocalTime().ToString("HH:mm:ss"));
            }
            return content;
        }

        private void RebuildMenu()
        {
            menu.Items.Clear();
            AddMenu("刷新 Codex 额度", delegate { nextQuota = DateTime.MinValue; RefreshQuota(); });
            AddMenu("显示详情", delegate { ShowDetails(Cursor.Position, true); });
            ToolStripMenuItem gpus = new ToolStripMenuItem("选择 GPU");
            gpus.DropDown.Font = menu.Font;
            gpus.DropDown.Renderer = menu.Renderer;
            GpuSnapshot selected = SelectedGpu();
            if (hardware != null)
                foreach (GpuSnapshot gpu in hardware.Gpus)
                {
                    string id = gpu.Id;
                    ToolStripMenuItem item = new ToolStripMenuItem(gpu.Name);
                    item.Checked = selected != null && selected.Id == id;
                    item.Click += delegate { settings.SelectedGpuId = id; SaveSettings(); UpdateView(); };
                    gpus.DropDownItems.Add(item);
                }
            gpus.Enabled = gpus.DropDownItems.Count > 0;
            menu.Items.Add(gpus);
            menu.Items.Add(new ToolStripSeparator());
            AddMenu("恢复自动位置", delegate { settings.ManualX = null; settings.ManualY = null; SaveSettings(); nextPlacement = DateTime.MinValue; });
            ToolStripMenuItem startup = new ToolStripMenuItem("开机启动");
            startup.Checked = AppSettings.StartupEnabled();
            startup.Click += delegate
            {
                try { AppSettings.SetStartup(!startup.Checked, Application.ExecutablePath); }
                catch { ShowNotice("无法保存开机启动设置，请检查当前用户权限。"); }
            };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            AddMenu("选择 Codex 程序路径…", SelectCodexPath);
            AddMenu("自动查找 Codex", delegate { settings.CodexPath = null; SaveSettings(); nextQuota = DateTime.MinValue; RefreshQuota(); });
            AddMenu("登录 Codex", LoginCodex);
            AddMenu("官方安装与登录说明", delegate { OpenUrl("https://learn.chatgpt.com/docs/cli"); });
            AddMenu("使用说明", delegate
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "使用说明.html");
                if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            });
            menu.Items.Add(new ToolStripSeparator());
            AddMenu("退出", delegate { Close(); });
        }

        private void AddMenu(string text, Action action)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate { action(); };
            menu.Items.Add(item);
        }
        private void SaveSettings()
        {
            try { settings.Save(); }
            catch { ShowNotice("设置未能保存；本次运行仍可继续使用。"); }
        }
        private void SelectCodexPath()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择官方 Codex 程序";
                dialog.Filter = "Codex 可执行程序 (codex.exe)|codex.exe|可执行程序 (*.exe)|*.exe";
                if (dialog.ShowDialog() != DialogResult.OK) return;
                settings.CodexPath = dialog.FileName;
                SaveSettings(); nextQuota = DateTime.MinValue; RefreshQuota();
            }
        }
        private void LoginCodex()
        {
            string path = CodexQuotaClient.FindCodexPath(settings.CodexPath);
            if (String.IsNullOrEmpty(path)) { OpenUrl("https://learn.chatgpt.com/docs/cli"); return; }
            try
            {
                // This interactive console belongs to an explicitly requested login action.
                Process.Start(new ProcessStartInfo(path, "login") { UseShellExecute = true });
                nextQuota = DateTime.UtcNow.AddSeconds(15);
            }
            catch { ShowNotice("无法启动官方登录流程，请尝试在终端运行 codex login。"); }
        }
        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
        private void ShowNotice(string message)
        {
            details.UpdateContent(new List<string> { "任务栏状态条", message }, scale, dark);
            details.Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - details.Width - 16, Screen.PrimaryScreen.WorkingArea.Bottom - details.Height - 16);
            detailsUntil = DateTime.UtcNow.AddSeconds(8); noticeUntil = detailsUntil; details.Show();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            CompactPainter.Draw(e.Graphics,ClientRectangle,scale,dark,hovered,hardware,SelectedGpu(),quota,network);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if(Width<1||Height<1)return;
            using(GraphicsPath path=VisualStyle.Round(new RectangleF(0,0,Width,Height),6*Math.Max(1,scale)))
            {
                Region previous=Region;
                Region=new Region(path);
                if(previous!=null)previous.Dispose();
            }
        }

        public void SetPreviewData(HardwareSnapshot h, CodexSnapshot q, NetworkSnapshot n = null)
        {
            hardware = h; quota = q; network = n; scale = 1.5f;
            Width = (int)(CompactPainter.PreferredWidthDip * scale); Height = (int)(CompactPainter.PreferredHeightDip * scale);
            positionState = "通知区左侧空白";
        }
        public void SavePreview(string directory)
        {
            IntPtr unused = Handle;
            foreach (bool theme in new bool[] { false, true })
            {
                dark = theme;
                using (Bitmap bitmap = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                    bitmap.Save(Path.Combine(directory, theme ? "monitor-dark.png" : "monitor-light.png"));
                }
                details.UpdateContent(BuildDetails(), scale, dark);
                IntPtr other = details.Handle;
                using (Bitmap bitmap = new Bitmap(details.Width, details.Height))
                {
                    details.DrawToBitmap(bitmap, new Rectangle(Point.Empty, details.Size));
                    bitmap.Save(Path.Combine(directory, theme ? "details-dark.png" : "details-light.png"));
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            shuttingDown = true;
            timer.Stop();
            tray.Visible = false;
            quotaClient.Dispose();
            details.Close();
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                shuttingDown = true;
                timer.Dispose();
                tray.Visible = false;
                Icon ownedIcon = tray.Icon;
                tray.Dispose(); if (ownedIcon != null) ownedIcon.Dispose();
                menu.Dispose(); details.Dispose(); quotaClient.Dispose();
                if (Interlocked.CompareExchange(ref hardwareBusy, 0, 0) == 0) { sampler.Dispose(); networkSampler.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
