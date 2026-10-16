// Animator.cs -- the alpha curves, and the rule that stops them.
//
// THE SALIENCE BUDGET, ENFORCED IN CODE. the operator already has 33 open items. A permanently lit,
// permanently moving indicator is an OFF indicator -- the second time he glances past it, this
// project is dead and he will not be able to say why. So: attention escalates on a bounded ladder
// and then STOPS MOVING for good. The count grows; the motion does not. Nothing in this strip may
// animate for more than four hours.

using System;
using System.Collections.Generic;

namespace CommandCenter
{
    internal static class Animator
    {
        public const int WorkingPeriodMs = 2400;
        public const int GlowEntryMs = 600;
        public const int FaultBlinkMs = 1200;

        // Escalation ladder, in minutes since the cell entered `attention`.
        public const int CalmMinutes = 120;      // 0-2h  steady
        public const int BreatheMinutes = 240;   // 2-4h  slow 6s breathe
                                                 // >4h   steady forever, never animates again

        private static readonly Dictionary<string, long> Entered = new Dictionary<string, long>();

        public static void NoteState(string key, IconState s, long nowMs)
        {
            string k = key + ":" + (int)s;
            if (!Entered.ContainsKey(k)) Entered[k] = nowMs;
            // Drop stale keys for this cell in other states, so a re-entry restarts its curve.
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, long> kv in Entered)
                if (kv.Key.StartsWith(key + ":", StringComparison.Ordinal) && kv.Key != k)
                    stale.Add(kv.Key);
            for (int i = 0; i < stale.Count; i++) Entered.Remove(stale[i]);
        }

        private static long Since(string key, IconState s, long nowMs)
        {
            long t;
            if (Entered.TryGetValue(key + ":" + (int)s, out t)) return nowMs - t;
            return 0;
        }

        /// <summary>
        /// Raised cosine, period 2400 ms, alpha 102..255. Per-cell phase offset spreads several
        /// working cells around the cycle instead of strobing them in unison.
        /// </summary>
        public static int WorkingAlpha(long nowMs, int cellIndex)
        {
            long t = (nowMs + cellIndex * 185L) % WorkingPeriodMs;
            double a = 102 + 153 * (1 - Math.Cos(2 * Math.PI * t / WorkingPeriodMs)) / 2.0;
            return Clamp((int)Math.Round(a));
        }

        public static int GlowRadius(long nowMs, string key, double s)
        {
            long t = Since(key, IconState.Attention, nowMs);
            int full = (int)Math.Round(7 * s, MidpointRounding.AwayFromZero);
            if (t >= GlowEntryMs) return full;
            double u = t / (double)GlowEntryMs;
            return (int)Math.Round(full * EaseOutBack(u), MidpointRounding.AwayFromZero);
        }

        public static int GlowAlpha(long nowMs, string key)
        {
            long t = Since(key, IconState.Attention, nowMs);
            int full = Palette.GlowAlpha();
            if (t < GlowEntryMs) return (int)(full * EaseOutCubic(t / (double)GlowEntryMs));

            double mins = t / 60000.0;
            if (mins < CalmMinutes) return full;
            if (mins < BreatheMinutes)
            {
                // One slow 6s breathe band, and only in this window.
                double phase = (t % 6000) / 6000.0;
                double k = (1 - Math.Cos(2 * Math.PI * phase)) / 2.0;
                return (int)(full * (0.72 + 0.28 * k));
            }
            return full;   // past 4h: steady forever. The badge count is the escalation now.
        }

        /// <summary>True while this cell still needs repainting. Drives the 33 ms render timer.</summary>
        public static bool IsAnimating(string key, IconState s, long nowMs)
        {
            if (s == IconState.Working) return true;
            if (s == IconState.Attention)
            {
                long t = Since(key, IconState.Attention, nowMs);
                if (t < GlowEntryMs) return true;
                double mins = t / 60000.0;
                return mins >= CalmMinutes && mins < BreatheMinutes;
            }
            if (s == IconState.Fault) return Since(key, IconState.Fault, nowMs) < FaultBlinkMs;
            return false;
        }

        public static int FaultAlpha(long nowMs, string key)
        {
            long t = Since(key, IconState.Fault, nowMs);
            if (t >= FaultBlinkMs) return 255;
            return ((t / 200) % 2 == 0) ? 255 : 90;
        }

        private static double EaseOutBack(double u)
        {
            double c1 = 1.70158, c3 = c1 + 1;
            double v = u - 1;
            return 1 + c3 * v * v * v + c1 * v * v;
        }

        private static double EaseOutCubic(double u)
        {
            double v = 1 - u;
            return 1 - v * v * v;
        }

        private static int Clamp(int a) { return a < 0 ? 0 : (a > 255 ? 255 : a); }
    }
}
