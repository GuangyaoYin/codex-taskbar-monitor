using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using CodexTaskbarMonitor;

public static class PlacementProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string name);

    [MTAThread]
    public static int Main(string[] args)
    {
        TaskbarEnvironment.EnableDpiAwareness();
        int width = args.Length > 0 ? Int32.Parse(args[0]) : 420;
        int height = args.Length > 1 ? Int32.Parse(args[1]) : 62;
        Stopwatch timer = Stopwatch.StartNew();
        TaskbarPlacement p = TaskbarEnvironment.Inspect(width, height);
        Console.WriteLine("Taskbar=" + p.TaskbarBounds + "; Safe=" + p.SafeArea + "; DPI=" + p.Dpi);
        Console.WriteLine("ShouldHide=" + p.ShouldHide + "; Reason=" + p.Reason + "; elapsedMs=" + timer.ElapsedMilliseconds);
        if (args.Length > 2 && args[2] == "--repeat")
        {
            for (int index = 0; index < 5; index++)
            {
                Thread.Sleep(2000);
                timer.Restart();
                p = TaskbarEnvironment.Inspect(width, height);
                Console.WriteLine("Repeat=" + (index + 1) + "; Safe=" + p.SafeArea + "; ShouldHide=" + p.ShouldHide + "; elapsedMs=" + timer.ElapsedMilliseconds);
            }
        }
        if (args.Length > 2 && args[2] == "--controls")
        {
            AutomationElement root = AutomationElement.FromHandle(FindWindow("Shell_TrayWnd", null));
            CacheRequest cache = new CacheRequest();
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.AutomationElementMode = AutomationElementMode.None;
            using (cache.Activate())
            {
                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    AutomationElement.AutomationElementInformation c = element.Cached;
                    if (!c.IsOffscreen && !c.BoundingRectangle.IsEmpty)
                        Console.WriteLine(c.ControlType.ProgrammaticName + "; class=" + c.ClassName + "; id=" + c.AutomationId + "; rect=" + c.BoundingRectangle);
                }
            }
        }
        return p.ShouldHide ? 2 : 0;
    }
}
