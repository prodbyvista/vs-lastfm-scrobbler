// Friends listening to the same artist, artist milestones, monthly wrap.
// C# 5 (built-in Windows compiler).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml;

namespace ITunesScrobbler
{
    class Scrobble
    {
        public string Artist = "", Track = "", Album = "", Image;
        public long Uts;
        public DateTime Local { get { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Uts).ToLocalTime(); } }
    }

    // ================================================================ extra Last.fm calls
    partial class LastFm
    {
        public List<string> GetFriends(string user)
        {
            var list = new List<string>();
            for (int page = 1; page <= 4; page++)
            {
                var d = Call("user.getFriends", new Dictionary<string, string> { { "user", user }, { "limit", "50" }, { "page", page.ToString() } }, false);
                foreach (XmlNode n in d.SelectNodes("/lfm/friends/user/name")) list.Add(n.InnerText.Trim());
                var fr = d.SelectSingleNode("/lfm/friends") as XmlElement;
                int pages;
                if (fr == null || !int.TryParse(fr.GetAttribute("totalPages"), out pages) || page >= pages) break;
            }
            return list;
        }

        // What a user is scrobbling right now, or null.
        public Track GetNowPlaying(string user)
        {
            var d = Call("user.getRecentTracks", new Dictionary<string, string> { { "user", user }, { "limit", "1" } }, false);
            var tr = d.SelectSingleNode("/lfm/recenttracks/track[@nowplaying='true']");
            if (tr == null) return null;
            return new Track { Artist = tr.SelectSingleNode("artist").InnerText.Trim(), Name = tr.SelectSingleNode("name").InnerText.Trim() };
        }

        // Scrobbles between two unix times (inclusive), oldest first.
        public List<Scrobble> GetScrobbles(string user, long from, long to, int maxPages)
        {
            var all = new List<Scrobble>();
            for (int page = 1; page <= maxPages; page++)
            {
                var d = Call("user.getRecentTracks", new Dictionary<string, string> {
                    { "user", user }, { "limit", "200" }, { "page", page.ToString() },
                    { "from", from.ToString() }, { "to", to.ToString() } }, false);
                foreach (XmlElement tr in d.SelectNodes("/lfm/recenttracks/track[not(@nowplaying='true')]"))
                {
                    var date = tr.SelectSingleNode("date") as XmlElement;
                    if (date == null) continue;
                    var s = new Scrobble();
                    s.Artist = tr.SelectSingleNode("artist").InnerText.Trim();
                    s.Track = tr.SelectSingleNode("name").InnerText.Trim();
                    var al = tr.SelectSingleNode("album");
                    s.Album = al != null ? al.InnerText.Trim() : "";
                    long.TryParse(date.GetAttribute("uts"), out s.Uts);
                    var img = tr.SelectSingleNode("image[@size='extralarge']");
                    if (img != null)
                    {
                        string u = img.InnerText.Trim();
                        if (u != "" && !u.Contains("2a96cbd8b46e442fc41c2b86b821562f")) s.Image = u;
                    }
                    all.Add(s);
                }
                var rt = d.SelectSingleNode("/lfm/recenttracks") as XmlElement;
                int pages;
                if (rt == null || !int.TryParse(rt.GetAttribute("totalPages"), out pages) || page >= pages) break;
                Thread.Sleep(200);   // be gentle with the API
            }
            all.Sort((a, b) => a.Uts.CompareTo(b.Uts));
            return all;
        }

        // number of scrobbles between two unix times
        public int GetScrobbleCount(string user, long from, long to)
        {
            var d = Call("user.getRecentTracks", new Dictionary<string, string> {
                { "user", user }, { "limit", "1" }, { "from", from.ToString() }, { "to", to.ToString() } }, false);
            var rt = d.SelectSingleNode("/lfm/recenttracks") as XmlElement;
            int n = 0;
            if (rt != null) int.TryParse(rt.GetAttribute("total"), out n);
            return n;
        }

        // a user's profile picture (large), null if they have none
        public string GetUserImage(string user)
        {
            var d = Call("user.getInfo", new Dictionary<string, string> { { "user", user } }, false);
            var n = d.SelectSingleNode("/lfm/user/image[@size='extralarge']") ?? d.SelectSingleNode("/lfm/user/image[@size='large']");
            string u = n != null ? n.InnerText.Trim() : "";
            return u == "" || u.Contains("2a96cbd8b46e442fc41c2b86b821562f") ? null : u;
        }

        public long GetArtistPlayCount(string artist, string user)
        {
            try { return ArtistPlayCount(artist, user, false); }
            catch (LastFmException ex)
            {
                // error 6 "the artist you supplied could not be found": happens for some names that come
                // straight from your scrobbles; Last.fm's own spelling correction usually finds them
                if (ex.Code != 6) throw;
                return ArtistPlayCount(artist, user, true);
            }
        }

        long ArtistPlayCount(string artist, string user, bool autocorrect)
        {
            var d = Call("artist.getInfo", new Dictionary<string, string> {
                { "artist", artist }, { "username", user }, { "autocorrect", autocorrect ? "1" : "0" } }, false);
            var n = d.SelectSingleNode("/lfm/artist/stats/userplaycount");
            long c = 0;
            if (n != null) long.TryParse(n.InnerText.Trim(), out c);
            return c;
        }

        // Track length in seconds, 0 if Last.fm doesn't know it.
        public int GetTrackDuration(string artist, string track)
        {
            try
            {
                var d = Call("track.getInfo", new Dictionary<string, string> { { "artist", artist }, { "track", track }, { "autocorrect", "1" } }, false);
                var n = d.SelectSingleNode("/lfm/track/duration");
                long ms = 0;
                if (n != null) long.TryParse(n.InnerText.Trim(), out ms);
                return (int)(ms / 1000);
            }
            catch (LastFmException ex) { if (ex.Transient) throw; return 0; }
        }
    }

    // ================================================================ track length cache (for listening time)
    static class Durations
    {
        static readonly object gate = new object();
        static Dictionary<string, int> map;
        static string FilePath { get { return Path.Combine(Settings.Dir, "durations.txt"); } }
        public static string Key(string artist, string track) { return (artist + "\t" + track).ToLowerInvariant(); }

        static void EnsureLoaded()
        {
            if (map != null) return;
            map = new Dictionary<string, int>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        var f = line.Split('\t');
                        int sec;
                        if (f.Length == 3 && int.TryParse(f[2], out sec)) map[(f[0] + "\t" + f[1]).ToLowerInvariant()] = sec;
                    }
            }
            catch (Exception ex) { Log.Write("Duration cache load failed: " + ex.Message); }
        }

        public static bool TryGet(string artist, string track, out int sec)
        {
            lock (gate) { EnsureLoaded(); return map.TryGetValue(Key(artist, track), out sec); }
        }

        public static void Set(string artist, string track, int sec)
        {
            lock (gate) { EnsureLoaded(); map[Key(artist, track)] = sec; }
        }

        public static void Save()
        {
            lock (gate)
            {
                if (map == null) return;
                try { File.WriteAllLines(FilePath, map.Select(kv => kv.Key + "\t" + kv.Value).ToArray(), new UTF8Encoding(false)); }
                catch (Exception ex) { Log.Write("Duration cache save failed: " + ex.Message); }
            }
        }
    }

    // ================================================================ friends listening to the same artist
    class FriendWatch
    {
        readonly Func<Settings> settings;
        readonly Func<Track> nowPlaying;
        readonly Action<Toast> notify;
        DateTime nextCheck = DateTime.UtcNow.AddSeconds(30);
        readonly Dictionary<string, DateTime> lastNotified = new Dictionary<string, DateTime>();
        volatile bool busy;

        public FriendWatch(Func<Settings> s, Func<Track> np, Action<Toast> n) { settings = s; nowPlaying = np; notify = n; }

        public void Tick()
        {
            var st = settings();
            if (busy || !st.FriendsEnabled || !st.IsConfigured || st.UserName == "" || DateTime.UtcNow < nextCheck) return;
            var t = nowPlaying();
            if (t == null) return;
            nextCheck = DateTime.UtcNow.AddMinutes(3);
            busy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Check(st, t); }
                catch (LastFmException ex) { if (!ex.Transient) Log.Write("Friends check failed: " + ex.Message); }
                catch (Exception ex) { Log.Write("Friends check error: " + ex.Message); }
                finally { busy = false; }
            });
        }

        static string Norm(string s) { return (s ?? "").Trim().ToLowerInvariant(); }

        void Check(Settings st, Track mine)
        {
            var api = new LastFm(st.ApiKey, st.ApiSecret);
            foreach (var f in FriendFeed.List(api, st.UserName, false).Take(FriendFeed.Max))
            {
                // only ask Last.fm about friends that are due (see FriendFeed); the rest use what's known
                FriendTrack theirs;
                if (FriendFeed.Due(f.Name)) { Thread.Sleep(250); theirs = FriendFeed.Check(api, f.Name); }
                else theirs = FriendFeed.Latest(f.Name);
                if (theirs == null || !theirs.NowPlaying || Norm(theirs.Artist) != Norm(mine.Artist)) continue;

                string key = Norm(f.Name) + "|" + Norm(mine.Artist);
                DateTime last;
                if (lastNotified.TryGetValue(key, out last) && DateTime.UtcNow - last < TimeSpan.FromHours(3)) continue;
                lastNotified[key] = DateTime.UtcNow;

                bool sameSong = Norm(theirs.Name) == Norm(mine.Name);
                string title = f.Name + " is listening to " + theirs.Artist + " too";
                string body = sameSong ? "Even the same song: " + theirs.Name + "." : "They're on " + theirs.Name + " right now.";
                Log.Write("Friend match: " + f.Name + " - " + theirs.Artist + " - " + theirs.Name);
                string profile = "https://www.last.fm/user/" + Uri.EscapeDataString(f.Name);
                string avatar = null;
                try { avatar = api.GetUserImage(f.Name); } catch { }
                notify(new Toast { Title = title, Body = body, Url = profile, Image = avatar, CircleImage = true, Attribution = "Friend on Last.fm" }.Button("Open profile", profile));
            }
        }
    }

    // ================================================================ artist milestones
    // Looks at every new scrobble (Spotify included) since the last check and announces
    // when an artist crosses a multiple of ArtistMilestoneEvery (default 500).
    static class ArtistMilestones
    {
        public static void Check(Settings st, LastFm api, Action<Toast> notify)
        {
            long every = st.ArtistMilestoneEvery;
            if (!st.ArtistMilestonesEnabled || every <= 0) return;
            long now = Engine.Now();
            long to = now - 120;   // give Last.fm's per-artist counts a moment to catch up
            if (st.LastArtistCheck <= 0)
            {
                st.LastArtistCheck = to; st.Save();
                Log.Write("Artist milestones: watching from now on (every " + every + " scrobbles per artist)");
                return;
            }
            if (to <= st.LastArtistCheck) return;

            var scrobbles = api.GetScrobbles(st.UserName, st.LastArtistCheck + 1, to, 5);
            if (scrobbles.Count == 0) { st.LastArtistCheck = to; st.Save(); return; }

            foreach (var g in scrobbles.GroupBy(s => s.Artist))
            {
                var mine = g.OrderBy(s => s.Uts).ToList();
                long count;
                try { count = api.GetArtistPlayCount(g.Key, st.UserName); }
                catch (LastFmException ex)
                {
                    // an artist Last.fm can't look up just gets skipped; it used to stop the whole check,
                    // so the same failure came back every few minutes
                    if (ex.Transient) throw;
                    Diag.Once("artist-missing:" + g.Key.ToLowerInvariant(), "Artist milestones: skipping \"" + g.Key + "\" (Last.fm has no artist page for it)");
                    continue;
                }
                long before = count - mine.Count;
                if (before < 0) continue;
                if (count / every <= before / every) continue;

                long milestone = count / every * every;
                int k = (int)(milestone - before);             // k-th new scrobble hit it
                string which = k >= 1 && k <= mine.Count ? mine[k - 1].Track : null;
                string n = milestone.ToString("N0", CultureInfo.InvariantCulture);
                Log.Write("Artist milestone: " + g.Key + " " + n + (which != null ? " - " + which : ""));
                string page = "https://www.last.fm/user/" + Uri.EscapeDataString(st.UserName) + "/library/music/" + Uri.EscapeDataString(g.Key);
                var hit = k >= 1 && k <= mine.Count ? mine[k - 1] : null;
                notify(new Toast
                {
                    Title = n + " " + g.Key + " scrobbles",
                    Body = which != null ? "Number " + n + " was " + which + (hit != null && hit.Album != "" ? ", from " + hit.Album : "") + "." : "You've scrobbled " + g.Key + " " + n + " times.",
                    Url = page, Image = hit != null ? hit.Image : null, Attribution = "Artist milestone"
                }.Button("Open artist page", page));
                Thread.Sleep(250);
            }
            st.LastArtistCheck = Math.Max(to, scrobbles.Max(s => s.Uts));
            st.Save();
        }
    }

    // ================================================================ monthly + yearly wraps
    class WrapData
    {
        public string User = "", Month = "";              // Month = the title: "October 2026", or "2026" for a year
        public bool Partial, Estimated, Record, Year;
        public int Scrobbles, ArtistCount, BusiestDayCount, BusiestHour, PrevBest, PrevYears, RepeatRun;
        public double TotalSec;
        public long AllTime;
        public List<MonthlyWrap.Entry> Artists, Albums, Tracks;
        public Dictionary<DateTime, int> PerDay;
        public DateTime BusiestDay, From, To;
        public int[] PerHour;
        public List<string> NewArtists = new List<string>();
        public List<MonthCell> Months = new List<MonthCell>();   // year wraps: one per month
        public string RepeatTrack;
    }

    class MonthCell { public DateTime Month; public int N; public string Artist = "", Image; }

    class MonthlyWrap
    {
        static readonly CultureInfo En = CultureInfo.InvariantCulture;
        readonly Func<Settings> settings;
        readonly Action<Toast> notify;
        DateTime nextCheck = DateTime.UtcNow.AddMinutes(1);
        volatile bool busy;
        string lastSummary, lastHero;

        public MonthlyWrap(Func<Settings> s, Action<Toast> n) { settings = s; notify = n; }

        public static string Folder { get { return Path.Combine(Settings.AppDir, "Wraps"); } }
        public bool Busy { get { return busy; } }

        // Automatic wraps: last month's on the 1st, last year's on 1 January (after December's).
        public void Tick()
        {
            var st = settings();
            if (busy || !st.WrapsEnabled || !st.IsConfigured || st.UserName == "" || DateTime.UtcNow < nextCheck) return;
            nextCheck = DateTime.UtcNow.AddMinutes(15);
            var now = DateTime.Now;
            var thisMonth = new DateTime(now.Year, now.Month, 1);
            var lastMonth = thisMonth.AddMonths(-1);
            string key = lastMonth.ToString("yyyy-MM", En);
            if (st.LastWrapMonth != key)
            {
                Start(st, lastMonth, thisMonth, false, false, s => s.LastWrapMonth = key);
                return;
            }

            int lastYear = now.Year - 1;
            string yearKey = lastYear.ToString(En);
            if (st.LastWrapYear == yearKey) return;
            if (st.LastWrapYear == "" && now.Month != 1)
            {
                // first start of a version with yearly wraps, months after the year ended:
                // don't start a long build out of the blue (the tray menu can make that year's wrap)
                st.LastWrapYear = yearKey; st.Save();
                return;
            }
            Start(st, new DateTime(lastYear, 1, 1), new DateTime(now.Year, 1, 1), false, true, s => s.LastWrapYear = yearKey);
        }

        // "This month so far"
        public void MakeNow()
        {
            var st = settings();
            if (busy || !st.IsConfigured || st.UserName == "") return;
            Start(st, new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1), DateTime.Now, true, false, null);
        }

        // "This year so far"
        public void MakeYearSoFar()
        {
            var st = settings();
            if (busy || !st.IsConfigured || st.UserName == "") return;
            Start(st, new DateTime(DateTime.Now.Year, 1, 1), DateTime.Now, true, true, null);
        }

        // A finished year (from the tray menu)
        public void MakeYear(int year)
        {
            var st = settings();
            if (busy || !st.IsConfigured || st.UserName == "" || year >= DateTime.Now.Year) return;
            string yearKey = year.ToString(En);
            Start(st, new DateTime(year, 1, 1), new DateTime(year + 1, 1, 1), false, true,
                year == DateTime.Now.Year - 1 ? (Action<Settings>)(s => s.LastWrapYear = yearKey) : null);
        }

        void Start(Settings st, DateTime from, DateTime to, bool partial, bool year, Action<Settings> markDone)
        {
            busy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string path = Build(st, from, to, partial, year);
                    if (markDone != null)
                    {
                        var cur = settings();   // the settings object may have been reloaded meanwhile
                        markDone(cur); cur.Save();
                    }
                    string title = year ? from.Year.ToString(En) : from.ToString("MMMM yyyy", En);
                    if (path == null)
                        notify(new Toast { Title = title, Body = "No scrobbles " + (year ? "that year" : "that month") + ", so no wrap." });
                    else
                    {
                        string wrapUri = new Uri(path).AbsoluteUri, shelfUri = new Uri(WrapsShelf.IndexPath).AbsoluteUri;
                        var t = new Toast
                        {
                            Title = "Your " + title + " wrap is ready" + (partial ? " (so far)" : ""),
                            Body = lastSummary ?? "Click to open it.",
                            Url = wrapUri, Hero = lastHero, Attribution = year ? "Yearly wrap" : "Monthly wrap"
                        };
                        notify(t.Button("Open wrap", wrapUri).Button("Open shelf", shelfUri));
                    }
                    nextCheck = DateTime.UtcNow.AddMinutes(1);   // another one may be due (December's, then the year's)
                }
                catch (LastFmException ex) { Log.Write("Wrap failed (" + ex.Message + "), will retry later"); nextCheck = DateTime.UtcNow.AddMinutes(30); }
                catch (Exception ex) { Log.Write("Wrap error: " + ex); nextCheck = DateTime.UtcNow.AddHours(1); }
                finally { busy = false; }
            });
        }

        static long Unix(DateTime local) { return (long)(local.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        internal class Entry { public string Name, Sub, Image; public int N; }

        static string TopImage(IEnumerable<Scrobble> g)
        {
            return g.Where(s => s.Image != null).GroupBy(s => s.Image).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
        }

        static List<Entry> Top(IEnumerable<IGrouping<string, Scrobble>> groups, Func<Scrobble, string> name, Func<Scrobble, string> sub, int take)
        {
            return groups.Select(g => new Entry
            {
                N = g.Count(),
                Name = name(g.First()),
                Sub = sub(g.First()),
                Image = TopImage(g)   // cover: the most-played album image within the group
            }).OrderByDescending(c => c.N).ThenBy(c => c.Name).Take(take).ToList();
        }

        // file name without ".html": "2026-10", "2026-10 (so far)", "2026", "2026 (so far)"
        static string Stem(DateTime from, bool year, bool partial)
        {
            return (year ? from.Year.ToString(En) : from.ToString("yyyy-MM", En)) + (partial ? " (so far)" : "");
        }

        string Build(Settings st, DateTime fromLocal, DateTime toLocal, bool partial, bool year)
        {
            var api = new LastFm(st.ApiKey, st.ApiSecret);
            string title = year ? fromLocal.Year.ToString(En) : fromLocal.ToString("MMMM yyyy", En);
            Log.Write("Building " + (year ? "yearly" : "monthly") + " wrap for " + title + (partial ? " (so far)" : "") + "...");
            var all = api.GetScrobbles(st.UserName, Unix(fromLocal), Unix(toLocal) - 1, year ? 500 : 100);
            if (all.Count == 0) return null;

            // ---- listening time: look up each distinct track's length once (cached in durations.txt)
            var distinct = all.GroupBy(s => Durations.Key(s.Artist, s.Track)).Select(g => g.First()).ToList();
            int looked = 0;
            foreach (var s in distinct)
            {
                int sec;
                if (Durations.TryGet(s.Artist, s.Track, out sec)) continue;
                sec = api.GetTrackDuration(s.Artist, s.Track);
                Durations.Set(s.Artist, s.Track, sec);
                if (++looked % 100 == 0) { Durations.Save(); Log.Verbose("Wrap: looked up " + looked + " track lengths..."); }
                Thread.Sleep(150);
            }
            Durations.Save();
            long knownSec = 0; int known = 0;
            foreach (var s in all) { int sec; if (Durations.TryGet(s.Artist, s.Track, out sec) && sec > 0) { knownSec += sec; known++; } }
            double avg = known > 0 ? (double)knownSec / known : 210;
            double totalSec = knownSec + (all.Count - known) * avg;
            bool estimated = known < all.Count;

            // ---- tops
            var artists = Top(all.GroupBy(s => s.Artist.ToLowerInvariant()), s => s.Artist, s => "", 10);
            var albums = Top(all.Where(s => s.Album != "").GroupBy(s => (s.Artist + "\t" + s.Album).ToLowerInvariant()), s => s.Album, s => s.Artist, 10);
            var tracks = Top(all.GroupBy(s => (s.Artist + "\t" + s.Track).ToLowerInvariant()), s => s.Track, s => s.Artist, 10);

            // ---- busiest day / hour
            var perDay = all.GroupBy(s => s.Local.Date).ToDictionary(g => g.Key, g => g.Count());
            var busiestDay = perDay.OrderByDescending(kv => kv.Value).First();
            var perHour = new int[24];
            foreach (var s in all) perHour[s.Local.Hour]++;
            int busiestHour = Array.IndexOf(perHour, perHour.Max());
            int artistCount = all.Select(s => s.Artist.ToLowerInvariant()).Distinct().Count();

            var w = new WrapData
            {
                User = st.UserName, Month = title, Partial = partial, Year = year, Scrobbles = all.Count, TotalSec = totalSec, Estimated = estimated,
                ArtistCount = artistCount, Artists = artists, Albums = albums, Tracks = tracks, PerDay = perDay,
                BusiestDay = busiestDay.Key, BusiestDayCount = busiestDay.Value, PerHour = perHour, BusiestHour = busiestHour,
                From = fromLocal, To = toLocal
            };
            try { w.AllTime = api.GetPlayCount(st.UserName); } catch (Exception ex) { Log.Write("Wrap: all-time count failed: " + ex.Message); }

            // ---- LOUD: biggest month of the past year / biggest year so far
            try
            {
                if (!year)
                {
                    for (int i = 1; i <= 12; i++)
                    {
                        var ms = fromLocal.AddMonths(-i);
                        w.PrevBest = Math.Max(w.PrevBest, api.GetScrobbleCount(st.UserName, Unix(ms), Unix(ms.AddMonths(1)) - 1));
                        Thread.Sleep(150);
                    }
                    w.Record = w.PrevBest > 0 && all.Count >= w.PrevBest;
                }
                else
                {
                    for (int i = 1; i <= 15; i++)   // back until the first year with nothing in it
                    {
                        var ys = fromLocal.AddYears(-i);
                        int n = api.GetScrobbleCount(st.UserName, Unix(ys), Unix(ys.AddYears(1)) - 1);
                        if (n == 0) break;
                        w.PrevBest = Math.Max(w.PrevBest, n);
                        w.PrevYears = i;
                        Thread.Sleep(150);
                    }
                    w.Record = w.PrevYears > 0 && all.Count >= w.PrevBest;
                }
            }
            catch (Exception ex) { Log.Write("Wrap: earlier " + (year ? "years" : "months") + " failed: " + ex.Message); }

            // ---- NEW: top artists you first heard in this period (all their plays fall inside it)
            foreach (var a in artists)
            {
                try
                {
                    long total = api.GetArtistPlayCount(a.Name, st.UserName);
                    if (total > 0 && total <= a.N) w.NewArtists.Add(a.Name);
                    Thread.Sleep(150);
                }
                catch { }
            }

            // ---- RPT: longest run of the same song back to back
            int run = 1;
            for (int i = 1; i < all.Count; i++)
            {
                bool same = string.Equals(all[i].Artist, all[i - 1].Artist, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(all[i].Track, all[i - 1].Track, StringComparison.OrdinalIgnoreCase);
                run = same ? run + 1 : 1;
                if (run > w.RepeatRun) { w.RepeatRun = run; w.RepeatTrack = all[i].Track + " / " + all[i].Artist; }
            }

            // ---- year: each month's count and #1 artist
            if (year)
                foreach (var mg in all.GroupBy(s => new DateTime(s.Local.Year, s.Local.Month, 1)).OrderBy(x => x.Key))
                {
                    var top = mg.GroupBy(s => s.Artist.ToLowerInvariant()).OrderByDescending(x => x.Count()).First();
                    w.Months.Add(new MonthCell { Month = mg.Key, N = mg.Count(), Artist = top.First().Artist, Image = TopImage(top) });
                }

            // ---- page (layout lives in WrapPage.cs)
            string html = WrapPage.Render(w);

            Directory.CreateDirectory(Folder);
            string stem = Stem(fromLocal, year, partial);
            string file = Path.Combine(Folder, stem + ".html");
            File.WriteAllText(file, html, new UTF8Encoding(false));
            Log.Write("Wrap saved: " + file);
            if (!partial)
            {
                // the finished wrap replaces its "so far" version
                string sofar = Stem(fromLocal, year, true) + ".html";
                try { File.Delete(Path.Combine(Folder, sofar)); } catch { }
                WrapsShelf.Forget(sofar);
            }
            WrapsShelf.Record(w, Path.GetFileName(file));
            string unit = year ? "year" : "month";
            lastSummary = (w.Artists.Count > 0 ? "Artist of the " + unit + ": " + w.Artists[0].Name + ". " : "") + w.Scrobbles.ToString("N0", En) + " scrobbles.";
            string heroKey = stem.Replace(" (so far)", "-sofar");
            lastHero = ToastSender.WrapHero(heroKey, year ? "YEARLY WRAP" : "MONTHLY WRAP",
                year ? fromLocal.Year.ToString(En) : fromLocal.ToString("MMM yyyy", En).ToUpperInvariant(),
                w.Scrobbles.ToString("N0", En) + " SCROBBLES" + (w.Artists.Count > 0 ? "  ·  #1 " + w.Artists[0].Name.ToUpperInvariant() : ""));
            try { WrapsShelf.Build(); } catch (Exception ex) { Log.Write("Wraps shelf: " + ex.Message); }
            return file;
        }
    }
}
