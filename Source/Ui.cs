// Settings and About windows (black & green, ASCII style), tray icon states, wraps shelf page.
// C# 5 (built-in Windows compiler).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ITunesScrobbler
{
    // ================================================================ shared look
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(5, 8, 6);
        public static readonly Color Field = Color.FromArgb(11, 20, 14);
        public static readonly Color Text = Color.FromArgb(143, 207, 156);
        public static readonly Color Hi = Color.FromArgb(70, 255, 142);
        public static readonly Color Dim = Color.FromArgb(59, 110, 74);
        public static readonly Color Line = Color.FromArgb(27, 58, 40);
        public static readonly Color Warn = Color.FromArgb(255, 176, 64);
        public static readonly Color Bad = Color.FromArgb(255, 99, 84);

        public static Font Mono(float size, FontStyle style = FontStyle.Regular)
        {
            foreach (var name in new[] { "Cascadia Mono", "Consolas", "Lucida Console" })
            {
                var f = new Font(name, size, style);
                if (f.Name == name) return f;
                f.Dispose();
            }
            return new Font(FontFamily.GenericMonospace, size, style);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // dark title bar on Windows 10 (attribute 20 on 20H1+, 19 on older builds)
        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            }
            catch { }
        }

        public static readonly Color Muted = Color.FromArgb(104, 158, 120);   // readable but clearly "not now" (disabled buttons)

        // NoPrefix: an "&" is a real character here (e.g. "save & close"), not a shortcut marker
        public static Size Measure(string text, Font font) { return TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix); }

        // Exact width of one line of text. MeasureText adds a few px of "overhang" room (about 10 px for
        // bold text), which made the hover card lopsided. That extra is the same for any text, so:
        // width(text+text) - width(text) = the real width of text.
        public static int TightWidth(string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return Measure(text + text, font).Width - Measure(text, font).Width;
        }

        // height of text wrapped to a width
        public static Size MeasureWrapped(string text, Font font, int width)
        {
            return TextRenderer.MeasureText(text, font, new Size(width, 0), TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        // "──[ TITLE ]──────" filled with dashes up to the given pixel width
        public static string Rule(string title, Font font, int width)
        {
            string s = "──[ " + title.ToUpperInvariant() + " ]";
            while (Measure(s + "─", font).Width <= width) s += "─";
            return s;
        }
    }

    // "[x] label" toggle: a clickable line of text instead of a Windows checkbox
    class AsciiToggle : Label
    {
        bool on;
        string caption;
        public bool Checked { get { return on; } set { on = value; Redraw(); } }
        public string Caption { get { return caption; } set { caption = value; Redraw(); } }

        public AsciiToggle()
        {
            AutoSize = false; Cursor = Cursors.Hand; ForeColor = Theme.Text; UseMnemonic = false;
            Click += (s, e) => Checked = !Checked;
            MouseEnter += (s, e) => ForeColor = Theme.Hi;
            MouseLeave += (s, e) => ForeColor = Theme.Text;
        }

        void Redraw() { Text = (on ? "[x] " : "[ ] ") + (caption ?? ""); }
    }

    class AsciiButton : Button
    {
        public AsciiButton(string text, Color accent)
        {
            Text = "[ " + text + " ]";
            UseMnemonic = false;   // keeps the "&" visible
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor = ControlPaint.Dark(accent, 0.45f);
            FlatAppearance.MouseOverBackColor = Color.FromArgb(40, accent);
            FlatAppearance.MouseDownBackColor = Color.FromArgb(70, accent);
            BackColor = Theme.Field; ForeColor = accent;
            Cursor = Cursors.Hand; UseVisualStyleBackColor = false;
        }
        public AsciiButton(string text) : this(text, Theme.Hi) { }

        // Width and height that fit the text in the window's font. The font is set here on purpose:
        // a button only inherits the window's font once it's added to it, so measuring before that
        // used the small default font and the text got cut off.
        public void Fit(Font f)
        {
            Font = f;
            var sz = Theme.Measure(Text, f);
            Size = new Size(sz.Width + 34, sz.Height + 16);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            FlatAppearance.BorderColor = Enabled ? ControlPaint.Dark(ForeColor, 0.45f) : Theme.Line;
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        }

        // Windows draws disabled flat buttons with near-black text; draw them ourselves in a readable dim green
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            var r = ClientRectangle;
            using (var b = new SolidBrush(Theme.Field)) e.Graphics.FillRectangle(b, r);
            using (var p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, r, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    // ================================================================ themed scrollbar
    // A slim green scrollbar. The real Windows scrollbar still does the scrolling (mouse wheel, keys);
    // it's just pushed outside the visible area (see AsciiForm.ScrollBox), and this one mirrors it.
    class TermScrollBar : Control
    {
        public Func<int[]> Read;      // { content size, visible size, position }
        public Action<int> Write;     // scroll to a position
        int max, page, pos;
        bool drag, hot;
        int dragY, dragPos;

        public TermScrollBar()
        {
            DoubleBuffered = true; BackColor = Theme.Bg; TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        public void Sync()
        {
            if (Read == null) return;
            int[] r;
            try { r = Read(); } catch { return; }
            if (r[0] == max && r[1] == page && r[2] == pos) return;
            max = r[0]; page = r[1]; pos = r[2];
            Invalidate();
        }

        bool Scrollable { get { return page > 0 && max > page; } }

        Rectangle Thumb()
        {
            if (!Scrollable) return Rectangle.Empty;
            int track = Height - 4;
            int th = Math.Max(Math.Min(28, track), (int)((long)track * page / max));
            int range = max - page;
            int ty = 2 + (int)((long)(track - th) * Math.Max(0, Math.Min(pos, range)) / Math.Max(1, range));
            return new Rectangle(1, ty, Width - 2, th);
        }

        void SetPos(int v)
        {
            v = Math.Max(0, Math.Min(v, Math.Max(0, max - page)));
            if (Write != null) try { Write(v); } catch { }
            Sync();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            using (var p = new Pen(Color.FromArgb(14, 30, 20))) g.DrawLine(p, Width / 2, 2, Width / 2, Height - 3);   // the rail
            var t = Thumb();
            if (t.IsEmpty) return;
            var c = drag ? Color.FromArgb(46, 150, 88) : hot ? Theme.Dim : Color.FromArgb(36, 86, 58);
            using (var b = new SolidBrush(c)) g.FillRectangle(b, t);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Scrollable) return;
            var t = Thumb();
            if (t.Contains(e.Location)) { drag = true; dragY = e.Y; dragPos = pos; Capture = true; Invalidate(); }
            else SetPos(e.Y < t.Top ? pos - page * 9 / 10 : pos + page * 9 / 10);   // click on the rail = a page
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = Thumb().Contains(e.Location);
            if (over != hot) { hot = over; Invalidate(); }
            if (!drag) return;
            var t = Thumb();
            int free = Math.Max(1, Height - 4 - t.Height);
            SetPos(dragPos + (int)((long)(e.Y - dragY) * (max - page) / free));
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (drag) { drag = false; Capture = false; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot) { hot = false; Invalidate(); } }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (Scrollable) SetPos(pos - Math.Sign(e.Delta) * Math.Max(1, page / 8)); }
    }

    // a scrolling panel that says when it scrolled or its content changed size
    class ScrollInner : Panel
    {
        public event Action Scrolled;
        void Raise() { var h = Scrolled; if (h != null) h(); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x115 || m.Msg == 0x20A) Raise();   // WM_VSCROLL, WM_MOUSEWHEEL
        }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Raise(); }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Raise(); }
    }

    // a rich text box that says when it scrolled (any way: wheel, keys, new text)
    class SyncRichTextBox : RichTextBox
    {
        public event Action Scrolled;
        void Raise() { var h = Scrolled; if (h != null) h(); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x115 || m.Msg == 0x20A || m.Msg == 0x100 || m.Msg == 0x05) Raise();   // WM_VSCROLL, WM_MOUSEWHEEL, WM_KEYDOWN, WM_SIZE
        }
        protected override void OnVScroll(EventArgs e) { base.OnVScroll(e); Raise(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Raise(); }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        const int EM_GETFIRSTVISIBLELINE = 0xCE, EM_GETLINECOUNT = 0xBA, EM_LINESCROLL = 0xB6;

        // measured in lines: exact, whatever unit Windows uses for this control's scrollbar
        public int LineCount { get { return IsHandleCreated ? (int)SendMessage(Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero) : 0; } }
        public int FirstVisibleLine { get { return IsHandleCreated ? (int)SendMessage(Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : 0; } }
        public int VisibleLines { get { return Math.Max(1, ClientSize.Height / Math.Max(1, TextRenderer.MeasureText("Ag", Font).Height)); } }
        public void ScrollLines(int delta) { if (IsHandleCreated && delta != 0) SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)delta); }
    }

    // A window in the app's terminal look, with helpers that size everything from the real font metrics
    // (so nothing is cut off at 125% / 150% display scaling).
    class AsciiForm : Form
    {
        protected const int L = 18;
        protected int Wd = 720, y = 14;   // every page of the app window has the same width

        public AsciiForm()
        {
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Mono(10f);
            AutoScaleMode = AutoScaleMode.None;
            HandleCreated += (s, e) => Theme.DarkTitleBar(this);
        }

        protected int LineH(Font f) { return Theme.Measure("Ag[", f).Height + 8; }

        // The pages live as tabs inside the app window: their close buttons close that window.
        protected void CloseWindow()
        {
            var host = !TopLevel && Parent != null ? Parent.FindForm() : null;
            if (host != null) host.Close(); else Close();
        }

        // called each time the page's tab is opened
        public virtual void TabShown() { }

        protected Label Text_(string text, Color color, Font font = null)
        {
            var f = font ?? Font;
            var l = new Label { Text = text, ForeColor = color, Font = f, AutoSize = false, UseMnemonic = false };
            l.SetBounds(L, y, Wd, LineH(f));
            Controls.Add(l); y += LineH(f);
            return l;
        }

        protected const int BarW = 10, BarGap = 6;

        // A scrolling box with the green scrollbar. Add the content to the returned panel, contentW wide.
        // The panel is wider than what's visible by Windows' own scrollbar, which sits outside the visible part.
        protected ScrollInner ScrollBox(int height, out int contentW)
        {
            var clip = new Panel { BackColor = Theme.Bg };
            clip.SetBounds(L, y, Wd - BarW - BarGap, height);
            var inner = new ScrollInner { AutoScroll = true, BackColor = Theme.Bg };
            inner.SetBounds(0, 0, clip.Width + SystemInformation.VerticalScrollBarWidth, height);
            clip.Controls.Add(inner);
            var bar = new TermScrollBar();
            bar.SetBounds(L + Wd - BarW, y, BarW, height);
            bar.Read = () => new[] { inner.DisplayRectangle.Height, inner.ClientSize.Height, -inner.AutoScrollPosition.Y };
            bar.Write = v => { inner.AutoScrollPosition = new Point(0, v); };
            inner.Scrolled += bar.Sync;
            Controls.Add(clip); Controls.Add(bar);
            contentW = clip.Width;
            y += height;
            return inner;
        }

        // the same for a rich text box (the log)
        protected TermScrollBar ScrollText(SyncRichTextBox box, int height)
        {
            var clip = new Panel { BackColor = box.BackColor };
            clip.SetBounds(L, y, Wd - BarW - BarGap, height);
            box.ScrollBars = RichTextBoxScrollBars.ForcedVertical;
            box.SetBounds(0, 0, clip.Width + SystemInformation.VerticalScrollBarWidth, height);
            clip.Controls.Add(box);
            var bar = new TermScrollBar();
            bar.SetBounds(L + Wd - BarW, y, BarW, height);
            bar.Read = () => new[] { box.LineCount, box.VisibleLines, box.FirstVisibleLine };
            bar.Write = v => box.ScrollLines(v - box.FirstVisibleLine);
            box.Scrolled += bar.Sync;
            Controls.Add(clip); Controls.Add(bar);
            y += height;
            return bar;
        }

        // a line of text that wraps onto as many lines as it needs within the window width
        protected Label Note(string text, Color color, Font font, int indent)
        {
            int w = Wd - indent;
            var sz = Theme.MeasureWrapped(text, font, w - 10);   // a label keeps a few px of padding, so wrap a bit early
            var l = new Label { Text = text, ForeColor = color, Font = font, AutoSize = false, UseMnemonic = false };
            l.SetBounds(L + indent, y, w, sz.Height + 6);
            Controls.Add(l); y += sz.Height + 8;
            return l;
        }

        protected void Section(string title)
        {
            y += 8;
            Text_(Theme.Rule(title, Font, Wd - 4), Theme.Hi);
        }

        protected void Field(string label, Control box)
        {
            string k = label + " ";
            int labW = Theme.Measure("secret ........", Font).Width + 10;
            var l = new Label { Text = k + new string('.', Math.Max(2, 15 - k.Length)), ForeColor = Theme.Dim, AutoSize = false, UseMnemonic = false };
            int h = LineH(Font) + 2;
            l.SetBounds(L, y + 3, labW, h - 4);
            box.SetBounds(L + labW, y, Wd - labW, h);
            Controls.Add(l); Controls.Add(box); y += h + 6;
        }

        protected void KeyValue(string key, string value, Color valueColor, out Label valueLabel)
        {
            string k = key + " ";
            int labW = Theme.Measure("all-time ........", Font).Width + 10;
            var l = new Label { Text = k + new string('.', Math.Max(2, 17 - k.Length)), ForeColor = Theme.Dim, AutoSize = false, UseMnemonic = false };
            valueLabel = new Label { Text = value, ForeColor = valueColor, AutoSize = false, UseMnemonic = false };
            int h = LineH(Font);
            l.SetBounds(L, y, labW, h); valueLabel.SetBounds(L + labW, y, Wd - labW, h);
            Controls.Add(l); Controls.Add(valueLabel); y += h;
        }

        protected void Toggle(AsciiToggle t, string caption, bool value)
        {
            t.Caption = caption; t.Checked = value;
            t.SetBounds(L, y, Wd, LineH(Font));
            Controls.Add(t); y += LineH(Font);
        }

        protected void Link(string text, string target, int indent, bool small)
        {
            var l = new LinkLabel { Text = text, LinkColor = small ? Theme.Dim : Theme.Hi, ActiveLinkColor = Theme.Hi, VisitedLinkColor = small ? Theme.Dim : Theme.Hi,
                LinkBehavior = LinkBehavior.HoverUnderline, AutoSize = true, UseMnemonic = false, Font = small ? Theme.Mono(9f) : Font, Left = L + indent, Top = y };
            l.LinkClicked += (s, e) => SettingsForm.OpenUrl(target);
            Controls.Add(l); y += LineH(l.Font);
        }
    }

    // ================================================================ the app window: one window, a tab per page
    // The pages (friends, history, log, settings, about) are the AsciiForms, placed inside as tabs.
    // A page is made the first time its tab is opened. The window keeps its width; its height follows the page.
    class AppWindow : Form
    {
        public static readonly string[] Tabs = { "friends", "stats", "history", "log", "settings", "about" };
        readonly Dictionary<string, Func<AsciiForm>> make;
        readonly Dictionary<string, AsciiForm> pages = new Dictionary<string, AsciiForm>();
        readonly TabStrip strip;
        string current;

        public AppWindow(Icon icon, Dictionary<string, Func<AsciiForm>> factories)
        {
            make = factories;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Mono(10f);
            AutoScaleMode = AutoScaleMode.None;
            Icon = icon;
            HandleCreated += (s, e) => Theme.DarkTitleBar(this);

            strip = new TabStrip(Tabs, Font);
            strip.Location = Point.Empty;
            strip.Selected += name => ShowTab(name);
            Controls.Add(strip);
            FormClosed += (s, e) => { foreach (var p in pages.Values) try { p.Close(); } catch { } };
        }

        public string Current { get { return current; } }

        public void ShowTab(string name)
        {
            Func<AsciiForm> factory;
            if (!make.TryGetValue(name, out factory)) return;
            AsciiForm page;
            if (!pages.TryGetValue(name, out page))
            {
                page = factory();
                var size = page.ClientSize;
                page.TopLevel = false;
                page.FormBorderStyle = FormBorderStyle.None;
                page.ClientSize = size;
                page.Location = new Point(0, strip.Height);
                Controls.Add(page);
                pages[name] = page;
                page.Show();
            }
            foreach (var kv in pages) if (kv.Key != name) kv.Value.Visible = false;
            page.Visible = true;
            page.BringToFront();
            current = name;
            strip.Active = name;
            Text = "V's Last.fm Scrobbler // " + name;

            int w = Math.Max(page.Width, strip.MinWidth);
            strip.Width = w;
            ClientSize = new Size(w, strip.Height + page.Height);
            // a taller page mustn't push the window off the bottom of the screen
            var wa = Screen.FromControl(this).WorkingArea;
            if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
            page.TabShown();
        }

        // Esc closes, Ctrl+Tab / Ctrl+Shift+Tab go through the tabs
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                int i = Array.IndexOf(Tabs, current), step = (keyData & Keys.Shift) != 0 ? Tabs.Length - 1 : 1;
                ShowTab(Tabs[(Math.Max(0, i) + step) % Tabs.Length]);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // "[ friends ]  history  log  settings  about" with a rule underneath
    class TabStrip : Control
    {
        public event Action<string> Selected;
        readonly string[] names;
        readonly Rectangle[] boxes;
        string active = "";
        int hot = -1;
        public readonly int MinWidth;

        public TabStrip(string[] tabNames, Font f)
        {
            names = tabNames; Font = f;
            DoubleBuffered = true; BackColor = Theme.Bg; TabStop = false;
            int textH = Theme.Measure("Ag[", f).Height;
            Height = textH + 34;
            boxes = new Rectangle[names.Length];
            int x = 18;
            for (int i = 0; i < names.Length; i++)
            {
                int w = Theme.Measure("[ " + names[i] + " ]", f).Width + 10;
                boxes[i] = new Rectangle(x, 12, w, textH + 12);
                x += w + 4;
            }
            MinWidth = x + 18;
            Width = MinWidth;
        }

        public string Active { set { active = value ?? ""; Invalidate(); } }

        int At(Point p) { for (int i = 0; i < boxes.Length; i++) if (boxes[i].Contains(p)) return i; return -1; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            int baseY = Height - 6;
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, 18, baseY, Width - 18, baseY);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            for (int i = 0; i < names.Length; i++)
            {
                var r = boxes[i];
                bool on = names[i] == active;
                if (on)
                {
                    using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, r.X, r.Y, r.Width, baseY - r.Y);
                    using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, baseY - r.Y);
                    using (var p = new Pen(Theme.Field)) g.DrawLine(p, r.X + 1, baseY, r.Right - 2, baseY);   // open at the bottom, like a real tab
                    TextRenderer.DrawText(g, "[ " + names[i] + " ]", Font, r, Theme.Hi, flags);
                }
                else TextRenderer.DrawText(g, names[i], Font, r, i == hot ? Theme.Text : Theme.Dim, flags);
            }
        }

        // tabs are clickable, so they (and only they) get the hand cursor
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = At(e.Location);
            Cursor = i >= 0 && names[i] != active ? Cursors.Hand : Cursors.Default;
            if (i != hot) { hot = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = At(e.Location);
            if (i >= 0 && Selected != null) Selected(names[i]);
        }
    }

    // ================================================================ settings window
    class SettingsForm : AsciiForm
    {
        readonly Settings st;
        readonly TextBox keyBox = Box(), secretBox = Box(), discordId = Box();
        readonly Label accountLabel = new Label();
        readonly AsciiButton connectBtn = new AsciiButton("1. connect to last.fm"), finishBtn = new AsciiButton("2. i've allowed access"),
                             closeBtn = new AsciiButton("save & close");
        readonly TextBox msStep = Box(), artistStep = Box();
        readonly Label stepHint = new Label(), artistHint = new Label();
        readonly AsciiToggle startup = new AsciiToggle(), discordOn = new AsciiToggle(),
                             msOn = new AsciiToggle(), artistMsOn = new AsciiToggle(), friendsOn = new AsciiToggle(), wrapsOn = new AsciiToggle(),
                             updatesOn = new AsciiToggle();
        string pendingToken;

        static TextBox Box() { return new TextBox { BackColor = Theme.Field, ForeColor = Theme.Hi, BorderStyle = BorderStyle.FixedSingle }; }

        public SettingsForm(Settings settings, Icon icon)
        {
            st = settings;
            Text = "V's Last.fm Scrobbler // settings";
            Icon = icon;

            Text_("v's last.fm scrobbler  v" + Program.Version, Theme.Hi, Theme.Mono(13f, FontStyle.Bold));
            Text_("~$ ./settings", Theme.Dim);

            Section("last.fm");
            Field("api key", keyBox); keyBox.Text = st.ApiKey;
            Field("secret", secretBox); secretBox.Text = st.ApiSecret; secretBox.UseSystemPasswordChar = true;
            Link("get or view your api key at last.fm/api/accounts", "https://www.last.fm/api/accounts", 0, true);
            accountLabel.AutoSize = false; accountLabel.UseMnemonic = false;
            accountLabel.SetBounds(L, y + 4, Wd, LineH(Font)); Controls.Add(accountLabel); y += LineH(Font) + 8;
            connectBtn.Fit(Font); finishBtn.Fit(Font);
            connectBtn.Location = new Point(L, y);
            if (connectBtn.Width + 12 + finishBtn.Width <= Wd) finishBtn.Location = new Point(L + connectBtn.Width + 12, y);
            else { y += connectBtn.Height + 8; finishBtn.Location = new Point(L, y); }   // big display scaling: stack them
            finishBtn.Enabled = false;
            Controls.Add(connectBtn); Controls.Add(finishBtn); y += finishBtn.Height + 10;

            Section("discord");
            Toggle(discordOn, "show what i'm playing on discord", st.DiscordEnabled);
            Field("app id", discordId); discordId.Text = st.DiscordClientId;
            Link("create an app at discord.com/developers", "https://discord.com/developers/applications", 0, true);
            Note("name it \"Apple Music\" and paste its application id above", Theme.Dim, Theme.Mono(9f), 0);

            Section("notifications");
            var small = Theme.Mono(9f);
            int capW = Theme.Measure("[x] artist milestone", Font).Width + 14;   // both toggles get the same width so the boxes line up
            StepRow(msOn, "total milestone", capW, st.MilestonesEnabled, msStep, st.MilestoneEvery, new long[] { 1000, 2500, 5000, 10000, 25000, 50000, 100000 }, "scrobbles");
            HintLine(stepHint, small);
            StepRow(artistMsOn, "artist milestone", capW, st.ArtistMilestonesEnabled, artistStep, st.ArtistMilestoneEvery, new long[] { 50, 100, 250, 500, 1000, 2500, 5000 }, "per artist");
            HintLine(artistHint, small);
            msStep.TextChanged += (s, e) => UpdateHint();
            artistStep.TextChanged += (s, e) => UpdateHint();
            UpdateHint();
            Toggle(friendsOn, "a friend is listening to the same artist", st.FriendsEnabled);
            Toggle(wrapsOn, "monthly & yearly wraps are ready", st.WrapsEnabled);

            Section("system");
            Toggle(startup, "start with windows", Startup.IsEnabled);
            Toggle(updatesOn, "check for updates when the app starts", st.CheckUpdates);
            UpdateRow();
            y += 14;
            closeBtn.Fit(Font);
            closeBtn.Location = new Point(L + Wd - closeBtn.Width, y); Controls.Add(closeBtn);
            y += closeBtn.Height + 18;
            ClientSize = new Size(L * 2 + Wd, y);

            connectBtn.Click += (s, e) => Connect();
            finishBtn.Click += (s, e) => Finish();
            closeBtn.Click += (s, e) => { SaveKeys(); Startup.Set(startup.Checked); CloseWindow(); };
            AcceptButton = closeBtn;
            RefreshAccount();
        }

        // "[x] total milestone [-] [ 5,000 ] [+] scrobbles": [-]/[+] step through sensible values, or type any number
        void StepRow(AsciiToggle t, string caption, int captionW, bool on, TextBox box, long value, long[] presets, string tail)
        {
            int h = LineH(Font), x = L;
            t.Caption = caption; t.Checked = on;
            t.SetBounds(x, y, captionW, h); Controls.Add(t); x += captionW;
            var minus = StepLabel("[-]"); minus.SetBounds(x, y, Theme.Measure("[-]", Font).Width + 8, h); Controls.Add(minus); x += minus.Width + 2;
            box.Text = value.ToString("N0", CultureInfo.InvariantCulture); box.TextAlign = HorizontalAlignment.Center;
            int bw = Theme.Measure("000,000", Font).Width + 14;
            box.SetBounds(x, y + 1, bw, h - 2); Controls.Add(box); x += bw + 2;
            var plus = StepLabel("[+]"); plus.SetBounds(x, y, Theme.Measure("[+]", Font).Width + 8, h); Controls.Add(plus); x += plus.Width + 6;
            var tl = new Label { Text = tail, ForeColor = Theme.Text, AutoSize = false, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
            tl.SetBounds(x, y, Math.Max(Theme.Measure(tail, Font).Width + 8, L + Wd - x), h); Controls.Add(tl);
            minus.Click += (s, e) =>
            {
                long cur = ParseStep(box.Text, value), next = presets.Where(p => p < cur).DefaultIfEmpty(presets[0]).Max();
                box.Text = next.ToString("N0", CultureInfo.InvariantCulture);
            };
            plus.Click += (s, e) =>
            {
                long cur = ParseStep(box.Text, value), next = presets.Where(p => p > cur).DefaultIfEmpty(presets[presets.Length - 1]).Min();
                box.Text = next.ToString("N0", CultureInfo.InvariantCulture);
            };
            y += h + 2;
        }

        static Label StepLabel(string text)
        {
            var l = new Label { Text = text, ForeColor = Theme.Dim, AutoSize = false, UseMnemonic = false, Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleCenter };
            l.MouseEnter += (s, e) => l.ForeColor = Theme.Hi;
            l.MouseLeave += (s, e) => l.ForeColor = Theme.Dim;
            return l;
        }

        static long ParseStep(string text, long fallback)
        {
            long v;
            string digits = new string((text ?? "").Where(c => char.IsDigit(c)).ToArray());
            return long.TryParse(digits, out v) && v > 0 ? v : fallback;
        }

        // small dim explanation under a milestone row, lined up with the caption text
        void HintLine(Label l, Font f)
        {
            l.ForeColor = Theme.Dim; l.AutoSize = false; l.UseMnemonic = false; l.Font = f;
            int indent = Theme.Measure("[x] ", Font).Width;
            l.SetBounds(L + indent, y - 2, Wd - indent, LineH(f)); Controls.Add(l); y += LineH(f) + 2;
        }

        void UpdateHint()
        {
            var en = CultureInfo.InvariantCulture;
            long every = Math.Max(100, ParseStep(msStep.Text, st.MilestoneEvery));
            stepHint.Text = st.LastMilestone >= 0
                ? "all your last.fm scrobbles · next at " + ((st.LastMilestone / every + 1) * every).ToString("N0", en)
                : "all your last.fm scrobbles · next one worked out after the first check";
            long a = Math.Max(10, ParseStep(artistStep.Text, st.ArtistMilestoneEvery));
            artistHint.Text = "when one artist reaches " + a.ToString("N0", en) + ", " + (a * 2).ToString("N0", en) + ", " + (a * 3).ToString("N0", en) + "… plays";
        }

        // [ check for updates ]  up to date (v4.1)   ->   [ update to v4.2 ]  v4.2 is out
        readonly AsciiButton updateBtn = new AsciiButton("check for updates");
        readonly Label updateLabel = new Label();
        bool checkingUpdate;

        void UpdateRow()
        {
            y += 6;
            updateBtn.Fit(Font);
            updateBtn.Location = new Point(L, y);
            updateLabel.AutoSize = false; updateLabel.UseMnemonic = false; updateLabel.ForeColor = Theme.Dim;
            updateLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(updateBtn); Controls.Add(updateLabel);
            ShowUpdateState(null);
            updateBtn.Click += (s, e) =>
            {
                if (Updater.Available) { if (Updater.InstallRequested != null) Updater.InstallRequested(); updateLabel.Text = "updating\u2026 the app restarts by itself"; return; }
                CheckUpdatesNow();
            };
            y += updateBtn.Height + 4;
        }

        void ShowUpdateState(string message)
        {
            bool newer = Updater.Available;
            updateBtn.Text = newer ? "[ update to v" + Updater.LatestVersion + " ]" : "[ check for updates ]";
            updateBtn.Fit(Font);
            updateBtn.ForeColor = newer ? Theme.Warn : Theme.Hi;
            updateBtn.FlatAppearance.BorderColor = ControlPaint.Dark(updateBtn.ForeColor, 0.45f);
            updateLabel.Text = message ?? (newer ? "v" + Updater.LatestVersion + " is out (you have v" + Program.Version + ")"
                : Updater.LatestTag != null ? "you're up to date (v" + Program.Version + ")" : "you have v" + Program.Version);
            updateLabel.ForeColor = newer ? Theme.Warn : Theme.Dim;
            updateLabel.SetBounds(updateBtn.Right + 12, updateBtn.Top, Math.Max(10, L + Wd - updateBtn.Right - 12), updateBtn.Height);
        }

        void CheckUpdatesNow()
        {
            if (checkingUpdate) return;
            checkingUpdate = true;
            updateBtn.Enabled = false;
            updateLabel.Text = "checking github\u2026"; updateLabel.ForeColor = Theme.Dim;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                try { Updater.Check(); }
                catch (Exception ex) { error = "couldn't reach github: " + ex.Message.ToLowerInvariant(); }
                if (error == null && Updater.Changed != null) Updater.Changed();   // the tray menu shows "update to ..." too
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        checkingUpdate = false;
                        updateBtn.Enabled = true;
                        ShowUpdateState(error);
                        if (error != null) updateLabel.ForeColor = Theme.Warn;
                    }));
                }
                catch { }
            });
        }

        void RefreshAccount()
        {
            bool ok = st.SessionKey != "";
            accountLabel.Text = ok ? "> connected as " + st.UserName : "> not connected yet";
            accountLabel.ForeColor = ok ? Theme.Hi : Theme.Bad;
        }

        void SaveKeys()
        {
            string k = keyBox.Text.Trim(), s = secretBox.Text.Trim();
            if (k != st.ApiKey || s != st.ApiSecret) { st.SessionKey = ""; st.UserName = ""; }   // new key -> new login
            st.ApiKey = k; st.ApiSecret = s;
            st.DiscordEnabled = discordOn.Checked;
            st.DiscordClientId = discordId.Text.Trim();
            st.MilestonesEnabled = msOn.Checked;
            st.ArtistMilestonesEnabled = artistMsOn.Checked;
            st.MilestoneEvery = Math.Max(100, ParseStep(msStep.Text, st.MilestoneEvery));
            st.ArtistMilestoneEvery = Math.Max(10, ParseStep(artistStep.Text, st.ArtistMilestoneEvery));
            st.FriendsEnabled = friendsOn.Checked;
            st.WrapsEnabled = wrapsOn.Checked;
            st.CheckUpdates = updatesOn.Checked;
            st.Save();
        }

        void Connect()
        {
            SaveKeys();
            if (st.ApiKey == "" || st.ApiSecret == "") { MessageBox.Show(this, "Enter your API key and shared secret first."); return; }
            try
            {
                var api = new LastFm(st.ApiKey, st.ApiSecret);
                pendingToken = api.GetToken();
                OpenUrl(api.AuthUrl(pendingToken));
                finishBtn.Enabled = true;
                accountLabel.Text = "> click \"yes, allow access\" in your browser, then press 2";
                accountLabel.ForeColor = Theme.Warn;
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't reach Last.fm:\n" + ex.Message + "\n\nCheck the API key and secret."); }
        }

        void Finish()
        {
            try
            {
                string sk, user;
                new LastFm(st.ApiKey, st.ApiSecret).GetSession(pendingToken, out sk, out user);
                st.SessionKey = sk; st.UserName = user; st.Save();
                finishBtn.Enabled = false;
                Log.Write("Connected as " + user);
                RefreshAccount();
            }
            catch (Exception ex) { MessageBox.Show(this, "Not authorised yet:\n" + ex.Message + "\n\nAllow access in the browser first, then try again."); }
        }

        public static void OpenUrl(string url) { try { Process.Start(url); } catch { } }
    }

    // ================================================================ about window
    class AboutForm : AsciiForm
    {
        public AboutForm(Settings st, Icon icon)
        {
            Text = "V's Last.fm Scrobbler // about";
            Icon = icon;

            var logoFont = Theme.Mono(9f, FontStyle.Bold);
            string logoText = WrapPage.Banner("V'S");
            var logo = new Label { Text = logoText, ForeColor = Theme.Hi, Font = logoFont, AutoSize = true, UseMnemonic = false, Left = L, Top = y };
            Controls.Add(logo); y += Theme.Measure("X", logoFont).Height * 5 + 14;
            Text_("v's last.fm scrobbler", Theme.Hi, Theme.Mono(13f, FontStyle.Bold));
            Text_("~$ ./about", Theme.Dim);

            Section("info");
            Label dummy, allTime;
            KeyValue("version", Program.Version + (Updater.Available ? "  \u00b7  v" + Updater.LatestVersion + " is out (tray menu > update)" : ""),
                Updater.Available ? Theme.Warn : Theme.Hi, out dummy);
            KeyValue("user", st.UserName != "" ? st.UserName : "not connected", st.UserName != "" ? Theme.Hi : Theme.Bad, out dummy);
            KeyValue("all-time", "loading...", Theme.Hi, out allTime);
            KeyValue("made by", "Vista, 2026", Theme.Hi, out dummy);

            Section("links");
            Link("> your last.fm profile", st.UserName != "" ? "https://www.last.fm/user/" + Uri.EscapeDataString(st.UserName) : "https://www.last.fm", 0, false);
            Link("> app folder", Settings.AppDir, 0, false);
            Link("> readme", Path.Combine(Settings.AppDir, "README.txt"), 0, false);
            Link("> releases on github", Updater.RepoUrl + "/releases", 0, false);
            y += 12;
            var close = new AsciiButton("close");
            close.Fit(Font); close.Location = new Point(L + Wd - close.Width, y);
            close.Click += (s, e) => CloseWindow();
            Controls.Add(close); CancelButton = close; AcceptButton = close;
            y += close.Height + 18;
            ClientSize = new Size(L * 2 + Wd, y);

            if (st.IsConfigured && st.UserName != "")
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string text;
                    try { text = new LastFm(st.ApiKey, st.ApiSecret).GetPlayCount(st.UserName).ToString("N0", CultureInfo.InvariantCulture) + " scrobbles"; }
                    catch { text = "couldn't reach last.fm"; }
                    try { BeginInvoke((Action)(() => allTime.Text = text)); } catch { }
                });
            else allTime.Text = "-";
        }
    }

    // ================================================================ dark tray menu
    // Paints the tray menu in the green terminal look. It only runs while the menu is open:
    // Windows asks it to draw when the menu appears or an item is hovered, nothing in between.
    class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Bg; } }
        public override Color MenuBorder { get { return Color.FromArgb(27, 58, 40); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(18, 48, 31); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(18, 48, 31); } }
        public override Color ImageMarginGradientBegin { get { return Theme.Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Bg; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Bg; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Theme.Bg; } }
    }

    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public Color StatusDot = Color.Empty;   // set by the tray app to match the icon badge

        // The status line at the top of the menu. The item's own Text only sets the menu's width;
        // this is what's drawn. When it's too long it scrolls (the tray app's timer moves StatusScroll).
        public string StatusLine = "";
        public int StatusScroll;                // px scrolled so far
        public bool StatusOverflows;            // set while painting: the line doesn't fit
        public int StatusCycle;                 // text width + gap = one full loop

        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        static string Kind(ToolStripItem item) { return item.Tag as string ?? ""; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            string k = Kind(e.Item);
            if (k == "head" || k == "status" || !e.Item.Selected || !e.Item.Enabled) return;   // headers and status never highlight
            using (var b = new SolidBrush(Color.FromArgb(18, 48, 31))) e.Graphics.FillRectangle(b, 2, 0, e.Item.Width - 4, e.Item.Height);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            string k = Kind(e.Item);
            bool hot = e.Item.Selected && e.Item.Enabled && k != "head" && k != "status";
            if (k == "status") { DrawStatus(e); return; }
            if (k == "head")
            {
                // disabled items: draw the text ourselves, Windows would grey it out
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, k == "head" ? Theme.Dim : Theme.Hi,
                    TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
                return;
            }
            if (k == "danger") e.TextColor = Theme.Bad;
            else if (k == "update") e.TextColor = hot ? Theme.Hi : Theme.Warn;
            else e.TextColor = hot ? Theme.Hi : Theme.Text;
            // every item starts with two spaces; the hovered one gets a ">" there instead
            if (hot && e.Text.StartsWith("  ")) e.Text = "> " + e.Text.Substring(2);
            base.OnRenderItemText(e);
        }

        // "● playing: artist - title": the dot stays put, the text scrolls inside its box when it doesn't fit
        void DrawStatus(ToolStripItemTextRenderEventArgs e)
        {
            var g = e.Graphics;
            var tr = e.TextRectangle;
            int d = Math.Max(6, e.TextFont.Height / 2);
            if (StatusDot != Color.Empty)
            {
                var r = new Rectangle(tr.X, tr.Y + (tr.Height - d) / 2, d, d);
                var oldMode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(StatusDot)) g.FillEllipse(b, r);
                g.SmoothingMode = oldMode;
            }
            int textX = tr.X + d + Math.Max(6, d);
            var area = new Rectangle(textX, tr.Y, Math.Max(10, e.Item.Width - textX - 12), tr.Height);
            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
            string line = StatusLine ?? "";
            var size = TextRenderer.MeasureText(g, line, e.TextFont, Size.Empty, flags);
            int ty = area.Y + (area.Height - size.Height) / 2;

            StatusOverflows = size.Width > area.Width;
            if (!StatusOverflows)
            {
                TextRenderer.DrawText(g, line, e.TextFont, new Point(area.X, ty), Theme.Hi, flags);
                return;
            }

            // marquee: the line, a gap, then the line again, so it loops without jumping
            int gap = TextRenderer.MeasureText(g, "   ·   ", e.TextFont, Size.Empty, flags).Width;
            StatusCycle = size.Width + gap;
            var saved = g.Save();
            g.SetClip(area);
            int x = area.X - StatusScroll;
            TextRenderer.DrawText(g, line, e.TextFont, new Point(x, ty), Theme.Hi, flags);
            TextRenderer.DrawText(g, "   ·   ", e.TextFont, new Point(x + size.Width, ty), Theme.Dim, flags);
            TextRenderer.DrawText(g, line, e.TextFont, new Point(x + StatusCycle, ty), Theme.Hi, flags);
            g.Restore(saved);

            // soft edges so the text fades in and out instead of being chopped
            int fade = Math.Min(18, area.Width / 6);
            if (StatusScroll > 0) Fade(g, new Rectangle(area.X, area.Y, fade, area.Height), true);
            Fade(g, new Rectangle(area.Right - fade, area.Y, fade, area.Height), false);
        }

        static void Fade(Graphics g, Rectangle r, bool left)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            var from = left ? Theme.Bg : Color.FromArgb(0, Theme.Bg);
            var to = left ? Color.FromArgb(0, Theme.Bg) : Theme.Bg;
            // the brush starts 1 px early: GDI+ gradients otherwise leave a hairline at the edge
            using (var b = new LinearGradientBrush(new Rectangle(r.X - 1, r.Y, r.Width + 2, r.Height), from, to, LinearGradientMode.Horizontal))
                g.FillRectangle(b, r);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Dim; base.OnRenderArrow(e); }
    }

    // ================================================================ hover card (replaces the plain Windows tooltip)
    // Shown when the mouse rests on the tray icon: a small black & green card with the app name and
    // the full status line. The icon's own tooltip text is left empty so Windows doesn't show its grey one too.
    class TrayHoverCard : Form
    {
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer showDelay = new System.Windows.Forms.Timer { Interval = 450 };
        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 100 };
        // level meter + progress line: ~30 frames a second, only while the card is open and a song plays
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 33 };
        readonly Font titleFont = Theme.Mono(9.75f, FontStyle.Bold), textFont = Theme.Mono(9.75f),
                     trackFont = Theme.Mono(10.5f, FontStyle.Bold), smallFont = Theme.Mono(8.75f), letterFont = Theme.Mono(20f, FontStyle.Bold);
        Point lastMove;
        DateTime lastMoveAt;
        Rectangle iconRect;          // where the icon is on screen; empty when Windows won't say
        bool armed;
        string status = "";
        Color dot = Color.Empty;
        Track playing;               // what's playing (null when nothing is)
        bool isPlayingState;         // the app's state is "playing" (not paused / offline / logged out)

        // layout, worked out in Relayout() and used by OnPaint()
        bool showStatus;
        Rectangle rCover, rLabel, rTrack, rArtist, rAlbum, rStatus;
        const int CoverSz = 64;
        public Func<bool> Suppressed;   // true while the tray menu is open
        public Func<PlaybackInfo> Progress;   // where the song is (set by the tray app)

        // bottom row: level meter under the cover (2 x 13 blocks, both rows exactly the same size),
        // progress line with the scrobble mark next to it
        const int SegW = 4, SegGap = 1, SegH = 4, RowGap = 2, MeterH = SegH * 2 + RowGap, MinBarW = 120;
        static readonly Color[] SegOn = { Color.FromArgb(70, 255, 142), Color.FromArgb(242, 225, 76), Color.FromArgb(255, 99, 84) };
        static readonly Color[] SegOff = { Color.FromArgb(15, 42, 26), Color.FromArgb(42, 39, 16), Color.FromArgb(46, 20, 17) };
        static readonly SolidBrush[] OnBrush = SegOn.Select(c => new SolidBrush(c)).ToArray(), OffBrush = SegOff.Select(c => new SolidBrush(c)).ToArray();
        Rectangle rMeter, rProgress, rBar;
        bool showRow, hasProgress;
        int litL, litR;
        string progressKey = "";

        const int Pad = 10, MaxTextW = 340;
        const string Title = "v's last.fm scrobbler";

        public TrayHoverCard(NotifyIcon icon)
        {
            tray = icon;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            Size = new Size(200, 60);

            tray.MouseMove += (s, e) => IconMoved();
            Covers.Ready += CoverArrived;
            tray.MouseDown += (s, e) => HideCard();
            showDelay.Tick += (s, e) =>
            {
                showDelay.Stop();
                if (StillOverIcon() && !IsSuppressed()) ShowCard(); else HideCard();
            };
            watch.Tick += (s, e) => { if (!StillOverIcon() || IsSuppressed()) HideCard(); };
            anim.Tick += (s, e) => AnimTick();
        }

        // never takes focus away from what you're doing
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8;   // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST
                return cp;
            }
        }

        bool IsSuppressed() { return Suppressed != null && Suppressed(); }

        // text = the status line; nowPlaying = the song (or null); playingState = the app is happily playing
        public void SetStatus(string text, Color dotColor, Track nowPlaying, bool playingState)
        {
            text = text ?? "";
            bool sameSong = (nowPlaying == null && playing == null)
                || (nowPlaying != null && playing != null && nowPlaying.Id == playing.Id && nowPlaying.ToString() == playing.ToString() && nowPlaying.Album == playing.Album);
            if (text == status && dotColor == dot && sameSong && playingState == isPlayingState) return;
            status = text; dot = dotColor; isPlayingState = playingState;
            playing = nowPlaying == null ? null : new Track { Artist = nowPlaying.Artist, Name = nowPlaying.Name, Album = nowPlaying.Album, Id = nowPlaying.Id };
            if (playing != null) { playing.Normalize(); Covers.ThumbFor(playing); }   // starts fetching the cover so it's ready on hover
            if (Visible) { Relayout(); Place(); Invalidate(); UpdateAnim(); }
        }

        // a cover finished downloading (background thread)
        void CoverArrived()
        {
            if (playing == null || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (Visible) Invalidate(); })); } catch { }
        }

        void IconMoved()
        {
            lastMove = Cursor.Position;
            lastMoveAt = DateTime.UtcNow;
            if (Visible || armed || IsSuppressed()) return;
            armed = true;
            iconRect = FindIconRect();
            showDelay.Start();
            watch.Start();
        }

        bool StillOverIcon()
        {
            var p = Cursor.Position;
            if (!iconRect.IsEmpty) { var r = iconRect; r.Inflate(1, 1); return r.Contains(p); }
            // Windows didn't say where the icon is: it sends mouse-move events while the cursor is over it,
            // so "hasn't moved since the last one" or "moved very recently" means it's still there
            return p == lastMove || (DateTime.UtcNow - lastMoveAt).TotalMilliseconds < 200;
        }

        void ShowCard()
        {
            Relayout();
            Place();
            if (!Visible) Show();
            UpdateAnim();
        }

        public void HideCard()
        {
            showDelay.Stop(); watch.Stop(); armed = false;
            StopAnim();
            if (Visible) Hide();
        }

        void UpdateAnim()
        {
            if (Visible && showRow) { if (!anim.Enabled) anim.Start(); }
            else StopAnim();
        }

        void StopAnim()
        {
            anim.Stop();
            AudioMeter.Release();
            litL = litR = 0;
        }

        // the progress snapshot, if it belongs to the song on the card and the length is known
        PlaybackInfo CurrentInfo()
        {
            var i = Progress != null ? Progress() : null;
            return i != null && playing != null && i.TrackId == playing.Id && i.Length > 0 ? i : null;
        }

        static double PosOf(PlaybackInfo i) { return Math.Max(0, Math.Min(i.Length, i.Pos + (DateTime.UtcNow - i.At).TotalSeconds)); }

        static string Clock(double sec) { int s = (int)Math.Max(0, sec); return (s / 60) + ":" + (s % 60).ToString("00"); }

        void AnimTick()
        {
            if (!Visible || !showRow) { StopAnim(); return; }

            // meter: jumps up at once, falls one block per frame (smooth, like an LED meter)
            float l, r;
            int nl = 0, nr = 0;
            if (AudioMeter.Read(out l, out r)) { nl = AudioMeter.Lit(l); nr = AudioMeter.Lit(r); }
            nl = Math.Max(nl, litL - 1); nr = Math.Max(nr, litR - 1);
            if (nl != litL || nr != litR) { litL = nl; litR = nr; Invalidate(rMeter); }

            // progress: the line appears once the length is known; repaint only when a pixel or the text changes
            var info = CurrentInfo();
            if ((info != null) != hasProgress) { Relayout(); Place(); Invalidate(); return; }
            if (info != null)
            {
                string key = BarFill(info) + "|" + info.Scrobbled + "|" + info.ScrobbleAt + "|" + info.Note;
                if (key != progressKey) { progressKey = key; Invalidate(rProgress); }
            }
        }

        int BarFill(PlaybackInfo info) { return (int)Math.Round(rBar.Width * PosOf(info) / info.Length); }

        int DotSize { get { return Math.Max(6, textFont.Height / 2); } }

        static Size One(string text, Font f) { return Theme.Measure(text ?? "", f); }

        void Relayout()
        {
            int dotW = DotSize + 8;
            var ts = Theme.Measure(Title, titleFont);
            int y = Pad + 1 + ts.Height + 8, right = Pad + 1 + Theme.TightWidth(Title, titleFont);

            if (playing != null)
            {
                // [cover]  ● now playing
                //          Track name (up to two lines)
                //          Artist
                //          Album
                int tx = Pad + 1 + CoverSz + 12, maxW = MaxTextW - CoverSz - 12;
                rCover = new Rectangle(Pad + 1, y, CoverSz, CoverSz);
                // heights first, so the text column can sit centred next to the cover
                int labelH = One("Ag", smallFont).Height, oneTrack = One("Ag", trackFont).Height;
                var trs = Theme.MeasureWrapped(playing.Name, trackFont, maxW);
                int trackH = Math.Min(trs.Height, oneTrack * 2 + 2);
                bool oneLine = trackH <= oneTrack + 1;
                int artistH = One("Ag", textFont).Height, albumH = playing.Album != "" ? labelH : 0;
                int colH = labelH + 3 + trackH + 2 + artistH + (albumH > 0 ? 1 + albumH : 0);
                int ty = y + Math.Max(0, (CoverSz - colH) / 2);

                // drawing boxes get the full measured width (Windows decides on "…" from that);
                // the card's width comes from the exact widths, so the margins match on both sides
                rLabel = new Rectangle(tx, ty, Math.Min(maxW, One("now playing", smallFont).Width + dotW), labelH); ty += labelH + 3;
                rTrack = new Rectangle(tx, ty, Math.Min(maxW, trs.Width + 2), trackH); ty += trackH + 2;
                rArtist = new Rectangle(tx, ty, Math.Min(maxW, One(playing.Artist, textFont).Width + 2), artistH); ty += artistH + 1;
                rAlbum = albumH > 0 ? new Rectangle(tx, ty, Math.Min(maxW, One(playing.Album, smallFont).Width + 2), albumH) : Rectangle.Empty;
                if (albumH > 0) ty += albumH;
                int textRight = tx + Math.Min(maxW, Math.Max(Theme.TightWidth("now playing", smallFont) + dotW - 2,
                    Math.Max(oneLine ? Theme.TightWidth(playing.Name, trackFont) : trs.Width,
                    Math.Max(Theme.TightWidth(playing.Artist, textFont), Theme.TightWidth(playing.Album, smallFont)))));
                right = Math.Max(right, textRight);
                y = Math.Max(rCover.Bottom, ty) + 4;

                // bottom row (only while it's playing): meter under the cover, progress line beside it
                showRow = isPlayingState;
                var info = showRow ? CurrentInfo() : null;
                hasProgress = info != null;
                rProgress = Rectangle.Empty;
                if (showRow)
                {
                    y += 6;
                    int lineH = labelH;
                    rMeter = new Rectangle(Pad + 1, y + Math.Max(0, (lineH - MeterH) / 2), CoverSz, MeterH);
                    if (hasProgress)
                    {
                        right = Math.Max(right, tx + MinBarW + 10 + Theme.TightWidth(Clock(info.Length), smallFont));
                        rProgress = new Rectangle(tx, y, 0, lineH * 2 + 2);   // width once the card's width is known
                        y += lineH * 2 + 2 + 4;
                    }
                    else y += Math.Max(lineH, MeterH) + 4;
                }
            }
            else { showRow = false; hasProgress = false; rProgress = Rectangle.Empty; }

            // the status line: always when nothing plays, otherwise only when something's off (paused, offline...)
            showStatus = playing == null || !isPlayingState;
            if (showStatus && playing != null) y += 6;
            if (showStatus)
            {
                var ss = Theme.MeasureWrapped(status, textFont, MaxTextW - dotW);
                rStatus = new Rectangle(Pad + 1 + dotW, y, ss.Width + 2, Math.Max(ss.Height, textFont.Height));
                bool single = ss.Height <= textFont.Height + 1;
                right = Math.Max(right, rStatus.X + (single ? Theme.TightWidth(status, textFont) : ss.Width));
                y = rStatus.Bottom + 4;
            }
            if (hasProgress)
            {
                // the bar: the space left of the length text, centred on the first line
                rProgress.Width = right - rProgress.X;
                var pi = CurrentInfo();
                int lenW = pi != null ? Theme.TightWidth(Clock(pi.Length), smallFont) : 0;
                rBar = new Rectangle(rProgress.X, rProgress.Y + One("Ag", smallFont).Height / 2 - 1, Math.Max(10, rProgress.Width - lenW - 10), 3);
            }
            progressKey = "";
            Size = new Size(right + Pad + 1, y + Pad - 2);
            // a drawing box may stick out past the right margin by its overhang allowance: harmless, the card clips it
        }

        // above the icon (taskbar at the bottom, or the hidden-icons flyout), below it for a top taskbar,
        // beside it for a side taskbar; always fully on screen
        void Place()
        {
            var a = !iconRect.IsEmpty ? iconRect : new Rectangle(lastMove.X - 8, lastMove.Y - 8, 16, 16);
            var wa = Screen.FromPoint(new Point(a.X + a.Width / 2, a.Y + a.Height / 2)).WorkingArea;
            int x = a.X + a.Width / 2 - Width / 2, y;
            if (a.Left >= wa.Right) { x = a.Left - Width - 8; y = a.Y + a.Height / 2 - Height / 2; }
            else if (a.Right <= wa.Left) { x = a.Right + 8; y = a.Y + a.Height / 2 - Height / 2; }
            else if (a.Bottom <= wa.Top) y = a.Bottom + 8;
            else y = a.Top - Height - 8;
            x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - Width - 4));
            y = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - Height - 4));
            Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            // a meter / progress frame only repaints the bottom row: skip the rest
            if (showRow && e.ClipRectangle.Top > rCover.Bottom && !showStatus) { PaintRow(g); return; }
            using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var p = new Pen(Theme.Dim)) g.DrawLine(p, 1, 1, Width - 2, 1);   // a brighter top edge, like a lit panel
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            var one = flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, Title, titleFont, new Point(Pad + 1, Pad + 1), Theme.Hi, flags | TextFormatFlags.SingleLine);

            if (playing != null)
            {
                var img = Covers.ThumbFor(playing);
                if (img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, rCover);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
                else
                {
                    using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, rCover);
                    string letter = playing.Artist.Length > 0 ? playing.Artist.Substring(0, 1).ToUpperInvariant() : "?";
                    TextRenderer.DrawText(g, letter, letterFont, rCover, Theme.Dim, flags | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, rCover.X, rCover.Y, rCover.Width - 1, rCover.Height - 1);

                int lx = rLabel.X;
                if (isPlayingState && dot != Color.Empty) { DrawDot(g, lx, rLabel.Y, smallFont.Height); lx += DotSize + 6; }
                TextRenderer.DrawText(g, "now playing", smallFont, new Rectangle(lx, rLabel.Y, rLabel.Right - lx + DotSize, rLabel.Height), Theme.Dim, one);
                TextRenderer.DrawText(g, playing.Name, trackFont, rTrack, Theme.Hi, flags | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, playing.Artist, textFont, rArtist, Theme.Text, one);
                if (!rAlbum.IsEmpty) TextRenderer.DrawText(g, playing.Album, smallFont, rAlbum, Theme.Dim, one);
                if (showRow) PaintRow(g);
            }

            if (showStatus)
            {
                if (dot != Color.Empty) DrawDot(g, Pad + 1, rStatus.Y, textFont.Height);
                TextRenderer.DrawText(g, status, textFont, new Rectangle(rStatus.X, rStatus.Y, rStatus.Width, rStatus.Height + 4), Theme.Text, flags | TextFormatFlags.WordBreak);
            }
        }

        void PaintRow(Graphics g)
        {
            // level meter: whole pixels only, so both rows come out exactly the same height
            for (int row = 0; row < 2; row++)
            {
                int lit = row == 0 ? litL : litR, yy = rMeter.Y + row * (SegH + RowGap);
                for (int i = 0; i < AudioMeter.Segments; i++)
                {
                    int zone = i < 9 ? 0 : i < 11 ? 1 : 2;
                    g.FillRectangle(i < lit ? OnBrush[zone] : OffBrush[zone], rMeter.X + i * (SegW + SegGap), yy, SegW, SegH);
                }
            }

            var info = hasProgress ? CurrentInfo() : null;
            if (info == null) return;
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int lineH = One("Ag", smallFont).Height;
            var bar = rBar;
            using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, bar);
            int fill = BarFill(info);
            if (fill > 0) using (var b = new SolidBrush(Theme.Text)) g.FillRectangle(b, bar.X, bar.Y, fill, bar.Height);
            if (info.ScrobbleAt >= 0 && info.ScrobbleAt <= info.Length)
            {
                int mx = bar.X + (int)Math.Round(bar.Width * info.ScrobbleAt / info.Length) - 1;
                using (var b = new SolidBrush(info.Scrobbled ? Theme.Hi : Theme.Dim)) g.FillRectangle(b, mx, bar.Y - 4, 3, bar.Height + 8);
            }
            string len = Clock(info.Length);
            TextRenderer.DrawText(g, len, smallFont, new Point(rProgress.Right - Theme.TightWidth(len, smallFont), rProgress.Y), Theme.Dim, flags);

            string note; Color c;
            if (info.Scrobbled) { note = "\u2713 scrobbled"; c = Theme.Hi; }
            else if (info.ScrobbleAt > info.Length) { note = "not enough left to scrobble"; c = Theme.Dim; }
            else if (info.ScrobbleAt >= 0) { note = "scrobbles at " + Clock(info.ScrobbleAt); c = Theme.Dim; }
            else { note = info.Note; c = Theme.Dim; }
            TextRenderer.DrawText(g, note, smallFont, new Rectangle(rProgress.X, rProgress.Y + lineH + 2, rProgress.Width, lineH), c, flags | TextFormatFlags.EndEllipsis);
        }

        void DrawDot(Graphics g, int x, int lineY, int lineH)
        {
            int d = DotSize;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, x, lineY + (lineH - d) / 2, d, d);
            g.SmoothingMode = SmoothingMode.Default;
        }

        // ---- where Windows put the icon (Windows 7+). NotifyIcon keeps the window handle and id private.
        [StructLayout(LayoutKind.Sequential)]
        struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem; }
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("shell32.dll")]
        static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        Rectangle FindIconRect()
        {
            try
            {
                var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var wf = typeof(NotifyIcon).GetField("window", bf);
                var idf = typeof(NotifyIcon).GetField("id", bf);
                if (wf == null || idf == null) return Rectangle.Empty;
                var nw = wf.GetValue(tray) as NativeWindow;
                if (nw == null || nw.Handle == IntPtr.Zero) return Rectangle.Empty;
                var nid = new NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)), hWnd = nw.Handle, uID = (int)idf.GetValue(tray) };
                RECT r;
                if (Shell_NotifyIconGetRect(ref nid, out r) != 0) return Rectangle.Empty;
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                // only trust it if the cursor really is inside (display scaling can put it in other units)
                var test = rect; test.Inflate(2, 2);
                return test.Contains(Cursor.Position) ? rect : Rectangle.Empty;
            }
            catch { return Rectangle.Empty; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Covers.Ready -= CoverArrived;
                showDelay.Dispose(); watch.Dispose(); anim.Dispose();
                foreach (var f in new[] { titleFont, textFont, trackFont, smallFont, letterFont }) f.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ================================================================ tray icon states
    enum TrayState { Idle, Playing, Paused, Offline, NotLoggedIn }

    static class TrayIcons
    {
        static readonly Dictionary<TrayState, Icon> cache = new Dictionary<TrayState, Icon>();

        // the app icon with a small status dot in the corner (matches the mockup)
        public static Icon For(TrayState state, Icon baseIcon)
        {
            Icon ic;
            if (cache.TryGetValue(state, out ic)) return ic;
            var size = SystemInformation.SmallIconSize;
            try
            {
                using (var src = new Icon(baseIcon, size)) using (var srcBmp = src.ToBitmap())
                using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        var attr = new ImageAttributes();
                        if (state == TrayState.Paused)
                            attr.SetColorMatrix(new ColorMatrix { Matrix33 = 0.55f });
                        else if (state == TrayState.NotLoggedIn)
                            attr.SetColorMatrix(new ColorMatrix(new[] {
                                new[] { .3f, .3f, .3f, 0, 0 }, new[] { .59f, .59f, .59f, 0, 0 }, new[] { .11f, .11f, .11f, 0, 0 },
                                new[] { 0f, 0, 0, 1, 0 }, new[] { 0f, 0, 0, 0, 1 } }));
                        g.DrawImage(srcBmp, new Rectangle(0, 0, size.Width, size.Height), 0, 0, srcBmp.Width, srcBmp.Height, GraphicsUnit.Pixel, attr);

                        Color? dot = null;
                        if (state == TrayState.Playing) dot = Color.FromArgb(35, 197, 82);
                        else if (state == TrayState.Paused) dot = Color.FromArgb(154, 160, 166);
                        else if (state == TrayState.Offline) dot = Color.FromArgb(255, 176, 32);
                        else if (state == TrayState.NotLoggedIn) dot = Color.FromArgb(229, 55, 43);
                        if (dot.HasValue)
                        {
                            float d = size.Width * 0.5f;
                            var rect = new RectangleF(size.Width - d, size.Height - d, d - 0.5f, d - 0.5f);
                            using (var ring = new SolidBrush(Color.FromArgb(20, 20, 20))) g.FillEllipse(ring, rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2);
                            using (var b = new SolidBrush(dot.Value)) g.FillEllipse(b, rect);
                        }
                    }
                    ic = Icon.FromHandle(bmp.GetHicon());   // kept for the app's lifetime (five icons at most)
                }
            }
            catch { ic = baseIcon; }
            cache[state] = ic;
            return ic;
        }
    }

    // ================================================================ wraps shelf (Wraps\index.html)
    // One section per year, newest first: the year's wrap as a wide card on top, then its months.
    static class WrapsShelf
    {
        static string MetaPath { get { return Path.Combine(Settings.Dir, "wraps.txt"); } }
        public static string IndexPath { get { return Path.Combine(MonthlyWrap.Folder, "index.html"); } }
        static readonly object Gate = new object();
        static readonly CultureInfo En = CultureInfo.InvariantCulture;

        // Month = "yyyy-MM", or "yyyy" for a year wrap; Strip = a year's scrobbles per month ("312,280,...")
        class Meta
        {
            public string File = "", Month = "", TopArtist = "", TopAlbum = "", AlbumArtist = "", Cover = "", Strip = "";
            public bool Partial; public int Scrobbles;
            public bool IsYear { get { return Month.Length == 4; } }
            public string YearKey { get { return Month.Length >= 4 ? Month.Substring(0, 4) : Month; } }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        static List<string> ReadLines() { return File.Exists(MetaPath) ? File.ReadAllLines(MetaPath, Encoding.UTF8).ToList() : new List<string>(); }

        // called after each wrap is saved
        public static void Record(WrapData w, string fileName)
        {
            lock (Gate)
            {
                try
                {
                    var lines = ReadLines();
                    lines.RemoveAll(l => l.StartsWith(fileName + "\t", StringComparison.OrdinalIgnoreCase));
                    var al = w.Albums.Count > 0 ? w.Albums[0] : null;
                    string strip = "";
                    if (w.Year)
                    {
                        var n = new int[12];
                        foreach (var m in w.Months) if (m.Month.Year == w.From.Year) n[m.Month.Month - 1] = m.N;
                        strip = string.Join(",", n.Select(x => x.ToString(En)).ToArray());
                    }
                    lines.Add(string.Join("\t", new[] { Clean(fileName), w.Year ? w.From.ToString("yyyy", En) : w.From.ToString("yyyy-MM", En), w.Partial ? "1" : "0", w.Scrobbles.ToString(En),
                        Clean(w.Artists.Count > 0 ? w.Artists[0].Name : ""), Clean(al != null ? al.Name : ""), Clean(al != null ? al.Sub : ""), Clean(al != null ? al.Image : ""), strip }));
                    File.WriteAllLines(MetaPath, lines.ToArray(), new UTF8Encoding(false));
                }
                catch (Exception ex) { Log.Write("Wraps shelf: couldn't save details: " + ex.Message); }
            }
        }

        // a "so far" wrap that's been replaced by the finished one
        public static void Forget(string fileName)
        {
            lock (Gate)
            {
                try
                {
                    var lines = ReadLines();
                    if (lines.RemoveAll(l => l.StartsWith(fileName + "\t", StringComparison.OrdinalIgnoreCase)) > 0)
                        File.WriteAllLines(MetaPath, lines.ToArray(), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        static Dictionary<string, Meta> ReadMeta()
        {
            var d = new Dictionary<string, Meta>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in ReadLines())
                {
                    var f = line.Split('\t');
                    if (f.Length < 8) continue;
                    int n; int.TryParse(f[3], out n);
                    d[f[0]] = new Meta { File = f[0], Month = f[1], Partial = f[2] == "1", Scrobbles = n, TopArtist = f[4], TopAlbum = f[5], AlbumArtist = f[6], Cover = f[7], Strip = f.Length > 8 ? f[8] : "" };
                }
            }
            catch { }
            return d;
        }

        static string E(string s) { return WebUtility.HtmlEncode(s ?? ""); }

        // builds Wraps\index.html from the wrap files that exist
        public static string Build()
        {
            lock (Gate)
            {
                Directory.CreateDirectory(MonthlyWrap.Folder);
                var meta = ReadMeta();
                var tapes = new List<Meta>();
                foreach (var path in Directory.GetFiles(MonthlyWrap.Folder, "*.html"))
                {
                    string name = Path.GetFileName(path);
                    if (name.Equals("index.html", StringComparison.OrdinalIgnoreCase)) continue;
                    Meta m;
                    if (!meta.TryGetValue(name, out m))
                    {
                        // no details saved (made by an old version): work it out from the file name
                        string stem = Path.GetFileNameWithoutExtension(name);
                        int sp = stem.IndexOf(' ');
                        m = new Meta { File = name, Month = sp > 0 ? stem.Substring(0, sp) : stem, Partial = name.Contains("so far") };
                    }
                    tapes.Add(m);
                }
                int months = tapes.Count(t => !t.IsYear), years = tapes.Count(t => t.IsYear);

                var h = new StringBuilder();
                h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>wraps · shelf</title>");
                h.Append("<style>").Append(WrapPage.Css).Append(ShelfCss).Append("</style></head><body><main>");
                h.Append("<header><div class=\"prompt mono\">~$ ls ./wraps<span class=\"cursor\">█</span></div>");
                h.Append("<pre class=\"banner\" aria-label=\"Wraps\">").Append(WrapPage.Banner("WRAPS")).Append("</pre>");
                h.Append("<div class=\"period mono\">").Append(months).Append(months == 1 ? " month" : " months");
                if (years > 0) h.Append(" · ").Append(years).Append(years == 1 ? " year" : " years");
                h.Append(" · pick one</div></header>");

                if (tapes.Count == 0) h.Append("<section class=\"card shelfempty mono\">no wraps yet. tray menu &gt; \"make a wrap of this month so far\"</section>");
                int thisYear = DateTime.Now.Year;
                foreach (var yg in tapes.GroupBy(t => t.YearKey).OrderByDescending(g => g.Key, StringComparer.Ordinal))
                {
                    int y;
                    bool isNum = int.TryParse(yg.Key, out y);
                    h.Append("<h2 class=\"yearhead mono\"><span class=\"muted\">──[</span> ").Append(E(yg.Key)).Append(" <span class=\"muted\">]</span><i></i></h2>");
                    h.Append("<div class=\"shelf\">");
                    var yearTapes = yg.Where(t => t.IsYear).OrderBy(t => t.Partial).ToList();
                    if (yearTapes.Count > 0) YearCard(h, yearTapes[0]);
                    else if (isNum && y < thisYear)
                        h.Append("<div class=\"card yearmissing mono\">no wrap of ").Append(y).Append(" as a whole yet · tray menu &gt; \"make a wrap of ").Append(y).Append("\"</div>");
                    else if (isNum && y == thisYear)
                        h.Append("<div class=\"card yearmissing mono\">your ").Append(y).Append(" wrap arrives on 1 january · want a peek? tray menu &gt; \"make a wrap of this year so far\"</div>");
                    foreach (var mt in yg.Where(t => !t.IsYear).OrderByDescending(t => t.Month, StringComparer.Ordinal).ThenBy(t => t.Partial))
                        MonthCard(h, mt);
                    h.Append("</div>");
                }
                h.Append("<footer class=\"mono\">// updated ").Append(E(DateTime.Now.ToString("d MMM yyyy HH:mm", En).ToLowerInvariant())).Append(" by v's last.fm scrobbler</footer></main></body></html>");
                File.WriteAllText(IndexPath, h.ToString(), new UTF8Encoding(false));
                return IndexPath;
            }
        }

        static void Cover(StringBuilder h, Meta t, string fallback)
        {
            if (!string.IsNullOrEmpty(t.Cover)) h.Append("<img class=\"wcover\" src=\"").Append(E(t.Cover)).Append("\" alt=\"\" loading=\"lazy\" referrerpolicy=\"no-referrer\">");
            else h.Append("<div class=\"wcover ph mono\">").Append(E(fallback)).Append("</div>");
        }

        static void MonthCard(StringBuilder h, Meta t)
        {
            DateTime dt;
            bool ok = DateTime.TryParseExact(t.Month, "yyyy-MM", En, DateTimeStyles.None, out dt);
            string title = ok ? dt.ToString("MMMM", En).ToLowerInvariant() : t.Month;
            h.Append("<a class=\"card wrapcard\" href=\"").Append(E(Uri.EscapeDataString(t.File))).Append("\">");
            Cover(h, t, title.Length > 0 ? title.Substring(0, 1).ToUpperInvariant() : "?");
            h.Append("<div class=\"wtext\"><div class=\"label mono\">// ").Append(E(t.Month)).Append(t.Partial ? " · so far" : "").Append("</div>");
            h.Append("<div class=\"wtitle\">").Append(E(title)).Append("</div>");
            if (t.Scrobbles > 0) h.Append("<div class=\"mono hi\">").Append(t.Scrobbles.ToString("N0", En)).Append(" scrobbles</div>");
            if (t.TopArtist != "") h.Append("<div class=\"s\">#1 ").Append(E(t.TopArtist)).Append("</div>");
            h.Append("<div class=\"wopen mono\">&gt; open</div></div></a>");
        }

        // the year's wrap: full width, bigger, with a little bar per month
        static void YearCard(StringBuilder h, Meta t)
        {
            h.Append("<a class=\"card wrapcard yearcard\" href=\"").Append(E(Uri.EscapeDataString(t.File))).Append("\">");
            Cover(h, t, t.Month);
            h.Append("<div class=\"wtext\"><div class=\"label mono\">// the whole year").Append(t.Partial ? " · so far" : "").Append("</div>");
            h.Append("<div class=\"wtitle\">").Append(E(t.Month)).Append("</div>");
            if (t.Scrobbles > 0) h.Append("<div class=\"mono hi\">").Append(t.Scrobbles.ToString("N0", En)).Append(" scrobbles</div>");
            if (t.TopArtist != "") h.Append("<div class=\"s\">#1 ").Append(E(t.TopArtist)).Append("</div>");
            var counts = (t.Strip ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => { int v; int.TryParse(x, out v); return v; }).ToList();
            if (counts.Count == 12 && counts.Max() > 0)
            {
                int max = counts.Max();
                string[] names = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
                h.Append("<div class=\"ystrip\" aria-hidden=\"true\">");
                for (int i = 0; i < 12; i++)
                    h.Append("<b title=\"").Append(names[i]).Append(": ").Append(counts[i].ToString("N0", En)).Append("\" style=\"height:")
                     .Append((counts[i] * 100.0 / max).ToString("0.#", En)).Append("%\"></b>");
                h.Append("</div>");
            }
            h.Append("<div class=\"wopen mono\">&gt; open</div></div></a>");
        }

        const string ShelfCss = @"
.shelf{display:grid;grid-template-columns:repeat(auto-fill,minmax(330px,1fr));gap:14px}
.wrapcard{display:flex;gap:16px;align-items:center;padding:14px;text-decoration:none;color:var(--text);transition:border-color .12s,transform .12s}
.wrapcard:hover{border-color:var(--hi);transform:translateY(-2px)}
.wcover{width:110px;height:110px;border-radius:8px;object-fit:cover;flex:none;box-shadow:0 0 0 1px var(--line)}
.wcover.ph{display:flex;align-items:center;justify-content:center;background:var(--faint);color:var(--hi);font-size:44px;font-weight:700}
.wtext{display:flex;flex-direction:column;gap:3px;min-width:0;flex:1}
.wtitle{font-size:24px;font-weight:700;letter-spacing:-.5px}
.wopen{margin-top:6px;color:var(--muted);font-size:13px}
.wrapcard:hover .wopen{color:var(--hi)}
.shelfempty{padding:20px;color:var(--muted)}
.yearhead{display:flex;align-items:center;gap:10px;font-size:16px;font-weight:600;color:var(--hi);margin:28px 0 12px}
.yearhead i{flex:1;height:1px;background:var(--line)}
.yearcard{grid-column:1/-1;padding:18px;gap:22px;border-color:#245a3b;background:linear-gradient(180deg,#0e1c13,var(--card))}
.yearcard .wcover{width:150px;height:150px}
.yearcard .wcover.ph{font-size:38px}
.yearcard .wtitle{font-size:44px;letter-spacing:-1px}
.ystrip{display:flex;align-items:flex-end;gap:3px;height:44px;margin-top:8px;max-width:360px}
.ystrip b{flex:1;min-height:2px;border-radius:2px 2px 0 0;background:repeating-linear-gradient(to top,var(--bar) 0 3px,rgba(47,191,104,.55) 3px 4px)}
.yearmissing{grid-column:1/-1;padding:14px 16px;color:var(--muted);font-size:13px;border-style:dashed}
@media (max-width:640px){.yearcard{flex-direction:column;align-items:flex-start}.yearcard .wtitle{font-size:34px}}
";
    }
}
