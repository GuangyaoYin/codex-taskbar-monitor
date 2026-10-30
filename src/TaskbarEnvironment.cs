using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace CodexTaskbarMonitor
{
    /// <summary>All rectangles and requested dimensions are physical screen pixels.</summary>
    public sealed class TaskbarPlacement
    {
        public Rectangle TaskbarBounds;
        public Rectangle SafeArea;
        public bool ShouldHide;
        public string Reason;
        public int Dpi;
    }

    public static class TaskbarEnvironment
    {
        private static int dpiInitialized;
        private static readonly object cacheLock = new object();
        private static IntPtr cachedTaskbar;
        private static Rectangle cachedBounds;
        private static Rectangle cachedTray;
        private static DateTime cacheTimeUtc = DateTime.MinValue;
        private static List<Rectangle> cachedButtons;

        /// <summary>Call once at process startup, before creating any windows.</summary>
        public static void EnableDpiAwareness()
        {
            if (Interlocked.Exchange(ref dpiInitialized, 1) != 0) return;
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; // Per Monitor v2.
                if (Marshal.GetLastWin32Error() == 5) return; // Manifest already set it.
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            try
            {
                int result = SetProcessDpiAwareness(2); // Windows 8.1/early Windows 10.
                if (result == 0 || result == unchecked((int)0x80070005)) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            SetProcessDPIAware();
        }

        /// <summary>
        /// Inspect on a background MTA thread. Re-resolves Explorer handles on every call.
        /// Does not modify Explorer, register an appbar, or read application/window titles.
        /// </summary>
        public static TaskbarPlacement Inspect(int desiredWidthPx, int desiredHeightPx)
        {
            TaskbarPlacement result = new TaskbarPlacement { ShouldHide = true, Dpi = 96, Reason = "任务栏尚未就绪" };
            if (desiredWidthPx < 1 || desiredHeightPx < 1)
            {
                result.Reason = "小窗尺寸无效";
                return result;
            }

            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            RECT raw;
            if (taskbar == IntPtr.Zero || !IsWindowVisible(taskbar) || !GetWindowRect(taskbar, out raw)) return result;
            Rectangle bounds = raw.ToRectangle();
            result.TaskbarBounds = bounds;
            result.Dpi = GetTaskbarDpi(taskbar);

            IntPtr primary = MonitorFromPoint(new POINT(0, 0), 1);
            MONITORINFO monitor = new MONITORINFO();
            monitor.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(primary, ref monitor) || bounds.Width <= 0 || bounds.Height <= 0) return result;
            Rectangle screen = monitor.rcMonitor.ToRectangle();
            if (MonitorFromWindow(taskbar, 2) != primary)
            {
                result.Reason = "主任务栏不在主显示器";
                return result;
            }

            APPBARDATA data = new APPBARDATA();
            data.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            bool autoHide = (SHAppBarMessage(4, ref data).ToUInt64() & 1UL) != 0;
            Rectangle visibleBar = Rectangle.Intersect(bounds, screen);
            int edgeTolerance = Math.Max(2, result.Dpi / 48);
            if (visibleBar.IsEmpty || (autoHide && visibleBar.Height < bounds.Height - edgeTolerance))
            {
                result.Reason = "任务栏已自动隐藏";
                return result;
            }
            if (bounds.Width <= bounds.Height ||
                (Math.Abs(bounds.Top - screen.Top) > edgeTolerance && Math.Abs(bounds.Bottom - screen.Bottom) > edgeTolerance))
            {
                result.Reason = "当前仅支持主屏顶部或底部的横向任务栏";
                return result;
            }
            if (IsPrimaryFullscreen(primary, screen, taskbar, edgeTolerance))
            {
                result.Reason = "主显示器正在全屏显示";
                return result;
            }

            IntPtr tray = FindChildByClass(taskbar, "TrayNotifyWnd");
            RECT trayRaw;
            if (tray == IntPtr.Zero || !IsWindowVisible(tray) || !GetWindowRect(tray, out trayRaw))
            {
                result.Reason = "无法确认系统通知区边界，已收起到托盘";
                return result;
            }
            Rectangle trayBounds = Rectangle.Intersect(trayRaw.ToRectangle(), visibleBar);
            if (trayBounds.Width <= 0 || trayBounds.Height <= 0 || trayBounds.Left <= bounds.Left)
            {
                result.Reason = "系统通知区边界尚未就绪";
                return result;
            }

            List<Rectangle> buttons;
            try { buttons = GetTaskbarButtons(taskbar, bounds, trayBounds); }
            catch (ElementNotAvailableException) { result.Reason = "任务栏正在重新加载"; return result; }
            catch (InvalidOperationException) { result.Reason = "任务栏布局暂不可读取"; return result; }
            catch (COMException) { result.Reason = "任务栏布局暂不可读取"; return result; }
            catch (UnauthorizedAccessException) { result.Reason = "无法读取任务栏布局"; return result; }
            if (buttons.Count == 0)
            {
                result.Reason = "无法确认任务按钮边界，已收起到托盘";
                return result;
            }

            // Use the actual interactive elements, not Win11's legacy MSTaskSwWnd
            // compatibility rectangle, which can extend across otherwise empty space.
            int rightBoundary = trayBounds.Left;
            foreach (Rectangle button in buttons)
                if (button.Left < trayBounds.Left && button.Right >= trayBounds.Left)
                    rightBoundary = Math.Min(rightBoundary, button.Left);
            int leftBoundary = visibleBar.Left;
            foreach (Rectangle button in buttons)
                if (button.Left < rightBoundary)
                    leftBoundary = Math.Max(leftBoundary, button.Right);

            int gap = Math.Max(3, (int)Math.Ceiling(result.Dpi * 4.0 / 96.0));
            int verticalInset = Math.Max(1, result.Dpi / 96);
            int left = leftBoundary + gap;
            int right = rightBoundary - gap;
            int top = visibleBar.Top + verticalInset;
            int bottom = visibleBar.Bottom - verticalInset;
            result.SafeArea = right > left && bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
            if (result.SafeArea.Width < desiredWidthPx || result.SafeArea.Height < desiredHeightPx)
            {
                result.Reason = "任务栏空白不足，已收起到托盘";
                return result;
            }
            result.ShouldHide = false;
            result.Reason = "任务栏空白可用";
            return result;
        }

        private static List<Rectangle> GetTaskbarButtons(IntPtr taskbar, Rectangle bar, Rectangle tray)
        {
            lock (cacheLock)
            {
                // Coalesce duplicate callers only. At the normal two-second poll interval
                // each inspection gets a fresh, batched cache without retaining UIA handles.
                if (cachedTaskbar == taskbar && cachedBounds == bar && cachedTray == tray && cachedButtons != null &&
                    (DateTime.UtcNow - cacheTimeUtc).TotalMilliseconds < 500)
                    return new List<Rectangle>(cachedButtons);

                AutomationElement root = AutomationElement.FromHandle(taskbar);
                if (root == null) throw new ElementNotAvailableException();
                CacheRequest cache = new CacheRequest();
                cache.Add(AutomationElement.BoundingRectangleProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.AutomationElementMode = AutomationElementMode.None;
                cache.TreeScope = TreeScope.Element;
                Condition interactive = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider));
                Condition visibleInteractive = new AndCondition(interactive,
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
                List<Rectangle> found = new List<Rectangle>();
                using (cache.Activate())
                {
                    AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, visibleInteractive);
                    foreach (AutomationElement element in elements)
                    {
                        AutomationElement.AutomationElementInformation info = element.Cached;
                        System.Windows.Rect rect = info.BoundingRectangle;
                        if (info.IsOffscreen || rect.IsEmpty || Double.IsNaN(rect.Left) || Double.IsInfinity(rect.Left) ||
                            Double.IsNaN(rect.Top) || Double.IsInfinity(rect.Top) || Double.IsNaN(rect.Width) ||
                            Double.IsInfinity(rect.Width) || Double.IsNaN(rect.Height) || Double.IsInfinity(rect.Height)) continue;
                        Rectangle physical = Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top),
                            (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
                        physical = Rectangle.Intersect(physical, bar);
                        if (physical.Width > 0 && physical.Height > 0 && physical.Left < tray.Left) found.Add(physical);
                    }
                }
                cachedTaskbar = taskbar;
                cachedBounds = bar;
                cachedTray = tray;
                cachedButtons = found;
                cacheTimeUtc = DateTime.UtcNow;
                return new List<Rectangle>(found);
            }
        }

        private static bool IsPrimaryFullscreen(IntPtr primary, Rectangle screen, IntPtr taskbar, int tolerance)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == taskbar || !IsWindowVisible(foreground) || IsIconic(foreground) ||
                MonitorFromWindow(foreground, 2) != primary) return false;
            uint processId;
            GetWindowThreadProcessId(foreground, out processId);
            if (processId == GetCurrentProcessId()) return false;
            string className = GetClass(foreground);
            if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd" ||
                className == "Shell_SecondaryTrayWnd") return false;
            int cloaked;
            if (DwmGetWindowAttributeInt(foreground, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            RECT raw;
            if (DwmGetWindowAttributeRect(foreground, 9, out raw, Marshal.SizeOf(typeof(RECT))) != 0 &&
                !GetWindowRect(foreground, out raw)) return false;
            Rectangle rect = raw.ToRectangle();
            return rect.Left <= screen.Left + tolerance && rect.Top <= screen.Top + tolerance &&
                   rect.Right >= screen.Right - tolerance && rect.Bottom >= screen.Bottom - tolerance;
        }

        private static IntPtr FindChildByClass(IntPtr parent, string className)
        {
            IntPtr direct = FindWindowEx(parent, IntPtr.Zero, className, null);
            if (direct != IntPtr.Zero) return direct;
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, delegate(IntPtr handle, IntPtr parameter)
            {
                if (GetClass(handle) != className) return true;
                found = handle;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static string GetClass(IntPtr handle)
        {
            StringBuilder name = new StringBuilder(256);
            GetClassName(handle, name, name.Capacity);
            return name.ToString();
        }

        private static int GetTaskbarDpi(IntPtr taskbar)
        {
            try { uint dpi = GetDpiForWindow(taskbar); if (dpi > 0) return (int)dpi; }
            catch (EntryPointNotFoundException) { }
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return 96;
            try { int dpi = GetDeviceCaps(dc, 88); return dpi > 0 ? dpi : 96; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage, uEdge;
            public RECT rc;
            public IntPtr lParam;
        }
        private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out RECT rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int awareness);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr handle);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr handle, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeRect(IntPtr handle, uint attribute, out RECT value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr handle, uint attribute, out int value, int size);
    }
}
