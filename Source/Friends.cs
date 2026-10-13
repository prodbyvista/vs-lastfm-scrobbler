// Friends window: what your Last.fm friends are listening to right now, or what they scrobbled last.
// Green terminal look, like the history window. C# 5 (built-in Windows compiler).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

namespace ITunesScrobbler
{
    class FriendInfo { public string Name = "", RealName = ""; }

    class FriendTrack
    {
        public string Artist = "", Name = "", Album = "", Image;
        public bool NowPlaying;
        public long Uts;
        public DateTime Local { get { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Uts).ToLocalTime(); } }
    }

    // ================================================================ the Last.fm calls it needs
    partial class LastFm
    {
        public List<FriendInfo> GetFriendInfos(string user)
        {
            var list = new List<FriendInfo>();
            for (int page = 1; page <= 4; page++)
            {
                var d = Call("user.getFriends", new Dictionary<string, string> { { "user", user }, { "limit", "50" }, { "page", page.ToString() } }, false);
                foreach (XmlElement u in d.SelectNodes("/lfm/friends/user"))
                {
                    var n = u.SelectSingleNode("name");
                    if (n == null) continue;
                    var rn = u.SelectSingleNode("realname");
                    list.Add(new FriendInfo { Name = n.InnerText.Trim(), RealName = rn != null ? rn.InnerText.Trim() : "" });
                }
                var fr = d.SelectSingleNode("/lfm/friends") as XmlElement;
                int pages;
                if (fr == null || !int.TryParse(fr.GetAttribute("totalPages"), out pages) || page >= pages) break;
            }
            return list;
        }

        // the song a user is playing now, or else their last scrobble; null if they have none
        public FriendTrack GetLatestTrack(string user)
        {
            var d = Call("user.getRecentTracks", new Dictionary<string, string> { { "user", user }, { "limit", "1" } }, false);
            var tr = d.SelectSingleNode("/lfm/recenttracks/track") as XmlElement;   // a now-playing track comes first
            if (tr == null) return null;
            var t = new FriendTrack();
            var a = tr.SelectSingleNode("artist"); if (a != null) t.Artist = a.InnerText.Trim();
            var n = tr.SelectSingleNode("name"); if (n != null) t.Name = n.InnerText.Trim();
            var al = tr.SelectSingleNode("album"); if (al != null) t.Album = al.InnerText.Trim();
            t.NowPlaying = tr.GetAttribute("nowplaying") == "true";
            var date = tr.SelectSingleNode("date") as XmlElement;
            if (date != null) long.TryParse(date.GetAttribute("uts"), out t.Uts);
            var img = tr.SelectSingleNode("image[@size='extralarge']") ?? tr.SelectSingleNode("image[@size='large']");
            if (img != null)
            {
                string u = img.InnerText.Trim();
                if (u != "" && !u.Contains("2a96cbd8b46e442fc41c2b86b821562f")) t.Image = u;
            }
            return t;
        }
    }

    // ================================================================ what friends are up to (shared)
    // One store for the friends tab and the "a friend is listening to the same artist" alerts.
    // Each friend is asked again only when it's worth it: every minute while they're listening,
    // rarely when they've been quiet for days. Before, every friend was asked every 2-3 minutes.
    static class FriendFeed
    {
        static readonly object gate = new object();
        static List<FriendInfo> friends;
        static DateTime friendsFetched = DateTime.MinValue;
        static readonly Dictionary<string, FriendTrack> latest = new Dictionary<string, FriendTrack>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // private / no scrobbles
        static readonly Dictionary<string, DateTime> checkedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public const int Max = 150;

        // the people you follow (asked again every 6 hours, or when forced)
        public static List<FriendInfo> List(LastFm api, string me, bool force)
        {
            lock (gate)
                if (!force && friends != null && DateTime.UtcNow - friendsFetched < TimeSpan.FromHours(6)) return friends.ToList();
            var fl = api.GetFriendInfos(me);
            lock (gate) { friends = fl; friendsFetched = DateTime.UtcNow; }
            Diag.Once("friends:" + fl.Count, "Friends on Last.fm: " + fl.Count);
            return fl.ToList();
        }

        static TimeSpan Interval(string name)
        {
            if (hidden.Contains(name)) return TimeSpan.FromMinutes(60);
            FriendTrack t;
            if (!latest.TryGetValue(name, out t)) return TimeSpan.Zero;
            if (t.NowPlaying) return TimeSpan.FromMinutes(1);
            var quiet = DateTime.Now - t.Local;
            if (quiet < TimeSpan.FromHours(1)) return TimeSpan.FromMinutes(2);
            if (quiet < TimeSpan.FromDays(1)) return TimeSpan.FromMinutes(5);
            if (quiet < TimeSpan.FromDays(7)) return TimeSpan.FromMinutes(15);
            return TimeSpan.FromMinutes(30);
        }

        public static bool Due(string name)
        {
            lock (gate)
            {
                DateTime at;
                if (!checkedAt.TryGetValue(name, out at)) return true;
                return DateTime.UtcNow - at >= Interval(name) - TimeSpan.FromSeconds(5);
            }
        }

        // asks Last.fm now; null = nothing to show (private or empty profile)
        public static FriendTrack Check(LastFm api, string name)
        {
            FriendTrack t;
            try { t = api.GetLatestTrack(name); }
            catch (LastFmException ex)
            {
                if (ex.Transient) throw;
                t = null;   // private profile etc.
            }
            lock (gate)
            {
                checkedAt[name] = DateTime.UtcNow;
                if (t == null) { latest.Remove(name); hidden.Add(name); }
                else { latest[name] = t; hidden.Remove(name); }
            }
            if (t != null && t.Image != null) Covers.Remember(new Track { Artist = t.Artist, Name = t.Name, Album = t.Album }, t.Image);
            return t;
        }

        public static FriendTrack Latest(string name) { lock (gate) { FriendTrack t; latest.TryGetValue(name, out t); return t; } }

        // everything known right now, for the friends tab
        public static List<FriendsForm.FriendRow> Snapshot()
        {
            lock (gate)
            {
                if (friends == null) return new List<FriendsForm.FriendRow>();
                return friends.Select(f =>
                {
                    FriendTrack t;
                    latest.TryGetValue(f.Name, out t);
                    return new FriendsForm.FriendRow { Info = f, Track = t, Hidden = hidden.Contains(f.Name) };
                }).ToList();
            }
        }
    }

    // ================================================================ friends window
    class FriendsForm : AsciiForm
    {

        readonly Settings st;
        readonly AsciiToggle fAll = new AsciiToggle(), fNow = new AsciiToggle();
        readonly Label summary = new Label(), statusLabel = new Label();
        readonly FriendsList list;
        readonly System.Windows.Forms.Timer auto = new System.Windows.Forms.Timer { Interval = 30000 };   // only friends that are due get asked
        readonly AsciiButton refreshBtn;
        volatile bool loading, closed;
        bool onlyNow;

        public FriendsForm(Settings settings, Icon icon)
        {
            st = settings;
            Text = "V's Last.fm Scrobbler // friends";
            Icon = icon;

            Text_("friends", Theme.Hi, Theme.Mono(13f, FontStyle.Bold));
            Text_("~$ ./friends --now", Theme.Dim);

            int x = L;
            foreach (var pair in new[] { Tuple.Create(fAll, "everyone"), Tuple.Create(fNow, "listening now") })
            {
                var t = pair.Item1; string name = pair.Item2;
                t.Caption = name; t.Checked = t == fAll;
                int w = Theme.Measure("[x] " + name, Font).Width + 18;
                t.SetBounds(x, y, w, LineH(Font)); x += w + 6;
                t.Click += (s, e) => { onlyNow = t == fNow; fAll.Checked = !onlyNow; fNow.Checked = onlyNow; list.Refill(); };
                Controls.Add(t);
            }
            summary.ForeColor = Theme.Dim; summary.TextAlign = ContentAlignment.MiddleRight; summary.AutoSize = false; summary.UseMnemonic = false;
            summary.SetBounds(x, y, L + Wd - x, LineH(Font)); Controls.Add(summary);
            y += LineH(Font) + 6;

            int contentW;
            var inner = ScrollBox(460, out contentW);
            list = new FriendsList(this) { Width = contentW, Left = 0, Top = 0 };
            inner.Controls.Add(list);
            y += 12;

            refreshBtn = new AsciiButton("refresh");
            var openBtn = new AsciiButton("open on last.fm");
            var closeBtn = new AsciiButton("close");
            foreach (var b in new[] { refreshBtn, openBtn, closeBtn }) b.Fit(Font);
            closeBtn.Location = new Point(L + Wd - closeBtn.Width, y);
            openBtn.Location = new Point(closeBtn.Left - 10 - openBtn.Width, y);
            refreshBtn.Location = new Point(openBtn.Left - 10 - refreshBtn.Width, y);
            statusLabel.ForeColor = Theme.Dim; statusLabel.AutoSize = false; statusLabel.UseMnemonic = false;
            statusLabel.SetBounds(L, y + 6, refreshBtn.Left - L - 10, LineH(Font));
            refreshBtn.Click += (s, e) => LoadFriends(true);
            openBtn.Click += (s, e) => SettingsForm.OpenUrl(st.UserName != "" ? "https://www.last.fm/user/" + Uri.EscapeDataString(st.UserName) + "/following" : "https://www.last.fm");
            closeBtn.Click += (s, e) => CloseWindow();
            Controls.Add(statusLabel); Controls.Add(refreshBtn); Controls.Add(openBtn); Controls.Add(closeBtn);
            CancelButton = closeBtn;
            y += closeBtn.Height + 16;
            ClientSize = new Size(L * 2 + Wd, y);

            Action coverReady = () => { try { if (IsHandleCreated) BeginInvoke((Action)(() => list.Invalidate())); } catch { } };
            Covers.Ready += coverReady;
            auto.Tick += (s, e) => { if (Visible) LoadFriends(false); };   // only while its tab is showing
            auto.Start();
            FormClosed += (s, e) => { closed = true; auto.Stop(); Covers.Ready -= coverReady; };
            list.Refill();
        }

        // opening the tab asks about the friends that are due
        public override void TabShown() { LoadFriends(false); }

        internal bool OnlyNow { get { return onlyNow; } }
        internal void SetSummary(string s) { summary.Text = s; }

        // snapshot of what's known, for the list
        internal List<FriendRow> Rows() { return FriendFeed.Snapshot(); }

        void SetStatus(string text, bool warn)
        {
            if (closed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    statusLabel.Text = text; statusLabel.ForeColor = warn ? Theme.Warn : Theme.Dim;
                    refreshBtn.Enabled = !loading;
                    list.Refill();
                }));
            }
            catch { }
        }

        // all = the refresh button: everyone now. Otherwise only the friends that are due.
        void LoadFriends(bool all)
        {
            if (loading) return;
            if (!st.IsConfigured || st.UserName == "") { statusLabel.Text = "connect to last.fm in settings first"; statusLabel.ForeColor = Theme.Warn; return; }
            loading = true;
            refreshBtn.Enabled = false;
            var api = new LastFm(st.ApiKey, st.ApiSecret);
            string me = st.UserName;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var fl = FriendFeed.List(api, me, all);
                    var todo = fl.Take(FriendFeed.Max).Where(f => all || FriendFeed.Due(f.Name)).ToList();
                    int done = 0;
                    foreach (var f in todo)
                    {
                        if (closed) return;
                        FriendFeed.Check(api, f.Name);
                        done++;
                        if (todo.Count >= 5 && (done % 5 == 0 || done == todo.Count)) SetStatus("checking " + done + " of " + todo.Count + "\u2026", false);
                        Thread.Sleep(150);   // gentle on Last.fm
                    }
                    loading = false;
                    SetStatus(fl.Count == 0 ? "you don't follow anyone on last.fm yet"
                        : "updated " + DateTime.Now.ToString("HH:mm") + " \u00b7 listening friends every minute, quiet ones less often", false);
                }
                catch (Exception ex)
                {
                    loading = false;
                    SetStatus("couldn't reach last.fm: " + ex.Message.ToLowerInvariant(), true);
                }
                finally { loading = false; }
            });
        }

        internal static string Ago(DateTime local)
        {
            var span = DateTime.Now - local;
            if (span.TotalMinutes < 2) return "just now";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " min ago";
            if (span.TotalHours < 24) return (int)span.TotalHours + " h ago";
            if (local.Date == DateTime.Today.AddDays(-1)) return "yesterday";
            if (span.TotalDays < 30) return (int)span.TotalDays + " days ago";
            return local.ToString("d MMM yyyy", CultureInfo.InvariantCulture).ToLowerInvariant();
        }

        internal class FriendRow { public FriendInfo Info; public FriendTrack Track; public bool Hidden; }

        // ---- the rows: painted directly (one control), "listening now" first, then by last scrobble
        class FriendsList : Control
        {
            readonly FriendsForm form;
            List<object> rows = new List<object>();   // string = section header, FriendRow = row
            readonly int RowH, HeadH, CoverSz;
            readonly Font small, letterFont;
            int hot = -1;

            public FriendsList(FriendsForm f)
            {
                form = f;
                DoubleBuffered = true; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = f.Font;
                small = Theme.Mono(9f);
                letterFont = Theme.Mono(12f, FontStyle.Bold);
                int textH = Theme.Measure("Ag", Font).Height, smallH = Theme.Measure("Ag", small).Height;
                HeadH = Math.Max(30, textH + 12);
                RowH = textH + smallH + 22;
                CoverSz = RowH - 12;
            }

            public void Refill()
            {
                var all = form.Rows();
                var now = all.Where(r => r.Track != null && r.Track.NowPlaying).OrderBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var rest = all.Where(r => r.Track == null || !r.Track.NowPlaying)
                              .OrderBy(r => r.Track == null ? 1 : 0).ThenByDescending(r => r.Track != null ? r.Track.Uts : 0)
                              .ThenBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase).ToList();
                form.SetSummary(now.Count + " listening now · " + all.Count + (all.Count == 1 ? " friend" : " friends"));
                rows = new List<object>();
                if (now.Count > 0) { rows.Add("listening now"); rows.AddRange(now.Cast<object>()); }
                else if (form.OnlyNow) rows.Add("nobody's listening right now");
                if (!form.OnlyNow && rest.Count > 0) { rows.Add("recently"); rows.AddRange(rest.Cast<object>()); }
                if (rows.Count == 0) rows.Add("nothing here yet");
                hot = -1;
                Height = rows.Sum(r => r is string ? HeadH : RowH) + 8;
                Invalidate();
            }

            int RowAt(int py)
            {
                int y = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    int h = rows[i] is string ? HeadH : RowH;
                    if (py >= y && py < y + h) return rows[i] is string ? -1 : i;
                    y += h;
                }
                return -1;
            }

            // a row opens that friend's profile, so only rows get the hand cursor
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int i = RowAt(e.Y);
                Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
                if (i != hot) { hot = i; Invalidate(); }
            }

            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                int i = RowAt(e.Y);
                var r = i >= 0 ? rows[i] as FriendRow : null;
                if (r != null) SettingsForm.OpenUrl("https://www.last.fm/user/" + Uri.EscapeDataString(r.Info.Name));
            }

            protected override void OnPaint(PaintEventArgs pe)
            {
                var g = pe.Graphics;
                int y = 0, w = Width;
                var one = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
                int textH = Theme.Measure("Ag", Font).Height, smallH = Theme.Measure("Ag", small).Height;
                using (var sep = new Pen(Color.FromArgb(16, 36, 26)))
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var head = rows[i] as string;
                        if (head != null)
                        {
                            TextRenderer.DrawText(g, Theme.Rule(head, Font, w - 8), Font, new Rectangle(0, y, w, HeadH), Theme.Hi,
                                TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                            y += HeadH; continue;
                        }
                        var r = (FriendRow)rows[i];
                        if (i == hot) using (var b = new SolidBrush(Color.FromArgb(18, 48, 31))) g.FillRectangle(b, 0, y, w, RowH);
                        var cover = new Rectangle(4, y + (RowH - CoverSz) / 2, CoverSz, CoverSz);
                        DrawCover(g, r, cover);

                        int tx = cover.Right + 12, top = y + (RowH - textH - smallH - 4) / 2;
                        // right side: "● listening now" or "12 min ago"
                        string when; Color whenColor;
                        if (r.Track != null && r.Track.NowPlaying) { when = "listening now"; whenColor = Theme.Hi; }
                        else if (r.Track != null && r.Track.Uts > 0) { when = Ago(r.Track.Local); whenColor = Theme.Dim; }
                        else { when = ""; whenColor = Theme.Dim; }
                        int whenW = when != "" ? Theme.Measure(when, small).Width + 4 : 0;
                        int rightX = w - 10 - whenW;
                        if (when != "")
                        {
                            TextRenderer.DrawText(g, when, small, new Point(rightX, top + 2), whenColor, one);
                            if (r.Track.NowPlaying)
                            {
                                int d = Math.Max(6, small.Height / 2);
                                g.SmoothingMode = SmoothingMode.AntiAlias;
                                using (var b = new SolidBrush(Color.FromArgb(35, 197, 82))) g.FillEllipse(b, rightX - d - 6, top + 2 + (smallH - d) / 2, d, d);
                                g.SmoothingMode = SmoothingMode.Default;
                                rightX -= d + 6;
                            }
                        }

                        string name = r.Info.Name + (r.Info.RealName != "" && !r.Info.RealName.Equals(r.Info.Name, StringComparison.OrdinalIgnoreCase) ? "  (" + r.Info.RealName + ")" : "");
                        TextRenderer.DrawText(g, name, Font, new Rectangle(tx, top, Math.Max(10, rightX - tx - 10), textH + 2), i == hot ? Theme.Hi : Color.FromArgb(220, 247, 228), one);

                        string line; Color lineColor;
                        if (r.Track != null) { line = r.Track.Name + " — " + r.Track.Artist; lineColor = r.Track.NowPlaying ? Theme.Text : Theme.Dim; }
                        else if (r.Hidden) { line = "no scrobbles to show (private or empty profile)"; lineColor = Theme.Dim; }
                        else { line = "…"; lineColor = Theme.Dim; }
                        TextRenderer.DrawText(g, line, small, new Rectangle(tx, top + textH + 4, w - tx - 10, smallH + 2), lineColor, one);

                        g.DrawLine(sep, 0, y + RowH - 1, w, y + RowH - 1);
                        y += RowH;
                    }
            }

            void DrawCover(Graphics g, FriendRow r, Rectangle rect)
            {
                Image img = null;
                if (r.Track != null && r.Track.Image != null)
                    img = Covers.ThumbFor(new Track { Artist = r.Track.Artist, Name = r.Track.Name, Album = r.Track.Album });
                if (img != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, rect);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
                else
                {
                    using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, rect);
                    string letter = r.Info.Name.Length > 0 ? r.Info.Name.Substring(0, 1).ToUpperInvariant() : "?";
                    TextRenderer.DrawText(g, letter, letterFont, rect, Theme.Dim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                }
                using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
        }
    }
}
