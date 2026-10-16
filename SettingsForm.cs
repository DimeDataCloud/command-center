// SettingsForm.cs -- position, size and layout, adjusted while you watch the strip change.
//
// Right-clicking Command Center pops a single small gear above the icon; clicking that opens this. The gear
// is a deliberate half-step rather than opening this window straight from the right-click: Command Center's
// left-click promise is "one click to the COO, never a menu on the way", and a right-click that
// threw a settings dialog over the taskbar would be the same interruption from the other button.
// A 28px gear is dismissible by ignoring it.
//
// Every control applies LIVE. The strip is four inches from this window; a settings dialog with an
// OK button would make him confirm a change he can already see.

using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace CommandCenter
{
    internal class SettingsForm : Form
    {
        private static SettingsForm _open;

        private readonly Action _apply;
        private Label _sizeEcho;
        private bool _loading;

        public static void ShowSingleton(Action apply)
        {
            // One window. A second copy would show stale values the moment the first changed one.
            if (_open != null && !_open.IsDisposed)
            {
                if (_open.WindowState == FormWindowState.Minimized)
                    _open.WindowState = FormWindowState.Normal;
                _open.Activate();
                return;
            }
            _open = new SettingsForm(apply);
            _open.FormClosed += delegate { _open = null; UiSettings.Flush(); };
            _open.Show();
        }

        private SettingsForm(Action apply)
        {
            _apply = apply;
            _loading = true;

            Text = "Command Center strip settings";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.None;   // same reason as GearButton: fixed pixel layout
            ClientSize = new Size(340, 476);
            Font = new Font("Segoe UI", 9f);

            // EVERY control is parented to its GroupBox, with coordinates relative to it. The first
            // version added them to the form instead: the group boxes then painted over the top and
            // the window opened showing three sliders and nothing else -- no labels, no combos, no
            // buttons. Caught by screenshotting it, not by reading it.

            // ---- position ----------------------------------------------------------------
            GroupBox gPos = Group("Position", 10, 150);

            AddLabel(gPos, "Anchor", 12, 26);
            ComboBox anchor = new ComboBox();
            anchor.DropDownStyle = ComboBoxStyle.DropDownList;
            anchor.Bounds = new Rectangle(96, 22, 208, 24);
            anchor.Items.AddRange(new object[] { "Left of the free span", "Centred in it", "Right of it" });
            anchor.SelectedIndex = Math.Max(0, Math.Min(2, UiSettings.Anchor));
            anchor.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                UiSettings.Anchor = anchor.SelectedIndex; Changed();
            };
            gPos.Controls.Add(anchor);

            Slider(gPos, "Nudge left / right", 54, UiSettings.OffsetXMin, UiSettings.OffsetXMax,
                UiSettings.OffsetX,
                delegate(int v) { return v + " px"; },
                delegate(int v) { UiSettings.OffsetX = v; });

            Slider(gPos, "Nudge up / down", 102, UiSettings.OffsetYMin, UiSettings.OffsetYMax,
                UiSettings.OffsetY,
                delegate(int v) { return v + " px"; },
                delegate(int v) { UiSettings.OffsetY = v; });

            // ---- size --------------------------------------------------------------------
            GroupBox gSize = Group("Size", 170, 96);

            Slider(gSize, "Icon size", 22,
                (int)(UiSettings.ScaleMin * 100), (int)(UiSettings.ScaleMax * 100),
                (int)Math.Round(UiSettings.Scale * 100),
                delegate(int v) { return v + "%"; },
                delegate(int v) { UiSettings.Scale = v / 100.0; });

            _sizeEcho = new Label();
            _sizeEcho.AutoSize = false;
            _sizeEcho.Bounds = new Rectangle(12, 70, 296, 16);
            _sizeEcho.ForeColor = SystemColors.GrayText;
            gSize.Controls.Add(_sizeEcho);

            // ---- layout ------------------------------------------------------------------
            GroupBox gLay = Group("Layout", 276, 150);

            AddLabel(gLay, "Density", 12, 26);
            ComboBox tier = new ComboBox();
            tier.DropDownStyle = ComboBoxStyle.DropDownList;
            tier.Bounds = new Rectangle(96, 22, 208, 24);
            tier.Items.AddRange(new object[] { "Auto (largest that fits)", "Full", "Compact", "Micro (Command Center only)" });
            tier.SelectedIndex = Math.Max(0, Math.Min(3, UiSettings.TierMode));
            tier.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                UiSettings.TierMode = tier.SelectedIndex; Changed();
            };
            gLay.Controls.Add(tier);

            Slider(gLay, "Spacing", 54,
                (int)(UiSettings.GapMin * 100), (int)(UiSettings.GapMax * 100),
                (int)Math.Round(UiSettings.Gap * 100),
                delegate(int v) { return v + "%"; },
                delegate(int v) { UiSettings.Gap = v / 100.0; });

            Slider(gLay, "Opacity", 102,
                (int)(UiSettings.OpacityMin * 100), (int)(UiSettings.OpacityMax * 100),
                (int)Math.Round(UiSettings.Opacity * 100),
                delegate(int v) { return v + "%"; },
                delegate(int v) { UiSettings.Opacity = v / 100.0; });

            // ---- buttons -----------------------------------------------------------------
            int y = 436;
            Button reset = new Button();
            reset.Text = "Reset to defaults";
            reset.Bounds = new Rectangle(14, y, 150, 28);
            reset.Click += delegate
            {
                UiSettings.ResetToDefaults();
                UiSettings.Flush();
                Changed();
                Reload();
            };
            Controls.Add(reset);

            Button close = new Button();
            close.Text = "Close";
            close.Bounds = new Rectangle(240, y, 84, 28);
            close.Click += delegate { Close(); };
            Controls.Add(close);
            AcceptButton = close;

            _loading = false;
            UpdateEcho();
            PlaceNearTray();
        }

        // ---- plumbing --------------------------------------------------------------------

        private void Changed()
        {
            UiSettings.MarkDirty();
            if (_apply != null) _apply();
            UpdateEcho();
        }

        /// <summary>
        /// Says what the strip is ACTUALLY drawing, which is not always what was asked for -- the
        /// size clamps to the taskbar height and the runway. Without this the slider just appears
        /// to stop responding near the top of its travel.
        /// </summary>
        private void UpdateEcho()
        {
            if (_sizeEcho == null) return;
            int want = (int)Math.Round(UiSettings.Scale * 100);
            int eff = (int)Math.Round(UiSettings.LastEffective * 100);
            _sizeEcho.Text = Math.Abs(eff - want) > 2
                ? string.Format(CultureInfo.InvariantCulture,
                    "drawing at {0}% -- capped, no more room on the taskbar", eff)
                : string.Format(CultureInfo.InvariantCulture, "drawing at {0}%", eff);
        }

        private void Reload()
        {
            // Rebuilding is simpler and less error-prone than reaching back into every control.
            Point at = Location;
            Close();
            SettingsForm f = new SettingsForm(_apply);
            f.Location = at;
            f.FormClosed += delegate { _open = null; UiSettings.Flush(); };
            _open = f;
            f.Show();
        }

        private void PlaceNearTray()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(wa.Left + 8, wa.Right - Width - 24),
                                 Math.Max(wa.Top + 8, wa.Bottom - Height - 24));
        }

        private GroupBox Group(string title, int top, int height)
        {
            GroupBox gb = new GroupBox();
            gb.Text = title;
            gb.Bounds = new Rectangle(10, top, 320, height);
            Controls.Add(gb);
            return gb;
        }

        private void AddLabel(Control parent, string text, int x, int top)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Text = text;
            l.Bounds = new Rectangle(x, top, 84, 18);
            parent.Controls.Add(l);
        }

        /// <summary>A labelled live TrackBar, parented INTO its group box.</summary>
        private void Slider(Control parent, string title, int top, int min, int max, int value,
                            Func<int, string> format, Action<int> set)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Bounds = new Rectangle(12, top, 296, 16);
            l.Text = title + "   " + format(value);
            parent.Controls.Add(l);

            TrackBar bar = new TrackBar();
            bar.Minimum = min;
            bar.Maximum = max;
            bar.Value = Math.Max(min, Math.Min(max, value));
            bar.TickStyle = TickStyle.None;
            bar.AutoSize = false;
            bar.Bounds = new Rectangle(6, top + 15, 306, 28);
            bar.SmallChange = 1;
            bar.LargeChange = Math.Max(1, (max - min) / 10);
            bar.ValueChanged += delegate
            {
                if (_loading) return;
                set(bar.Value);
                Changed();
                l.Text = title + "   " + format(bar.Value);
            };
            parent.Controls.Add(bar);
        }
    }

    /// <summary>
    /// The little gear that appears above Command Center on a right-click.
    ///
    /// A ToolStripDropDown, NOT a Form. A borderless Form cannot be 30x30: Windows enforces
    /// SM_CXMIN/SM_CYMIN as the minimum tracking size, so it came back 136x39 and the gear sat wide
    /// and off-centre above the icon -- measured, not guessed. Setting AutoScaleMode.None does not
    /// help because the floor is imposed by the OS via WM_GETMINMAXINFO, not by WinForms scaling.
    /// A drop-down has no such floor, and it already closes itself when you click elsewhere.
    /// </summary>
    internal static class GearButton
    {
        private const int Size1 = 30;
        private static ToolStripDropDown _open;

        public static void ShowAt(int centreX, int bottomY, Action onClick)
        {
            HideNow();

            Panel face = new Panel();
            face.Size = new Size(Size1, Size1);
            bool[] hot = new bool[1];

            face.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                Rectangle r = new Rectangle(0, 0, Size1 - 1, Size1 - 1);

                Color bg = hot[0]
                    ? (Theme.IsLight ? Color.FromArgb(0xE8, 0xF1, 0xFB) : Color.FromArgb(0x3A, 0x3A, 0x3A))
                    : (Theme.IsLight ? Color.White : Color.FromArgb(0x2B, 0x2B, 0x2B));
                using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, r);
                using (Pen p = new Pen(Theme.IsLight ? Color.FromArgb(0xB4, 0xB4, 0xB4)
                                                     : Color.FromArgb(0x55, 0x55, 0x55)))
                    g.DrawRectangle(p, r);

                using (Font f = new Font("Segoe Fluent Icons", 15f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush b = new SolidBrush(Theme.IsLight ? Color.FromArgb(0x28, 0x28, 0x28)
                                                                   : Color.FromArgb(0xEC, 0xEC, 0xEC)))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("", f, b, new RectangleF(0, 0, Size1, Size1), sf);
                }
            };
            face.MouseEnter += delegate { hot[0] = true; face.Invalidate(); };
            face.MouseLeave += delegate { hot[0] = false; face.Invalidate(); };
            face.Click += delegate
            {
                HideNow();
                if (onClick != null) onClick();
            };
            face.Cursor = Cursors.Hand;

            ToolStripControlHost host = new ToolStripControlHost(face);
            host.AutoSize = false;
            host.Size = face.Size;
            host.Margin = Padding.Empty;
            host.Padding = Padding.Empty;

            ToolStripDropDown dd = new ToolStripDropDown();
            dd.AutoSize = false;
            dd.Padding = Padding.Empty;
            dd.Margin = Padding.Empty;
            dd.DropShadowEnabled = true;
            dd.Size = new Size(Size1 + 2, Size1 + 2);
            dd.Items.Add(host);

            _open = dd;
            dd.Show(new Point(centreX - (Size1 / 2), bottomY - Size1 - 8));
        }

        public static void HideNow()
        {
            if (_open != null) { try { _open.Close(); _open.Dispose(); } catch { } }
            _open = null;
        }
    }
}
