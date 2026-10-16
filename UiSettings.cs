// UiSettings.cs -- the things the operator can turn, persisted across restarts.
//
// Ported from the menubar-marquee pattern ([[builds/menubar-marquee]]): live slider rows that live
// INSIDE the menu, apply on every value change, and persist. The marquee's defaults are 17pt icons
// / 26pt gap / 0.85 opacity with a 10-72 range; the equivalents here are multipliers rather than
// absolute px, because this strip already scales every metric through Sc(v, s) for DPI. Folding a
// user multiplier into that same `s` means one number resizes the glyphs, the cells, the Command Center mark,
// the badges and the budget arc together, in proportion, with no second layout path to keep in sync.
//
// Written to .command-center, never to Halo\ -- same rule as every other runtime artifact.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CommandCenter
{
    internal static class UiSettings
    {
        // Range ends are deliberate, not round numbers picked for looks:
        //   ScaleMax 2.0  -- Full tier needs 260px at 1.0 and the measured runway is ~500px, so 1.9x
        //                    is roughly where it stops fitting. The renderer clamps to the real
        //                    span anyway; this just stops the slider offering the absurd.
        //   ScaleMin 0.80 -- below this the 22px glyph box lands under 18px and the Fluent glyphs
        //                    stop being distinguishable from each other at a glance.
        public const double ScaleMin = 0.80, ScaleMax = 2.00, ScaleDefault = 1.00;
        public const double GapMin = 0.40, GapMax = 3.00, GapDefault = 1.00;
        public const double OpacityMin = 0.35, OpacityMax = 1.00, OpacityDefault = 1.00;

        // Position. The strip normally sits at the left edge of the measured free span; these let
        // the operator move it inside that span without the app second-guessing him.
        public const int OffsetXMin = -400, OffsetXMax = 400;
        public const int OffsetYMin = -60, OffsetYMax = 60;

        public const int AnchorLeft = 0, AnchorCentre = 1, AnchorRight = 2;

        // Layout tier: 0 = auto (largest that fits), 1/2/3 = pinned Full/Compact/Micro.
        public const int TierAuto = 0, TierFull = 1, TierCompact = 2, TierMicro = 3;

        public static double Scale = ScaleDefault;
        public static double Gap = GapDefault;
        public static double Opacity = OpacityDefault;
        public static int OffsetX;
        public static int OffsetY;
        public static int Anchor = AnchorLeft;
        public static int TierMode = TierAuto;

        /// <summary>
        /// What the renderer actually used last frame, after clamping to the runway and the
        /// taskbar's height. The menu shows this next to the requested value so a clamp is visible
        /// rather than looking like a slider that stopped responding.
        /// </summary>
        public static double LastEffective = ScaleDefault;

        private static bool _dirty;

        public static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static string File1 { get { return Paths.State1("ui.json"); } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(File1)) return;
                string j = File.ReadAllText(File1);
                Scale = Clamp(Num(j, "scale", ScaleDefault), ScaleMin, ScaleMax);
                Gap = Clamp(Num(j, "gap", GapDefault), GapMin, GapMax);
                Opacity = Clamp(Num(j, "opacity", OpacityDefault), OpacityMin, OpacityMax);
                OffsetX = (int)Clamp(Num(j, "offsetX", 0), OffsetXMin, OffsetXMax);
                OffsetY = (int)Clamp(Num(j, "offsetY", 0), OffsetYMin, OffsetYMax);
                Anchor = (int)Clamp(Num(j, "anchor", AnchorLeft), AnchorLeft, AnchorRight);
                TierMode = (int)Clamp(Num(j, "tierMode", TierAuto), TierAuto, TierMicro);
                LastEffective = Scale;
                Log.Info(string.Format(CultureInfo.InvariantCulture,
                    "ui: loaded scale={0:F2} gap={1:F2} opacity={2:F2}", Scale, Gap, Opacity));
            }
            catch (Exception ex)
            {
                // A corrupt settings file must never stop the strip from drawing. Defaults are
                // always a valid answer here, unlike supervisor.json where the safe fallback is
                // "paused" -- there is no unsafe direction for an icon size.
                Log.Warn("ui: settings unreadable, using defaults: " + ex.Message);
            }
        }

        public static void MarkDirty() { _dirty = true; }

        /// <summary>
        /// Flushed on menu close and on quit rather than on every slider tick -- a drag fires
        /// dozens of change events and each one would be a separate file write.
        /// </summary>
        public static void Flush()
        {
            if (!_dirty) return;
            try
            {
                Supervisor.WriteAtomicPublic(File1, string.Format(CultureInfo.InvariantCulture,
                    "{{\"scale\":{0:F3},\"gap\":{1:F3},\"opacity\":{2:F3}," +
                    "\"offsetX\":{3},\"offsetY\":{4},\"anchor\":{5},\"tierMode\":{6},\"version\":1}}\n",
                    Scale, Gap, Opacity, OffsetX, OffsetY, Anchor, TierMode));
                _dirty = false;
                Log.Info(string.Format(CultureInfo.InvariantCulture,
                    "ui: saved scale={0:F2} gap={1:F2} opacity={2:F2}", Scale, Gap, Opacity));
            }
            catch (Exception ex) { Log.Warn("ui: could not save settings: " + ex.Message); }
        }

        public static void ResetToDefaults()
        {
            Scale = ScaleDefault; Gap = GapDefault; Opacity = OpacityDefault;
            OffsetX = 0; OffsetY = 0; Anchor = AnchorLeft; TierMode = TierAuto;
            MarkDirty();
        }

        private static double Num(string json, string key, double fallback)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9]*\\.?[0-9]+)");
            double v;
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                                             CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        /// <summary>
        /// The size multiplier actually usable, given the room available. ONE implementation --
        /// StripWindow draws with it and -Selftest reports it, and if those two ever disagreed the
        /// diagnostic would confirm a size the strip is not using.
        ///
        /// Two independent ceilings, both of which must hold or the slider becomes a trap:
        ///   HEIGHT -- the glyph box has to fit inside the taskbar (48px on this machine).
        ///   RUNWAY -- Full tier must still fit the free span. Without this, dragging the size
        ///             slider UP eventually demotes the layout to Compact, so the icons visibly get
        ///             SMALLER -- the exact opposite of the gesture that caused it.
        /// </summary>
        public static double EffectiveScale(double dpiSc, int spanWidth, int stripH)
        {
            double want = Clamp(Scale, ScaleMin, ScaleMax);
            if (dpiSc <= 0) dpiSc = 1.0;

            if (stripH > 8)
            {
                double maxByHeight = (stripH - 6.0) / (IconRenderer.GLYPH * dpiSc);
                if (want > maxByHeight) want = maxByHeight;
            }

            if (spanWidth > 8)
            {
                int needAtOne = IconRenderer.WidthFor(LayoutTier.Full, dpiSc);
                if (needAtOne > 0)
                {
                    double maxByRunway = (double)spanWidth / needAtOne;
                    if (want > maxByRunway) want = maxByRunway;
                }
            }

            if (want < ScaleMin) want = ScaleMin;
            LastEffective = want;
            return want;
        }

        public static byte AlphaByte()
        {
            int a = (int)Math.Round(255.0 * Clamp(Opacity, OpacityMin, OpacityMax));
            if (a < 0) a = 0; if (a > 255) a = 255;
            return (byte)a;
        }
    }
}
