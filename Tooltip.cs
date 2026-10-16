// Tooltip.cs -- hover detail, without stealing focus.
//
// The strip is WS_EX_NOACTIVATE, and so is this. Hovering a desk must never interrupt what he is
// typing. That is also why this is a NativeWindow rather than a Form: a Form drags in activation
// logic and background painting we would only have to suppress.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace CommandCenter
{
    internal class TooltipWindow : NativeWindow, IDisposable
    {
        private bool _created;

        public void Ensure()
        {
            if (_created) return;
            CreateParams cp = new CreateParams();
            cp.Caption = "CommandCenterTip";
            cp.X = 0; cp.Y = 0; cp.Width = 10; cp.Height = 10;
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                       | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            CreateHandle(cp);
            _created = true;
        }

        public void Hide()
        {
            if (!_created) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
        }

        public void ShowAt(string text, int anchorX, int anchorBottomY)
        {
            if (string.IsNullOrEmpty(text)) { Hide(); return; }
            Ensure();

            bool light = Theme.IsLight;
            Color bg = light ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF2, 0x24, 0x24, 0x24);
            Color fg = light ? Color.FromArgb(0x18, 0x18, 0x18) : Color.FromArgb(0xEC, 0xEC, 0xEC);
            Color line = light ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x24, 255, 255, 255);

            using (Font f = new Font("Consolas", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                SizeF sz;
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics pg = Graphics.FromImage(probe))
                    sz = pg.MeasureString(text, f);

                int pad = 10;
                int w = (int)Math.Ceiling(sz.Width) + pad * 2;
                int h = (int)Math.Ceiling(sz.Height) + pad * 2;
                if (w < 120) w = 120;
                if (w > 720) w = 720;

                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        Rectangle r = new Rectangle(0, 0, w - 1, h - 1);
                        using (GraphicsPath p = Rounded(r, 8))
                        {
                            using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
                            using (Pen pen = new Pen(line, 1f)) g.DrawPath(pen, p);
                        }
                        using (SolidBrush tb = new SolidBrush(fg))
                            g.DrawString(text, f, tb, new RectangleF(pad, pad, w - pad * 2, h - pad * 2));
                    }

                    int x = anchorX;
                    int y = anchorBottomY - h - 8;
                    Rectangle screen = Screen.PrimaryScreen.Bounds;
                    if (x + w > screen.Right - 8) x = screen.Right - w - 8;
                    if (x < screen.Left + 8) x = screen.Left + 8;
                    if (y < screen.Top + 8) y = screen.Top + 8;

                    Push(bmp, x, y, w, h);
                }
            }
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private void Push(Bitmap bmp, int x, int y, int w, int h)
        {
            IntPtr screenDC = Native.GetDC(IntPtr.Zero);
            IntPtr memDC = Native.CreateCompatibleDC(screenDC);
            IntPtr hb = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hb = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDC, hb);
                Native.SIZE size = new Native.SIZE(w, h);
                Native.POINT src = new Native.POINT(0, 0);
                Native.POINT dst = new Native.POINT(x, y);
                Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
                bf.BlendOp = Native.AC_SRC_OVER; bf.BlendFlags = 0;
                bf.SourceConstantAlpha = 255; bf.AlphaFormat = Native.AC_SRC_ALPHA;
                Native.UpdateLayeredWindow(Handle, screenDC, ref dst, ref size, memDC, ref src, 0,
                    ref bf, Native.ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(memDC, old);
                if (hb != IntPtr.Zero) Native.DeleteObject(hb);
                if (memDC != IntPtr.Zero) Native.DeleteDC(memDC);
                if (screenDC != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Dispose()
        {
            if (_created && Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    internal static class Tooltip
    {
        private static TooltipWindow _w;

        public static void Show(Cell c, Span span, Native.RECT tray, int yOffset)
        {
            if (_w == null) _w = new TooltipWindow();
            if (c == null || string.IsNullOrEmpty(c.Tooltip)) { _w.Hide(); return; }
            _w.ShowAt(c.Tooltip, span.Left + c.Bounds.X, tray.Top + yOffset);
        }

        public static void HideNow() { if (_w != null) _w.Hide(); }

        public static void Dispose() { if (_w != null) { _w.Dispose(); _w = null; } }
    }
}
