// Stats tab: today / this week / this month / this year at a glance, the last 30 days as bars,
// and this week's top artists and tracks. Green terminal look. C# 5 (built-in Windows compiler).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ITunesScrobbler
{
    class StatsData
    {
        public int Today, Week, Month, Year, Streak;
        public bool StreakCapped;          // every day of the last 30 had scrobbles: "30+"
        public long AllTime;
        public double PerDay;              // average over the last 30 days
        public int[] Days = new int[30];   // oldest first, the last one is today
        public DateTime FirstDay;
        public List<StatsEntry> Artists = new List<StatsEntry>(), Tracks = new List<StatsEntry>();
        public DateTime Fetched;
    }

    class StatsEntry { public string Name = "", Sub = ""; public int N; public Track Cover; }

    class StatsForm : AsciiForm
    {
        static StatsData last;   // kept between openings
        readonly Settings st;
        readonly StatsView view;
        readonly Label statusLabel = new Label();
        readonly AsciiButton refreshBtn;
        volatile bool loading, closed;

        public StatsForm(Settings settings, Icon icon)
        {
            st = settings;
            Text = "V's Last.fm Scrobbler // stats";
            Icon = icon;

            Text_("stats", Theme.Hi, Theme.Mono(13f, FontStyle.Bold));
            Text_("~$ ./stats --now", Theme.Dim);
            y += 4;

            view = new StatsView(Font) { Left = L, Top = y, Width = Wd };
            Controls.Add(view);
            y += view.Height + 12;

            refreshBtn = new AsciiButton("refresh");
            var openBtn = new AsciiButton("open on last.fm");
            var closeBtn = new AsciiButton("close");
            foreach (var b in new[] { refreshBtn, openBtn, closeBtn }) b.Fit(Font);
            closeBtn.Location = new Point(L + Wd - closeBtn.Width, y);
            openBtn.Location = new Point(closeBtn.Left - 10 - openBtn.Width, y);
            refreshBtn.Location = new Point(openBtn.Left - 10 - refreshBtn.Width, y);
            statusLabel.ForeColor = Theme.Dim; statusLabel.AutoSize = false; statusLabel.UseMnemonic = false;
            statusLabel.SetBounds(L, y + 6, refreshBtn.Left - L - 10, LineH(Font));
            refreshBtn.Click += (s, e) => Fetch(true);
            openBtn.Click += (s, e) => SettingsForm.OpenUrl(st.UserName != "" ? "https://www.last.fm/user/" + Uri.EscapeDataString(st.UserName) + "/library" : "https://www.last.fm");
            closeBtn.Click += (s, e) => CloseWindow();
            Controls.Add(statusLabel); Controls.Add(refreshBtn); Controls.Add(openBtn); Controls.Add(closeBtn);
            y += closeBtn.Height + 16;
            ClientSize = new Size(L * 2 + Wd, y);

            Action coverReady = () => { try { if (IsHandleCreated) BeginInvoke((Action)(() => view.Invalidate())); } catch { } };
            Covers.Ready += coverReady;
            FormClosed += (s, e) => { closed = true; Covers.Ready -= coverReady; };
            if (last != null) { view.Data = last; statusLabel.Text = "updated " + last.Fetched.ToString("HH:mm"); }
        }

        // opening the tab refreshes when the numbers are more than 5 minutes old
        public override void TabShown()
        {
            if (last == null || DateTime.Now - last.Fetched > TimeSpan.FromMinutes(5)) Fetch(false);
        }

        static long Unix(DateTime local) { return (long)(local.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        void Fetch(bool user)
        {
            if (loading) return;
            if (!st.IsConfigured || st.UserName == "") { statusLabel.Text = "connect to last.fm in settings first"; statusLabel.ForeColor = Theme.Warn; return; }
            loading = true;
            refreshBtn.Enabled = false;
            statusLabel.Text = "counting…"; statusLabel.ForeColor = Theme.Dim;
            var api = new LastFm(st.ApiKey, st.ApiSecret);
            string me = st.UserName;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string msg; bool warn = false;
                try
                {
                    var d = Build(api, me);
                    last = d;
                    msg = "updated " + d.Fetched.ToString("HH:mm");
                    if (!closed) try { BeginInvoke((Action)(() => view.Data = d)); } catch { }
                }
                catch (Exception ex) { msg = "couldn't reach last.fm: " + ex.Message.ToLowerInvariant(); warn = true; }
                loading = false;
                if (closed) return;
                try { BeginInvoke((Action)(() => { statusLabel.Text = msg; statusLabel.ForeColor = warn ? Theme.Warn : Theme.Dim; refreshBtn.Enabled = true; })); } catch { }
            });
        }

        static StatsData Build(LastFm api, string user)
        {
            var now = DateTime.Now;
            var today = now.Date;
            int dow = ((int)today.DayOfWeek + 6) % 7;   // Monday = 0
            var weekStart = today.AddDays(-dow);
            var monthStart = new DateTime(now.Year, now.Month, 1);
            var yearStart = new DateTime(now.Year, 1, 1);
            var first = today.AddDays(-29);
            var start = monthStart < first ? monthStart : first;

            var all = api.GetScrobbles(user, Unix(start), Unix(now), 80);
            var d = new StatsData { FirstDay = first, Fetched = now };
            foreach (var s in all)
            {
                var t = s.Local;
                if (t.Date == today) d.Today++;
                if (t >= weekStart) d.Week++;
                if (t >= monthStart) d.Month++;
                int i = (int)(t.Date - first).TotalDays;
                if (i >= 0 && i < 30) d.Days[i]++;
            }
            d.PerDay = d.Days.Sum() / 30.0;

            // streak: days in a row with at least one scrobble, up to today (today may still be empty)
            int k = d.Days[29] > 0 ? 29 : 28;
            while (k >= 0 && d.Days[k] > 0) { d.Streak++; k--; }
            d.StreakCapped = k < 0;

            // this week's tops, each with a cover from the scrobbles themselves
            var week = all.Where(s => s.Local >= weekStart).ToList();
            d.Artists = week.GroupBy(s => s.Artist.ToLowerInvariant())
                .Select(g => Entry(g, g.First().Artist, ""))
                .OrderByDescending(e => e.N).ThenBy(e => e.Name).Take(5).ToList();
            d.Tracks = week.GroupBy(s => (s.Artist + "\t" + s.Track).ToLowerInvariant())
                .Select(g => Entry(g, g.First().Track, g.First().Artist))
                .OrderByDescending(e => e.N).ThenBy(e => e.Name).Take(5).ToList();

            try { d.Year = api.GetScrobbleCount(user, Unix(yearStart), Unix(now)); } catch (LastFmException ex) { if (ex.Transient) throw; }
            try { d.AllTime = api.GetPlayCount(user); } catch (LastFmException ex) { if (ex.Transient) throw; }
            return d;
        }

        static StatsEntry Entry(IEnumerable<Scrobble> g, string name, string sub)
        {
            var list = g.ToList();
            var e = new StatsEntry { Name = name, Sub = sub, N = list.Count };
            var withImg = list.Where(s => s.Image != null).GroupBy(s => s.Image).OrderByDescending(x => x.Count()).FirstOrDefault();
            if (withImg != null)
            {
                var s0 = withImg.First();
                e.Cover = new Track { Artist = s0.Artist, Name = s0.Track, Album = s0.Album };
                Covers.Remember(e.Cover, s0.Image);
            }
            return e;
        }
    }

    // ---- everything on the stats tab is painted here (one control)
    class StatsView : Control
    {
        StatsData data;
        readonly Font big, small, letter;
        readonly int tileH, chartH, rowH, lineH;
        int hotDay = -1;
        static readonly CultureInfo En = CultureInfo.InvariantCulture;

        public StatsView(Font f)
        {
            Font = f;
            DoubleBuffered = true; BackColor = Theme.Bg; ForeColor = Theme.Text;
            big = Theme.Mono(19f, FontStyle.Bold);
            small = Theme.Mono(9f);
            letter = Theme.Mono(10f, FontStyle.Bold);
            lineH = Theme.Measure("Ag[", f).Height + 8;
            tileH = Theme.Measure("0", big).Height + Theme.Measure("Ag", small).Height + 24;
            chartH = 130;
            rowH = Math.Max(32, Theme.Measure("Ag", f).Height + 14);
            Height = tileH + 10 + lineH + 8 + lineH + chartH + 22 + 8 + lineH + lineH + rowH * 5 + 4;
        }

        public StatsData Data { set { data = value; Invalidate(); } }

        static string N(long n) { return n.ToString("N0", En); }

        // where the chart is (also used for hovering the bars)
        Rectangle ChartRect { get { int top = tileH + 10 + lineH + 8 + lineH; return new Rectangle(0, top, Width, chartH); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            var one = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            if (data == null)
            {
                TextRenderer.DrawText(g, "counting your scrobbles…", Font, new Point(0, 8), Theme.Dim, one);
                return;
            }

            // ---- four tiles
            string[] labels = { "today", "this week", "this month", "this year" };
            long[] values = { data.Today, data.Week, data.Month, data.Year };
            int gap = 10, tw = (Width - gap * 3) / 4;
            for (int i = 0; i < 4; i++)
            {
                var r = new Rectangle(i * (tw + gap), 0, tw, tileH);
                using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, r);
                using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                TextRenderer.DrawText(g, N(values[i]), big, new Point(r.X + 12, r.Y + 9), Theme.Hi, one);
                TextRenderer.DrawText(g, labels[i], small, new Point(r.X + 13, r.Bottom - 9 - Theme.Measure("Ag", small).Height), Theme.Dim, one);
            }
            int y = tileH + 10;

            // ---- one line of extras
            string streak = data.Streak == 0 ? "no streak right now" : "streak " + (data.StreakCapped ? "30+" : data.Streak.ToString(En)) + (data.Streak == 1 ? " day" : " days");
            string extras = (data.AllTime > 0 ? "all-time " + N(data.AllTime) + "  ·  " : "") + streak + "  ·  " + data.PerDay.ToString("0", En) + " a day (last 30 days)";
            TextRenderer.DrawText(g, extras, Font, new Rectangle(0, y, Width, lineH), Theme.Text, one | TextFormatFlags.VerticalCenter);
            y += lineH + 8;

            // ---- last 30 days
            TextRenderer.DrawText(g, Theme.Rule("last 30 days", Font, Width - 4), Font, new Rectangle(0, y, Width, lineH), Theme.Hi, one | TextFormatFlags.VerticalCenter);
            var cr = ChartRect;
            int max = Math.Max(1, data.Days.Max());
            float bw = (float)cr.Width / 30;
            for (int i = 0; i < 30; i++)
            {
                int h = data.Days[i] == 0 ? 0 : Math.Max(2, (int)Math.Round((cr.Height - 6) * data.Days[i] / (double)max));
                var bar = new RectangleF(cr.X + i * bw + 2, cr.Bottom - h, bw - 4, h);
                bool today = i == 29, hot = i == hotDay;
                var c = hot || today ? Theme.Hi : Color.FromArgb(47, 191, 104);
                if (h > 0) using (var b = new SolidBrush(c)) g.FillRectangle(b, bar);
                else using (var p = new Pen(Theme.Line)) g.DrawLine(p, bar.X, cr.Bottom - 1, bar.Right, cr.Bottom - 1);
                var day = data.FirstDay.AddDays(i);
                if ((29 - i) % 5 == 0)   // a label every 5 days, counting back from today
                {
                    string lab = today ? "today" : day.Day.ToString(En);
                    var lw = Theme.Measure(lab, small).Width;
                    float lx = Math.Min(cr.Right - lw, Math.Max(0, cr.X + i * bw + bw / 2 - lw / 2f));
                    TextRenderer.DrawText(g, lab, small, new Point((int)lx, cr.Bottom + 5), today ? Theme.Hi : Theme.Dim, one);
                }
            }
            // the hovered day's number, top right of the chart
            if (hotDay >= 0)
            {
                var day = data.FirstDay.AddDays(hotDay);
                string tip = day.ToString("ddd d MMM", En).ToLowerInvariant() + " · " + N(data.Days[hotDay]) + " scrobbles";
                int tipW = Theme.Measure(tip, small).Width;
                TextRenderer.DrawText(g, tip, small, new Point(cr.Right - tipW, cr.Y), Theme.Hi, one);
            }
            y = cr.Bottom + 22 + 8;

            // ---- this week: top artists | top tracks
            TextRenderer.DrawText(g, Theme.Rule("this week", Font, Width - 4), Font, new Rectangle(0, y, Width, lineH), Theme.Hi, one | TextFormatFlags.VerticalCenter);
            y += lineH;
            int colW = (Width - 24) / 2;
            DrawTop(g, "top artists", data.Artists, new Rectangle(0, y, colW, lineH + rowH * 5), false);
            DrawTop(g, "top tracks", data.Tracks, new Rectangle(colW + 24, y, colW, lineH + rowH * 5), true);
        }

        void DrawTop(Graphics g, string title, List<StatsEntry> items, Rectangle r, bool withSub)
        {
            var one = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, "$ " + title, small, new Point(r.X, r.Y + 4), Theme.Dim, one);
            int y = r.Y + lineH - 4;
            if (items.Count == 0) { TextRenderer.DrawText(g, "(nothing yet this week)", small, new Point(r.X, y + 6), Theme.Dim, one); return; }
            int max = Math.Max(1, items[0].N);
            int smallH = Theme.Measure("Ag", small).Height, textH = Theme.Measure("Ag", Font).Height;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                int rankW = Theme.Measure("00", small).Width + 8, cs = rowH - 8;
                TextRenderer.DrawText(g, (i + 1).ToString("00"), small, new Point(r.X, y + (rowH - smallH) / 2), Theme.Dim, one);
                var cover = new Rectangle(r.X + rankW, y + 4, cs, cs);
                var img = it.Cover != null ? Covers.ThumbFor(it.Cover) : null;
                if (img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, cover);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
                else
                {
                    using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, cover);
                    TextRenderer.DrawText(g, it.Name.Length > 0 ? it.Name.Substring(0, 1).ToUpperInvariant() : "?", letter, cover, Theme.Dim,
                        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, cover.X, cover.Y, cover.Width - 1, cover.Height - 1);

                string count = N(it.N);
                int countW = Theme.Measure(count, small).Width;
                int tx = cover.Right + 10, textW = r.Right - tx - countW - 10;
                string name = withSub && it.Sub != "" ? it.Name + " — " + it.Sub : it.Name;
                TextRenderer.DrawText(g, name, Font, new Rectangle(tx, y + 5, Math.Max(10, textW), textH + 2), Color.FromArgb(220, 247, 228), one);
                TextRenderer.DrawText(g, count, small, new Point(r.Right - countW, y + 6), Theme.Dim, one);
                // a thin bar under the name: how it compares to #1
                int barW = (int)Math.Round((r.Right - tx) * it.N / (double)max);
                using (var b = new SolidBrush(i == 0 ? Theme.Hi : Color.FromArgb(47, 191, 104))) g.FillRectangle(b, tx, y + rowH - 9, Math.Max(2, barW), 2);
                y += rowH;
            }
        }

        // hovering a bar shows that day's number (nothing to click, so the cursor stays normal)
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = -1;
            var cr = ChartRect;
            if (data != null && e.Y >= cr.Y && e.Y < cr.Bottom + 20 && e.X >= cr.X && e.X < cr.Right) i = Math.Min(29, (int)((e.X - cr.X) / ((float)cr.Width / 30)));
            if (i != hotDay) { hotDay = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hotDay != -1) { hotDay = -1; Invalidate(); } }
    }
}
