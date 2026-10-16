// Native.cs -- every P/Invoke the strip needs, in one place.
//
// C# 5 ONLY. The in-box compiler is Microsoft (R) Visual C# Compiler 4.8.9221.0, which is the
// Roslyn-era binary running in C# 5 mode. Verified 2026-08-04: a probe using $"..." fails with
// error CS1056: Unexpected character '$'. No string interpolation, no ?., no nameof, no
// expression-bodied members, no auto-property initializers, no using static, anywhere in this
// codebase. There is no .NET SDK on this machine, so there is no other compiler to switch to.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace CommandCenter
{
    internal static class Native
    {
        // ---- window styles -------------------------------------------------------------------
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_POPUP = unchecked((int)0x80000000);

        // ---- SetWindowPos --------------------------------------------------------------------
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const uint SWP_NOSENDCHANGING = 0x0400;

        // ---- messages ------------------------------------------------------------------------
        public const int WM_DESTROY = 0x0002;
        public const int WM_CLOSE = 0x0010;
        public const int WM_QUERYENDSESSION = 0x0011;
        public const int WM_ENDSESSION = 0x0016;
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_TIMECHANGE = 0x001E;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_WINDOWPOSCHANGED = 0x0047;
        public const int WM_TIMER = 0x0113;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MOUSELEAVE = 0x02A3;
        public const int WM_POWERBROADCAST = 0x0218;
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_WTSSESSION_CHANGE = 0x02B1;

        public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        public const int PBT_APMRESUMESUSPEND = 0x0007;
        public const int WTS_SESSION_LOCK = 0x7;
        public const int WTS_SESSION_UNLOCK = 0x8;
        public const int NOTIFY_FOR_THIS_SESSION = 0;

        // ---- timers --------------------------------------------------------------------------
        public const int TIMER_RENDER = 1;   //   33 ms, only while something animates
        public const int TIMER_ZORDER = 2;   // 1000 ms, z-order backstop
        public const int TIMER_GEOM = 3;     // 2000 ms, cheap rect compare
        public const int TIMER_STATE = 4;    // 30000 ms, state safety poll
        public const int TIMER_ZASSERT = 5;  //  120 ms one-shot, debounce
        public const int TIMER_SETTINGS = 6; //  250 ms one-shot, WM_SETTINGCHANGE burst debounce

        // wParam of WM_SETTINGCHANGE when the work area moved. Carries no lParam string, so it has
        // to be matched numerically rather than by name like the others.
        public const int SPI_SETWORKAREA = 0x002F;

        // ---- structs -------------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } } }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy;
            public SIZE(int w, int h) { cx = w; cy = h; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int ULW_ALPHA = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd, hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }
        public const uint ABM_NEW = 0x00000000;
        public const uint ABM_REMOVE = 0x00000001;
        public const uint ABM_SETPOS = 0x00000003;
        public const uint ABM_GETSTATE = 0x00000004;
        public const uint ABM_GETTASKBARPOS = 0x00000005;
        public const uint ABE_BOTTOM = 3;
        public const int ABS_AUTOHIDE = 0x0000001;

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public int dwHoverTime;
        }
        public const uint TME_LEAVE = 0x00000002;

        // ---- user32 --------------------------------------------------------------------------
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);

        // Per-process GDI (flags=0) and USER (flags=1) handle counts. Both are capped at 10,000 per
        // process by Windows, and a layered-window repaint loop that leaks either one dies of it
        // days later with no obvious cause. Sampled into every heartbeat so the trend is on record
        // before the crash, not reconstructed after it.
        [DllImport("user32.dll")] public static extern int GetGuiResources(IntPtr proc, int flags);

        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        public const uint GW_HWNDNEXT = 2;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr h, StringBuilder s, int max);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr h, int index);

        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr l);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr h, IntPtr id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string msg);

        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

        // wtsapi32.dll, NOT user32.dll. Declaring these on user32 compiles fine and then throws
        // EntryPointNotFoundException at runtime, which killed startup outright on first launch.
        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr h, int flags);
        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr h);

        /// <summary>
        /// Session-lock notifications are a hardening feature, not a prerequisite. If the API is
        /// unavailable the strip must still run -- so every optional P/Invoke gets a guarded
        /// wrapper rather than being called bare from the startup path.
        /// </summary>
        public static bool TryRegisterSessionNotification(IntPtr h)
        {
            try { return WTSRegisterSessionNotification(h, NOTIFY_FOR_THIS_SESSION); }
            catch (Exception ex) { Log.Warn("session notifications unavailable: " + ex.Message); return false; }
        }

        public static void TryUnregisterSessionNotification(IntPtr h)
        {
            try { WTSUnRegisterSessionNotification(h); } catch { }
        }

        // PerMonitorV2 gives us this; the app.manifest is what makes it report anything but 96.
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);

        // WinEvent hooks -- these carry the z-order load so we never poll SetWindowPos.
        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild,
            uint thread, uint time);
        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod,
            WinEventProc cb, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_OBJECT_REORDER = 0x8004;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        // ---- gdi32 ---------------------------------------------------------------------------
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
            IntPtr src, int sx, int sy, int rop);
        public const int SRCCOPY = 0x00CC0020;

        // ---- kernel32 ------------------------------------------------------------------------
        // /target:winexe means no console is allocated, so Console.WriteLine goes nowhere -- the
        // first -Selftest run printed absolutely nothing and exited blank. Attaching to the parent
        // console is what makes the self-test machine-checkable from PowerShell, which is the whole
        // point of having one.
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AllocConsole();
        [DllImport("kernel32.dll")] public static extern bool FreeConsole();
        public const int ATTACH_PARENT_PROCESS = -1;

        // AttachConsole alone is NOT enough, and this cost a real debugging cycle. Under
        // /target:winexe the CLR caches a null stdout the first time Console is touched, so
        // Console.OpenStandardOutput() keeps handing back Stream.Null even after a console exists
        // -- the self-test printed nothing and exited 0, which is the worst kind of failure. The
        // fix is to open the console device CONOUT$ by name and bind that.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFile(string name, uint access, uint share,
            IntPtr sec, uint disposition, uint flags, IntPtr template);
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint OPEN_EXISTING = 3;

        // Needed to tell the two cases apart. If the parent REDIRECTED our stdout (a pipe or a
        // file, e.g. Start-Process -RedirectStandardOutput), GetStdHandle returns a real handle and
        // we must write to THAT. If we were launched with no console at all, it returns NULL and
        // CONOUT$ is correct. Binding CONOUT$ in the redirected case sends output to a console
        // nobody is reading and leaves the redirect file empty -- observed exactly that.
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int which);
        public const int STD_OUTPUT_HANDLE = -11;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // ---- shell32 -------------------------------------------------------------------------
        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);

        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        public const int QUNS_BUSY = 2;
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        public const int QUNS_PRESENTATION_MODE = 4;

        // PrintWindow with PW_RENDERFULLCONTENT is how we capture the taskbar WITHOUT capturing our
        // own topmost strip. CopyFromScreen would include the strip, so every scan would mark our
        // own icons as "occupied content" -- a monotonic ratchet that shrinks the span to zero
        // within about a minute. This flag (Win8.1+) is what makes DirectComposition/XAML render.
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        public const uint PW_RENDERFULLCONTENT = 0x00000002;

        // ---- helpers -------------------------------------------------------------------------
        public static string ClassOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
        }

        /// <summary>
        /// True iff <paramref name="a"/> precedes <paramref name="b"/> in the top-level z-order,
        /// i.e. a is painted above b. Walks the band rather than trusting a flag, because both
        /// windows are WS_EX_TOPMOST and the flag therefore tells us nothing about their order.
        /// Costs about six GetWindow calls today (&lt;0.05 ms).
        /// </summary>
        public static bool IsAbove(IntPtr a, IntPtr b)
        {
            IntPtr h = GetTopWindow(IntPtr.Zero);
            while (h != IntPtr.Zero)
            {
                if (h == a) return true;
                if (h == b) return false;
                h = GetWindow(h, GW_HWNDNEXT);
            }
            return false;   // neither found -> assume buried, which fails safe toward re-asserting
        }

        public static bool TaskbarAutoHide()
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            IntPtr r = SHAppBarMessage(ABM_GETSTATE, ref d);
            return ((int)r & ABS_AUTOHIDE) != 0;
        }

        /// <summary>Which screen edge the taskbar is docked to. Win11 only supports ABE_BOTTOM.</summary>
        public static uint TaskbarEdge()
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            IntPtr ok = SHAppBarMessage(ABM_GETTASKBARPOS, ref d);
            if (ok == IntPtr.Zero) return ABE_BOTTOM;
            return d.uEdge;
        }

        public static int NotificationState()
        {
            int s;
            if (SHQueryUserNotificationState(out s) != 0) return 1;  // S_OK==0; on failure assume normal
            return s;
        }

        public static uint DpiOf(IntPtr h)
        {
            try
            {
                uint d = GetDpiForWindow(h);
                if (d >= 48 && d <= 960) return d;
            }
            catch (EntryPointNotFoundException) { }
            return 96;
        }
    }
}
