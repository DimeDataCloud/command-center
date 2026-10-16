// StripWindow.cs -- the layered, topmost, non-activating overlay that IS the command center.
//
// WHY AN OVERLAY AND NOT SOMETHING SUPPORTED. Windows 11 removed deskbands; there is no sanctioned
// way to add content to the taskbar. Every shipped alternative needs something we will not do:
//   TranslucentTB      restyles Shell_TrayWnd, cannot add content
//   Windhawk / EP      DLL injection into explorer.exe, elevation, breaks per Windows build
//   RetroBar           replaces the taskbar wholesale
//   7+ Taskbar Tweaker needs MSTaskListWClass content, which Win11 does not have (measured vis=0)
//   TrafficMonitor     SetParent's a child HWND into Shell_TrayWnd -- cross-process child means
//                      input-queue attachment, so if OUR thread stalls THE WHOLE TASKBAR HANGS.
//                      Disqualifying, and the reason this file never calls SetParent.
//
// WHY THIS CAN WORK, measured 2026-08-04 rather than assumed:
//   Shell_TrayWnd EXSTYLE = 0x88 (TOPMOST|TOOLWINDOW), so we must be topmost too and must win an
//   ordering fight INSIDE the topmost band. That band is ordinary: ThumbnailDeviceHelperWnd sits
//   above the taskbar right now, and Microsoft's own layered+topmost ClickToDo IslandWindow sits
//   below it. Further, DesktopWindowContentBridge is a CHILD of Shell_TrayWnd (the bottom-most
//   one), and DWM composites a top-level's whole child tree together -- so being above
//   Shell_TrayWnd means being above its XAML. Compositing is not the problem; z-order is.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CommandCenter
{
    internal class StripWindow : NativeWindow, IDisposable
    {
        public IntPtr TrayHandle;
        public string MonitorKey = "primary";

        private Span _span;
        private int _pendingScans;   // consecutive scans demanding a re-anchor (needs 2 to commit)
        private int _heldWobbles;    // span changes absorbed without moving, since the last anchor
        private Native.RECT _tray;
        private uint _dpi = 96;
        private int _stripH = 48;
        private int _yOffset;                  // Tier 2 float lifts us off the bar
        private List<Cell> _cells = new List<Cell>();
        private string _hoverKey;
        private bool _visible = true;
        private bool _renderTimerOn;
        private bool _tracking;
        private string _geomSig = "";
        private long _lastFullScan;
        private uint _msgTaskbarCreated;
        private IntPtr _hookFg, _hookReorder;
        private Native.WinEventProc _hookProc;   // held: a collected delegate crashes the callback
        private int _buryCount;
        private long _buryWindowStart;
        private int _tier = 1;                   // 1 = overlay on the bar, 2 = float above it
        private long _tier1RetryAt;
        private int _tier1BackoffMin = 30;
        private bool _disposed;

        public event Action<Cell, bool> CellClicked;   // (cell, isRightClick)

        public Span CurrentSpan { get { return _span; } }

        // Where the strip was ACTUALLY drawn last frame. Anchor and nudge mean this is no longer
        // the same as CurrentSpan.Left, and every popup anchors to it -- a flyout that opens where
        // the strip used to be is worse than one that does not open.
        public int OriginX { get { return _originX; } }
        public int OriginY { get { return _originY; } }
        private int _originX, _originY;
        public uint Dpi { get { return _dpi; } }
        public Native.RECT TrayRect { get { return _tray; } }
        public int Tier { get { return _tier; } }
        public bool IsVisible { get { return _visible; } }
        public List<Cell> Cells { get { return _cells; } }

        public bool Create(IntPtr tray)
        {
            TrayHandle = tray;
            if (!Native.GetWindowRect(tray, out _tray)) return false;
            _dpi = Native.DpiOf(tray);
            _stripH = _tray.Height;

            CreateParams cp = new CreateParams();
            cp.Caption = "CommandCenterStrip";
            cp.X = _tray.Left; cp.Y = _tray.Top;
            cp.Width = 10; cp.Height = _stripH;
            cp.Style = Native.WS_POPUP;
            // NOACTIVATE so clicking never steals focus from what he is typing in.
            // TOOLWINDOW so it never appears in alt-tab.
            cp.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST
                       | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            CreateHandle(cp);

            _msgTaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            Native.TryRegisterSessionNotification(Handle);

            // WinEvent hooks carry the z-order load, so we never poll SetWindowPos -- an
            // unconditional 1 Hz SetWindowPos causes DWM churn and can itself provoke explorer
            // into re-asserting, which is the very fight we are trying not to have.
            _hookProc = OnWinEvent;
            _hookFg = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            _hookReorder = Native.SetWinEventHook(Native.EVENT_OBJECT_REORDER, Native.EVENT_OBJECT_REORDER,
                IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

            Native.SetTimer(Handle, (IntPtr)Native.TIMER_ZORDER, 1000, IntPtr.Zero);
            Native.SetTimer(Handle, (IntPtr)Native.TIMER_GEOM, 2000, IntPtr.Zero);
            Native.SetTimer(Handle, (IntPtr)Native.TIMER_STATE, 30000, IntPtr.Zero);

            Rescan(true);
            Render();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            return true;
        }

        private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild,
                                uint thread, uint time)
        {
            if (ev == Native.EVENT_OBJECT_REORDER && hwnd != TrayHandle) return;
            // Coalesce: many reorder events arrive in bursts, and checking z-order per event would
            // turn a window-open into dozens of walks.
            Native.SetTimer(Handle, (IntPtr)Native.TIMER_ZASSERT, 120, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            try
            {
                switch (m.Msg)
                {
                    case Native.WM_TIMER:
                        OnTimer((int)m.WParam);
                        return;

                    case Native.WM_MOUSEMOVE:
                        OnMouseMove(LoWord(m.LParam), HiWord(m.LParam));
                        return;

                    case Native.WM_MOUSELEAVE:
                        _tracking = false;
                        if (_hoverKey != null) { _hoverKey = null; Render(); }
                        return;

                    case Native.WM_LBUTTONUP:
                    case Native.WM_RBUTTONUP:
                        OnClick(LoWord(m.LParam), HiWord(m.LParam), m.Msg == Native.WM_RBUTTONUP);
                        return;

                    case Native.WM_WINDOWPOSCHANGED:
                        {
                            Native.WINDOWPOS wp = (Native.WINDOWPOS)Marshal.PtrToStructure(
                                m.LParam, typeof(Native.WINDOWPOS));
                            if (wp.hwndInsertAfter != Native.HWND_TOPMOST)
                                Native.SetTimer(Handle, (IntPtr)Native.TIMER_ZASSERT, 120, IntPtr.Zero);
                        }
                        break;

                    case Native.WM_DISPLAYCHANGE:
                        Log.Info("strip: WM_DISPLAYCHANGE -- full rescan");
                        Rescan(true); Render();
                        return;

                    case Native.WM_DPICHANGED:
                        _dpi = (uint)LoWord(m.WParam);
                        Log.Info("strip: WM_DPICHANGED -> " + _dpi);
                        Rescan(true); Render();
                        return;

                    case Native.WM_SETTINGCHANGE:
                        {
                            // Windows broadcasts this to every top-level window for a long list of
                            // unrelated reasons -- environment changes, policy refreshes, mouse
                            // settings. Rescanning on all of them fired the ~100ms PrintWindow full
                            // scan for events that could not possibly have moved the taskbar. Match
                            // only the ones that can, and debounce: a single theme switch sends 3-6
                            // of these in a burst, and each one was a full rescan and repaint.
                            string what = m.LParam == IntPtr.Zero
                                ? null : Marshal.PtrToStringAuto(m.LParam);
                            bool relevant =
                                (int)m.WParam == Native.SPI_SETWORKAREA ||
                                what == "ImmersiveColorSet"  || what == "TraySettings" ||
                                what == "TaskbarStateChanged" || what == "WindowsThemeElement" ||
                                what == "UserInteractionMode";
                            if (relevant)
                                Native.SetTimer(Handle, (IntPtr)Native.TIMER_SETTINGS, 250, IntPtr.Zero);
                        }
                        return;

                    case Native.WM_TIMECHANGE:
                        // The clock moved -- DST, an NTP correction, or a manual change. Every
                        // cadence in the supervisor is wall-clock arithmetic against DateTime.Now,
                        // so a BACKWARD jump makes "minutes since last attempt" negative and parks
                        // the whole fleet silently until real time catches up (an hour, every
                        // November). A forward jump makes all thirteen look overdue at once.
                        Log.Warn("strip: WM_TIMECHANGE -- system clock changed, rebasing cadences");
                        Supervisor.OnClockChanged();
                        return;

                    case Native.WM_WTSSESSION_CHANGE:
                        {
                            int ev = (int)m.WParam;
                            if (ev == Native.WTS_SESSION_LOCK)
                            {
                                // A skip-permissions agent fleet running against an unattended
                                // desktop is the scenario you do not want to have to explain.
                                Supervisor.SessionLocked = true;
                                Log.Info("session locked -- supervisor will not dispatch");
                                SetVisible(false);
                            }
                            else if (ev == Native.WTS_SESSION_UNLOCK)
                            {
                                Supervisor.SessionLocked = false;
                                Log.Info("session unlocked");
                                Supervisor.SuppressFor(TimeSpan.FromSeconds(90), "session unlock");
                                Rescan(true); SetVisible(true); Render();
                            }
                        }
                        return;

                    case Native.WM_POWERBROADCAST:
                        {
                            int ev = (int)m.WParam;
                            if (ev == Native.PBT_APMRESUMEAUTOMATIC || ev == Native.PBT_APMRESUMESUSPEND)
                            {
                                // Every officer reads as massively overdue after a resume. Without
                                // a grace period the due-set would be the whole fleet at once.
                                Supervisor.SuppressFor(TimeSpan.FromMinutes(3), "power resume");
                                Rescan(true); Render();
                            }
                        }
                        break;

                    case Native.WM_ENDSESSION:
                    case Native.WM_QUERYENDSESSION:
                        Cleanup();
                        break;

                    case Native.WM_DESTROY:
                        Cleanup();
                        break;
                }

                if (m.Msg == (int)_msgTaskbarCreated && _msgTaskbarCreated != 0)
                {
                    Log.Info("strip: explorer restarted -- re-anchoring");
                    TrayHandle = Native.FindWindow("Shell_TrayWnd", null);
                    if (TrayHandle != IntPtr.Zero) { Rescan(true); Render(); Reassert(true); }
                    return;
                }
            }
            catch (Exception ex) { Log.Err("strip: WndProc", ex); }

            base.WndProc(ref m);
        }

        private void OnTimer(int id)
        {
            long now = Environment.TickCount;
            switch (id)
            {
                case Native.TIMER_ZASSERT:
                    Native.KillTimer(Handle, (IntPtr)Native.TIMER_ZASSERT);
                    Reassert(false);
                    break;

                case Native.TIMER_ZORDER:
                    Reassert(false);
                    CheckObstruction();
                    break;

                case Native.TIMER_GEOM:
                    {
                        // The cheap signature (4 GetWindowRect, ~0.16 ms) catches every change that
                        // has an HWND -- which is every window open/close, since those slide
                        // ReBarWindow32's left edge. The forced full scan exists only to catch
                        // XAML-only drift that has no HWND at all, e.g. the Widgets weather text
                        // changing width from "70F Clear" to something longer.
                        //
                        // That full scan costs ~100 ms because PrintWindow makes the taskbar
                        // re-render its whole XAML tree, and at a 30 s interval it was the single
                        // largest idle cost in the process. Two minutes bounds the worst-case
                        // exposure (an icon briefly overlapping widened widget text) at something
                        // nobody will notice, and cuts the idle cost by 4x.
                        string sig = Geometry.CheapSignature(TrayHandle);
                        bool forced = (now - _lastFullScan) > 120000;
                        if (sig != _geomSig || forced) { Rescan(forced); Render(); }
                    }
                    break;

                case Native.TIMER_SETTINGS:
                    // One-shot: the burst has settled, now do the expensive part exactly once.
                    Native.KillTimer(Handle, (IntPtr)Native.TIMER_SETTINGS);
                    Theme.Refresh();
                    Rescan(true); Render();
                    break;

                case Native.TIMER_STATE:
                    StateStore.Refresh();
                    Render();
                    break;

                case Native.TIMER_RENDER:
                    Render();
                    break;
            }
        }

        // ---- geometry ------------------------------------------------------------------------

        public void Rescan(bool full)
        {
            if (TrayHandle == IntPtr.Zero || !Native.IsWindow(TrayHandle))
            {
                TrayHandle = Native.FindWindow("Shell_TrayWnd", null);
                if (TrayHandle == IntPtr.Zero) { SetVisible(false); return; }
            }
            if (!Native.GetWindowRect(TrayHandle, out _tray)) { SetVisible(false); return; }

            _dpi = Native.DpiOf(TrayHandle);
            _stripH = _tray.Height;
            _geomSig = Geometry.CheapSignature(TrayHandle);
            _lastFullScan = Environment.TickCount;

            // Win11 cannot dock the taskbar anywhere but the bottom. If that ever changes, float
            // rather than guess -- writing edge-anchoring for an impossible case is how specs rot.
            if (Native.TaskbarEdge() != Native.ABE_BOTTOM && _tier == 1)
            {
                Log.Warn("strip: taskbar is not bottom-docked -- switching to float tier");
                _tier = 2;
            }

            string trace;
            Span s = Geometry.Scan(TrayHandle, Handle, _tray, _dpi, out trace);
            if (s == null)
            {
                Log.Warn("strip: geometry scan failed (" + trace + ") -- keeping last good span");
                if (_span == null) { SetVisible(false); return; }
            }
            else
            {
                // STICKY ANCHOR -- the position is COMMITTED, not recomputed every scan.
                //
                // The free span genuinely moves during ordinary use. Win11 centres the task buttons,
                // so ReBarWindow32's left edge marches ~22px every time any window opens or closes,
                // and the left bound comes off a pixel scan that is noisy by nature. An 8px
                // hysteresis does not survive either: measured 477..521px of drift across six
                // minutes of normal use, which slid all fourteen glyphs sideways while the operator was
                // reaching for one.
                //
                // A strip sitting slightly off-centre in its runway is invisible. A strip that moves
                // under the cursor is this component failing at its only job. So re-anchor ONLY when
                // the committed placement stops working -- it would collide, or a better tier has
                // become available -- never merely because the numbers changed.
                double scNow = RenderScale();
                int need = IconRenderer.WidthFor(IconRenderer.Tier, scNow);

                // What matters is where the CONTENT ends, not the nominal span width.
                bool fits = _span != null && _span.Left >= s.Left && _span.Left + need <= s.Right;
                bool tierChange = _span != null &&
                    ChooseTier(s.Width, scNow) != IconRenderer.Tier;

                if (_span == null)
                {
                    _span = s; _pendingScans = 0; _heldWobbles = 0;
                    Log.Info(string.Format("strip: span {0} tray={1}x{2} scan={3:F1}ms {4}",
                        s, _tray.Width, _tray.Height, Geometry.LastScanMs, trace));
                }
                else if (!fits || tierChange)
                {
                    // A real change must survive two consecutive scans before it moves anything, so
                    // a single-frame scan artifact can never relayout the strip. TIMER_GEOM is 2s,
                    // so a genuine change lands within ~4s -- and genuine changes are rare.
                    if (++_pendingScans >= 2)
                    {
                        string why = !fits ? "no longer fits" : "tier change";
                        Log.Info(string.Format(
                            "strip: re-anchored {0} -> {1} ({2}, absorbed {3} wobble(s)) scan={4:F1}ms {5}",
                            _span, s, why, _heldWobbles, Geometry.LastScanMs, trace));
                        _span = s; _pendingScans = 0; _heldWobbles = 0;
                    }
                }
                else
                {
                    _pendingScans = 0;
                    if (s.Left != _span.Left || s.Width != _span.Width) _heldWobbles++;
                    if (Geometry.LastScanMs > 40)
                    {
                        // The forced full rescan is the only expensive thing this process does at
                        // rest. If it ever grows, it must be visible rather than showing up as a
                        // mystery in Task Manager.
                        Log.Info(string.Format("strip: rescan {0:F1}ms (anchor held)", Geometry.LastScanMs));
                    }
                }
            }

            Theme.Refresh();

            double sc = RenderScale();
            LayoutTier t = ChooseTier(_span.Width, sc);
            if (t != IconRenderer.Tier)
            {
                Log.Info("strip: layout tier -> " + t + " (span " + _span.Width + "px)");
                IconRenderer.Tier = t;
            }

            _yOffset = _tier == 2 ? -(_stripH - 8) - 6 : 0;
            if (_tier == 2) _stripH = 40;
        }

        private void CheckObstruction()
        {
            // Hide during fullscreen, presentation and busy states. A red business-status indicator
            // on the taskbar during a screen share is the worst possible moment for it.
            int st = Native.NotificationState();
            bool obstruct = st == Native.QUNS_BUSY
                         || st == Native.QUNS_RUNNING_D3D_FULL_SCREEN
                         || st == Native.QUNS_PRESENTATION_MODE;
            if (!obstruct && Native.TaskbarAutoHide())
            {
                Native.RECT r;
                // When auto-hidden the bar slides off-screen; following it would put us there too.
                if (Native.GetWindowRect(TrayHandle, out r) && r.Top >= Screen.PrimaryScreen.Bounds.Bottom - 4)
                    obstruct = true;
            }
            if (obstruct == _visible) SetVisible(!obstruct);

            if (_tier == 2 && Environment.TickCount > _tier1RetryAt) TryReturnToTier1();
        }

        private void SetVisible(bool v)
        {
            if (_visible == v) return;
            _visible = v;
            Native.ShowWindow(Handle, v ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
            if (!v) StopRenderTimer();
            else Render();
            Log.Info("strip: " + (v ? "shown" : "hidden"));
        }

        // ---- z-order -------------------------------------------------------------------------

        private void Reassert(bool force)
        {
            if (!_visible || TrayHandle == IntPtr.Zero) return;
            if (!force && Native.IsAbove(Handle, TrayHandle)) return;

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE
                | Native.SWP_NOSENDCHANGING | Native.SWP_NOOWNERZORDER);

            if (_tier != 1) return;

            long now = Environment.TickCount;
            if (now - _buryWindowStart > 60000) { _buryWindowStart = now; _buryCount = 0; }
            _buryCount++;
            if (_buryCount >= 6)
            {
                // TIER 2: THE INSIGHT THAT MAKES THIS SAFE TO SHIP. Z-order only matters where
                // rectangles intersect. Floating just above the taskbar means we no longer overlap
                // Shell_TrayWnd at all, so explorer re-topmosting it becomes harmless -- the fight
                // cannot happen rather than being won repeatedly.
                Log.Warn("strip: buried " + _buryCount + "x in 60s -- switching to float tier");
                _tier = 2;
                _tier1RetryAt = now + _tier1BackoffMin * 60000L;
                _buryCount = 0;
                Rescan(true);
                Render();
            }
        }

        private void TryReturnToTier1()
        {
            Log.Info("strip: retrying overlay tier");
            _tier = 1;
            _buryCount = 0;
            _buryWindowStart = Environment.TickCount;
            _tier1BackoffMin = Math.Min(240, _tier1BackoffMin * 2);
            _tier1RetryAt = Environment.TickCount + _tier1BackoffMin * 60000L;
            Rescan(true);
            Render();
        }

        // ---- render --------------------------------------------------------------------------

        /// <summary>
        /// DPI scale times the operator's size multiplier, clamped to what will actually fit.
        ///
        /// Two independent ceilings, and both have to hold or the slider becomes a trap:
        ///   HEIGHT -- the glyph box must fit inside the taskbar (48px on this machine).
        ///   RUNWAY -- Full tier must still fit the free span. Without this one, dragging the size
        ///             slider UP eventually demotes the layout to Compact, so the icons visibly get
        ///             SMALLER and the desk cells shrink -- the exact opposite of the gesture.
        ///
        /// The clamped result is published so the menu can show that it clamped, rather than
        /// looking like a slider that quietly stopped responding near the top of its travel.
        /// </summary>
        private double RenderScale()
        {
            double dpiSc = _dpi / 96.0;
            return dpiSc * UiSettings.EffectiveScale(dpiSc, _span == null ? 0 : _span.Width, _stripH);
        }

        /// <summary>
        /// Re-pick the tier and repaint after a settings change. Deliberately does NOT re-measure
        /// the taskbar: a slider drag fires dozens of these and Rescan(true) costs a ~100ms
        /// PrintWindow of the whole taskbar, which would make dragging feel like sludge. Nothing
        /// about the taskbar moved -- only what we intend to draw into it.
        /// </summary>
        public void Relayout()
        {
            if (_disposed) return;
            if (_span == null) { Rescan(true); Render(); return; }
            LayoutTier t = ChooseTier(_span.Width, RenderScale());
            if (t != IconRenderer.Tier) IconRenderer.Tier = t;
            Render();
        }

        /// <summary>
        /// Auto-fit unless the operator pinned a tier. Pinning is deliberate: "largest that fits" is
        /// the right default and the wrong answer when he wants the compact layout on a wide bar.
        /// </summary>
        private LayoutTier ChooseTier(int spanW, double sc)
        {
            switch (UiSettings.TierMode)
            {
                case UiSettings.TierFull: return LayoutTier.Full;
                case UiSettings.TierCompact: return LayoutTier.Compact;
                case UiSettings.TierMicro: return LayoutTier.Micro;
                default: return IconRenderer.PickTier(spanW, sc, IconRenderer.Tier);
            }
        }

        /// <summary>
        /// Where the strip sits inside the taskbar. Anchor is the choice, offset nudges from there.
        /// Clamped to the tray's own bounds rather than to the free span -- he asked to be able to
        /// position it, so moving it over the task buttons is allowed if that is what he wants, but
        /// moving it off-screen never is.
        /// </summary>
        private int AnchorX(int w)
        {
            int baseX;
            switch (UiSettings.Anchor)
            {
                case UiSettings.AnchorCentre: baseX = _span.Left + (_span.Width - w) / 2; break;
                case UiSettings.AnchorRight: baseX = _span.Right - w; break;
                default: baseX = _span.Left; break;
            }
            int x = baseX + UiSettings.OffsetX;
            if (x + w > _tray.Right) x = _tray.Right - w;
            if (x < _tray.Left) x = _tray.Left;
            return x;
        }

        public void Render()
        {
            if (_disposed || _span == null || !_visible) return;
            try
            {
                double sc = RenderScale();
                long now = Environment.TickCount;

                _cells = IconRenderer.Layout(_span, _stripH, sc, IconRenderer.Tier);
                for (int i = 0; i < _cells.Count; i++) Animator.NoteState(_cells[i].Key, _cells[i].State, now);

                int w = IconRenderer.WidthFor(IconRenderer.Tier, sc);
                if (w > _span.Width) w = _span.Width;
                if (w < 8) return;

                int x = AnchorX(w);
                int y = _tray.Top + _yOffset + UiSettings.OffsetY;
                // Never let a saved offset park the strip off-screen. A position setting that can
                // hide the strip is a setting the operator cannot get back to in order to undo it.
                if (y + _stripH > _tray.Bottom) y = _tray.Bottom - _stripH;
                if (y < _tray.Top - 200) y = _tray.Top - 200;
                _originX = x; _originY = y;

                using (Bitmap bmp = new Bitmap(w, _stripH, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        IconRenderer.Paint(g, _cells, w, _stripH, sc, _hoverKey, now);
                    PushLayered(bmp, x, y, w, _stripH);
                }

                SyncRenderTimer(now);
            }
            catch (Exception ex) { Log.Err("strip: render", ex); }
        }

        private void PushLayered(Bitmap bmp, int x, int y, int w, int h)
        {
            IntPtr screenDC = Native.GetDC(IntPtr.Zero);
            IntPtr memDC = Native.CreateCompatibleDC(screenDC);
            IntPtr hBitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                // Format32bppPArgb is ALREADY premultiplied, which is exactly what
                // UpdateLayeredWindow wants -- passing straight ARGB here produces dark fringing
                // around every antialiased glyph edge.
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDC, hBitmap);

                Native.SIZE size = new Native.SIZE(w, h);
                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(x, y);
                Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
                bf.BlendOp = Native.AC_SRC_OVER;
                bf.BlendFlags = 0;
                // Per-pixel alpha from the bitmap is scaled by this, so one byte dims the whole
                // strip uniformly -- glyphs, rings, badges and the budget arc together -- at no
                // rendering cost. Same lever as the marquee's --opacity.
                bf.SourceConstantAlpha = UiSettings.AlphaByte();
                bf.AlphaFormat = Native.AC_SRC_ALPHA;

                Native.UpdateLayeredWindow(Handle, screenDC, ref dst, ref size, memDC, ref src, 0,
                    ref bf, Native.ULW_ALPHA);
            }
            finally
            {
                // Every path, every frame. This runs up to 30x/second while something animates;
                // leaking one HBITMAP per frame exhausts the 10,000-object GDI quota in minutes.
                if (old != IntPtr.Zero) Native.SelectObject(memDC, old);
                if (hBitmap != IntPtr.Zero) Native.DeleteObject(hBitmap);
                if (memDC != IntPtr.Zero) Native.DeleteDC(memDC);
                if (screenDC != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        /// <summary>
        /// The render timer runs ONLY while something is actually animating. At rest this process
        /// costs zero render wakeups and three cheap timers -- which is the difference between an
        /// ambient tool and a battery complaint.
        /// </summary>
        private void SyncRenderTimer(long now)
        {
            bool want = false;
            for (int i = 0; i < _cells.Count && !want; i++)
                if (Animator.IsAnimating(_cells[i].Key, _cells[i].State, now)) want = true;
            if (!_visible) want = false;

            if (want && !_renderTimerOn)
            {
                Native.SetTimer(Handle, (IntPtr)Native.TIMER_RENDER, 33, IntPtr.Zero);
                _renderTimerOn = true;
            }
            else if (!want && _renderTimerOn)
            {
                StopRenderTimer();
            }
        }

        private void StopRenderTimer()
        {
            if (!_renderTimerOn) return;
            Native.KillTimer(Handle, (IntPtr)Native.TIMER_RENDER);
            _renderTimerOn = false;
        }

        // ---- input ---------------------------------------------------------------------------

        private void OnMouseMove(int x, int y)
        {
            if (!_tracking)
            {
                Native.TRACKMOUSEEVENT tme = new Native.TRACKMOUSEEVENT();
                tme.cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT));
                tme.dwFlags = Native.TME_LEAVE;
                tme.hwndTrack = Handle;
                tme.dwHoverTime = 0;
                Native.TrackMouseEvent(ref tme);
                _tracking = true;
            }
            Cell c = HitTest(x, y);
            string k = c == null ? null : c.Key;
            if (k != _hoverKey)
            {
                _hoverKey = k;
                Render();
                Tooltip.Show(c, _span, _tray, _yOffset);
            }
        }

        private void OnClick(int x, int y, bool right)
        {
            Cell c = HitTest(x, y);
            if (c == null) return;
            Action<Cell, bool> h = CellClicked;
            if (h != null) h(c, right);
        }

        private Cell HitTest(int x, int y)
        {
            for (int i = 0; i < _cells.Count; i++)
                if (_cells[i].Bounds.Contains(x, y)) return _cells[i];
            return null;
        }

        private static int LoWord(IntPtr p) { return unchecked((short)(long)p); }
        private static int HiWord(IntPtr p) { return unchecked((short)((long)p >> 16)); }

        // ---- teardown ------------------------------------------------------------------------

        private void Cleanup()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                StopRenderTimer();
                Native.KillTimer(Handle, (IntPtr)Native.TIMER_ZORDER);
                Native.KillTimer(Handle, (IntPtr)Native.TIMER_GEOM);
                Native.KillTimer(Handle, (IntPtr)Native.TIMER_STATE);
                if (_hookFg != IntPtr.Zero) Native.UnhookWinEvent(_hookFg);
                if (_hookReorder != IntPtr.Zero) Native.UnhookWinEvent(_hookReorder);
                Native.TryUnregisterSessionNotification(Handle);
            }
            catch { }
        }

        public void Dispose()
        {
            Cleanup();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
