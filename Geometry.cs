// Geometry.cs -- find the free horizontal span on the taskbar, at runtime, every time.
//
// WHY THIS IS NOT A CONSTANT. The first draft of this plan hardcoded "the left third is empty,
// x=16..534", derived from WindowFromPoint returning Shell_TrayWnd across x=20..600. That was
// wrong: Win11 taskbar content is XAML hosted in Windows.UI.Composition.DesktopWindowContentBridge
// and has NO child HWND of its own, so HWND probing is blind to it. A screen capture plus a
// per-column luminance-variance scan found the Widgets weather button sitting at x=20..77 --
// exactly where the strip was going to be drawn.
//
// The converse is also true: ReBarWindow32's rect is (627..1116) but its PIXELS only run 639..1105
// with gaps, so trusting pixels alone would let us place an icon at x=670 inside a live control.
//
// Therefore: UNION both sources, never subtract. Measured 2026-08-04 on this machine.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace CommandCenter
{
    internal class Span
    {
        public int Left;
        public int Width;
        public Span(int l, int w) { Left = l; Width = w; }
        public int Right { get { return Left + Width; } }
        public override string ToString()
        {
            return string.Format("{0}..{1} w={2}", Left, Left + Width, Width);
        }
    }

    internal static class Geometry
    {
        // Tuning constants. All px at 96 DPI; scaled by dpi/96 at the call site.
        public const double VAR_THRESHOLD = 12.0;
        public const int GAP_MERGE = 8;
        public const int PAD = 6;
        public const int MIN_SPAN = 48;
        public const int EDGE_MARGIN = 12;

        // Classes to skip in the HWND pass, and why each one would break the scan:
        //   DesktopWindowContentBridge -- spans the entire 1920px bar, would mark everything occupied
        //   InputSite.WindowClass      -- zero width
        //   TrayDummySearchControl     -- zero width
        //   Windows.UI.Core.CoreWindow -- invisible, reports a bogus 65535x65535 rect
        private static readonly string[] SkipClasses = new string[] {
            "Windows.UI.Composition.DesktopWindowContentBridge",
            "Windows.UI.Input.InputSite.WindowClass",
            "TrayDummySearchControl",
            "Windows.UI.Core.CoreWindow"
        };

        /// <summary>
        /// Full scan. Returns null if the taskbar cannot be resolved -- callers must keep their last
        /// good span and retry rather than painting against a stale or bogus rect.
        /// </summary>
        /// <summary>Wall time of the last full scan, ms. Logged so the idle cost stays visible.</summary>
        public static double LastScanMs;

        public static Span Scan(IntPtr hTray, IntPtr hStrip, Native.RECT tray, uint dpi, out string trace)
        {
            System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            try { return ScanInner(hTray, hStrip, tray, dpi, out trace); }
            finally { LastScanMs = _sw.Elapsed.TotalMilliseconds; }
        }

        private static Span ScanInner(IntPtr hTray, IntPtr hStrip, Native.RECT tray, uint dpi, out string trace)
        {
            trace = "";
            double s = dpi / 96.0;
            int trayW = tray.Width, trayH = tray.Height;

            // Sanity. A 48px bar is normal; 24..120 covers small/large icon settings and scaling.
            if (trayH < 24 || trayH > 120 || trayW < 200)
            {
                trace = string.Format("bogus tray rect {0}x{1}", trayW, trayH);
                return null;
            }

            bool[] occupied = new bool[trayW];

            // ---- pass 1: HWND rects ----------------------------------------------------------
            List<string> hwndHits = new List<string>();
            Native.EnumChildWindows(hTray, delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                string cls = Native.ClassOf(h);
                for (int i = 0; i < SkipClasses.Length; i++)
                    if (cls == SkipClasses[i]) return true;

                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return true;
                if (r.Width <= 0 || r.Width >= trayW) return true;   // zero-width or full-bar: ignore

                int a = r.Left - tray.Left, b = r.Right - tray.Left;
                if (b <= 0 || a >= trayW) return true;
                if (a < 0) a = 0;
                if (b > trayW) b = trayW;
                for (int x = a; x < b; x++) occupied[x] = true;
                hwndHits.Add(string.Format("{0}:{1}..{2}", cls, a, b));
                return true;
            }, IntPtr.Zero);

            // ---- pass 2: pixels --------------------------------------------------------------
            bool[] content;
            double bgLum;
            if (!ScanPixels(hTray, hStrip, tray, dpi, out content, out bgLum))
            {
                trace = "pixel scan failed";
                return null;
            }
            for (int x = 0; x < trayW; x++) if (content[x]) occupied[x] = true;

            LastBackgroundLuminance = bgLum;

            // ---- runs, merge, dilate ---------------------------------------------------------
            List<int[]> runs = ExtractRuns(occupied);
            runs = MergeCloseRuns(runs, Sc(GAP_MERGE, s));
            runs = Dilate(runs, Sc(PAD, s), trayW);
            runs = MergeCloseRuns(runs, 0);

            // ---- free runs -------------------------------------------------------------------
            List<int[]> free = Complement(runs, trayW);
            int minSpan = Sc(MIN_SPAN, s);

            int[] best = null;
            for (int i = 0; i < free.Count; i++)
            {
                int[] f = free[i];
                int w = f[1] - f[0];
                if (w < minSpan) continue;
                if (f[0] >= trayW / 2) continue;          // must start in the left half
                if (best == null || w > best[1] - best[0]) best = f;
            }
            if (best == null)
            {
                trace = "no free run >= " + minSpan + "px in the left half";
                return null;
            }

            int L = Math.Max(best[0], Sc(EDGE_MARGIN, s));
            int R = Math.Min(best[1], (int)(trayW * 0.62));
            if (R - L < minSpan)
            {
                trace = string.Format("free run collapsed after clamp: {0}..{1}", L, R);
                return null;
            }

            trace = string.Format("hwnd[{0}] free={1}..{2} bg={3:F1}",
                string.Join(",", hwndHits.ToArray()), L, R, bgLum);

            return new Span(tray.Left + L, R - L);
        }

        /// <summary>Background luminance from the last scan -- Theme reuses it at zero extra cost.</summary>
        public static double LastBackgroundLuminance = -1;

        private static int Sc(int v, double s) { return (int)Math.Round(v * s, MidpointRounding.AwayFromZero); }

        // ------------------------------------------------------------------------------------
        private static bool ScanPixels(IntPtr hTray, IntPtr hStrip, Native.RECT tray, uint dpi,
                                       out bool[] content, out double bgLum)
        {
            int w = tray.Width, h = tray.Height;
            content = new bool[w];
            bgLum = -1;

            IntPtr screenDC = IntPtr.Zero, memDC = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
            Bitmap managed = null;
            try
            {
                screenDC = Native.GetDC(IntPtr.Zero);
                memDC = Native.CreateCompatibleDC(screenDC);
                bmp = Native.CreateCompatibleBitmap(screenDC, w, h);
                old = Native.SelectObject(memDC, bmp);

                bool ok = Native.PrintWindow(hTray, memDC, Native.PW_RENDERFULLCONTENT);
                managed = Image.FromHbitmap(bmp);

                // Blank guard. PrintWindow can silently produce an empty surface on some builds;
                // an all-black capture would read as "no content anywhere" and hand back the whole
                // bar as free space -- the strip would then be drawn straight over the Start button.
                if (!ok || IsEffectivelyBlank(managed))
                {
                    managed.Dispose(); managed = null;
                    // Fallback: hide ourselves for the duration of one screen grab. Gap is <15 ms,
                    // under a single 60 Hz frame. Only reachable from the 30 s forced rescan.
                    bool hid = false;
                    if (hStrip != IntPtr.Zero && Native.IsWindow(hStrip))
                    { Native.ShowWindow(hStrip, Native.SW_HIDE); hid = true; }
                    try
                    {
                        managed = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(managed))
                            g.CopyFromScreen(tray.Left, tray.Top, 0, 0, new Size(w, h),
                                             CopyPixelOperation.SourceCopy);
                    }
                    finally
                    {
                        if (hid) Native.ShowWindow(hStrip, Native.SW_SHOWNOACTIVATE);
                    }
                }

                return Analyze(managed, dpi, content, out bgLum);
            }
            catch (Exception ex)
            {
                Log.Warn("geometry: pixel scan threw: " + ex.Message);
                return false;
            }
            finally
            {
                // Every path. A resident process that leaks one GDI object per 30 s scan hits the
                // 10,000-handle process quota in about three days and then stops rendering.
                if (managed != null) managed.Dispose();
                if (memDC != IntPtr.Zero && old != IntPtr.Zero) Native.SelectObject(memDC, old);
                if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
                if (memDC != IntPtr.Zero) Native.DeleteDC(memDC);
                if (screenDC != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        /// <summary>
        /// PrintWindow can silently return an empty surface. An all-black capture would read as
        /// "no content anywhere" and hand back the whole bar as free space -- the strip would then
        /// be drawn straight over the Start button. LockBits, not GetPixel: GetPixel locks and
        /// unlocks the bitmap on every single call.
        /// </summary>
        private static bool IsEffectivelyBlank(Bitmap bm)
        {
            BitmapData bd = bm.LockBits(new Rectangle(0, 0, bm.Width, bm.Height),
                                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                double sum = 0;
                int y = bm.Height / 2;
                int step = Math.Max(1, bm.Width / 64);
                byte[] row = new byte[bd.Stride];
                System.Runtime.InteropServices.Marshal.Copy(
                    new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row, 0, bd.Stride);
                for (int x = 0; x < bm.Width; x += step)
                {
                    int i = x * 4;
                    if (i + 2 >= row.Length) break;
                    sum += 0.299 * row[i + 2] + 0.587 * row[i + 1] + 0.114 * row[i + 0];
                }
                return sum < 1.0;
            }
            finally { bm.UnlockBits(bd); }
        }

        private static bool Analyze(Bitmap bm, uint dpi, bool[] content, out double bgLum)
        {
            bgLum = -1;
            int w = Math.Min(bm.Width, content.Length), h = bm.Height;

            // Y_MARGIN IS LOAD-BEARING, NOT COSMETIC. Verified 2026-08-04: scanning all 48 rows
            // returns a single occupied run of 0..1919 -- the entire bar reads as content. Cause:
            // row y=0 is the taskbar's 1px top border at luminance 181 against a 224 background,
            // which contributes about 36 variance to EVERY column, way above the threshold of 12.
            // With rows 4..43 the quiet-zone max variance drops to 2.00 (avg 1.09), a 6x margin.
            // Do NOT "fix" a false-positive by raising VAR_THRESHOLD -- that would also swallow
            // genuine low-contrast glyphs, which is the failure this scan exists to prevent.
            int margin = Math.Max(2, (int)Math.Round(4 * (dpi / 96.0), MidpointRounding.AwayFromZero));
            int y0 = margin, y1 = h - margin;
            if (y1 - y0 < 4) { y0 = 0; y1 = h; }
            int ny = y1 - y0;

            BitmapData bd = bm.LockBits(new Rectangle(0, 0, bm.Width, h),
                                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] buf = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, buf, 0, buf.Length);

                List<double> quiet = new List<double>();
                for (int x = 0; x < w; x++)
                {
                    double s1 = 0, s2 = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int i = y * stride + x * 4;         // BGRA
                        double lum = 0.299 * buf[i + 2] + 0.587 * buf[i + 1] + 0.114 * buf[i + 0];
                        s1 += lum; s2 += lum * lum;
                    }
                    double mean = s1 / ny;
                    double var = s2 / ny - mean * mean;
                    if (var < 0) var = 0;
                    content[x] = var > VAR_THRESHOLD;
                    if (!content[x]) quiet.Add(mean);
                }

                if (quiet.Count > 0)
                {
                    quiet.Sort();
                    bgLum = quiet[quiet.Count / 2];   // median of quiet columns == the bar's own colour
                }
                return true;
            }
            finally { bm.UnlockBits(bd); }
        }

        // ------------------------------------------------------------------------------------
        private static List<int[]> ExtractRuns(bool[] occ)
        {
            List<int[]> runs = new List<int[]>();
            int start = -1;
            for (int x = 0; x < occ.Length; x++)
            {
                if (occ[x] && start < 0) start = x;
                else if (!occ[x] && start >= 0) { runs.Add(new int[] { start, x }); start = -1; }
            }
            if (start >= 0) runs.Add(new int[] { start, occ.Length });
            return runs;
        }

        private static List<int[]> MergeCloseRuns(List<int[]> runs, int gap)
        {
            List<int[]> outp = new List<int[]>();
            for (int i = 0; i < runs.Count; i++)
            {
                if (outp.Count > 0 && runs[i][0] - outp[outp.Count - 1][1] <= gap)
                    outp[outp.Count - 1][1] = Math.Max(outp[outp.Count - 1][1], runs[i][1]);
                else outp.Add(new int[] { runs[i][0], runs[i][1] });
            }
            return outp;
        }

        private static List<int[]> Dilate(List<int[]> runs, int pad, int max)
        {
            List<int[]> outp = new List<int[]>();
            for (int i = 0; i < runs.Count; i++)
                outp.Add(new int[] { Math.Max(0, runs[i][0] - pad), Math.Min(max, runs[i][1] + pad) });
            return outp;
        }

        private static List<int[]> Complement(List<int[]> runs, int width)
        {
            List<int[]> free = new List<int[]>();
            int cur = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i][0] > cur) free.Add(new int[] { cur, runs[i][0] });
                cur = Math.Max(cur, runs[i][1]);
            }
            if (cur < width) free.Add(new int[] { cur, width });
            return free;
        }

        // ------------------------------------------------------------------------------------
        /// <summary>
        /// Cheap change detector, run every 2 s. Compares the tray rect plus the three control rects
        /// that actually move (Start, ReBarWindow32, TrayNotifyWnd). ReBarWindow32 is the important
        /// one: the taskbar is centre-aligned, so its left edge slides about 22px left for every
        /// window opened -- the runway genuinely shrinks during ordinary use. About 0.02 ms.
        /// </summary>
        public static string CheapSignature(IntPtr hTray)
        {
            Native.RECT t;
            if (!Native.GetWindowRect(hTray, out t)) return "none";
            string sig = string.Format("{0},{1},{2},{3}", t.Left, t.Top, t.Right, t.Bottom);
            string[] classes = new string[] { "Start", "ReBarWindow32", "TrayNotifyWnd" };
            for (int i = 0; i < classes.Length; i++)
            {
                IntPtr h = Native.FindWindowEx(hTray, IntPtr.Zero, classes[i], null);
                Native.RECT r;
                if (h != IntPtr.Zero && Native.GetWindowRect(h, out r))
                    sig += string.Format("|{0}:{1},{2}", classes[i], r.Left, r.Right);
                else sig += "|" + classes[i] + ":-";
            }
            return sig;
        }

        /// <summary>All taskbars, primary first. DISPLAY1 sits at x=-1920 here, so signed ints only.</summary>
        public static List<IntPtr> AllTaskbars()
        {
            List<IntPtr> bars = new List<IntPtr>();
            IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero) bars.Add(primary);
            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                bars.Add(h);
            return bars;
        }
    }
}
