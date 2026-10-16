// Flyout.cs -- the card that opens when a lit cell is clicked, and where a decision gets made.
//
// Closed-choice asks get one button per option and answering is a single click. Anything
// open-ended gets "Take it to Command Center", which opens the COO terminal with the question staged --
// because Command Center is the interface, and a free-text answer belongs in a conversation, not a text box
// on a taskbar popup.
//
// This is a real Form, unlike the strip and tooltip: it must take focus to be clicked, and it must
// close when focus leaves. That is the one place in this app where activation is wanted.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CommandCenter
{
    internal class AskRecord
    {
        public string Id, Officer, Title, Why, Unblocks, Urgency, LedgerRow;
        public List<string> Options = new List<string>();
        public DateTime RaisedAt;
    }

    internal static class Asks
    {
        /// <summary>All open asks, most urgent first, then oldest first.</summary>
        public static List<AskRecord> Open(string officer)
        {
            List<AskRecord> outp = new List<AskRecord>();
            try
            {
                if (!Directory.Exists(Paths.State)) return outp;
                string[] files = Directory.GetFiles(Paths.State, "*.ask.json");
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileName(files[i]);
                    string stem = name.Substring(0, name.Length - ".ask.json".Length);
                    int dot = stem.IndexOf('.');
                    if (dot <= 0) continue;
                    string off = stem.Substring(0, dot);
                    if (officer != null && off != officer) continue;

                    string b = Path.Combine(Paths.State, stem);
                    if (File.Exists(b + ".answer.json")) continue;
                    if (File.Exists(b + ".closed.json")) continue;
                    if (Snoozed(b + ".snoozed.json")) continue;

                    AskRecord r = Parse(File.ReadAllText(files[i]));
                    if (r == null) continue;

                    // IDENTITY COMES FROM THE FILENAME, NEVER FROM THE CONTENTS.
                    //
                    // Answer() and Snooze() build a path out of these two fields. Taking them from
                    // inside the JSON took a file path from officer-authored text: an `id` of
                    // `..\..\BrainAI\tasks\outbound-loop` walks straight out of the state directory,
                    // and Path.Combine DISCARDS its first argument entirely if the second is rooted,
                    // so an `officer` of `C:` relocates the write to another drive. This app already
                    // treats officer-written files as untrusted -- it is why the spawn allowlist is
                    // compiled in rather than read from disk -- and an ask is exactly such a file.
                    //
                    // The filename is safe by construction: it came back from enumerating this one
                    // directory, so it cannot name another. The allowlist and charset checks below
                    // are belt and braces on top of that.
                    r.Officer = off;
                    r.Id = stem.Substring(dot + 1);
                    if (!Roster.IsAllowed(r.Officer) || !SafeId(r.Id))
                    {
                        Log.Warn("asks: ignoring '" + name + "' -- officer or id is not a plain " +
                                 "identifier, so it cannot be answered safely");
                        continue;
                    }
                    outp.Add(r);
                }
            }
            catch (Exception ex) { Log.Warn("asks: scan failed: " + ex.Message); }

            outp.Sort(delegate(AskRecord a, AskRecord b2)
            {
                int ua = Rank(a.Urgency), ub = Rank(b2.Urgency);
                if (ua != ub) return ub - ua;
                return a.RaisedAt.CompareTo(b2.RaisedAt);
            });
            return outp;
        }

        /// <summary>
        /// Letters, digits, dash, underscore. No dot (it is the officer/id separator), no slash, no
        /// colon -- so nothing that survives this can traverse a directory or root a path.
        /// </summary>
        private static bool SafeId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                       || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        private static int Rank(string u)
        {
            if (u == "high") return 2;
            if (u == "normal") return 1;
            return 0;
        }

        private static bool Snoozed(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                Match m = Regex.Match(File.ReadAllText(path), "\"until\"\\s*:\\s*\"([^\"]+)\"");
                DateTime t;
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, out t)) return t > DateTime.Now;
            }
            catch { }
            return false;
        }

        // Hand-rolled rather than JavaScriptSerializer: these files are small, the shape is fixed,
        // and a parser that cannot throw on malformed input keeps a bad file from blanking the UI.
        private static AskRecord Parse(string j)
        {
            try
            {
                AskRecord r = new AskRecord();
                r.Id = S(j, "id"); r.Officer = S(j, "officer"); r.Title = S(j, "title");
                r.Why = S(j, "why"); r.Unblocks = S(j, "unblocks");
                r.Urgency = S(j, "urgency"); r.LedgerRow = S(j, "ledgerRow");
                DateTime t;
                r.RaisedAt = DateTime.TryParse(S(j, "raisedAt"), out t) ? t : DateTime.Now;

                Match om = Regex.Match(j, "\"options\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                if (om.Success)
                    foreach (Match v in Regex.Matches(om.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                        r.Options.Add(Unescape(v.Groups[1].Value));

                // Only the title is required of the CONTENTS -- it is the one field with nothing but
                // a display job. Id and Officer are overwritten from the filename by the caller, so
                // demanding them here would reject a perfectly answerable ask over a missing field.
                if (string.IsNullOrEmpty(r.Title)) return null;
                return r;
            }
            catch { return null; }
        }

        private static string S(string j, string key)
        {
            Match m = Regex.Match(j, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Unescape(m.Groups[1].Value) : "";
        }

        private static string Unescape(string s)
        {
            return s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
        }

        /// <summary>
        /// Answer an ask. THE COMMAND CENTER IS THE ONLY WRITER of answer.json -- the officer reads
        /// it and writes its own closed.json. One writer per file, which is how SEC-020 stays fixed.
        /// Writing is guarded so a double-click cannot produce two answers.
        /// </summary>
        public static bool Answer(AskRecord r, string answer)
        {
            try
            {
                string f = SidecarPath(r, "answer");
                if (f == null) return false;
                if (File.Exists(f)) { Log.Info("asks: " + r.Id + " already answered -- ignoring"); return false; }

                string json = string.Format(
                    "{{\"id\":\"{0}\",\"answer\":\"{1}\",\"answeredAt\":\"{2}\",\"by\":\"flyout\"}}\n",
                    Escape(r.Id), Escape(answer), DateTime.Now.ToString("o"));
                string tmp = f + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(f)) { File.Delete(tmp); return false; }
                File.Move(tmp, f);
                Log.Info("asks: answered " + r.Id + " (" + r.Officer + ") = " + answer);
                return true;
            }
            catch (Exception ex) { Log.Err("asks: answer failed", ex); return false; }
        }

        /// <summary>"Not today." A control plane that cannot be told "later" gets ignored instead.</summary>
        public static void Snooze(AskRecord r)
        {
            try
            {
                string f = SidecarPath(r, "snoozed");
                if (f == null) return;
                DateTime until = DateTime.Now.Date.AddDays(1).AddHours(9);
                File.WriteAllText(f,
                    string.Format("{{\"id\":\"{0}\",\"until\":\"{1}\"}}\n",
                                  Escape(r.Id), until.ToString("o")),
                    new UTF8Encoding(false));
                Log.Info("asks: snoozed " + r.Id + " until " + until.ToString("g"));
            }
            catch (Exception ex) { Log.Err("asks: snooze failed", ex); }
        }

        /// <summary>
        /// The path of one sidecar beside an ask, or null if it would not land in the state
        /// directory. Re-validated HERE rather than trusted from the caller: these two methods are
        /// the only things in the app that turn an ask into a filesystem write, so the guarantee
        /// belongs at the write, not at whoever happened to build the record.
        /// </summary>
        private static string SidecarPath(AskRecord r, string kind)
        {
            if (r == null || !Roster.IsAllowed(r.Officer) || !SafeId(r.Id))
            {
                Log.Err("asks: REFUSED to write " + kind + " -- '" + (r == null ? "?" : r.Officer) +
                        "/" + (r == null ? "?" : r.Id) + "' is not a plain identifier");
                return null;
            }
            return Path.Combine(Paths.State, r.Officer + "." + r.Id + "." + kind + ".json");
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }
    }

    internal class FlyoutForm : Form
    {
        private static FlyoutForm _open;

        // ONE SET OF FONTS FOR THE PROCESS, NOT ONE PER ROW PER OPEN.
        //
        // WinForms does NOT dispose Control.Font -- a control never owns the font it is handed. So
        // every `l.Font = new Font(...)` here leaked a GDI font handle each time a flyout opened,
        // one per officer row plus chrome, permanently. Clicking through four desks a few times an
        // hour for a week is thousands of handles in a process explicitly built to run for weeks,
        // and the heartbeat's gdi= counter would have climbed with no other explanation.
        //
        // These live for the life of the process by design, which is why nothing disposes them.
        private static readonly Font FRow = new Font("Consolas", 8.5f);
        private static readonly Font FHeader = new Font("Segoe UI", 11f, FontStyle.Bold);
        private static readonly Font FBold = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        private static readonly Font FMuted = new Font("Segoe UI", 8.75f);
        private static readonly Font FBtn = new Font("Segoe UI", 9f);
        private static readonly Font FBtnBold = new Font("Segoe UI", 9f, FontStyle.Bold);

        // One 1x1 measuring surface as well, for the same reason: it was built and torn down once
        // per row per open. Only ever touched from the UI thread.
        private static readonly Bitmap MeasureBmp = new Bitmap(1, 1);
        private static readonly Graphics MeasureG = Graphics.FromImage(MeasureBmp);

        public static void ShowFor(string title, List<AskRecord> asks, List<OfficerState> officers,
                                   int anchorX, int anchorTopY)
        {
            CloseAny();
            FlyoutForm f = new FlyoutForm(title, asks, officers);
            _open = f;
            f.PositionAbove(anchorX, anchorTopY);
            f.Show();
            f.Activate();
        }

        public static void CloseAny()
        {
            if (_open != null && !_open.IsDisposed) { try { _open.Close(); } catch { } }
            _open = null;
        }

        private FlyoutForm(string title, List<AskRecord> asks, List<OfficerState> officers)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            BackColor = Theme.IsLight ? Color.FromArgb(0xFA, 0xFA, 0xFA) : Color.FromArgb(0x1F, 0x1F, 0x1F);
            ForeColor = Theme.IsLight ? Color.FromArgb(0x18, 0x18, 0x18) : Color.FromArgb(0xEC, 0xEC, 0xEC);
            Padding = new Padding(1);
            Width = 460;

            FlowLayoutPanel root = new FlowLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.FlowDirection = FlowDirection.TopDown;
            root.WrapContents = false;
            root.AutoScroll = true;
            root.Padding = new Padding(14, 12, 14, 12);
            Controls.Add(root);

            root.Controls.Add(Header(title));

            if (asks != null && asks.Count > 0)
                for (int i = 0; i < asks.Count; i++) AddAskCard(root, asks[i]);

            if (officers != null && officers.Count > 0)
            {
                root.Controls.Add(Divider());
                for (int i = 0; i < officers.Count; i++) AddOfficerRow(root, officers[i]);
            }

            if ((asks == null || asks.Count == 0) && (officers == null || officers.Count == 0))
                root.Controls.Add(Muted("Nothing waiting on you here."));

            root.Controls.Add(Divider());
            Button coo = Flat("Talk to Command Center", true);
            coo.Click += delegate { Close(); CommandTerminal.Open(null); };
            root.Controls.Add(coo);

            int h = 0;
            foreach (Control c in root.Controls) h += c.Height + c.Margin.Vertical;
            Height = Math.Min(620, h + root.Padding.Vertical + 8);
        }

        private void AddAskCard(FlowLayoutPanel root, AskRecord r)
        {
            root.Controls.Add(Bold(r.Title));
            if (!string.IsNullOrEmpty(r.Why)) root.Controls.Add(Muted("Yours because: " + r.Why));
            if (!string.IsNullOrEmpty(r.Unblocks)) root.Controls.Add(Muted("Unblocks: " + r.Unblocks));
            root.Controls.Add(Muted(r.Officer + " \u00b7 raised " + Ago(r.RaisedAt) +
                (string.IsNullOrEmpty(r.LedgerRow) ? "" : " \u00b7 " + r.LedgerRow)));

            FlowLayoutPanel row = new FlowLayoutPanel();
            row.FlowDirection = FlowDirection.LeftToRight;
            row.AutoSize = true;
            row.WrapContents = true;
            row.Width = 420;
            row.Margin = new Padding(0, 6, 0, 2);

            if (r.Options.Count > 0)
            {
                for (int i = 0; i < r.Options.Count; i++)
                {
                    string opt = r.Options[i];
                    Button b = Flat(opt, i == 0);
                    AskRecord captured = r;
                    b.Click += delegate
                    {
                        // Disable immediately: two rapid clicks must not produce two answers, and
                        // Answer() also refuses to overwrite an existing file.
                        b.Enabled = false;
                        if (Asks.Answer(captured, opt)) { Close(); }
                        else b.Enabled = true;
                    };
                    row.Controls.Add(b);
                }
            }
            else
            {
                // Open-ended -> the COO, with the question staged to a file rather than shoved
                // into a command line.
                Button b = Flat("Take it to Command Center", true);
                AskRecord captured = r;
                b.Click += delegate
                {
                    Close();
                    CommandTerminal.Open("Officer: " + captured.Officer + "\nQuestion: " + captured.Title +
                                      "\nWhy it is mine: " + captured.Why +
                                      "\nUnblocks: " + captured.Unblocks);
                };
                row.Controls.Add(b);
            }

            Button snooze = Flat("Not today", false);
            AskRecord cap2 = r;
            snooze.Click += delegate { Asks.Snooze(cap2); Close(); };
            row.Controls.Add(snooze);

            root.Controls.Add(row);
            root.Controls.Add(Divider());
        }

        private void AddOfficerRow(FlowLayoutPanel root, OfficerState o)
        {
            string row = o.LastRow;
            if (row != null && row.Length > 48) row = row.Substring(0, 46) + "..";
            Label l = Muted(string.Format("{0,-9} {1,-10} {2,-6} {3}",
                o.Officer.Name, IconRenderer.StateName(o.State), o.AgeText(), row == null ? "(no ledger yet)" : row));
            l.Font = FRow;
            root.Controls.Add(l);
        }

        // ---- chrome --------------------------------------------------------------------------

        private Label Header(string t)
        {
            Label l = new Label();
            l.Text = t;
            l.Font = FHeader;
            l.AutoSize = false; l.Width = 420; l.Height = 24;
            l.Margin = new Padding(0, 0, 0, 6);
            return l;
        }

        private Label Bold(string t)
        {
            Label l = new Label();
            l.Text = t;
            l.Font = FBold;
            l.AutoSize = false; l.Width = 420;
            l.Height = TextHeight(t, l.Font, 420);
            l.Margin = new Padding(0, 4, 0, 2);
            return l;
        }

        private Label Muted(string t)
        {
            Label l = new Label();
            l.Text = t;
            l.Font = FMuted;
            l.ForeColor = Theme.IsLight ? Color.FromArgb(0x5A, 0x5A, 0x5A) : Color.FromArgb(0xA0, 0xA0, 0xA0);
            l.AutoSize = false; l.Width = 420;
            l.Height = TextHeight(t, l.Font, 420);
            l.Margin = new Padding(0, 0, 0, 1);
            return l;
        }

        private int TextHeight(string t, Font f, int w)
        {
            return (int)Math.Ceiling(MeasureG.MeasureString(t, f, w).Height) + 2;
        }

        private Panel Divider()
        {
            Panel p = new Panel();
            p.Height = 1; p.Width = 420;
            p.BackColor = Theme.IsLight ? Color.FromArgb(0xE2, 0xE2, 0xE2) : Color.FromArgb(0x33, 0x33, 0x33);
            p.Margin = new Padding(0, 8, 0, 8);
            return p;
        }

        private Button Flat(string text, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = false;
            b.Height = 30;
            b.Width = Math.Max(84, TextWidth(text) + 28);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Theme.IsLight ? Color.FromArgb(0xD0, 0xD0, 0xD0) : Color.FromArgb(0x44, 0x44, 0x44);
            b.BackColor = primary ? Theme.Accent
                : (Theme.IsLight ? Color.FromArgb(0xF2, 0xF2, 0xF2) : Color.FromArgb(0x2C, 0x2C, 0x2C));
            b.ForeColor = primary ? Color.White : ForeColor;
            b.Font = primary ? FBtnBold : FBtn;
            b.Cursor = Cursors.Hand;
            b.Margin = new Padding(0, 0, 8, 6);
            return b;
        }

        private int TextWidth(string t)
        {
            return (int)Math.Ceiling(MeasureG.MeasureString(t, FBtn).Width);
        }

        public void PositionAbove(int anchorX, int anchorTopY)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x = anchorX - 8;
            if (x + Width > wa.Right - 8) x = wa.Right - Width - 8;
            if (x < wa.Left + 8) x = wa.Left + 8;
            int y = anchorTopY - Height - 10;
            if (y < wa.Top + 8) y = wa.Top + 8;
            Location = new Point(x, y);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Close();   // click-away dismiss; approvals must never trap focus
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.IsLight ? Color.FromArgb(0xD8, 0xD8, 0xD8) : Color.FromArgb(0x3A, 0x3A, 0x3A)))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        private static string Ago(DateTime t)
        {
            TimeSpan d = DateTime.Now - t;
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + "h ago";
            return (int)d.TotalDays + "d ago";
        }
    }
}
