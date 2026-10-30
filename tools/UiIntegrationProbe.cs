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
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [STAThread]
    public static int Main(string[] args)
    {
        string product = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            "..", "release", "CodexTaskbarMonitor-1.1.0-win-x64", "CodexTaskbarMonitor.exe"));
        if (args.Length > 0) product = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            return e.Name.StartsWith("CodexTaskbarMonitor,", StringComparison.Ordinal) ? Assembly.LoadFrom(product) : null;
        };
        Assembly.LoadFrom(product);
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
            form.SetPreviewData(hardware, quota);

            // Emulate Win10's standard 40-physical-pixel taskbar at 100% DPI.
            // This is geometry integration, not a claim of running Windows 10.
            Rectangle win10Safe = new Rectangle(screen.Left + 600, screen.Bottom - 39, 500, 38);
            Apply(form, new TaskbarPlacement
            {
                TaskbarBounds = new Rectangle(screen.Left, screen.Bottom - 40, screen.Width, 40),
                SafeArea = win10Safe, ShouldHide = false, Dpi = 96, Reason = "Synthetic Windows 10 40px taskbar"
            });
            Check("Win10 40px geometry fits 240x38", form.Visible && form.Size == new Size(240, 38) && win10Safe.Contains(form.Bounds));
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

            Apply(form, new TaskbarPlacement { SafeArea = new Rectangle(win10Safe.Left, win10Safe.Top, 239, 38), Dpi = 96, Reason = "No space" });
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
            int oldHeight = details.Height;
            SetField(form, "quota", MakeQuota(4));
            Invoke(form, "UpdateView");
            Check("Growing details stay inside primary screen", details.Height > oldHeight && screen.Contains(details.Bounds));
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
                    form.Width == (int)(240 * Math.Max(1, real.Dpi / 96.0f)) &&
                    form.Height == (int)(38 * Math.Max(1, real.Dpi / 96.0f)));
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
                Check("Worst-case rows fit 240x38 geometry", topSize.Width + 16 <= 240 && bottomSize.Width + 16 <= 240 &&
                    3 + topSize.Height <= 21 && 21 + bottomSize.Height <= 38);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                Check("Both rows contain rendered text pixels", Ink(bitmap, new Rectangle(5, 2, 230, 17)) > 15 && Ink(bitmap, new Rectangle(5, 20, 230, 16)) > 15);
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
        Console.WriteLine("Failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void RunPainterMatrix()
    {
        string output = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "..", "validation", "v1.1"));
        Directory.CreateDirectory(output);
        string[] states = { "single", "dual", "unknown", "stale-dual", "missing", "loading" };
        int cellWidth = 380, cellHeight = 85;
        using (Bitmap sheet = new Bitmap(cellWidth * 3, cellHeight * 8))
        using (Graphics grid = Graphics.FromImage(sheet))
        using (Font captionFont = new Font("Times New Roman", 12, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush captionBrush = new SolidBrush(Color.FromArgb(35, 40, 48)))
        {
            grid.Clear(Color.FromArgb(222, 226, 231));
            int frame = 0;
            bool allEdgesClear = true;
            bool allRowsPainted = true;
            bool allMetricGaps = true;
            float smallestGap = Single.MaxValue;
            foreach (int dpi in new int[] { 96, 144 })
                foreach (bool dark in new bool[] { false, true })
                    foreach (string state in states)
                    {
                        float s = dpi / 96.0f;
                        HardwareSnapshot hardware = MakeHardware();
                        CodexSnapshot quota = MakeQuota(1);
                        if (state == "single") quota.Buckets[0].Windows.RemoveAt(0);
                        if (state == "unknown")
                        {
                            hardware.CpuPercent = null; hardware.MemoryPercent = null; hardware.Gpus[0].UtilizationPercent = null;
                            quota.Buckets.Clear();
                        }
                        if (state == "stale-dual") { quota.IsStale = true; quota.Status = CodexStatus.Error; }
                        if (state == "missing") { quota.Buckets.Clear(); quota.Status = CodexStatus.Missing; }
                        if (state == "loading") { quota = null; hardware = null; }
                        using (Bitmap bitmap = new Bitmap((int)(240 * s), (int)(38 * s)))
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                        {
                            GpuSnapshot gpu = hardware != null && hardware.Gpus.Count > 0 ? hardware.Gpus[0] : null;
                            CompactPainter.Draw(graphics, new Rectangle(Point.Empty, bitmap.Size), s, dark, false, hardware, gpu, quota);
                            Color background = MonitorPalette.For(dark).Surface;
                            bool edges = ClearEdges(bitmap, background, Math.Max(1, (int)(2 * s)));
                            bool rows = Ink(bitmap, new Rectangle((int)(4 * s), (int)(2 * s), (int)(232 * s), (int)(17 * s))) > 30 &&
                                Ink(bitmap, new Rectangle((int)(4 * s), (int)(20 * s), (int)(232 * s), (int)(16 * s))) > 20;
                            allEdgesClear &= edges;
                            allRowsPainted &= rows;
                            // Label starts at x+12; the right-aligned value ends at
                            // x+70. Measure its actual bold face, as the painter does.
                            float gap = 58 * s - MixedText.Measure(graphics, "RAM", 10 * s).Width - BoldWidth(graphics, "100%", 12.5f * s);
                            smallestGap = Math.Min(smallestGap, gap / s);
                            allMetricGaps &= gap >= 3.5f * s;
                            int column = frame % 3, row = frame / 3;
                            grid.DrawString(dpi + " DPI / " + (dark ? "dark" : "light") + " / " + state, captionFont, captionBrush,
                                column * cellWidth + 10, row * cellHeight + 4);
                            grid.DrawImageUnscaled(bitmap, column * cellWidth + 10, row * cellHeight + 23);
                            frame++;
                        }
                    }
            Check("CompactPainter 24-case matrix leaves all outer edges clear", allEdgesClear);
            Check("CompactPainter 24-case matrix paints both rows", allRowsPainted);
            Check("Worst-case metric labels and bold values keep readable separation", allMetricGaps);
            Console.WriteLine("SmallestMetricGapDIP=" + smallestGap.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            string path = Path.Combine(output, "qa-compact-matrix.png");
            sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("PainterMatrix=" + path);
        }
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
            bucket.Windows.Add(new QuotaWindow { UsedPercent = 100, WindowDurationMins = 300, ResetsAtUtc = DateTime.UtcNow.AddHours(1) });
            bucket.Windows.Add(new QuotaWindow { UsedPercent = 100, WindowDurationMins = 10080, ResetsAtUtc = DateTime.UtcNow.AddDays(1) });
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
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    }
    private static void SetField(object instance, string name, object value)
    {
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
    }
}
