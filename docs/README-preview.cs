using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using CodexTaskbarMonitor;

// Generates only synthetic README examples using the real released painter.
// Compile to validation/README-preview.exe with System.Drawing and the product
// EXE referenced. Run with the project root as the optional first argument.
// No Forms, hardware sampler, credentials, RPC, or network request is used.
public static class ReadmePreview
{
    public static int Main(string[] args)
    {
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) :
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), ".."));
        string product = Path.Combine(root, "release", "CodexTaskbarMonitor-1.2.1-win-x64", "CodexTaskbarMonitor.exe");
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            return e.Name.StartsWith("CodexTaskbarMonitor,", StringComparison.Ordinal) ? Assembly.LoadFrom(product) : null;
        };
        Assembly.LoadFrom(product);
        Render(root);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Render(string root)
    {
        HardwareSnapshot hardware = new HardwareSnapshot { CpuPercent = 7, MemoryPercent = 28 };
        GpuSnapshot gpu = new GpuSnapshot { Id = "synthetic-gpu", Name = "Example GPU", UtilizationPercent = 12 };
        CodexSnapshot quota = new CodexSnapshot { Status = CodexStatus.Ready, StatusMessage = "Synthetic example" };
        QuotaBucket bucket = new QuotaBucket { Id = "codex", Name = "Codex" };
        // Preserve the server's used-percent fixture; the painter shows 52% remaining.
        bucket.Windows.Add(new QuotaWindow { UsedPercent = 48, WindowDurationMins = 10080 });
        quota.Buckets.Add(bucket);
        NetworkSnapshot network = new NetworkSnapshot { DownloadBytesPerSecond = 1258291, UploadBytesPerSecond = 35840, InterfaceName = "Example adapter" };
        string output = Path.Combine(root, "docs", "images");
        Directory.CreateDirectory(output);
        foreach (bool dark in new bool[] { false, true })
        {
            using (Bitmap bitmap = new Bitmap(CompactPainter.PreferredWidthDip * 2, CompactPainter.PreferredHeightDip * 2))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                CompactPainter.Draw(graphics, new Rectangle(Point.Empty, bitmap.Size), 2, dark, false, hardware, gpu, quota, network);
                string path = Path.Combine(output, dark ? "monitor-dark.png" : "monitor-light.png");
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine(path);
            }
        }
    }
}
