// BrandMarks.cs -- six full-colour marks, drawn as paths. Replaces the Segoe Fluent glyph pass.
//
// the operator: "they look cheap", "i want them to have color and style", "make the icons look more
// like official favicons", "i want them to be fully fledged icons".
//
// So: the modern app-tile idiom -- a rounded-square tile carrying its own brand colour with the
// symbol knocked out of it in white. Slack, Notion, Linear, Figma. Command Center stays a disc because it is
// the brand, not a department, and it must read as a different KIND of object.
//
// FLAT ON PURPOSE. An earlier draft added a full-width glossy sweep and a specular hotspot; that is
// Aqua/Web-2.0, and it is precisely what reads as *cheap* to someone who just said the icons look
// cheap. The only shading here is a shallow vertical ramp that darkens downward on a light taskbar
// and lightens upward on a dark one -- never toward the background.
//
// ---------------------------------------------------------------------------------------------
// THE PROBLEM THIS FILE HAD TO SOLVE, and the reason it is not just prettier glyphs:
//
// This strip is a STATE DISPLAY first. Until now state WAS the icon's colour. Give every mark its
// own brand colour and that channel is gone -- the strip becomes decoration.
//
// State therefore rides on a channel that is not hue: a CHROMA LADDER. Every mark is mixed toward a
// fixed neutral of known luminance by an amount that depends only on state. Full colour = alive,
// grey = quiet, and it reads across all six in one glance. Desaturating toward a KNOWN-LUMINANCE
// neutral rather than toward transparency is the load-bearing trick: it removes the hue without
// giving up contrast, so a quiet mark is still legible.
//
// A corner chip in the state colour is the redundant second channel, for the states where chroma
// alone is ambiguous.
// ---------------------------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CommandCenter
{
    internal static class BrandMarks
    {
        /// <summary>
        /// A brand pair. Body is the tile; Edge is the rim and the bottom of the ramp. They are a
        /// VALUE split, not a hue step -- two hues at the same lightness read as one flat colour at
        /// 22px, which is what killed the first draft's Sell (green over teal) and Asks (rose over
        /// rose).
        /// </summary>
        private sealed class Brand
        {
            public readonly Color Body;
            public readonly Color Edge;
            public Brand(Color body, Color edge) { Body = body; Edge = edge; }
        }

        // Contrast of each Body against the light taskbar (luminance 224) is asserted by
        // Program.ContrastCheck at every -Selftest, not by this comment. The first draft carried
        // hand-written figures that were 0.06 optimistic and a gate that never read them.
        private static readonly Brand B_CORE    = new Brand(Color.FromArgb(0x43, 0x38, 0xCA), Color.FromArgb(0x2A, 0x22, 0x8C));
        private static readonly Brand B_SELL    = new Brand(Color.FromArgb(0x05, 0x76, 0x4E), Color.FromArgb(0x03, 0x4A, 0x31));
        private static readonly Brand B_ATTRACT = new Brand(Color.FromArgb(0x6D, 0x28, 0xD9), Color.FromArgb(0x47, 0x15, 0x93));
        private static readonly Brand B_CAPITAL = new Brand(Color.FromArgb(0x92, 0x57, 0x09), Color.FromArgb(0x5E, 0x37, 0x04));
        private static readonly Brand B_MACHINE = new Brand(Color.FromArgb(0x3F, 0x4E, 0x63), Color.FromArgb(0x27, 0x31, 0x3F));
        private static readonly Brand B_ASKS    = new Brand(Color.FromArgb(0xB0, 0x13, 0x53), Color.FromArgb(0x74, 0x0B, 0x37));

        public static Color BodyOf(string key) { return BrandOf(key).Body; }

        private static Brand BrandOf(string key)
        {
            switch (key)
            {
                case "core": return B_CORE;
                case "Sell": return B_SELL;
                case "Attract": return B_ATTRACT;
                case "Capital": return B_CAPITAL;
                case "Machine": return B_MACHINE;
                default: return B_ASKS;
            }
        }

        /// <summary>Every key this file can draw. Used by the contact sheet and the contrast gate.</summary>
        public static readonly string[] Keys =
            new string[] { "core", "Sell", "Attract", "Capital", "Machine", "asks" };

        // ---- state -> how much colour ----------------------------------------------------------

        /// <summary>
        /// Fraction of full brand chroma, by state. STATE IS PASSED IN, never inferred.
        ///
        /// The first draft reverse-engineered state out of the alpha it was handed, and got it
        /// wrong: its light-theme thresholds mapped never(145) and idle(190) to the same rung, so
        /// two of six states rendered pixel-identical -- and `never` is 8 of 13 officers today. It
        /// broke the moment Palette's idle alpha was retuned 170 -> 190, which had already happened
        /// once. The caller has IconState in hand; taking it deletes the whole class of bug.
        /// </summary>
        private static double Chroma(IconState s)
        {
            switch (s)
            {
                case IconState.Never: return 0.06;   // barely there: has not run once
                case IconState.Idle: return 0.30;
                case IconState.Due: return 0.68;
                case IconState.Working: return 1.00;
                case IconState.Attention: return 1.00;
                default: return 0.16;                // Fault: near-grey body, red chip carries it
            }
        }

        private static int BodyAlpha(IconState s)
        {
            switch (s)
            {
                case IconState.Never: return 130;
                case IconState.Idle: return 210;
                case IconState.Due: return 240;
                default: return 255;
            }
        }

        /// <summary>
        /// The neutral every quiet mark is mixed toward. Chosen for LUMINANCE, not for taste: it has
        /// to stay clear of the taskbar it sits on, so a desaturated mark loses its hue and keeps
        /// its contrast.
        /// </summary>
        private static Color Neutral()
        {
            return Theme.IsLight ? Color.FromArgb(0x44, 0x48, 0x50) : Color.FromArgb(0xCB, 0xD1, 0xDA);
        }

        private static Color Mix(Color a, Color b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>Brand colour after the state ladder has taken its chroma away.</summary>
        public static Color Toned(Color brand, IconState s)
        {
            return Mix(Neutral(), brand, Chroma(s));
        }

        // ---- geometry --------------------------------------------------------------------------

        /// <summary>
        /// The square the mark is drawn in, clamped to BOTH the cell and the taskbar height, then
        /// centred. Everything else in this file is a fraction of it, so every silhouette is
        /// resolution-independent and the 0.80x..1.91x range needs no special cases.
        /// </summary>
        private static Rectangle MarkBox(Rectangle b, double s)
        {
            int side = (int)Math.Round(24 * s);
            if (side > b.Width - 2) side = b.Width - 2;
            if (side > b.Height - 4) side = b.Height - 4;
            if (side < 8) side = 8;
            return new Rectangle(b.X + (b.Width - side) / 2, b.Y + (b.Height - side) / 2, side, side);
        }

        private static Rectangle FRect(Rectangle k, double x, double y, double w, double h)
        {
            return new Rectangle(
                k.X + (int)Math.Round(k.Width * x), k.Y + (int)Math.Round(k.Height * y),
                Math.Max(1, (int)Math.Round(k.Width * w)), Math.Max(1, (int)Math.Round(k.Height * h)));
        }

        private static PointF FPt(Rectangle k, double x, double y)
        {
            return new PointF((float)(k.X + k.Width * x), (float)(k.Y + k.Height * y));
        }

        // ---- the one family pass ---------------------------------------------------------------

        /// <summary>
        /// Draw one mark. This is the only entry point; Paint() and PaintOne() both route here so
        /// the taskbar and the -IconSheet contact sheet cannot diverge.
        /// </summary>
        public static void Draw(Graphics g, string key, Rectangle bounds, double s,
                                IconState state, Color stateColour, int chipAlpha)
        {
            Rectangle k = MarkBox(bounds, s);
            Brand br = BrandOf(key);
            Color body = Toned(br.Body, state);
            Color edge = Toned(br.Edge, state);
            int a = BodyAlpha(state);

            // Contact shadow. Separation from the bar is most of what reads as "made" rather than
            // "typed"; skipped when tiny, where it only muddies the form.
            if (k.Width >= 18 && Theme.IsLight)
            {
                // ONE pixel down, never more. The first render offset it by `s`, which at 1.5x is
                // 2px of hard black sitting proud of the tile -- it read as a printing error rather
                // than a shadow. A contact shadow is a hairline or it is a mistake.
                Rectangle sh = k; sh.Offset(0, 1);
                using (GraphicsPath sp = TilePath(key, sh))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb((int)(a * 0.13), Color.Black)))
                    g.FillPath(sb, sp);
            }

            using (GraphicsPath tile = TilePath(key, k))
            {
                // Shallow vertical ramp. ONLY away from the background: darker downward on a light
                // bar, lighter upward on a dark one. The rule is IconRenderer's and it is the one
                // that keeps measured contrast honest.
                Color top = Theme.IsLight ? body : Mix(body, Color.White, 0.22);
                Color bot = Theme.IsLight ? Mix(body, edge, 0.85) : body;
                using (LinearGradientBrush lg = new LinearGradientBrush(
                           new Rectangle(k.X, k.Y - 1, Math.Max(1, k.Width), Math.Max(1, k.Height + 2)),
                           Color.FromArgb(a, top), Color.FromArgb(a, bot), LinearGradientMode.Vertical))
                    g.FillPath(lg, tile);

                // Interior detail BEFORE the rim. The first draft stroked the rim first and then
                // painted symbol shapes over it, so four of six marks had their outline erased
                // exactly where the symbol met the edge.
                //
                // CLIPPED TO THE TILE. Symbols draw arcs and polygons sized in fractions of the
                // box, and any of them can run past the rounded corner -- Attract's outermost
                // broadcast arc did exactly that, sweeping across the taskbar outside its own icon.
                // Clipping is one call and it makes every future symbol safe by construction.
                GraphicsState st = g.Save();
                g.SetClip(tile, CombineMode.Intersect);
                DrawSymbol(g, key, k, a, state);
                g.Restore(st);

                using (Pen rp = new Pen(Color.FromArgb((int)(a * 0.92), edge), Math.Max(1f, (float)(1.1 * s))))
                    g.DrawPath(rp, tile);
            }

            DrawChip(g, k, s, state, stateColour, chipAlpha, key == "core");
        }

        /// <summary>
        /// The container. Command Center is a disc; the four desks and asks are rounded squares. One shape
        /// difference does more to say "this one is not a department" than any amount of styling.
        /// </summary>
        private static GraphicsPath TilePath(string key, Rectangle k)
        {
            GraphicsPath p = new GraphicsPath();
            if (key == "core") { p.AddEllipse(k); return p; }
            int r = Math.Max(2, (int)Math.Round(k.Width * 0.26));
            int d = r * 2;
            if (k.Width <= d || k.Height <= d) { p.AddRectangle(k); return p; }
            p.AddArc(k.X, k.Y, d, d, 180, 90);
            p.AddArc(k.Right - d, k.Y, d, d, 270, 90);
            p.AddArc(k.Right - d, k.Bottom - d, d, d, 0, 90);
            p.AddArc(k.X, k.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// The knock-out. White on a light bar; on a dark bar the tile is already light-on-dark, so
        /// the symbol is knocked out in a deep tone instead of white to keep the contrast direction
        /// consistent.
        /// </summary>
        private static Color Knock(int a)
        {
            return Theme.IsLight
                ? Color.FromArgb((int)(a * 0.96), Color.White)
                : Color.FromArgb((int)(a * 0.96), Color.FromArgb(0x14, 0x18, 0x1F));
        }

        private static void DrawSymbol(Graphics g, string key, Rectangle k, int a, IconState state)
        {
            using (SolidBrush b = new SolidBrush(Knock(a)))
            {
                switch (key)
                {
                    case "core": CoreMark(g, k, b, a); break;
                    case "Sell": Sell(g, k, b); break;
                    case "Attract": Attract(g, k, b); break;
                    case "Capital": Capital(g, k, b); break;
                    case "Machine": Machine(g, k, b); break;
                    default: Asks(g, k, b); break;
                }
            }
        }

        // ---- the six symbols -------------------------------------------------------------------

        /// <summary>Command Center: a ring with a solid core -- the orchestrator, knocked out of the disc.</summary>
        private static void CoreMark(Graphics g, Rectangle k, SolidBrush b, int a)
        {
            Rectangle ring = FRect(k, 0.24, 0.24, 0.52, 0.52);
            float w = Math.Max(1.4f, k.Width * 0.085f);
            using (Pen p = new Pen(b.Color, w)) g.DrawEllipse(p, ring);
            Rectangle core = FRect(k, 0.42, 0.42, 0.16, 0.16);
            if (core.Width >= 2) g.FillEllipse(b, core);
        }

        /// <summary>Sell: an upward arrow breaking out of the tile -- outbound revenue.</summary>
        private static void Sell(Graphics g, Rectangle k, SolidBrush b)
        {
            PointF[] head = new PointF[] {
                FPt(k, 0.50, 0.20), FPt(k, 0.76, 0.46), FPt(k, 0.61, 0.46),
                FPt(k, 0.61, 0.78), FPt(k, 0.39, 0.78), FPt(k, 0.39, 0.46), FPt(k, 0.24, 0.46) };
            g.FillPolygon(b, head);
        }

        /// <summary>
        /// Attract: a broadcast fan -- three arcs radiating from a dot. NOT a globe or a horseshoe;
        /// both are ring-shaped and confusable with Command Center's disc once antialiasing closes the gap.
        /// </summary>
        private static void Attract(Graphics g, Rectangle k, SolidBrush b)
        {
            Rectangle dot = FRect(k, 0.24, 0.62, 0.16, 0.16);
            if (dot.Width >= 2) g.FillEllipse(b, dot);

            float w = Math.Max(1.4f, k.Width * 0.085f);
            using (Pen p = new Pen(b.Color, w))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                for (int i = 1; i <= 3; i++)
                {
                    double f = 0.30 * i;
                    Rectangle arc = new Rectangle(
                        (int)Math.Round(k.X + k.Width * 0.30 - k.Width * f),
                        (int)Math.Round(k.Y + k.Height * 0.70 - k.Height * f),
                        Math.Max(2, (int)Math.Round(k.Width * f * 2)),
                        Math.Max(2, (int)Math.Round(k.Height * f * 2)));
                    g.DrawArc(p, arc, 270, 60);
                }
            }
        }

        /// <summary>Capital: a coin stack -- vertical, so its optical weight matches its neighbours.</summary>
        private static void Capital(Graphics g, Rectangle k, SolidBrush b)
        {
            for (int i = 0; i < 3; i++)
            {
                Rectangle c = FRect(k, 0.22, 0.26 + 0.17 * i, 0.56, 0.13);
                g.FillEllipse(b, c);
            }
        }

        /// <summary>Machine: a die with pins -- engineering and infrastructure.</summary>
        private static void Machine(Graphics g, Rectangle k, SolidBrush b)
        {
            Rectangle die = FRect(k, 0.30, 0.30, 0.40, 0.40);
            g.FillRectangle(b, die);

            // Pins only where there is room for a real gap between them. Below that they smear into
            // one grey bar and the die stops reading as a chip.
            if (k.Width >= 20)
            {
                int pw = Math.Max(1, (int)Math.Round(k.Width * 0.055));
                int pl = Math.Max(2, (int)Math.Round(k.Width * 0.11));
                for (int i = 0; i < 2; i++)
                {
                    double at = 0.42 + 0.24 * i;
                    g.FillRectangle(b, new Rectangle(k.X + (int)(k.Width * at), k.Y + (int)(k.Height * 0.19), pw, pl));
                    g.FillRectangle(b, new Rectangle(k.X + (int)(k.Width * at), k.Y + (int)(k.Height * 0.70), pw, pl));
                    g.FillRectangle(b, new Rectangle(k.X + (int)(k.Width * 0.19), k.Y + (int)(k.Height * at), pl, pw));
                    g.FillRectangle(b, new Rectangle(k.X + (int)(k.Width * 0.70), k.Y + (int)(k.Height * at), pl, pw));
                }
            }
        }

        /// <summary>Asks: a raised hand / flag -- the one mark that is asking for something.</summary>
        private static void Asks(Graphics g, Rectangle k, SolidBrush b)
        {
            int poleW = Math.Max(1, (int)Math.Round(k.Width * 0.09));
            g.FillRectangle(b, new Rectangle(k.X + (int)(k.Width * 0.28), k.Y + (int)(k.Height * 0.20), poleW, (int)(k.Height * 0.60)));
            PointF[] flag = new PointF[] {
                FPt(k, 0.37, 0.22), FPt(k, 0.78, 0.34), FPt(k, 0.37, 0.46) };
            g.FillPolygon(b, flag);
        }

        // ---- the state chip --------------------------------------------------------------------

        /// <summary>
        /// A chip in the state colour at a fixed corner, knocked out of the tile by a ring in the
        /// taskbar's OWN measured tone. Redundant with the chroma ladder on purpose: chroma answers
        /// "is this alive", the chip answers "which state exactly".
        ///
        /// Three fixes over the first draft, all of which a screenshot catches: it scales with the
        /// mark box instead of sitting at a fixed 7.8px (49% of a small mark, and overflowing the
        /// cell); the knock-out ring is sourced from Theme.BackgroundLuminance rather than a
        /// hardcoded 0xF4F4F4 that haloed on the real bar; and Command Center's chip is pulled inside the
        /// budget arc's 13.5px radius rather than sitting on top of it.
        /// </summary>
        /// <summary>
        /// The chip's own colour, which is NOT Palette's state colour.
        ///
        /// Palette was tuned for a monochrome glyph sitting directly on the taskbar, so `due` there
        /// is #3C3C3C -- near black. Painted as a filled disc on a saturated tile that reads as a
        /// hole punched through the icon, not as a status light. A chip needs a signal colour: it
        /// sits on brand colour, not on the bar, so it takes its own small palette and only has to
        /// separate from the tile.
        /// </summary>
        private static Color ChipColour(IconState s, Color fallback)
        {
            switch (s)
            {
                case IconState.Due: return Color.FromArgb(0xF5, 0xB3, 0x1E);        // amber: your move
                case IconState.Working: return Color.FromArgb(0x2E, 0xC4, 0xFF);    // cyan: in flight
                case IconState.Attention: return Color.FromArgb(0x35, 0xD6, 0x7A);  // green: answer me
                case IconState.Fault: return Color.FromArgb(0xFF, 0x45, 0x45);      // red
                case IconState.Never: return Color.FromArgb(0xC9, 0xCE, 0xD6);      // pale: not yet run
                default: return fallback;
            }
        }

        private static void DrawChip(Graphics g, Rectangle k, double s, IconState state,
                                     Color paletteColour, int alpha, bool isCore)
        {
            Color stateColour = ChipColour(state, paletteColour);
            if (state == IconState.Idle) return;        // quiet is the absence of a chip
            if (k.Width < 14) return;                   // no room; the chroma ladder still carries it

            // Sized and placed so the chip sits WHOLLY INSIDE the tile. Overhanging the corner made
            // it read as a bite taken out of the icon rather than a status indicator, and on Command Center it
            // collided with the concentric budget arc. The knock-out ring is part of the footprint,
            // so the inset has to clear r + ring, not just r.
            float r = Math.Max(2.0f, k.Width * 0.155f);
            float ring = Math.Max(1.0f, r * 0.42f);
            float inset = r + ring * 0.5f + 1f;
            float cx, cy;
            if (isCore)
            {
                // Inside the disc AND inside DrawBudgetArc's 13.5*s radius: 45 degrees down-right at
                // 0.62 of the radius keeps both clearances at every scale.
                float rad = (k.Width * 0.5f - inset) * 0.72f;
                cx = k.X + k.Width * 0.5f + rad * 0.707f;
                cy = k.Y + k.Height * 0.5f + rad * 0.707f;
            }
            else
            {
                cx = k.Right - inset;
                cy = k.Bottom - inset;
            }
            RectangleF outer = new RectangleF(cx - r, cy - r, r * 2, r * 2);

            byte bg = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Theme.BackgroundLuminance)));
            using (Pen kp = new Pen(Color.FromArgb(alpha, bg, bg, bg), ring))
                g.DrawEllipse(kp, outer);

            if (state == IconState.Never)
            {
                // Hollow: neither healthy nor broken. Stroked, not filled, so it is not just a
                // smaller dot.
                using (Pen p = new Pen(Color.FromArgb(alpha, stateColour), Math.Max(1.2f, r * 0.45f)))
                    g.DrawEllipse(p, RectangleF.Inflate(outer, -r * 0.30f, -r * 0.30f));
                return;
            }

            if (state == IconState.Fault)
            {
                // A triangle. The one state that must survive greyscale, a bad angle and a glance.
                using (GraphicsPath tp = new GraphicsPath())
                {
                    tp.AddPolygon(new PointF[] {
                        new PointF(cx, cy - r), new PointF(cx + r, cy + r * 0.8f), new PointF(cx - r, cy + r * 0.8f) });
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, stateColour)))
                        g.FillPath(b, tp);
                }
                return;
            }

            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, stateColour)))
                g.FillEllipse(b, outer);

            // ATTENTION vs WORKING. The most important distinction on the strip and the first draft
            // did not carry it at all -- both were the same colour, the same chip, separated only by
            // an alpha wobble on a 2px annulus that is invisible in a still frame. Attention now
            // gets a knocked-out bar through the chip: a different SHAPE, not a different alpha.
            if (state == IconState.Attention && r >= 3.0f)
            {
                using (SolidBrush w = new SolidBrush(Color.FromArgb(alpha, bg, bg, bg)))
                    g.FillRectangle(w, cx - r * 0.16f, cy - r * 0.52f, r * 0.32f, r * 0.72f);
            }
        }
    }
}
