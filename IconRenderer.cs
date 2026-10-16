// IconRenderer.cs -- draw the strip into a premultiplied ARGB surface for UpdateLayeredWindow.
//
// Six cells: [Command Center] [Sell] [Attract] [Capital] [Machine] [asks]. NOT thirteen.
// workspace CLAUDE.md says the operator "should never have to ask 'what is outbound doing' by opening a
// ledger" and that Command Center hands him "one ranked list ... never raw departmental output." Thirteen
// icons is thirteen queues rendered in pixels -- it reimposes exactly what the COO layer removed,
// on the one surface he cannot look away from. Geometry agrees: the taskbar is centre-aligned, so
// ReBarWindow32's left edge slides ~22px left per opened window, and a 14-cell layout gets painted
// over after about five more windows.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CommandCenter
{
    internal class Cell
    {
        public string Key;          // "core" | desk name | "asks"
        public string Label;
        public string Glyph;
        public Rectangle Bounds;
        public IconState State;
        public int Badge;
        public string Tooltip;
    }

    internal enum LayoutTier { Full, Compact, Micro }

    internal static class IconRenderer
    {
        // 96-DPI metrics. Every one is multiplied by dpi/96 at layout time.
        public const int CORE_W_FULL = 40, CORE_GAP_FULL = 10, CELL_W_FULL = 32, DESK_GAP_FULL = 10;
        public const int CORE_W_COMPACT = 34, CORE_GAP_COMPACT = 6, CELL_W_COMPACT = 22, DESK_GAP_COMPACT = 4;
        public const int LEAD_PAD = 4;
        public const int TRAIL_PAD = 6;
        public const int GLYPH = 22;

        public static LayoutTier Tier = LayoutTier.Full;

        // Segoe Fluent Icons codepoints, verified present at C:\Windows\Fonts\SegoeIcons.ttf.
        private const string GLYPH_CORE    = "\uE9A9";  // Globe-ish ring -- the orchestrator
        private const string GLYPH_SELL    = "\uE8EC";  // Megaphone
        private const string GLYPH_ATTRACT = "\uE774";  // Globe
        private const string GLYPH_CAPITAL = "\uE825";  // Bank / money
        private const string GLYPH_MACHINE = "\uE950";  // Processor
        private const string GLYPH_ASKS    = "\uE7E7";  // Flag / raised hand

        /// <summary>
        /// Total pixel width of a tier, INCLUDING the leading pad the layout actually starts at and
        /// a trailing pad for the badge, which overhangs its cell by 2px. Omitting those clipped
        /// the last cell -- the asks badge was visibly sliced off the right edge.
        /// </summary>
        public static int WidthFor(LayoutTier t, double s)
        {
            switch (t)
            {
                case LayoutTier.Full:
                    return Sc(LEAD_PAD + CORE_W_FULL + 5 * CELL_W_FULL + TRAIL_PAD, s)
                           + CoreGapPx(t, s) + 4 * DeskGapPx(t, s);
                case LayoutTier.Compact:
                    return Sc(LEAD_PAD + CORE_W_COMPACT + 5 * CELL_W_COMPACT + TRAIL_PAD, s)
                           + CoreGapPx(t, s) + 4 * DeskGapPx(t, s);
                default:
                    return Sc(LEAD_PAD + CORE_W_COMPACT + TRAIL_PAD, s);
            }
        }

        // The gaps carry the user's spacing multiplier. Both WidthFor and Layout MUST route through
        // these two -- they were a single inline expression each until the multiplier arrived, and
        // if the two ever disagree by a pixel the tier picked no longer matches the layout drawn,
        // which shows up as the asks badge sliced off the right edge rather than as a crash.
        public static int CoreGapPx(LayoutTier t, double s)
        {
            int b = t == LayoutTier.Full ? CORE_GAP_FULL : CORE_GAP_COMPACT;
            return (int)Math.Round(b * s * UiSettings.Gap, MidpointRounding.AwayFromZero);
        }

        public static int DeskGapPx(LayoutTier t, double s)
        {
            int b = t == LayoutTier.Full ? DESK_GAP_FULL : DESK_GAP_COMPACT;
            return (int)Math.Round(b * s * UiSettings.Gap, MidpointRounding.AwayFromZero);
        }

        private static int Sc(int v, double s)
        {
            return (int)Math.Round(v * s, MidpointRounding.AwayFromZero);
        }

        /// <summary>Largest tier that fits, with 6% hysteresis on promotion to stop oscillation.</summary>
        public static LayoutTier PickTier(int spanW, double s, LayoutTier current)
        {
            LayoutTier[] order = new LayoutTier[] { LayoutTier.Full, LayoutTier.Compact, LayoutTier.Micro };
            for (int i = 0; i < order.Length; i++)
            {
                int need = WidthFor(order[i], s);
                bool promoting = (int)order[i] < (int)current;
                if (spanW >= (promoting ? need * 1.06 : need)) return order[i];
            }
            return LayoutTier.Micro;
        }

        public static List<Cell> Layout(Span span, int height, double s, LayoutTier tier)
        {
            List<Cell> cells = new List<Cell>();
            int haloW = Sc(tier == LayoutTier.Full ? CORE_W_FULL : CORE_W_COMPACT, s);
            int haloGap = CoreGapPx(tier, s);
            int cellW = Sc(tier == LayoutTier.Full ? CELL_W_FULL : CELL_W_COMPACT, s);
            int gap = DeskGapPx(tier, s);
            int x = Sc(LEAD_PAD, s);

            Cell halo = new Cell();
            halo.Key = "core"; halo.Label = "Command Center"; halo.Glyph = GLYPH_CORE;
            halo.Bounds = new Rectangle(x, 0, haloW, height);
            halo.State = StateStore.CoreState();
            halo.Badge = StateStore.TotalOpenAsks;
            halo.Tooltip = CoreTooltip();
            cells.Add(halo);
            x += haloW;

            if (tier == LayoutTier.Micro) return cells;
            x += haloGap;

            // THE SALIENCE BUDGET, D3: AT MOST ONE CELL MAY BE LIT AT A TIME.
            // He already has 33 open items. Four blue desks is four things shouting equally, which
            // is the same as nothing shouting -- and it is exactly the "raw departmental output"
            // workspace CLAUDE.md forbids Command Center from handing him. The single most urgent desk is lit; the
            // rest fall back to their next-worst officer state, and the total rides as a numeral on
            // the Command Center icon. One ranked list, in pixels.
            string lit = MostUrgentDesk();

            string[] glyphs = new string[] { GLYPH_SELL, GLYPH_ATTRACT, GLYPH_CAPITAL, GLYPH_MACHINE };
            for (int i = 0; i < StateStore.Desks.Count && i < 4; i++)
            {
                DeskState d = StateStore.Desks[i];
                Cell c = new Cell();
                c.Key = d.Name; c.Label = d.Name; c.Glyph = glyphs[i];
                c.Bounds = new Rectangle(x, 0, cellW, height);
                c.State = (d.State == IconState.Attention && d.Name != lit)
                          ? DemoteAttention(d) : d.State;
                // A BADGE MEANS "THIS NEEDS YOU", NOTHING ELSE, and it counts ASKS -- not officers.
                // The first render put a count on every desk (4/3/3/3) because it counted non-idle
                // officers, so four blue badges appeared with nothing actually pending: exactly the
                // wallpaper failure the salience budget exists to prevent. It also read d.State
                // rather than the demoted c.State, so a desk that had just been quieted still wore
                // a badge. Routine due/never counts live in the tooltip.
                c.Badge = (c.State == IconState.Attention || c.State == IconState.Fault)
                          ? DeskAskCount(d) : 0;
                c.Tooltip = DeskTooltip(d);
                cells.Add(c);
                x += cellW + gap;
            }

            Cell asks = new Cell();
            asks.Key = "asks"; asks.Label = "Asks"; asks.Glyph = GLYPH_ASKS;
            asks.Bounds = new Rectangle(x, 0, cellW, height);
            asks.State = StateStore.TotalOpenAsks > 0 ? IconState.Attention : IconState.Idle;
            asks.Badge = StateStore.TotalOpenAsks;
            asks.Tooltip = StateStore.TotalOpenAsks == 0
                ? "No decisions waiting on you"
                : StateStore.TotalOpenAsks + " decision(s) waiting on you";
            cells.Add(asks);

            return cells;
        }

        private static int DeskAskCount(DeskState d)
        {
            int n = 0;
            for (int i = 0; i < d.Officers.Count; i++) n += d.Officers[i].OpenAsks;
            return n;
        }

        /// <summary>
        /// Which single desk earns the lit state: fault outranks attention, then the desk holding
        /// the most urgent ask, ties broken by the oldest ask. Deterministic, so the lit cell does
        /// not hop around between renders.
        /// </summary>
        private static string MostUrgentDesk()
        {
            string best = null;
            int bestScore = -1;
            DateTime bestAge = DateTime.MaxValue;

            for (int i = 0; i < StateStore.Desks.Count; i++)
            {
                DeskState d = StateStore.Desks[i];
                if (d.State == IconState.Fault) return d.Name;   // a broken officer always wins
                if (d.State != IconState.Attention) continue;

                for (int k = 0; k < d.Officers.Count; k++)
                {
                    List<AskRecord> asks = Asks.Open(d.Officers[k].Officer.Name);
                    for (int j = 0; j < asks.Count; j++)
                    {
                        int score = asks[j].Urgency == "high" ? 2 : (asks[j].Urgency == "normal" ? 1 : 0);
                        if (score > bestScore || (score == bestScore && asks[j].RaisedAt < bestAge))
                        { bestScore = score; bestAge = asks[j].RaisedAt; best = d.Name; }
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// A desk that has an ask but is not the lit one still must not read as healthy. Show its
        /// worst NON-attention officer state, floored at `due` -- something is pending there.
        /// </summary>
        private static IconState DemoteAttention(DeskState d)
        {
            IconState worst = IconState.Due;
            for (int i = 0; i < d.Officers.Count; i++)
            {
                IconState s = d.Officers[i].State;
                if (s == IconState.Attention) continue;
                if (StateStore.Severity(s) > StateStore.Severity(worst)) worst = s;
            }
            return worst;
        }

        private static string CoreTooltip()
        {
            if (StateStore.IsStoodDown())
                return "Command Center -- account usage limit, resets " + (StateStore.LimitUntilRaw == null
                    ? StateStore.LimitUntil.ToString("t") : StateStore.LimitUntilRaw);
            if (StateStore.SuiteDrift) return "Command Center -- loop suite reports DRIFT";
            string mode = StateStore.Paused ? "paused" : "running";
            string budget = StateStore.BudgetUsed < 0 ? "" :
                string.Format("  |  sends {0}/{1}", StateStore.BudgetUsed, StateStore.BudgetCeiling);
            return string.Format("Command Center -- {0}, {1} in flight{2}",
                mode, Supervisor.InFlight(), budget);
        }

        private static string DeskTooltip(DeskState d)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(d.Name);
            for (int i = 0; i < d.Officers.Count; i++)
            {
                OfficerState o = d.Officers[i];
                sb.Append("\n  ");
                sb.Append(o.Officer.Name.PadRight(9));
                sb.Append(StateName(o.State).PadRight(10));
                sb.Append(o.AgeText());
                if (o.LastRow != null)
                {
                    string r = o.LastRow;
                    if (r.Length > 52) r = r.Substring(0, 50) + "..";
                    sb.Append("  " + r);
                }
            }
            return sb.ToString();
        }

        public static string StateName(IconState s)
        {
            switch (s)
            {
                case IconState.Never: return "never";
                case IconState.Idle: return "idle";
                case IconState.Due: return "due";
                case IconState.Working: return "working";
                case IconState.Attention: return "attention";
                default: return "fault";
            }
        }

        // ---- painting ------------------------------------------------------------------------

        /// <summary>
        /// Draw ONE mark into an arbitrary rectangle. The strip's Paint loop and the -IconSheet
        /// contact sheet both route through this, so the sheet cannot drift from what the taskbar
        /// actually shows -- a preview that renders differently from the real thing is worse than
        /// no preview, because it is trusted.
        /// </summary>
        public static void PaintOne(Graphics g, string key, Rectangle bounds, double s,
                                    Color colour, int alpha, IconState state)
        {
            SmoothingMode oldS = g.SmoothingMode;
            TextRenderingHint oldT = g.TextRenderingHint;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int glyphPx = Sc(GLYPH, s);
            using (Font f = new Font("Segoe Fluent Icons", glyphPx * 0.62f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                RectangleF rf = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);

                Cell tmp = new Cell();
                tmp.Key = key;
                tmp.Bounds = bounds;
                tmp.State = state;

                BrandMarks.Draw(g, key, bounds, s, state, colour, alpha);
                if (state == IconState.Due) DrawDeskTick(g, tmp, s);
            }

            g.SmoothingMode = oldS;
            g.TextRenderingHint = oldT;
        }

        /// <summary>The Fluent codepoint for a cell key. One table, used by layout and by preview.</summary>
        public static string GlyphFor(string key)
        {
            switch (key)
            {
                case "core": return GLYPH_CORE;
                case "Sell": return GLYPH_SELL;
                case "Attract": return GLYPH_ATTRACT;
                case "Capital": return GLYPH_CAPITAL;
                case "Machine": return GLYPH_MACHINE;
                default: return GLYPH_ASKS;
            }
        }

        public static void Paint(Graphics g, List<Cell> cells, int width, int height, double s,
                                 string hoverKey, long nowMs)
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // ONE font, and only when something needs it. The Segoe Fluent Icons face was still
            // being constructed here every frame after BrandMarks replaced the glyph pass -- a GDI
            // font handle created and destroyed 30 times a second to draw nothing. The badge font is
            // built only when a badge is actually on screen, which is almost never.
            bool needBadge = false;
            for (int i = 0; i < cells.Count && !needBadge; i++) if (cells[i].Badge > 0) needBadge = true;

            using (Font fSmall = needBadge
                       ? new Font("Segoe UI", Math.Max(8f, (float)(8 * s)), FontStyle.Bold, GraphicsUnit.Pixel)
                       : null)
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    Cell c = cells[i];
                    Color colour; int alpha;
                    Palette.For(c.State, out colour, out alpha);

                    if (c.State == IconState.Working)
                        alpha = Animator.WorkingAlpha(nowMs, i);

                    if (c.Key == hoverKey)
                    {
                        // Hover is where desk identity gets to show. The cells stay NEUTRAL at rest
                        // on purpose -- the salience budget (D3) allows exactly one lit cell, and
                        // four permanently tinted desks is the "four things shouting equally"
                        // failure that rule exists to prevent. Under the cursor there is only ever
                        // one, so the tint costs nothing and teaches which desk is which.
                        Color plate = Palette.HoverPlate();
                        if (c.Key != "core" && c.Key != "asks")
                            plate = Color.FromArgb(Theme.IsLight ? 0x26 : 0x30, Palette.DeskTint(c.Key));
                        using (SolidBrush hb = new SolidBrush(plate))
                            FillRounded(g, hb, c.Bounds, Sc(6, s));
                    }

                    if (c.State == IconState.Attention)
                        DrawGlow(g, c, s, nowMs);

                    // The mark. Full-colour brand tiles, drawn as paths -- see BrandMarks. State
                    // rides a chroma ladder plus a corner chip rather than the mark's own hue,
                    // because a brand-coloured icon has spent the colour channel that used to carry
                    // state, and this strip is a state display first.
                    BrandMarks.Draw(g, c.Key, c.Bounds, s, c.State, colour, alpha);

                    if (c.State == IconState.Due) DrawDeskTick(g, c, s);
                    if (c.Badge > 0) DrawBadge(g, c, fSmall, s);
                    if (c.Key == "core" && StateStore.BudgetUsed >= 0) DrawBudgetArc(g, c, s);
                }
            }
        }

        /// <summary>
        /// The Command Center mark: an open ring with a solid core. Drawn rather than typeset so it reads as
        /// the orchestrator and not as another department, and so the budget arc has a ring to sit
        /// concentric with.
        /// </summary>
        private static void DrawCoreMark(Graphics g, Cell c, double s, Color colour, int alpha)
        {
            int d = (int)Math.Round(17 * s, MidpointRounding.AwayFromZero);
            Rectangle ring = new Rectangle(
                c.Bounds.X + (c.Bounds.Width - d) / 2,
                c.Bounds.Y + (c.Bounds.Height - d) / 2, d, d);

            float pw = Math.Max(1.6f, (float)(1.8 * s));

            // Contact shadow, same rule and same reason as the glyphs.
            if (s >= 0.9 && alpha > 60)
            {
                using (Pen sp = new Pen(Color.FromArgb(
                           (int)(alpha * (Theme.IsLight ? 0.16 : 0.26)), Color.Black), pw))
                {
                    Rectangle sr = ring;
                    sr.Offset(0, Math.Max(1, (int)Math.Round(1.0 * s)));
                    g.DrawEllipse(sp, sr);
                }
            }

            // The ring, lit from above.
            Color rTop = Theme.IsLight ? colour : Shade(colour, 0.35);
            Color rBot = Theme.IsLight ? Shade(colour, -0.30) : colour;
            using (LinearGradientBrush lg = new LinearGradientBrush(
                       new Rectangle(ring.X, ring.Y, Math.Max(1, ring.Width), Math.Max(1, ring.Height)),
                       Color.FromArgb(alpha, rTop), Color.FromArgb(alpha, rBot),
                       LinearGradientMode.Vertical))
            using (Pen p = new Pen(lg, pw))
                g.DrawEllipse(p, ring);

            int cd = (int)Math.Round(6 * s, MidpointRounding.AwayFromZero);
            Rectangle core = new Rectangle(
                c.Bounds.X + (c.Bounds.Width - cd) / 2,
                c.Bounds.Y + (c.Bounds.Height - cd) / 2, cd, cd);

            // A radial highlight offset up-left reads as a sphere rather than a dot. Below ~4px
            // there are not enough pixels to carry it and it just looks like noise, so fall back.
            if (cd >= 4)
            {
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(core);
                    using (PathGradientBrush pg = new PathGradientBrush(gp))
                    {
                        pg.CenterPoint = new PointF(core.X + core.Width * 0.35f,
                                                    core.Y + core.Height * 0.32f);
                        pg.CenterColor = Color.FromArgb(alpha, Shade(colour, 0.55));
                        pg.SurroundColors = new Color[] { Color.FromArgb(alpha, colour) };
                        g.FillEllipse(pg, core);
                    }
                }
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, colour)))
                    g.FillEllipse(b, core);
            }
        }

        /// <summary>Move a colour toward white (t &gt; 0) or black (t &lt; 0).</summary>
        private static Color Shade(Color c, double t)
        {
            if (t >= 0)
                return Color.FromArgb(c.A,
                    (int)Math.Min(255, c.R + (255 - c.R) * t),
                    (int)Math.Min(255, c.G + (255 - c.G) * t),
                    (int)Math.Min(255, c.B + (255 - c.B) * t));
            double k = Math.Max(0.0, 1.0 + t);
            return Color.FromArgb(c.A, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
        }

        /// <summary>
        /// The glyph, with depth. Two passes:
        ///
        ///   1. A contact shadow one pixel below. That separation from the taskbar behind it is
        ///      most of what reads as "quality" rather than "a character in a font". Skipped below
        ///      0.9x, where at these stroke weights it only muddies the form.
        ///   2. A vertical gradient fill.
        ///
        /// THE RULE FOR THE GRADIENT: it may only ever move AWAY from the background -- darker on a
        /// light taskbar, lighter on a dark one. Palette's colours were contrast-measured against
        /// the real taskbar (idle 3.0:1, due 5.5:1, fault 5.3:1) and a gradient that brightened a
        /// dark glyph on a light bar would look better in isolation while failing the one test that
        /// decides whether the operator can read the strip at a glance.
        /// </summary>
        private static void DrawGlyph(Graphics g, string glyph, Font f, StringFormat sf,
                                      RectangleF rf, Rectangle bounds, Color colour, int alpha, double s)
        {
            if (s >= 0.9 && alpha > 60)
            {
                int sa = (int)Math.Min(255, alpha * (Theme.IsLight ? 0.18 : 0.28));
                using (SolidBrush shb = new SolidBrush(Color.FromArgb(sa, Color.Black)))
                {
                    RectangleF sr = rf;
                    sr.Offset(0, Math.Max(1f, (float)s));
                    g.DrawString(glyph, f, shb, sr, sf);
                }
            }

            Color top = Theme.IsLight ? colour : Shade(colour, 0.30);
            Color bot = Theme.IsLight ? Shade(colour, -0.28) : colour;

            using (LinearGradientBrush lg = new LinearGradientBrush(
                       new Rectangle(bounds.X, bounds.Y,
                                     Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)),
                       Color.FromArgb(alpha, top), Color.FromArgb(alpha, bot),
                       LinearGradientMode.Vertical))
                g.DrawString(glyph, f, lg, rf, sf);
        }

        private static void DrawGlow(Graphics g, Cell c, double s, long nowMs)
        {
            int r = Animator.GlowRadius(nowMs, c.Key, s);
            int a = Animator.GlowAlpha(nowMs, c.Key);
            if (r <= 0 || a <= 0) return;
            Color gc = Palette.GlowColour();
            for (int i = r; i > 0; i--)
            {
                int aa = (int)(a * (i / (double)r) * 0.30);
                if (aa <= 0) continue;
                Rectangle rr = Rectangle.Inflate(c.Bounds, -Sc(4, s) + i, -Sc(10, s) + i);
                using (Pen p = new Pen(Color.FromArgb(Math.Min(255, aa), gc), 1f))
                    DrawRounded(g, p, rr, Sc(7, s));
            }
        }

        /// <summary>
        /// The 2px desk-tinted rule under a `due` cell.
        ///
        /// It used to sit 8px off the cell bottom, which was right under a 22px font glyph and wrong
        /// under a brand tile -- it floated detached, well below the mark, reading as a separate
        /// object. It now hangs off the TILE, so it stays attached at every scale.
        /// </summary>
        private static void DrawDeskTick(Graphics g, Cell c, double s)
        {
            Color t = Palette.DeskTint(c.Key);
            int side = (int)Math.Round(24 * s);
            if (side > c.Bounds.Width - 2) side = c.Bounds.Width - 2;
            if (side > c.Bounds.Height - 4) side = c.Bounds.Height - 4;
            if (side < 8) side = 8;
            int tileBottom = c.Bounds.Y + (c.Bounds.Height - side) / 2 + side;

            int w = (int)(side * 0.62);
            int h = Math.Max(2, Sc(2, s));
            Rectangle r = new Rectangle(c.Bounds.X + (c.Bounds.Width - w) / 2,
                                        tileBottom + Math.Max(2, (int)Math.Round(2 * s)), w, h);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(210, t))) g.FillRectangle(b, r);
        }

        private static void DrawFaultRule(Graphics g, Cell c, double s, Color colour)
        {
            int w = (int)(c.Bounds.Width * 0.50);
            int h = Math.Max(2, Sc(2, s));
            Rectangle r = new Rectangle(c.Bounds.X + (c.Bounds.Width - w) / 2,
                                        c.Bounds.Bottom - Sc(8, s), w, h);
            using (SolidBrush b = new SolidBrush(colour)) g.FillRectangle(b, r);
        }

        private static void DrawBadge(Graphics g, Cell c, Font f, double s)
        {
            string txt = c.Badge > 9 ? "9+" : c.Badge.ToString();
            SizeF sz = g.MeasureString(txt, f);
            int d = (int)Math.Max(sz.Width + Sc(6, s), Sc(13, s));
            int h = Sc(13, s);
            Rectangle r = new Rectangle(c.Bounds.Right - d + Sc(2, s), c.Bounds.Y + Sc(7, s), d, h);
            using (SolidBrush b = new SolidBrush(Palette.GlowColour())) FillRounded(g, b, r, h / 2);
            using (SolidBrush w = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat())   // GDI+ object, and this runs per frame
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(txt, f, w, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
            }
        }

        /// <summary>
        /// The send-budget arc around Command Center. Red once the projected burn crosses the ceiling before
        /// the cycle ends -- which is TRUE TODAY (231/900 at ~46/day breaches 2026-08-17, eleven
        /// days early). Autonomy makes it arrive sooner, so it must be visible before it happens.
        /// </summary>
        private static void DrawBudgetArc(Graphics g, Cell c, double s)
        {
            double frac = Math.Min(1.0, StateStore.BudgetUsed / (double)Math.Max(1, StateStore.BudgetCeiling));
            bool over = StateStore.BudgetDaily >= 0 && StateStore.BudgetDaily < 30;

            int d = (int)Math.Round(27 * s, MidpointRounding.AwayFromZero);
            Rectangle rr = new Rectangle(c.Bounds.X + (c.Bounds.Width - d) / 2,
                                         c.Bounds.Y + (c.Bounds.Height - d) / 2, d, d);
            float pw = Math.Max(1.6f, (float)(1.8 * s));

            // A TRACK RING BEHIND THE ARC. Without it a 26%-full gauge renders as a lone quarter
            // stroke floating beside the mark and reads as a rendering artefact, not a measurement.
            Color track = Theme.IsLight ? Color.FromArgb(38, 0, 0, 0) : Color.FromArgb(46, 255, 255, 255);
            using (Pen tp = new Pen(track, pw)) g.DrawEllipse(tp, rr);

            Color arc = over ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Palette.GlowColour();
            using (Pen p = new Pen(Color.FromArgb(over ? 235 : 170, arc), pw))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, rr, -90f, (float)(360.0 * frac));
            }
        }

        private static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static void FillRounded(Graphics g, Brush b, Rectangle r, int radius)
        {
            using (GraphicsPath p = RoundedPath(r, radius)) g.FillPath(b, p);
        }

        private static void DrawRounded(Graphics g, Pen pen, Rectangle r, int radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = RoundedPath(r, radius)) g.DrawPath(pen, p);
        }
    }
}
