// Scrobble history (store + window) and the log viewer window. Green terminal look.
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
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ITunesScrobbler
{
    // ================================================================ history store (Data\history.txt)
    class HistoryEntry
    {
        public long Timestamp;            // when it was played (unix)
        public string Status = "queued";  // queued | sent | refused
        public string Reason = "";
        public string Artist = "", Track = "", Album = "";
        public int Duration;
        public string Key { get { return Timestamp + "|" + Artist.ToLowerInvariant() + "|" + Track.ToLowerInvariant(); } }
        public DateTime Local { get { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Timestamp).ToLocalTime(); } }
    }

    static class History
    {
        const int Keep = 200;
        static readonly object gate = new object();
        static List<HistoryEntry> items;
        public static event Action Changed;

        static string FilePath { get { return Path.Combine(Settings.Dir, "history.txt"); } }
        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

        static void Load()
        {
            if (items != null) return;
            items = new List<HistoryEntry>();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var f = line.Split('\t');
                    if (f.Length < 7) continue;
                    long ts; int dur;
                    if (!long.TryParse(f[0], out ts)) continue;
                    int.TryParse(f[6], out dur);
                    items.Add(new HistoryEntry { Timestamp = ts, Status = f[1], Reason = f[2], Artist = f[3], Track = f[4], Album = f[5], Duration = dur });
                }
            }
            catch (Exception ex) { Log.Write("History load failed: " + ex.Message); }
        }

        static void Save()
        {
            try
            {
                if (items.Count > Keep) items = items.OrderByDescending(e => e.Timestamp).Take(Keep).ToList();
                File.WriteAllLines(FilePath, items.Select(e => e.Timestamp + "\t" + e.Status + "\t" + Clean(e.Reason) + "\t" + Clean(e.Artist) + "\t" +
                    Clean(e.Track) + "\t" + Clean(e.Album) + "\t" + e.Duration).ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("History save failed: " + ex.Message); }
        }

        static void Set(Pending p, string status, string reason)
        {
            var e = new HistoryEntry { Timestamp = p.Timestamp, Status = status, Reason = reason ?? "", Artist = p.Track.Artist, Track = p.Track.Name, Album = p.Track.Album, Duration = p.Track.Duration };
            lock (gate)
            {
                Load();
                items.RemoveAll(x => x.Key == e.Key);
                items.Add(e);
                Save();
            }
            var h = Changed; if (h != null) try { h(); } catch { }
            if (status == "queued") Covers.Prefetch(e);
        }

        public static void Queued(Pending p) { Set(p, "queued", ""); }
        public static void Sent(Pending p) { Set(p, "sent", ""); }
        public static void Refused(Pending p, string reason) { Set(p, "refused", reason); }

        public static List<HistoryEntry> Recent(int n)
        {
            lock (gate) { Load(); return items.OrderByDescending(e => e.Timestamp).Take(n).ToList(); }
        }
    }

    // ================================================================ cover thumbnails for the history window
    // Covers aren't known when a song is scrobbled, so they're looked up in the background (Last.fm first,
    // Apple's catalogue as a fallback) and kept as small PNGs in Data\cache\covers\. Data\covers.txt remembers
    // which cover belongs to which album, including "none found" (tried again after a week).
    static class Covers
    {
        static readonly object gate = new object();
        static Dictionary<string, string> urls;                               // key -> url, or "-<unix>" = none found then
        static readonly Dictionary<string, Image> thumbs = new Dictionary<string, Image>();   // null = no cover
        static readonly Dictionary<string, DateTime> failedAt = new Dictionary<string, DateTime>();
        static readonly Queue<KeyValuePair<string, Track>> todo = new Queue<KeyValuePair<string, Track>>();
        static readonly HashSet<string> queued = new HashSet<string>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static Thread worker;
        static Func<Settings> settings;
        const int ThumbPx = 96;   // sharp enough for the hover card's bigger cover too

        public static event Action Ready;   // a thumbnail arrived (raised on a background thread)

        static string Dir { get { return Path.Combine(Path.Combine(Settings.Dir, "cache"), "covers"); } }
        static string MapPath { get { return Path.Combine(Settings.Dir, "covers.txt"); } }

        public static void Init(Func<Settings> s) { settings = s; }

        // one cover per album (per song when there's no album); same cleanup as scrobbling uses
        static string Key(Track t)
        {
            var c = new Track { Artist = t.Artist, Name = t.Name, Album = t.Album };
            c.Normalize();
            return (c.Artist + "\t" + (c.Album != "" ? c.Album : "#" + c.Name)).ToLowerInvariant();
        }

        static Track TrackOf(HistoryEntry e) { return new Track { Artist = e.Artist, Name = e.Track, Album = e.Album }; }

        static string FileFor(string key)
        {
            using (var md5 = MD5.Create())
                return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(key)).Take(10).Select(b => b.ToString("x2"))) + ".png";
        }

        static void LoadMap()
        {
            if (urls != null) return;
            urls = new Dictionary<string, string>();
            try
            {
                if (File.Exists(MapPath))
                    foreach (var line in File.ReadAllLines(MapPath, Encoding.UTF8))
                    {
                        var f = line.Split(new[] { '\t' }, 2);   // url, then the key (which has a tab in it)
                        if (f.Length == 2 && f[0] != "") urls[f[1]] = f[0];
                    }
            }
            catch (Exception ex) { Log.Write("Cover list load failed: " + ex.Message); }
        }

        static void SaveMap()
        {
            try
            {
                if (urls.Count > 4000) urls = urls.Skip(urls.Count - 3000).ToDictionary(kv => kv.Key, kv => kv.Value);
                File.WriteAllLines(MapPath, urls.Select(kv => kv.Value + "\t" + kv.Key.Replace('\r', ' ').Replace('\n', ' ')).ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("Cover list save failed: " + ex.Message); }
        }

        // a cover found elsewhere (Discord Rich Presence looks one up for every song it shows)
        public static void Remember(Track t, string url)
        {
            if (string.IsNullOrEmpty(url) || t == null) return;
            string k = Key(t);
            lock (gate)
            {
                LoadMap();
                string cur;
                if (urls.TryGetValue(k, out cur) && cur == url) return;
                urls[k] = url;
                SaveMap();
            }
        }

        // start fetching right away (called when a song is scrobbled) so the history has it ready
        public static void Prefetch(HistoryEntry e)
        {
            var t = TrackOf(e);
            string k = Key(t);
            lock (gate)
            {
                if (thumbs.ContainsKey(k) || File.Exists(Path.Combine(Dir, FileFor(k)))) return;
                Enqueue(k, t);
            }
        }

        // the thumbnail for a history row; null while it's on its way (Ready fires) or when there's none
        public static Image Thumb(HistoryEntry e) { return ThumbFor(TrackOf(e)); }

        // same for any song (the hover card's now playing, the friends window)
        public static Image ThumbFor(Track track)
        {
            if (track == null) return null;
            var t = new Track { Artist = track.Artist, Name = track.Name, Album = track.Album };
            string k = Key(t);
            lock (gate)
            {
                Image img;
                if (thumbs.TryGetValue(k, out img)) return img;
                string file = Path.Combine(Dir, FileFor(k));
                if (File.Exists(file))
                {
                    try
                    {
                        using (var fs = File.OpenRead(file)) using (var tmp = Image.FromStream(fs)) img = new Bitmap(tmp);   // copy: don't keep the file locked
                    }
                    catch { img = null; }
                    thumbs[k] = img;
                    return img;
                }
                Enqueue(k, t);
                return null;
            }
        }

        // caller holds the lock
        static void Enqueue(string k, Track t)
        {
            DateTime failed;
            if (failedAt.TryGetValue(k, out failed) && DateTime.UtcNow - failed < TimeSpan.FromMinutes(10)) return;   // offline etc.: not every repaint
            if (!queued.Add(k)) return;
            todo.Enqueue(new KeyValuePair<string, Track>(k, t));
            if (worker == null)
            {
                worker = new Thread(Work) { IsBackground = true, Name = "Covers", Priority = ThreadPriority.BelowNormal };
                worker.Start();
            }
            wake.Set();
        }

        static void Work()
        {
            while (true)
            {
                wake.WaitOne(60000);
                while (true)
                {
                    KeyValuePair<string, Track> job;
                    lock (gate) { if (todo.Count == 0) break; job = todo.Dequeue(); }
                    bool changed = false;
                    try { changed = Fetch(job.Key, job.Value); }
                    catch (Exception ex)
                    {
                        lock (gate) failedAt[job.Key] = DateTime.UtcNow;
                        Log.Verbose("Cover for " + job.Value + " not fetched: " + ex.Message);
                    }
                    finally { lock (gate) queued.Remove(job.Key); }
                    if (changed) { var h = Ready; if (h != null) try { h(); } catch { } }
                    Thread.Sleep(200);   // gentle on Last.fm
                }
            }
        }

        // true when something new can be drawn (a thumbnail, or the news that there is none)
        static bool Fetch(string k, Track t)
        {
            string url;
            bool known;
            lock (gate) { LoadMap(); known = urls.TryGetValue(k, out url); }

            if (known && url.StartsWith("-"))
            {
                long when;
                if (long.TryParse(url.Substring(1), out when) && Engine.Now() - when < 7 * 86400)
                {
                    lock (gate) thumbs[k] = null;
                    return true;
                }
                known = false;   // a week has passed: try again
            }
            if (!known)
            {
                url = null;
                var st = settings != null ? settings() : null;
                if (st != null && st.ApiKey != "")
                {
                    string how;
                    try { url = new LastFm(st.ApiKey, st.ApiSecret).GetAlbumArt(t, out how); }
                    catch (LastFmException ex) { if (ex.Transient) throw; }
                }
                if (url == null) url = DiscordPresence.AppleArt(t);
                lock (gate) { urls[k] = url ?? ("-" + Engine.Now()); SaveMap(); }
                if (url == null) { lock (gate) thumbs[k] = null; return true; }
            }

            byte[] data;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "VsLastfmScrobbler/3";
                data = wc.DownloadData(url);
            }
            Directory.CreateDirectory(Dir);
            using (var ms = new MemoryStream(data))
            using (var src = new Bitmap(ms))
            using (var small = new Bitmap(ThumbPx, ThumbPx))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // centre-crop to a square (Apple's are square, some Last.fm ones aren't)
                    int side = Math.Min(src.Width, src.Height);
                    g.DrawImage(src, new Rectangle(0, 0, ThumbPx, ThumbPx), new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side), GraphicsUnit.Pixel);
                }
                small.Save(Path.Combine(Dir, FileFor(k)), ImageFormat.Png);
            }
            lock (gate) thumbs.Remove(k);   // the next paint loads it from disk
            return true;
        }
    }

    // ================================================================ history window
    class HistoryForm : AsciiForm
    {
        readonly Settings st;
        readonly Action<HistoryEntry> resend;
        readonly AsciiToggle fAll = new AsciiToggle(), fSent = new AsciiToggle(), fQueued = new AsciiToggle(), fRefused = new AsciiToggle();
        readonly Label summary = new Label();
        readonly HistoryList list;
        string filter = "all";

        public HistoryForm(Settings settings, Icon icon, Action<HistoryEntry> resendCb)
        {
            st = settings; resend = resendCb;
            Text = "V's Last.fm Scrobbler // history";
            Icon = icon;

            Text_("scrobble history", Theme.Hi, Theme.Mono(13f, FontStyle.Bold));
            Text_("~$ ./history --last 50", Theme.Dim);

            // filter row: works like radio buttons
            int x = L;
            foreach (var pair in new[] { Tuple.Create(fAll, "all"), Tuple.Create(fSent, "sent"), Tuple.Create(fQueued, "queued"), Tuple.Create(fRefused, "refused") })
            {
                var t = pair.Item1; string name = pair.Item2;
                t.Caption = name; t.Checked = name == "all";
                int w = Theme.Measure("[x] " + name, Font).Width + 18;
                t.SetBounds(x, y, w, LineH(Font)); x += w + 6;
                t.Click += (s, e) => { filter = name; foreach (var o in new[] { fAll, fSent, fQueued, fRefused }) o.Checked = o == t; list.Refill(); };
                Controls.Add(t);
            }
            summary.ForeColor = Theme.Dim; summary.TextAlign = ContentAlignment.MiddleRight; summary.AutoSize = false;
            summary.SetBounds(x, y, L + Wd - x, LineH(Font)); Controls.Add(summary);
            y += LineH(Font) + 6;

            int contentW;
            var inner = ScrollBox(430, out contentW);
            list = new HistoryList(this) { Width = contentW, Left = 0, Top = 0 };
            inner.Controls.Add(list);
            y += 12;

            var resendBtn = new AsciiButton("resend refused", Theme.Bad);
            var openBtn = new AsciiButton("open on last.fm");
            var closeBtn = new AsciiButton("close");
            foreach (var b in new[] { resendBtn, openBtn, closeBtn }) b.Fit(Font);
            closeBtn.Location = new Point(L + Wd - closeBtn.Width, y);
            openBtn.Location = new Point(closeBtn.Left - 10 - openBtn.Width, y);
            resendBtn.Location = new Point(openBtn.Left - 10 - resendBtn.Width, y);
            resendBtn.Click += (s, e) =>
            {
                var refused = History.Recent(200).Where(h => h.Status == "refused").ToList();
                foreach (var h in refused) resend(h);
                list.Refill();
            };
            openBtn.Click += (s, e) => SettingsForm.OpenUrl(st.UserName != "" ? "https://www.last.fm/user/" + Uri.EscapeDataString(st.UserName) + "/library" : "https://www.last.fm");
            closeBtn.Click += (s, e) => CloseWindow();
            Controls.Add(resendBtn); Controls.Add(openBtn); Controls.Add(closeBtn);
            CancelButton = closeBtn;
            y += closeBtn.Height + 16;
            ClientSize = new Size(L * 2 + Wd, y);

            Action changed = () => { try { BeginInvoke((Action)(() => list.Refill())); } catch { } };
            History.Changed += changed;
            Action coverReady = () => { try { BeginInvoke((Action)(() => list.Invalidate())); } catch { } };
            Covers.Ready += coverReady;
            FormClosed += (s, e) => { History.Changed -= changed; Covers.Ready -= coverReady; };
            list.Refill();
        }

        public override void TabShown() { list.ForgetSeen(); list.Refill(); }

        internal string Filter { get { return filter; } }
        internal void SetSummary(string s) { summary.Text = s; }

        // the rows: painted directly (one control), grouped by day
        class HistoryList : Control
        {
            readonly HistoryForm form;
            List<object> rows = new List<object>();   // string = day header, HistoryEntry = row
            // songs from the same album back to back show the cover once: the rows after it get a thin line instead
            HashSet<HistoryEntry> sameAlbumAbove = new HashSet<HistoryEntry>(), sameAlbumBelow = new HashSet<HistoryEntry>();
            readonly int RowH, HeadH, ThumbSz;
            readonly Font letterFont;

            // a new scrobble slides in at the top and glows for a moment. Runs ~1.7 s at ~60 fps,
            // only while this tab is on screen; nothing runs the rest of the time.
            const double SlideMs = 280, FadeMs = 1400;
            HashSet<string> knownKeys;              // what the list showed last time (null before the first fill)
            HistoryEntry animRow;
            int animY;
            DateTime animStart;
            readonly System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer { Interval = 15 };

            public HistoryList(HistoryForm f)
            {
                form = f;
                DoubleBuffered = true; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = f.Font;
                int textH = Theme.Measure("Ag", Font).Height;
                HeadH = Math.Max(30, textH + 12);
                RowH = Math.Max(36, textH + 18);   // room for a cover thumbnail
                ThumbSz = RowH - 8;
                letterFont = Theme.Mono(Math.Max(8f, Font.Size - 1f), FontStyle.Bold);
                animTimer.Tick += (s, e) => AnimTick();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) animTimer.Dispose();
                base.Dispose(disposing);
            }

            double Elapsed { get { return (DateTime.UtcNow - animStart).TotalMilliseconds; } }

            // 0 -> 1, fast at first and settling softly (ease-out cubic)
            static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - Math.Pow(1 - t, 3); }

            int SlideOffset { get { return animRow == null ? 0 : (int)Math.Round(RowH * (1 - Ease(Elapsed / SlideMs))); } }
            double Glow { get { return animRow == null ? 0 : 1 - Math.Max(0, Math.Min(1, (Elapsed - SlideMs) / FadeMs)); } }

            void AnimTick()
            {
                if (animRow == null || !Visible) { StopAnim(); return; }
                double t = Elapsed;
                if (t >= SlideMs + FadeMs) { StopAnim(); Invalidate(new Rectangle(0, animY, Width, RowH)); return; }
                // while sliding the rows below move too; after that only the new row changes colour
                if (t < SlideMs + 20) Invalidate(new Rectangle(0, animY, Width, Math.Max(RowH, Height - animY)));
                else Invalidate(new Rectangle(0, animY, Width, RowH));
            }

            void StopAnim() { animTimer.Stop(); animRow = null; }

            // opening the tab shows the list as it is, without animating what came in while it was hidden
            public void ForgetSeen() { knownKeys = null; StopAnim(); }

            static Color Mix(Color a, Color b, double k)
            {
                return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
            }

            public void Refill()
            {
                var all = History.Recent(50);
                form.SetSummary(all.Count(e => e.Status == "sent") + " sent · " + all.Count(e => e.Status == "queued") + " queued · " + all.Count(e => e.Status == "refused") + " refused");
                var shown = form.Filter == "all" ? all : all.Where(e => e.Status == form.Filter).ToList();
                rows = new List<object>();
                sameAlbumAbove = new HashSet<HistoryEntry>(); sameAlbumBelow = new HashSet<HistoryEntry>();
                string lastDay = null;
                HistoryEntry prev = null;
                foreach (var e in shown)
                {
                    var d = e.Local.Date;
                    string day = d == DateTime.Today ? "today" : d == DateTime.Today.AddDays(-1) ? "yesterday" : d.ToString("ddd d MMM", CultureInfo.InvariantCulture);
                    if (day != lastDay) { rows.Add(day); lastDay = day; prev = null; }   // a new day shows the cover again
                    if (prev != null && SameAlbum(prev, e)) { sameAlbumAbove.Add(e); sameAlbumBelow.Add(prev); }
                    rows.Add(e);
                    prev = e;
                }
                if (rows.Count == 0) rows.Add("nothing here yet");
                Height = rows.Sum(r => r is string ? HeadH : RowH) + 8;

                // a scrobble that wasn't there before, at the very top, while you're looking: animate it
                var keys = new HashSet<string>(all.Select(e => e.Key));
                var top = rows.OfType<HistoryEntry>().FirstOrDefault();
                if (knownKeys != null && top != null && !knownKeys.Contains(top.Key) && Visible)
                {
                    animRow = top;
                    animStart = DateTime.UtcNow;
                    animTimer.Start();
                }
                else if (animRow != null)
                {
                    // the list was rebuilt mid-animation (e.g. queued -> sent): keep going on the new copy of the row
                    string k = animRow.Key;
                    animRow = rows.OfType<HistoryEntry>().FirstOrDefault(x => x.Key == k);
                    if (animRow == null) StopAnim();
                }
                if (animRow != null)
                {
                    animY = 0;
                    foreach (var r in rows) { if (r == (object)animRow) break; animY += r is string ? HeadH : RowH; }
                }
                knownKeys = keys;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs pe)
            {
                var g = pe.Graphics;
                int y = 0, w = Width, off = SlideOffset, shift = 0;
                double glow = Glow;
                var clip = pe.ClipRectangle;
                var flags = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
                using (var sep = new Pen(Color.FromArgb(16, 36, 26)))
                foreach (var r in rows)
                {
                    int h = r is string ? HeadH : RowH;
                    if (r == (object)animRow && off > 0)
                    {
                        // the gap opens from the top; the new row slides down into it
                        var state = g.Save();
                        g.SetClip(new Rectangle(0, y, w, RowH - off), CombineMode.Intersect);
                        PaintRow(g, (HistoryEntry)r, y - off, w, flags, sep, glow);
                        g.Restore(state);
                        shift = off;   // the rows below are still on their way down
                        y += h; continue;
                    }
                    int dy = y - shift;
                    if (dy > clip.Bottom) break;
                    if (dy + h >= clip.Top)
                    {
                        var head = r as string;
                        if (head != null) TextRenderer.DrawText(g, Theme.Rule(head, Font, w - 8), Font, new Rectangle(0, dy, w, HeadH), Theme.Hi, flags);
                        else PaintRow(g, (HistoryEntry)r, dy, w, flags, sep, r == (object)animRow ? glow : 0);
                    }
                    y += h;
                }
            }

            void PaintRow(Graphics g, HistoryEntry e, int y, int w, TextFormatFlags flags, Pen sep, double glow)
            {
                {
                    if (glow > 0) using (var b = new SolidBrush(Mix(Theme.Bg, Color.FromArgb(30, 74, 48), glow))) g.FillRectangle(b, 0, y, w, RowH);
                    if (e.Status == "refused") using (var b = new SolidBrush(Color.FromArgb(26, 13, 11))) g.FillRectangle(b, 0, y, w, RowH);
                    string tag = e.Status == "sent" ? "[SENT]" : e.Status == "queued" ? "[QUEUED]" : "[REFUSED]";
                    Color tagColor = e.Status == "sent" ? Theme.Hi : e.Status == "queued" ? Theme.Warn : Theme.Bad;
                    int tagW = Theme.Measure("[REFUSED]", Font).Width + 8, timeW = Theme.Measure("00:00", Font).Width + 14;
                    TextRenderer.DrawText(g, e.Local.ToString("HH:mm"), Font, new Rectangle(2, y, timeW, RowH), Theme.Dim, flags);
                    var thumbRect = new Rectangle(timeW, y + (RowH - ThumbSz) / 2, ThumbSz, ThumbSz);
                    if (sameAlbumAbove.Contains(e)) DrawAlbumLine(g, thumbRect, y, sameAlbumBelow.Contains(e));
                    else
                    {
                        DrawCover(g, e, thumbRect);
                        if (sameAlbumBelow.Contains(e))
                            using (var p = new Pen(Theme.Line, 2)) g.DrawLine(p, thumbRect.X + thumbRect.Width / 2, thumbRect.Bottom, thumbRect.X + thumbRect.Width / 2, y + RowH);
                    }
                    int textX = timeW + ThumbSz + 12;
                    string text = e.Artist + " - " + e.Track + (e.Status == "refused" && e.Reason != "" ? "  · " + e.Reason : "");
                    var textColor = e.Status == "refused" ? Theme.Bad : Color.FromArgb(220, 247, 228);
                    if (glow > 0 && e.Status != "refused") textColor = Mix(textColor, Theme.Hi, glow);
                    TextRenderer.DrawText(g, text, Font, new Rectangle(textX, y, w - textX - tagW - 8, RowH), textColor,
                        flags | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, tag, Font, new Rectangle(w - tagW, y, tagW, RowH), tagColor, flags);
                    g.DrawLine(sep, 0, y + RowH - 1, w, y + RowH - 1);
                }
            }

            static bool SameAlbum(HistoryEntry a, HistoryEntry b)
            {
                return a.Album != "" && string.Equals(a.Album, b.Album, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
            }

            // "same album as above": a line down from the cover, ending in a small dot on the last song of the run
            void DrawAlbumLine(Graphics g, Rectangle thumb, int rowY, bool continues)
            {
                int cx = thumb.X + thumb.Width / 2, mid = rowY + RowH / 2;
                using (var p = new Pen(Theme.Line, 2)) g.DrawLine(p, cx, rowY, cx, continues ? rowY + RowH : mid);
                var oldMode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Theme.Dim)) g.FillEllipse(b, cx - 3, mid - 3, 6, 6);
                g.SmoothingMode = oldMode;
            }

            // the album cover, or the artist's first letter while it loads / when there's none (like the wraps)
            void DrawCover(Graphics g, HistoryEntry e, Rectangle r)
            {
                var img = Covers.Thumb(e);
                if (img != null)
                {
                    var oldMode = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, r);
                    g.InterpolationMode = oldMode;
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
                else
                {
                    using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, r);
                    string letter = e.Artist.Length > 0 ? e.Artist.Substring(0, 1).ToUpperInvariant() : "?";
                    TextRenderer.DrawText(g, letter, letterFont, r, Theme.Dim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                }
                using (var p = new Pen(Theme.Line)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
        }
    }

    // ================================================================ log viewer
    class LogForm : AsciiForm
    {
        readonly Settings st;
        readonly AsciiToggle fEvents = new AsciiToggle(), fWarn = new AsciiToggle(), fErr = new AsciiToggle(), fVerbose = new AsciiToggle();
        readonly TextBox search = new TextBox { BackColor = Theme.Field, ForeColor = Theme.Hi, BorderStyle = BorderStyle.FixedSingle };
        readonly SyncRichTextBox box = new SyncRichTextBox();
        TermScrollBar logBar;
        readonly Label counts = new Label();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 1500 };
        long lastLength = -1;
        DateTime lastWrite;

        public LogForm(Settings settings, Icon icon)
        {
            st = settings;
            Text = "V's Last.fm Scrobbler // log";
            Icon = icon;

            Text_("log", Theme.Hi, Theme.Mono(13f, FontStyle.Bold));

            int x = L;
            foreach (var pair in new[] { Tuple.Create(fEvents, "events", Theme.Hi), Tuple.Create(fWarn, "warnings", Theme.Warn), Tuple.Create(fErr, "errors", Theme.Bad), Tuple.Create(fVerbose, "verbose", Theme.Text) })
            {
                var t = pair.Item1;
                t.Caption = pair.Item2; t.Checked = pair.Item2 != "verbose" || st.VerboseLog;
                int w = Theme.Measure("[x] " + pair.Item2, Font).Width + 18;
                t.SetBounds(x, y, w, LineH(Font)); x += w + 6;
                t.Click += (s, e) => Reload(true);
                Controls.Add(t);
            }
            // verbose also switches detailed logging on/off for the app
            fVerbose.Click += (s, e) => { st.VerboseLog = fVerbose.Checked; Log.VerboseEnabled = st.VerboseLog; st.Save(); };
            var lab = new Label { Text = "> filter", ForeColor = Theme.Dim, AutoSize = false, UseMnemonic = false };
            int labW = Theme.Measure("> filter", Font).Width + 8, boxW = 190;
            lab.SetBounds(L + Wd - boxW - labW, y + 3, labW, LineH(Font)); Controls.Add(lab);
            search.SetBounds(L + Wd - boxW, y, boxW, LineH(Font));
            search.TextChanged += (s, e) => Reload(true);
            Controls.Add(search);
            y += LineH(Font) + 6;

            Text_(Theme.Rule("data\\log.txt", Font, Wd - 4), Theme.Hi);

            box.ReadOnly = true; box.BorderStyle = BorderStyle.None; box.BackColor = Color.FromArgb(3, 5, 4); box.ForeColor = Theme.Text;
            box.Font = Theme.Mono(9.5f); box.WordWrap = true; box.DetectUrls = false;   // long lines wrap: no sideways scrolling
            logBar = ScrollText(box, 420);
            y += 12;

            counts.ForeColor = Theme.Dim; counts.AutoSize = false; counts.UseMnemonic = false;
            var diag = new AsciiButton("write diagnostics"); var copy = new AsciiButton("copy"); var open = new AsciiButton("open file");
            foreach (var b in new[] { diag, copy, open }) b.Fit(Font);
            open.Location = new Point(L + Wd - open.Width, y);
            copy.Location = new Point(open.Left - 10 - copy.Width, y);
            diag.Location = new Point(copy.Left - 10 - diag.Width, y);
            counts.SetBounds(L, y + 6, diag.Left - L - 10, LineH(Font));
            diag.Click += (s, e) => { Diag.WriteReport(); Reload(true); };
            copy.Click += (s, e) => { try { if (box.TextLength > 0) Clipboard.SetText(box.Text); } catch { } };
            open.Click += (s, e) => { try { Process.Start("notepad.exe", "\"" + Settings.LogPath + "\""); } catch { } };
            Controls.Add(counts); Controls.Add(diag); Controls.Add(copy); Controls.Add(open);
            y += open.Height + 16;
            ClientSize = new Size(L * 2 + Wd, y);

            poll.Tick += (s, e) => { if (!IsDisposed && Visible) Reload(false); };   // only while the log tab is showing
            poll.Start();
            FormClosed += (s, e) => poll.Stop();
            Reload(true);
        }

        public override void TabShown() { Reload(true); }

        // what kind of line is it? (the app writes plain text; this sorts it for colouring)
        static int Kind(string msg)
        {
            string m = msg.ToLowerInvariant();
            if (msg.StartsWith("· ")) return 4;                                                         // verbose
            if (m.Contains("failed") || m.Contains("error") || m.Contains("refused") || m.Contains("rejected") || m.StartsWith("dropped")) return 2;
            if (m.Contains("unreachable") || m.Contains("couldn't") || m.Contains("unavailable") || m.Contains("not found") || m.Contains("didn't")) return 1;
            if (m.StartsWith("milestone") || m.StartsWith("artist milestone") || m.StartsWith("friend match") || m.StartsWith("wrap saved")) return 3;
            return 0;
        }

        void Reload(bool force)
        {
            try
            {
                var fi = new FileInfo(Settings.LogPath);
                long len = fi.Exists ? fi.Length : 0;
                DateTime wt = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
                if (!force && len == lastLength && wt == lastWrite) return;
                lastLength = len; lastWrite = wt;

                string[] lines = new string[0];
                if (fi.Exists)
                    using (var fs = new FileStream(Settings.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8)) lines = sr.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                // multi-line entries (diagnostics) belong to the line before them
                var entries = new List<string>();
                foreach (var l in lines)
                {
                    if (l.Length == 0) continue;
                    if (l.Length > 20 && l[4] == '-' && l[10] == ' ' && l[13] == ':') entries.Add(l);
                    else if (entries.Count > 0) entries[entries.Count - 1] += "\n" + l;
                    else entries.Add(l);
                }

                string q = search.Text.Trim().ToLowerInvariant();
                int nWarn = 0, nErr = 0, shown = 0;
                box.SuspendLayout();
                box.Clear();
                foreach (var en in entries)
                {
                    string stamp = en.Length > 21 ? en.Substring(0, 19) : "";
                    string msg = en.Length > 21 ? en.Substring(21) : en;
                    int k = Kind(msg);
                    if (k == 1) nWarn++;
                    if (k == 2) nErr++;
                    if ((k == 0 || k == 3) && !fEvents.Checked) continue;
                    if (k == 1 && !fWarn.Checked) continue;
                    if (k == 2 && !fErr.Checked) continue;
                    if (k == 4 && !fVerbose.Checked) continue;
                    if (q != "" && !en.ToLowerInvariant().Contains(q)) continue;
                    shown++;
                    Append(stamp + "  ", Theme.Dim);
                    string prefix = k == 1 ? "! " : k == 2 ? "x " : k == 3 ? "* " : "";
                    Color c = k == 1 ? Theme.Warn : k == 2 ? Theme.Bad : k == 3 ? Theme.Hi : k == 4 ? Theme.Dim : Theme.Text;
                    Append(prefix + msg.ToLowerInvariant() + "\n", c);
                }
                Append("> █", Theme.Hi);
                box.SelectionStart = box.TextLength; box.ScrollToCaret();
                box.ResumeLayout();
                logBar.Sync();
                counts.Text = shown + " lines · " + nWarn + (nWarn == 1 ? " warning" : " warnings") + " · " + nErr + (nErr == 1 ? " error" : " errors");
            }
            catch (Exception ex) { counts.Text = "couldn't read the log: " + ex.Message; }
        }

        void Append(string text, Color c)
        {
            box.SelectionStart = box.TextLength; box.SelectionLength = 0;
            box.SelectionColor = c;
            box.AppendText(text);
        }
    }
}
