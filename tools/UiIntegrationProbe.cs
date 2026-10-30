using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using CodexTaskbarMonitor;

// Integration checks against the packaged executable. All probe windows have
// zero opacity, do not appear on the taskbar, and use the product's NOACTIVATE
// behavior. No system settings, startup registry keys, or saved preferences are changed.
public static class UiIntegrationProbe
{
    private static int failures;
    private static bool probeObservedWithFocus;
    private static string reportDirectory;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [STAThread]
    public static int Main(string[] args)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), ".."));
        string[] builds = Directory.GetDirectories(Path.Combine(projectRoot, "release"), "CodexTaskbarMonitor-*-win-x64");
        Array.Sort(builds, delegate(string left, string right) { return Directory.GetLastWriteTimeUtc(right).CompareTo(Directory.GetLastWriteTimeUtc(left)); });
        if (builds.Length == 0 && args.Length == 0) { Console.WriteLine("Build the product first or provide its EXE path."); return 98; }
        string product = builds.Length > 0 ? Path.Combine(builds[0], "CodexTaskbarMonitor.exe") : String.Empty;
        if (args.Length > 0) product = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            return e.Name.StartsWith("CodexTaskbarMonitor,", StringComparison.Ordinal) ? Assembly.LoadFrom(product) : null;
        };
        Assembly productAssembly = Assembly.LoadFrom(product);
        Version version = productAssembly.GetName().Version;
        reportDirectory = Path.Combine(projectRoot, "validation", "v" + version.Major + "." + version.Minor + (version.Build > 0 ? "." + version.Build : String.Empty));
        Console.WriteLine("Product=" + product);
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(product))
            Console.WriteLine("SHA256=" + BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant());
        Console.WriteLine("UTC=" + DateTime.UtcNow.ToString("o"));
        try { return Run(); }
        catch (Exception e)
        {
            Console.WriteLine("UNEXPECTED=" + e.GetType().Name + ": " + e.Message);
            return 99;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        TaskbarEnvironment.EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        IntPtr initialForeground = GetForegroundWindow();
        Rectangle screen = Screen.PrimaryScreen.Bounds;
        Console.WriteLine("PrimaryScreen=" + screen);

        using (MonitorForm form = new MonitorForm(false))
        {
            form.Opacity = 0;
            DetailsWindow details = (DetailsWindow)Field(form, "details");
            details.Opacity = 0;
            AppSettings settings = (AppSettings)Field(form, "settings");
            settings.ManualX = null;
            settings.ManualY = null;
            HardwareSnapshot hardware = MakeHardware();
            CodexSnapshot quota = MakeQuota(1);
            NetworkSnapshot network = new NetworkSnapshot { DownloadBytesPerSecond = 12.5 * 1048576, UploadBytesPerSecond = 1.2 * 1048576,
                InterfaceName = "Synthetic Ethernet", StatusMessage = "Synthetic UI integration sample" };
            form.SetPreviewData(hardware, quota, network);

            // Emulate Win10's standard 40-physical-pixel taskbar at 100% DPI.
            // This is geometry integration, not a claim of running Windows 10.
            Rectangle win10Safe = new Rectangle(screen.Left + 600, screen.Bottom - 39, 500, 38);
            Apply(form, new TaskbarPlacement
            {
                TaskbarBounds = new Rectangle(screen.Left, screen.Bottom - 40, screen.Width, 40),
                SafeArea = win10Safe, ShouldHide = false, Dpi = 96, Reason = "Synthetic Windows 10 40px taskbar"
            });
            Check("Win10 40px geometry fits preferred width and 38px height", form.Visible &&
                form.Size == new Size(CompactPainter.PreferredWidthDip, CompactPainter.PreferredHeightDip) && win10Safe.Contains(form.Bounds));
            Console.WriteLine("Win10SyntheticBounds=" + form.Bounds);

            Point clampedTopLeft = (Point)Invoke(form, "Clamp", new Point(screen.Left - 1000, screen.Top - 1000), win10Safe);
            Point clampedBottomRight = (Point)Invoke(form, "Clamp", new Point(screen.Right + 1000, screen.Bottom + 1000), win10Safe);
            Check("Drag clamp keeps both extreme rectangles inside safe gap",
                win10Safe.Contains(new Rectangle(clampedTopLeft, form.Size)) &&
                win10Safe.Contains(new Rectangle(clampedBottomRight, form.Size)));

            settings.ManualX = screen.Right + 500;
            settings.ManualY = screen.Top - 500;
            Apply(form, new TaskbarPlacement { SafeArea = win10Safe, Dpi = 96, Reason = "Synthetic manual-position clamp" });
            Check("Saved out-of-range position is clamped", win10Safe.Contains(form.Bounds));
            settings.ManualX = null;
            settings.ManualY = null;

            Apply(form, new TaskbarPlacement { SafeArea = new Rectangle(win10Safe.Left, win10Safe.Top, CompactPainter.PreferredWidthDip - 1, 38), Dpi = 96, Reason = "No space" });
            Check("Insufficient width collapses main window", !form.Visible);
            Invoke(form, "ShowDetails", new Point(screen.Right - 50, screen.Bottom - 20), true);
            Check("Tray can open details while main is collapsed", details.Visible && screen.Contains(details.Bounds));
            for (int index = 0; index < 2; index++)
            {
                Thread.Sleep(2050);
                Application.DoEvents();
                Apply(form, new TaskbarPlacement { ShouldHide = true, Dpi = 96, Reason = "No space" });
            }
            Check("Repeated placement polls preserve tray details", details.Visible);

            Apply(form, new TaskbarPlacement { SafeArea = win10Safe, Dpi = 96, Reason = "Safe" });
            Invoke(form, "ShowDetails", form.Location, false);
            List<string> detailContent = (List<string>)Invoke(form, "BuildDetails");
            Check("Network details expose both directions and selected interface", detailContent.Exists(delegate(string line) { return line.StartsWith("下载：", StringComparison.Ordinal); }) &&
                detailContent.Exists(delegate(string line) { return line.StartsWith("上传：", StringComparison.Ordinal); }) &&
                detailContent.Exists(delegate(string line) { return line.Contains("Synthetic Ethernet"); }));
            int oldHeight = details.Height;
            SetField(form, "quota", MakeQuota(4));
            Invoke(form, "UpdateView");
            bool growingDetailsFit = details.Height > oldHeight && screen.Contains(details.Bounds);
            bool retryDetailsFit = VerifyRetryDetails(form);
            Check("Growing and retry-state details remain readable inside their layout", growingDetailsFit && retryDetailsFit);
            Console.WriteLine("DetailsGrowth=" + oldHeight + "->" + details.Height + "; bounds=" + details.Bounds);

            Apply(form, new TaskbarPlacement { ShouldHide = true, Dpi = 96, Reason = "Taskbar hidden" });
            Check("Taskbar-associated details follow main hiding", !form.Visible && !details.Visible);

            // Test physical-pixel scaling and live taskbar geometry without relying
            // on the legacy oversized Windows 11 compatibility toolbar rectangle.
            TaskbarPlacement real = TaskbarEnvironment.Inspect(1, 1);
            Console.WriteLine("ActualTaskbar=" + real.TaskbarBounds + "; safe=" + real.SafeArea + "; DPI=" + real.Dpi + "; hidden=" + real.ShouldHide);
            if (!real.ShouldHide)
            {
                Apply(form, real);
                Check("Current physical-DPI placement fits safe gap", form.Visible && real.SafeArea.Contains(form.Bounds) &&
                    form.Width == (int)(CompactPainter.PreferredWidthDip * Math.Max(1, real.Dpi / 96.0f)) &&
                    form.Height == (int)(CompactPainter.PreferredHeightDip * Math.Max(1, real.Dpi / 96.0f)));
            }
            else Console.WriteLine("SKIP actual placement: " + real.Reason);

            // Paint a worst-case compact line and verify it has enough physical
            // room and actual pixels in both rows, using the requested font families.
            SetField(form, "hardware", MakeHardware());
            SetField(form, "quota", MakeQuota(1));
            Apply(form, new TaskbarPlacement { SafeArea = win10Safe, Dpi = 96, Reason = "Font rendering" });
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Font chinese = new Font("SimSun", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font western = new Font("Times New Roman", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                string top = "CPU 100%   RAM 100%   GPU 100%";
                string bottom = DisplayText.CompactQuota(MakeQuota(1));
                SizeF topSize = MixedText.Measure(graphics, top, 12.5f);
                SizeF bottomSize = MixedText.Measure(graphics, bottom, 12.5f);
                Check("Configured font families exist", chinese.FontFamily.GetName(0x0409) == "SimSun" && western.FontFamily.GetName(0x0409) == "Times New Roman");
                Check("Original hardware and quota rows still fit their 240x38 panel", topSize.Width + 16 <= 240 && bottomSize.Width + 16 <= 240 &&
                    3 + topSize.Height <= 21 && 21 + bottomSize.Height <= 38);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                Check("Both rows contain rendered text pixels", Ink(bitmap, new Rectangle(5, 2, 230, 17)) > 15 && Ink(bitmap, new Rectangle(5, 20, 230, 16)) > 15);
                Check("Main form passes sampled network values to both painted rows", Ink(bitmap, new Rectangle(266, 1, 70, 16)) > 5 &&
                    Ink(bitmap, new Rectangle(266, 21, 70, 15)) > 5);
                Console.WriteLine("TextMetrics top=" + topSize + "; bottom=" + bottomSize + "; fonts=" + chinese.FontFamily.Name + "," + western.FontFamily.Name);
            }

            // Exercise the real asynchronous manual-refresh/error path with an
            // explicitly nonexistent custom executable, so no account/network or
            // Codex child process is used by this test.
            settings.CodexPath = Path.Combine(Path.GetTempPath(), "codex-ui-probe-missing-" + Guid.NewGuid().ToString("N") + ".exe");
            Invoke(form, "RefreshQuota");
            Stopwatch wait = Stopwatch.StartNew();
            CodexSnapshot refreshed = null;
            while (wait.ElapsedMilliseconds < 4000)
            {
                Application.DoEvents();
                refreshed = (CodexSnapshot)Field(form, "quota");
                if (refreshed != null && refreshed.Status == CodexStatus.Missing) break;
                Thread.Sleep(10);
            }
            Check("Manual refresh returns missing-path state without blocking UI", refreshed != null && refreshed.Status == CodexStatus.Missing);

            uint foregroundProcess;
            GetWindowThreadProcessId(GetForegroundWindow(), out foregroundProcess);
            Check("Probe windows did not acquire focus at sampled checkpoints", !probeObservedWithFocus && foregroundProcess != (uint)Process.GetCurrentProcess().Id);
            Console.WriteLine("ForegroundUnchanged=" + (initialForeground == GetForegroundWindow()));
            details.Hide();
            form.Hide();
        }
        RunPainterMatrix();
        VerifyRemainingQuota();
        Console.WriteLine("Failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static bool VerifyRetryDetails(MonitorForm form)
    {
        object originalQuota = Field(form, "quota");
        object originalNext = Field(form, "nextQuota");
        object originalBusy = Field(form, "quotaBusy");
        bool fits = true;
        int cases = 0;
        Directory.CreateDirectory(reportDirectory);
        try
        {
            CodexSnapshot failed = MakeQuota(1);
            failed.Status = CodexStatus.Error;
            failed.IsStale = true;
            failed.StatusMessage = "额度查询超时；已保留最后一次成功数据。";
            SetField(form, "quota", failed);
            SetField(form, "nextQuota", DateTime.UtcNow.AddSeconds(30));
            foreach (bool querying in new bool[] { false, true })
            {
                SetField(form, "quotaBusy", querying ? 1 : 0);
                List<string> content = (List<string>)Invoke(form, "BuildDetails");
                fits &= content.Exists(delegate(string line) { return querying ? line == "额度查询：正在重新查询" : line.StartsWith("自动重试：", StringComparison.Ordinal); });
                foreach (int dpi in new int[] { 96, 144 })
                    foreach (bool dark in new bool[] { false, true })
                    {
                        float s = dpi / 96.0f;
                        using (DetailsWindow preview = new DetailsWindow())
                        {
                            preview.Opacity = 0;
                            preview.UpdateContent(content, s, dark);
                            using (Bitmap bitmap = new Bitmap(preview.Width, preview.Height))
                            using (Graphics graphics = Graphics.FromImage(bitmap))
                            {
                                float valueColumn = (float)Field(preview, "labelWidth");
                                foreach (object row in (System.Collections.IEnumerable)Field(preview, "lines"))
                                {
                                    if ((bool)Field(row, "Divider")) continue;
                                    bool secondary = (bool)Field(row, "Secondary");
                                    bool column = (bool)Field(row, "ValueColumn");
                                    string label = (string)Field(row, "Label");
                                    string value = (string)Field(row, "Value");
                                    float font = (secondary ? 11.5f : 13) * s;
                                    SizeF valueSize = MixedText.Measure(graphics, value, font);
                                    fits &= 14 * s + (column ? valueColumn : 0) + valueSize.Width <= preview.Width - 13 * s;
                                    fits &= valueSize.Height <= (float)Field(row, "Height");
                                    if (!String.IsNullOrEmpty(label)) fits &= MixedText.Measure(graphics, label, font).Width + 2 * s <= valueColumn;
                                }
                                preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size));
                                fits &= ClearEdges(bitmap, MonitorPalette.For(dark).HoverSurface, Math.Max(1, (int)(2 * s)));
                                if (dpi == 144)
                                    bitmap.Save(Path.Combine(reportDirectory, "qa-details-" + (querying ? "requery" : "retry") + (dark ? "-dark.png" : "-light.png")),
                                        System.Drawing.Imaging.ImageFormat.Png);
                            }
                        }
                        cases++;
                    }
            }
        }
        finally
        {
            SetField(form, "quota", originalQuota);
            SetField(form, "nextQuota", originalNext);
            SetField(form, "quotaBusy", originalBusy);
        }
        Console.WriteLine("RetryDetailsCases=" + cases + "; LabelsAndValuesFit=" + fits);
        return fits;
    }

    private static void RunPainterMatrix()
    {
        string output = reportDirectory;
        Directory.CreateDirectory(output);
        string[] states = { "single", "dual", "unknown", "stale-dual", "missing", "loading", "net-zero", "net-fast", "quota-57-28", "quota-low", "quota-empty", "quota-invalid" };
        int cellWidth = 540, cellHeight = 85;
        using (Bitmap sheet = new Bitmap(cellWidth * 2, cellHeight * 24))
        using (Graphics grid = Graphics.FromImage(sheet))
        using (Font captionFont = new Font("Times New Roman", 12, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush captionBrush = new SolidBrush(Color.FromArgb(35, 40, 48)))
        {
            grid.Clear(Color.FromArgb(222, 226, 231));
            int frame = 0;
            bool allEdgesClear = true;
            bool allRowsPainted = true;
            bool allMetricGaps = true;
            bool allNetworkRowsPainted = true;
            bool allNetworkGaps = true;
            bool legacyPanelUnchanged = true;
            bool remainingMarkerFits = true;
            float smallestGap = Single.MaxValue;
            float smallestNetworkGap = Single.MaxValue;
            foreach (int dpi in new int[] { 96, 144 })
                foreach (bool dark in new bool[] { false, true })
                    foreach (string state in states)
                    {
                        float s = dpi / 96.0f;
                        HardwareSnapshot hardware = MakeHardware();
                        CodexSnapshot quota = MakeQuota(1);
                        NetworkSnapshot network = new NetworkSnapshot { DownloadBytesPerSecond = 12.5 * 1048576, UploadBytesPerSecond = 1.2 * 1048576 };
                        if (state == "single") quota.Buckets[0].Windows.RemoveAt(0);
                        if (state == "unknown")
                        {
                            hardware.CpuPercent = null; hardware.MemoryPercent = null; hardware.Gpus[0].UtilizationPercent = null;
                            quota.Buckets.Clear();
                            network.DownloadBytesPerSecond = null; network.UploadBytesPerSecond = null;
                        }
                        if (state == "stale-dual") { quota.IsStale = true; quota.Status = CodexStatus.Error; quota.Buckets[0].Windows[0].UsedPercent = 43; quota.Buckets[0].Windows[1].UsedPercent = 72; }
                        if (state == "missing") { quota.Buckets.Clear(); quota.Status = CodexStatus.Missing; }
                        if (state == "loading") { quota = null; hardware = null; network = null; }
                        if (state == "net-zero") { network.DownloadBytesPerSecond = 0; network.UploadBytesPerSecond = 0; }
                        if (state == "net-fast") { network.DownloadBytesPerSecond = 1024.0 * 1048576; network.UploadBytesPerSecond = 1023.95 * 1048576; }
                        if (state == "quota-57-28") { quota.Buckets[0].Windows[0].UsedPercent = 43; quota.Buckets[0].Windows[1].UsedPercent = 72; }
                        if (state == "quota-low") { quota.Buckets[0].Windows[0].UsedPercent = 95; quota.Buckets[0].Windows[1].UsedPercent = 80; }
                        if (state == "quota-empty") { quota.Buckets[0].Windows[0].UsedPercent = 100; quota.Buckets[0].Windows[1].UsedPercent = 100; }
                        if (state == "quota-invalid") { quota.Buckets[0].Windows[0].UsedPercent = Double.NaN; quota.Buckets[0].Windows[1].UsedPercent = Double.PositiveInfinity; }
                        using (Bitmap bitmap = new Bitmap((int)(CompactPainter.PreferredWidthDip * s), (int)(CompactPainter.PreferredHeightDip * s)))
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                        {
                            GpuSnapshot gpu = hardware != null && hardware.Gpus.Count > 0 ? hardware.Gpus[0] : null;
                            CompactPainter.Draw(graphics, new Rectangle(Point.Empty, bitmap.Size), s, dark, false, hardware, gpu, quota, network);
                            Color background = MonitorPalette.For(dark).Surface;
                            bool edges = ClearEdges(bitmap, background, Math.Max(1, (int)(2 * s)));
                            bool rows = Ink(bitmap, new Rectangle((int)(4 * s), (int)(2 * s), (int)(232 * s), (int)(17 * s))) > 30 &&
                                Ink(bitmap, new Rectangle((int)(4 * s), (int)(20 * s), (int)(232 * s), (int)(16 * s))) > 20;
                            allEdgesClear &= edges;
                            allRowsPainted &= rows;
                            allNetworkRowsPainted &= Ink(bitmap, new Rectangle((int)(266 * s), (int)(1 * s), (int)(70 * s), (int)(16 * s))) > 5 &&
                                Ink(bitmap, new Rectangle((int)(266 * s), (int)(21 * s), (int)(70 * s), (int)(15 * s))) > 5;
                            string[] speeds = { DisplayText.Speed(network == null ? null : network.DownloadBytesPerSecond),
                                DisplayText.Speed(network == null ? null : network.UploadBytesPerSecond), "1024 MB/s" };
                            foreach (string speed in speeds)
                            {
                                float networkGap = 74 * s - MixedText.Measure(graphics, speed, 11.5f * s).Width;
                                smallestNetworkGap = Math.Min(smallestNetworkGap, networkGap / s);
                                allNetworkGaps &= networkGap >= 4 * s;
                            }
                            using (Bitmap legacy = new Bitmap((int)(240 * s), (int)(38 * s)))
                            using (Graphics legacyGraphics = Graphics.FromImage(legacy))
                            {
                                CompactPainter.Draw(legacyGraphics, new Rectangle(Point.Empty, legacy.Size), s, dark, false, hardware, gpu, quota);
                                legacyPanelUnchanged &= SamePixels(bitmap, legacy);
                            }
                            // Label starts at x+12; the right-aligned value ends at
                            // x+70. Measure its actual bold face, as the painter does.
                            float gap = 58 * s - MixedText.Measure(graphics, "RAM", 10 * s).Width - BoldWidth(graphics, "100%", 12.5f * s);
                            smallestGap = Math.Min(smallestGap, gap / s);
                            allMetricGaps &= gap >= 3.5f * s;
                            remainingMarkerFits &= 54 * s - (21 * s + BoldWidth(graphics, "Codex", 11.5f * s)) >= 1 * s &&
                                54 * s + MixedText.Measure(graphics, "剩余", 9 * s).Width <= 73 * s;
                            int column = frame % 2, row = frame / 2;
                            grid.DrawString(dpi + " DPI / " + (dark ? "dark" : "light") + " / " + state, captionFont, captionBrush,
                                column * cellWidth + 10, row * cellHeight + 4);
                            grid.DrawImageUnscaled(bitmap, column * cellWidth + 10, row * cellHeight + 23);
                            frame++;
                        }
                    }
            Check("CompactPainter 48-case matrix leaves all outer edges clear", allEdgesClear);
            Check("CompactPainter 48-case matrix paints both original rows", allRowsPainted);
            Check("Worst-case metric labels and bold values keep readable separation", allMetricGaps);
            Check("Network download and upload both paint independently of Codex state", allNetworkRowsPainted);
            Check("Network value and arrow retain at least 4DIP separation", allNetworkGaps);
            Check("Original 240DIP panel remains pixel-identical in wide and legacy draws", legacyPanelUnchanged);
            Check("Remaining marker stays separated from Codex and cycle labels at both DPIs", remainingMarkerFits);
            Check("Network unknown and invalid values are not reported as measured zero", DisplayText.Speed(null) == "--" &&
                DisplayText.Speed(Double.NaN) == "--" && DisplayText.Speed(Double.PositiveInfinity) == "--" && DisplayText.Speed(-1) == "--");
            Check("Network formatter scales numeric values into B KB MB and GB per second", DisplayText.Speed(0) == "0 B/s" &&
                DisplayText.Speed(1023) == "1023 B/s" && DisplayText.Speed(1024) == "1.0 KB/s" &&
                DisplayText.Speed(1048576) == "1.0 MB/s" && DisplayText.Speed(1073741824) == "1.0 GB/s");
            Console.WriteLine("SmallestMetricGapDIP=" + smallestGap.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine("SmallestNetworkGapDIP=" + smallestNetworkGap.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            string path = Path.Combine(output, "qa-compact-matrix.png");
            sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("PainterMatrix=" + path);
        }
    }

    private static void VerifyRemainingQuota()
    {
        Check("Quota conversion displays 57 remaining for 43 used and clamps valid bounds", DisplayText.RemainingPercent(43) == 57 &&
            DisplayText.RemainingPercent(0) == 100 && DisplayText.RemainingPercent(100) == 0 &&
            DisplayText.RemainingPercent(-5) == 100 && DisplayText.RemainingPercent(110) == 0);
        Check("Unknown quota values remain unknown rather than becoming 100 remaining", !DisplayText.RemainingPercent(null).HasValue &&
            !DisplayText.RemainingPercent(Double.NaN).HasValue && !DisplayText.RemainingPercent(Double.PositiveInfinity).HasValue);

        CodexSnapshot dual = MakeQuota(1);
        dual.Buckets[0].Windows[0].UsedPercent = 43;
        dual.Buckets[0].Windows[1].UsedPercent = 72;
        string compact = DisplayText.CompactQuota(dual);
        List<string> details = DisplayText.Details(null, null, dual, null);
        Check("Compact text and details identify 57 and 28 as remaining per cycle", compact.Contains("剩余") && compact.Contains("57%") && compact.Contains("28%") &&
            !compact.Contains("43%") && !compact.Contains("72%") && details.Exists(delegate(string line) { return line.Contains("5h剩余 57%"); }) &&
            details.Exists(delegate(string line) { return line.Contains("周剩余 28%"); }) && !details.Exists(delegate(string line) { return line.Contains("已用"); }));

        bool missingHasNoHundred = !DisplayText.CompactQuota(null).Contains("100%");
        foreach (CodexStatus status in new CodexStatus[] { CodexStatus.Missing, CodexStatus.LoginRequired, CodexStatus.Error, CodexStatus.Ready })
            missingHasNoHundred &= !DisplayText.CompactQuota(new CodexSnapshot { Status = status }).Contains("100%");
        CodexSnapshot invalid = MakeQuota(1);
        invalid.Buckets[0].Windows[0].UsedPercent = Double.NaN;
        invalid.Buckets[0].Windows[1].UsedPercent = Double.PositiveInfinity;
        missingHasNoHundred &= DisplayText.CompactQuota(invalid).Contains("--") && !DisplayText.CompactQuota(invalid).Contains("100%");
        Check("Missing empty and invalid quota snapshots never fabricate 100 remaining", missingHasNoHundred);

        bool progressCorrect = true;
        bool warningCorrect = true;
        foreach (int dpi in new int[] { 96, 144 })
            foreach (bool dark in new bool[] { false, true })
            {
                float s = dpi / 96.0f;
                MonitorPalette p = MonitorPalette.For(dark);
                using (Bitmap full = RenderSingleQuota(0, false, s, dark))
                using (Bitmap half = RenderSingleQuota(50, false, s, dark))
                using (Bitmap empty = RenderSingleQuota(100, false, s, dark))
                using (Bitmap unknown = RenderSingleQuota(Double.NaN, false, s, dark))
                using (Bitmap low = RenderSingleQuota(95, false, s, dark))
                using (Bitmap medium = RenderSingleQuota(80, false, s, dark))
                using (Bitmap high = RenderSingleQuota(20, false, s, dark))
                using (Bitmap stale = RenderSingleQuota(100, true, s, dark))
                {
                    Rectangle track = new Rectangle((int)(90 * s), (int)(28 * s), (int)(100 * s), Math.Max(1, (int)(3 * s)));
                    Rectangle value = new Rectangle((int)(211 * s), (int)(22 * s), (int)(22 * s), (int)(13 * s));
                    Rectangle staleValue = new Rectangle((int)(198 * s), (int)(22 * s), (int)(18 * s), (int)(13 * s));
                    int fullPixels = ColorPixels(full, track, p.Accent);
                    int halfPixels = ColorPixels(half, track, p.Accent);
                    progressCorrect &= fullPixels > 0 && halfPixels > fullPixels * 0.25 && halfPixels < fullPixels * 0.75 &&
                        FillPixels(empty, track, p) == 0 && FillPixels(unknown, track, p) == 0;
                    warningCorrect &= ColorPixels(low, track, p.Danger) > 0 && ColorPixels(medium, track, p.Warning) > 0 &&
                        ColorPixels(high, track, p.Accent) > 0 && ColorPixels(empty, value, p.Danger) > 0 &&
                        ColorPixels(low, value, p.Danger) > 0 && ColorPixels(medium, value, p.Warning) > 0 &&
                        ColorPixels(high, value, p.Foreground) > 0 && ColorPixels(stale, staleValue, p.Warning) > 0;
                    Console.WriteLine("RemainingPixels dpi=" + dpi + "; dark=" + dark + "; full=" + fullPixels + "; half=" + halfPixels + "; empty=" + FillPixels(empty, track, p));
                }
            }
        Check("Remaining progress fills high half and zero in the correct direction", progressCorrect);
        Check("Low remaining warns including red zero digits and stale warning priority", warningCorrect);
    }

    private static Bitmap RenderSingleQuota(double used, bool stale, float scale, bool dark)
    {
        CodexSnapshot quota = MakeQuota(1);
        quota.Buckets[0].Windows.RemoveAt(0);
        quota.Buckets[0].Windows[0].UsedPercent = used;
        quota.IsStale = stale;
        if (stale) quota.Status = CodexStatus.Error;
        Bitmap bitmap = new Bitmap((int)(240 * scale), (int)(38 * scale));
        using (Graphics graphics = Graphics.FromImage(bitmap))
            CompactPainter.Draw(graphics, new Rectangle(Point.Empty, bitmap.Size), scale, dark, false, MakeHardware(), null, quota);
        return bitmap;
    }

    private static int FillPixels(Bitmap bitmap, Rectangle area, MonitorPalette palette)
    {
        return ColorPixels(bitmap, area, palette.Accent) + ColorPixels(bitmap, area, palette.Warning) + ColorPixels(bitmap, area, palette.Danger);
    }

    private static int ColorPixels(Bitmap bitmap, Rectangle area, Color expected)
    {
        int pixels = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                if (Math.Abs(color.R - expected.R) + Math.Abs(color.G - expected.G) + Math.Abs(color.B - expected.B) < 18) pixels++;
            }
        return pixels;
    }

    private static bool SamePixels(Bitmap wide, Bitmap legacy)
    {
        for (int y = 0; y < legacy.Height; y++)
            for (int x = 0; x < legacy.Width; x++)
                if (wide.GetPixel(x, y).ToArgb() != legacy.GetPixel(x, y).ToArgb()) return false;
        return true;
    }

    private static float BoldWidth(Graphics graphics, string text, float size)
    {
        using (Font font = new Font("Times New Roman", size, FontStyle.Bold, GraphicsUnit.Pixel))
        using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
        {
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return graphics.MeasureString(text, font, Int32.MaxValue, format).Width;
        }
    }

    private static bool ClearEdges(Bitmap bitmap, Color background, int margin)
    {
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (x < margin || y >= bitmap.Height - margin || x >= bitmap.Width - margin)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R != background.R || pixel.G != background.G || pixel.B != background.B) return false;
                }
        return true;
    }

    private static int Ink(Bitmap bitmap, Rectangle area)
    {
        int count = 0;
        Color background = bitmap.GetPixel(1, 1);
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                Color c = bitmap.GetPixel(x, y);
                if (Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B) > 75) count++;
            }
        return count;
    }

    private static HardwareSnapshot MakeHardware()
    {
        HardwareSnapshot hardware = new HardwareSnapshot { CpuPercent = 100, MemoryPercent = 100,
            MemoryTotalBytes = 64L * 1024 * 1024 * 1024, MemoryAvailableBytes = 0, TimestampUtc = DateTime.UtcNow };
        hardware.Gpus.Add(new GpuSnapshot { Id = "probe-gpu", Name = "NVIDIA GeForce RTX 3090 测试 GPU", UtilizationPercent = 100,
            DedicatedUsedBytes = 6L * 1024 * 1024 * 1024, DedicatedTotalBytes = 24L * 1024 * 1024 * 1024, Source = "Synthetic integration data" });
        return hardware;
    }
    private static CodexSnapshot MakeQuota(int buckets)
    {
        CodexSnapshot result = new CodexSnapshot { Status = CodexStatus.Ready, StatusMessage = "额度已更新。", LastSuccessUtc = DateTime.UtcNow };
        for (int index = 0; index < buckets; index++)
        {
            QuotaBucket bucket = new QuotaBucket { Id = index == 0 ? "codex" : "test-" + index, Name = "Codex 测试额度 " + index };
            bucket.Windows.Add(new QuotaWindow { UsedPercent = 0, WindowDurationMins = 300, ResetsAtUtc = DateTime.UtcNow.AddHours(1) });
            bucket.Windows.Add(new QuotaWindow { UsedPercent = 0, WindowDurationMins = 10080, ResetsAtUtc = DateTime.UtcNow.AddDays(1) });
            result.Buckets.Add(bucket);
        }
        return result;
    }
    private static void Check(string label, bool passed)
    {
        uint foregroundProcess;
        GetWindowThreadProcessId(GetForegroundWindow(), out foregroundProcess);
        if (foregroundProcess == (uint)Process.GetCurrentProcess().Id) probeObservedWithFocus = true;
        Console.WriteLine((passed ? "PASS " : "FAIL ") + label);
        if (!passed) failures++;
    }
    private static void Apply(MonitorForm form, TaskbarPlacement placement) { Invoke(form, "ApplyPlacement", placement); }
    private static object Invoke(object instance, string name, params object[] arguments)
    {
        MethodInfo method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return method.Invoke(instance, arguments);
    }
    private static object Field(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(instance);
    }
    private static void SetField(object instance, string name, object value)
    {
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
    }
}
