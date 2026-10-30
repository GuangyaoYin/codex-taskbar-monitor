using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace CodexTaskbarMonitor
{
    // Draw each script explicitly: Chinese SimSun; Latin and numbers Times New Roman.
    public static class MixedText
    {
        public static bool IsEastAsian(char c)
        {
            return c >= 0x2E80 && c <= 0xFFEF;
        }

        public static SizeF Draw(Graphics graphics, string text, float sizePx, Color color,
            float x, float y, bool bold)
        {
            if (String.IsNullOrEmpty(text)) return SizeF.Empty;
            using (Font western = new Font("Times New Roman", sizePx, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font chinese = new Font("SimSun", sizePx, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush brush = new SolidBrush(color))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                float start = x;
                float height = 0;
                int index = 0;
                while (index < text.Length)
                {
                    bool east = IsEastAsian(text[index]);
                    int end = index + 1;
                    while (end < text.Length && IsEastAsian(text[end]) == east) end++;
                    string run = text.Substring(index, end - index);
                    Font font = east ? chinese : western;
                    SizeF measured = graphics.MeasureString(run, font, Int32.MaxValue, format);
                    graphics.DrawString(run, font, brush, x, y, format);
                    x += measured.Width;
                    height = Math.Max(height, measured.Height);
                    index = end;
                }
                return new SizeF(x - start, height);
            }
        }

        public static SizeF Measure(Graphics graphics, string text, float sizePx)
        {
            return Draw(graphics, text, sizePx, Color.Transparent, 0, 0, false);
        }
    }

    public sealed class MixedMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            float size = e.TextFont.SizeInPoints * e.Graphics.DpiY / 72.0f;
            SizeF extent = MixedText.Measure(e.Graphics, e.Text, size);
            float y = e.TextRectangle.Top + (e.TextRectangle.Height - extent.Height) / 2;
            MixedText.Draw(e.Graphics, e.Text, size, e.TextColor, e.TextRectangle.Left, y, false);
        }
    }

    public static class DisplayText
    {
        public static string Percent(double? value)
        {
            if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value)) return "--";
            return Math.Max(0, Math.Min(100, value.Value)).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        public static string Bytes(long value)
        {
            return (value / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
        }

        public static string Speed(double? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue || Double.IsNaN(bytesPerSecond.Value) ||
                Double.IsInfinity(bytesPerSecond.Value) || bytesPerSecond.Value < 0) return "--";
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
            double value = bytesPerSecond.Value;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 || value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        public static string WindowLabel(int minutes)
        {
            if (minutes == 10080) return "周";
            if (minutes == 300) return "5h";
            if (minutes == 1440) return "日";
            if (minutes > 0 && minutes % 1440 == 0) return (minutes / 1440).ToString() + "日";
            if (minutes > 0 && minutes % 60 == 0) return (minutes / 60).ToString() + "h";
            return minutes > 0 ? minutes.ToString() + "min" : "额度";
        }

        public static QuotaBucket MainBucket(CodexSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Buckets.Count == 0) return null;
            foreach (QuotaBucket bucket in snapshot.Buckets)
                if (String.Equals(bucket.Id, "codex", StringComparison.OrdinalIgnoreCase)) return bucket;
            return snapshot.Buckets[0];
        }

        public static string CompactQuota(CodexSnapshot snapshot)
        {
            if (snapshot == null) return "Codex 查询中…";
            QuotaBucket bucket = MainBucket(snapshot);
            if (bucket != null && bucket.Windows.Count > 0)
            {
                StringBuilder text = new StringBuilder("Codex");
                foreach (QuotaWindow window in bucket.Windows)
                    text.Append("  ").Append(WindowLabel(window.WindowDurationMins)).Append(" ").Append(Percent(window.UsedPercent));
                if (snapshot.IsStale || snapshot.Status != CodexStatus.Ready) text.Append("  旧");
                return text.ToString();
            }
            if (snapshot.Status == CodexStatus.Missing) return "Codex 未安装";
            if (snapshot.Status == CodexStatus.LoginRequired) return "Codex 请登录";
            if (snapshot.Status == CodexStatus.Ready) return "Codex 暂无额度数据";
            return "Codex 查询失败";
        }

        public static List<string> Details(HardwareSnapshot hardware, GpuSnapshot gpu, CodexSnapshot codex, string placement, NetworkSnapshot network = null)
        {
            List<string> lines = new List<string>();
            lines.Add("任务栏状态条");
            if (hardware != null)
            {
                lines.Add("CPU 使用率：" + Percent(hardware.CpuPercent));
                if (hardware.MemoryTotalBytes > 0)
                    lines.Add("内存：" + Bytes(hardware.MemoryTotalBytes - hardware.MemoryAvailableBytes) + " / " + Bytes(hardware.MemoryTotalBytes) + "  " + Percent(hardware.MemoryPercent));
                else lines.Add("内存：暂不可用");
                if (!String.IsNullOrEmpty(hardware.Error)) lines.Add("采集状态：" + hardware.Error);
            }
            if (gpu != null)
            {
                lines.Add("GPU：" + gpu.Name);
                lines.Add("GPU 使用率：" + Percent(gpu.UtilizationPercent));
                if (gpu.DedicatedUsedBytes.HasValue)
                    lines.Add("专用显存：" + Bytes(gpu.DedicatedUsedBytes.Value) + (gpu.DedicatedTotalBytes.HasValue ? " / " + Bytes(gpu.DedicatedTotalBytes.Value) : ""));
                if (!gpu.UtilizationPercent.HasValue) lines.Add("GPU 状态：驱动暂未提供使用率");
            }
            else lines.Add("GPU：暂不可用");
            lines.Add("");
            if (network != null)
            {
                lines.Add("下载：" + Speed(network.DownloadBytesPerSecond));
                lines.Add("上传：" + Speed(network.UploadBytesPerSecond));
                if (!String.IsNullOrEmpty(network.InterfaceName)) lines.Add("网卡：" + network.InterfaceName);
                if (!String.IsNullOrEmpty(network.StatusMessage)) lines.Add("网速状态：" + network.StatusMessage);
                lines.Add("");
            }
            if (codex == null) lines.Add("Codex：正在查询");
            else
            {
                lines.Add("Codex：" + (codex.IsStale ? "数据已过时；" : "") + codex.StatusMessage);
                foreach (QuotaBucket bucket in codex.Buckets)
                {
                    string name = !String.IsNullOrEmpty(bucket.Name) ? bucket.Name : bucket.Id;
                    foreach (QuotaWindow window in bucket.Windows)
                    {
                        lines.Add(name + "  " + WindowLabel(window.WindowDurationMins) + "已用 " + Percent(window.UsedPercent));
                        lines.Add("重置：" + (window.ResetsAtUtc.HasValue ? window.ResetsAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "未提供"));
                    }
                }
                lines.Add("额度更新：" + (codex.LastSuccessUtc.HasValue ? codex.LastSuccessUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未成功"));
            }
            if (!String.IsNullOrEmpty(placement)) { lines.Add(""); lines.Add("位置：" + placement); }
            lines.Add("");
            lines.Add("拖动调整位置；右键打开菜单");
            return lines;
        }
    }

    public sealed class DetailsWindow : Form
    {
        private sealed class DetailLine
        {
            public string Label;
            public string Value;
            public bool Secondary;
            public bool Divider;
            public bool ValueColumn;
            public float Height;
        }
        private readonly List<DetailLine> lines = new List<DetailLine>();
        private string title = "任务栏状态条";
        private float labelWidth = 80;
        private float scale = 1;
        private bool dark;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000080; return p; }
        }
        public DetailsWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
        }
        public void UpdateContent(List<string> content, float dpiScale, bool darkTheme)
        {
            scale = Math.Max(1, dpiScale); dark = darkTheme;
            title = content != null && content.Count > 0 ? content[0] ?? "任务栏状态条" : "任务栏状态条";
            lines.Clear();
            labelWidth = 80 * scale;
            float max = 316 * scale;
            using (Graphics g = CreateGraphics())
            {
                // A stable value column makes labels recede and numeric values
                // scan vertically, while long values retain the existing wrapping.
                if (content != null)
                    for (int i = 1; i < content.Count; i++)
                    {
                        string text = content[i] ?? String.Empty;
                        int colon = text.IndexOf('：');
                        if (colon <= 0 || colon > 24) continue;
                        float fontSize = (IsSecondary(text) ? 11.5f : 13) * scale;
                        labelWidth = Math.Max(labelWidth, Math.Min(104 * scale,
                            MixedText.Measure(g, text.Substring(0, colon + 1), fontSize).Width + 8 * scale));
                    }
                max = Math.Max(max, MixedText.Measure(g, title, 14 * scale).Width + 52 * scale);
                if (content != null)
                    for (int i = 1; i < content.Count; i++)
                        AddContent(g, content[i] ?? String.Empty);
                float height = 40 * scale;
                foreach (DetailLine line in lines)
                {
                    height += line.Height;
                    if (line.Divider) continue;
                    float fontSize = (line.Secondary ? 11.5f : 13) * scale;
                    float rowWidth = MixedText.Measure(g, line.Value, fontSize).Width;
                    if (line.ValueColumn) rowWidth += labelWidth;
                    max = Math.Max(max, rowWidth + 28 * scale);
                }
                Size = new Size((int)Math.Ceiling(Math.Min(max, 640 * scale)), (int)Math.Ceiling(height + 11 * scale));
            }
            MonitorPalette palette = MonitorPalette.For(dark);
            BackColor = palette.HoverSurface;
            using (GraphicsPath path = VisualStyle.Round(new RectangleF(0, 0, Width, Height), 6 * scale))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
            Invalidate();
        }

        private static bool IsSecondary(string text)
        {
            return text.StartsWith("采集来源", StringComparison.Ordinal) ||
                text.StartsWith("额度更新", StringComparison.Ordinal) ||
                text.StartsWith("位置：", StringComparison.Ordinal) ||
                text.StartsWith("自动重试：", StringComparison.Ordinal) ||
                text.StartsWith("网速状态：", StringComparison.Ordinal) ||
                text.StartsWith("拖动调整", StringComparison.Ordinal);
        }

        private void AddContent(Graphics graphics, string original)
        {
            if (String.IsNullOrWhiteSpace(original))
            {
                if (lines.Count > 0 && !lines[lines.Count - 1].Divider)
                    lines.Add(new DetailLine { Divider = true, Height = 11 * scale });
                return;
            }
            bool secondary = IsSecondary(original);
            int colon = original.IndexOf('：');
            bool column = colon > 0 && colon <= 24;
            string label = column ? original.Substring(0, colon + 1) : String.Empty;
            string remaining = column ? original.Substring(colon + 1) : original;
            float fontSize = (secondary ? 11.5f : 13) * scale;
            float maximum = 612 * scale - (column ? labelWidth : 0);
            bool first = true;
            do
            {
                int length = remaining.Length;
                if (MixedText.Measure(graphics, remaining, fontSize).Width > maximum)
                {
                    int low = 1, high = length;
                    while (low < high)
                    {
                        int middle = (low + high + 1) / 2;
                        if (MixedText.Measure(graphics, remaining.Substring(0, middle), fontSize).Width <= maximum) low = middle;
                        else high = middle - 1;
                    }
                    length = low;
                    if (length > 1 && length < remaining.Length && Char.IsHighSurrogate(remaining[length - 1])) length--;
                }
                lines.Add(new DetailLine { Label = first ? label : String.Empty, Value = remaining.Substring(0, length),
                    Secondary = secondary, ValueColumn = column, Height = (secondary ? 17 : 19) * scale });
                remaining = remaining.Substring(length);
                first = false;
            } while (remaining.Length > 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            MonitorPalette palette = MonitorPalette.For(dark);
            Graphics graphics = e.Graphics;
            graphics.Clear(palette.HoverSurface);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            VisualStyle.Icon(graphics, 3, 14 * scale, 13 * scale, 11 * scale, palette.Accent, scale);
            MixedText.Draw(graphics, title, 14 * scale, palette.Foreground, 34 * scale, 9 * scale, true);
            using (Pen divider = new Pen(palette.Divider, Math.Max(1, scale * 0.65f)))
            {
                graphics.DrawLine(divider, 14 * scale, 32 * scale, Width - 14 * scale, 32 * scale);
                float y = 40 * scale;
                foreach (DetailLine line in lines)
                {
                    if (line.Divider)
                        graphics.DrawLine(divider, 14 * scale, y + 3 * scale, Width - 14 * scale, y + 3 * scale);
                    else
                    {
                        float fontSize = (line.Secondary ? 11.5f : 13) * scale;
                        if (!String.IsNullOrEmpty(line.Label))
                            MixedText.Draw(graphics, line.Label, fontSize, palette.Muted, 14 * scale, y, false);
                        MixedText.Draw(graphics, line.Value, fontSize, line.Secondary ? palette.Muted : palette.Foreground,
                            14 * scale + (line.ValueColumn ? labelWidth : 0), y, false);
                    }
                    y += line.Height;
                }
            }
        }
    }
}
