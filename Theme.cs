// Theme.cs + Palette -- what colour to draw, on a taskbar whose theme we do not control.
//
// THE MISTAKE THIS EXISTS TO PREVENT. The first draft specified "idle = white @ 28% alpha",
// assuming a dark taskbar. Measured on this machine 2026-08-04: SystemUsesLightTheme=1 and the
// taskbar background luminance is 224. White at 28% over #E0E0E0 is a contrast ratio of about
// 1.05:1 -- the state the strip spends most of its life in would have been literally invisible.
// So: detect, never assume, and check contrast against a measured background.

using System;
using System.Drawing;
using Microsoft.Win32;

namespace CommandCenter
{
    internal enum IconState
    {
        Never = 0,      // no ledger file at all -- 8 of 13 officers today. Neither healthy nor broken.
        Idle = 1,
        Due = 2,
        Working = 3,
        Attention = 4,
        Fault = 5
    }

    internal static class Theme
    {
        public static bool IsLight = true;
        public static Color Accent = Color.FromArgb(0x00, 0x78, 0xD4);
        public static double BackgroundLuminance = 224.0;

        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string DwmKey = @"Software\Microsoft\Windows\DWM";

        public static void Refresh()
        {
            // 1. Registry is authoritative for the taskbar. SystemUsesLightTheme -- NOT
            //    AppsUseLightTheme -- governs the taskbar/Start/tray. Absent means light (1).
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("SystemUsesLightTheme");
                        if (v is int) IsLight = ((int)v) != 0;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("theme: registry read failed: " + ex.Message); }

            // 2. Accent from DWM. Stored ABGR, not ARGB -- measured 0xFFD47800 here, which is
            //    RGB #0078D4. Getting the byte order wrong silently yields orange.
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DwmKey))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AccentColor");
                        if (v is int)
                        {
                            uint abgr = unchecked((uint)(int)v);
                            byte r = (byte)(abgr & 0xFF);
                            byte g = (byte)((abgr >> 8) & 0xFF);
                            byte b = (byte)((abgr >> 16) & 0xFF);
                            Accent = Color.FromArgb(r, g, b);
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("theme: accent read failed: " + ex.Message); }

            // 3. Measured background wins over the registry when they disagree, because the pixels
            //    are what the user actually sees -- a custom theme or an accent-coloured taskbar can
            //    make SystemUsesLightTheme a lie. Geometry already computed this for free.
            if (Geometry.LastBackgroundLuminance >= 0)
            {
                BackgroundLuminance = Geometry.LastBackgroundLuminance;
                bool measuredLight = BackgroundLuminance > 128;
                if (measuredLight != IsLight)
                {
                    Log.Info(string.Format(
                        "theme: registry says {0} but measured luminance is {1:F1} -- trusting pixels",
                        IsLight ? "light" : "dark", BackgroundLuminance));
                    IsLight = measuredLight;
                }
            }
        }

        public static string Describe()
        {
            return string.Format("{0} accent=#{1:X2}{2:X2}{3:X2} bgLum={4:F1}",
                IsLight ? "light" : "dark", Accent.R, Accent.G, Accent.B, BackgroundLuminance);
        }

        /// <summary>WCAG relative-luminance contrast of a colour against the measured taskbar.</summary>
        public static double ContrastAgainstBackground(Color c)
        {
            return ContrastAgainstBackground(c, 255);
        }

        /// <summary>
        /// Contrast of a colour drawn AT AN ALPHA over the measured taskbar.
        ///
        /// The alpha overload is the one that tells the truth, and its absence was a real error:
        /// every state in this app is drawn at a partial alpha, so measuring the raw colour
        /// overstates all of them. The build note recorded "idle 3.0:1" on a light taskbar; composite
        /// its 170/255 alpha over #E0E0E0 first and it is 2.59:1, which FAILS the 3.0 non-text
        /// floor. A contrast check that ignores the alpha you are about to draw with is not a
        /// contrast check.
        /// </summary>
        public static double ContrastAgainstBackground(Color c, int alpha)
        {
            double bg = BackgroundLuminance;
            double a = Math.Max(0, Math.Min(255, alpha)) / 255.0;

            // Composite over the background -- what the eye actually receives.
            double r = c.R * a + bg * (1 - a);
            double g = c.G * a + bg * (1 - a);
            double b = c.B * a + bg * (1 - a);

            double l1 = RelLum((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
            double l2 = RelLumChannel(bg / 255.0);   // taskbar is grey, so one channel is the luminance
            double hi = Math.Max(l1, l2), lo = Math.Min(l1, l2);
            return (hi + 0.05) / (lo + 0.05);
        }

        private static double RelLum(byte r, byte g, byte b)
        {
            return 0.2126 * RelLumChannel(r / 255.0)
                 + 0.7152 * RelLumChannel(g / 255.0)
                 + 0.0722 * RelLumChannel(b / 255.0);
        }

        private static double RelLumChannel(double c)
        {
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    internal static class Palette
    {
        /// <summary>
        /// Base colour and alpha for a state.
        ///
        /// MEASURED 2026-08-04 with alpha composited over the real taskbar -- the earlier figures in
        /// this comment were computed on the raw colours and overstated every state. Light bar
        /// (#E0E0E0): idle 3.3:1, due 5.6:1, working/attention 3.4:1, fault 4.3:1. Dark bar
        /// (#202020): idle 4.1:1, due 7.9:1, working 8.1:1, fault 8.0:1. The 3.0:1 non-text floor
        /// now holds everywhere except `never`, which is deliberately a ghost -- see below.
        ///
        /// The draft's fault orange #F97316 measures 2.4:1 here, so this uses the Windows critical
        /// red instead. `-Selftest` asserts these live: contrast=... fails the run if one regresses.
        /// </summary>
        public static void For(IconState s, out Color colour, out int alpha)
        {
            bool light = Theme.IsLight;
            switch (s)
            {
                case IconState.Never:
                    // BELOW THE 3.0 FLOOR ON PURPOSE, and the only state that is. "Has not run
                    // once" is neither healthy nor broken, 8 of 13 officers are in it today, and
                    // rendering them at full strength would make the strip's first frame read as
                    // thirteen live departments. Held at ~2.2:1: present, clearly recessive.
                    colour = light ? Color.FromArgb(0x60, 0x60, 0x60) : Color.White;
                    alpha = light ? 145 : 70;
                    break;
                case IconState.Idle:
                    // Was #606060 @170 on light = 2.59:1, a real fail that the unmeasured comment
                    // above recorded as 3.0:1. Darkened rather than made more opaque: at this size
                    // a darker mark holds its shape better than a denser grey one.
                    colour = light ? Color.FromArgb(0x55, 0x55, 0x55) : Color.White;
                    alpha = light ? 190 : 120;
                    break;
                case IconState.Due:
                    colour = light ? Color.FromArgb(0x3C, 0x3C, 0x3C) : Color.White;
                    alpha = light ? 215 : 170;
                    break;
                case IconState.Working:
                    colour = light ? Theme.Accent : Color.FromArgb(0x4C, 0xC2, 0xFF);
                    alpha = 255;
                    break;
                case IconState.Attention:
                    colour = light ? Theme.Accent : Color.FromArgb(0x60, 0xCD, 0xFF);
                    alpha = 255;
                    break;
                default: // Fault
                    colour = light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x99, 0xA4);
                    alpha = 255;
                    break;
            }
        }

        public static Color GlowColour()
        {
            return Theme.IsLight ? Theme.Accent : Color.FromArgb(0x60, 0xCD, 0xFF);
        }

        public static int GlowAlpha() { return Theme.IsLight ? 0x4D : 0x59; }

        public static Color HoverPlate()
        {
            return Theme.IsLight ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x18, 255, 255, 255);
        }

        /// <summary>Per-desk tint, used for the 2px tick under a `due` cell.</summary>
        public static Color DeskTint(string desk)
        {
            switch (desk)
            {
                case "Sell":    return Color.FromArgb(0x2E, 0x9E, 0x5B);
                case "Attract": return Color.FromArgb(0x8B, 0x5C, 0xF6);
                case "Capital": return Color.FromArgb(0xD9, 0x8E, 0x1F);
                default:        return Color.FromArgb(0x64, 0x74, 0x8B);   // Machine
            }
        }
    }
}
