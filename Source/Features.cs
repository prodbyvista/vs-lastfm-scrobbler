// Persistent scrobble queue, Last.fm milestone notifications and Discord Rich Presence.
// C# 5 (built-in Windows compiler).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace ITunesScrobbler
{
    class Pending
    {
        public Track Track;
        public long Timestamp;
    }

    // ================================================================ offline queue
    // Every scrobble goes through this queue, which lives in queue.txt next to the exe.
    // It survives being offline, Last.fm outages, crashes, reboots and shutdowns.
    class ScrobbleQueue
    {
        const int MaxAgeSeconds = 14 * 24 * 3600 - 3600;   // Last.fm rejects scrobbles older than 14 days
        readonly object gate = new object();
        readonly List<Pending> items = new List<Pending>();
        volatile bool busy;
        DateTime nextAttempt = DateTime.MinValue;
        int backoffSeconds = 30;
        bool offline;

        public Action SessionProblem;      // login rejected
        public Action<int> Sent;           // n scrobbles accepted

        static string FilePath { get { return Path.Combine(Settings.Dir, "queue.txt"); } }

        public int Count { get { lock (gate) return items.Count; } }
        public bool Offline { get { return offline; } }

        public ScrobbleQueue()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var f = line.Split('\t');
                    if (f.Length < 7) continue;
                    long ts; int dur, num;
                    if (!long.TryParse(f[0], out ts)) continue;
                    int.TryParse(f[5], out dur); int.TryParse(f[6], out num);
                    var t = new Track { Artist = f[1], Name = f[2], Album = f[3], AlbumArtist = f[4], Duration = dur, TrackNumber = num };
                    t.Normalize();   // fixes entries queued by older versions
                    items.Add(new Pending { Track = t, Timestamp = ts });
                }
                if (items.Count > 0) Log.Write("Loaded " + items.Count + " queued scrobble(s) from queue.txt");
            }
            catch (Exception ex) { Log.Write("Queue load failed: " + ex.Message); }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

        void SaveLocked()
        {
            try
            {
                var lines = items.Select(p => p.Timestamp + "\t" + Clean(p.Track.Artist) + "\t" + Clean(p.Track.Name) + "\t" +
                    Clean(p.Track.Album) + "\t" + Clean(p.Track.AlbumArtist) + "\t" + p.Track.Duration + "\t" + p.Track.TrackNumber).ToArray();
                string tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Write("Queue save failed: " + ex.Message); }
        }

        public void Add(Pending p)
        {
            p.Track.Normalize();
            lock (gate) { items.Add(p); SaveLocked(); nextAttempt = DateTime.MinValue; }
            History.Queued(p);
            Log.Verbose("Queued: " + p.Track);
        }

        public void Tick(Settings st)
        {
            if (busy || !st.IsConfigured || st.Paused || DateTime.UtcNow < nextAttempt || Count == 0) return;
            busy = true;
            var api = new LastFm(st.ApiKey, st.ApiSecret);
            string sk = st.SessionKey;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Flush(api, sk); }
                catch (Exception ex) { Log.Write("Queue flush error: " + ex.Message); nextAttempt = DateTime.UtcNow.AddSeconds(60); }
                finally { busy = false; }
            });
        }

        void Remove(IEnumerable<Pending> done)
        {
            lock (gate) { foreach (var p in done) items.Remove(p); SaveLocked(); }
        }

        void Flush(LastFm api, string sk)
        {
            while (true)
            {
                List<Pending> batch;
                long now = Engine.Now();
                lock (gate)
                {
                    var tooOld = items.Where(p => now - p.Timestamp > MaxAgeSeconds).ToList();
                    foreach (var p in tooOld) { items.Remove(p); History.Refused(p, "older than 14 days"); Log.Write("Dropped (older than 14 days, Last.fm won't accept it): " + p.Track); }
                    if (tooOld.Count > 0) SaveLocked();
                    batch = items.OrderBy(p => p.Timestamp).Take(50).ToList();
                }
                if (batch.Count == 0) return;

                try
                {
                    var ignored = api.ScrobbleBatch(batch, sk);
                    Remove(batch);
                    for (int i = 0; i < batch.Count; i++)
                    {
                        string why;
                        if (ignored.TryGetValue(i, out why)) History.Refused(batch[i], why); else History.Sent(batch[i]);
                    }
                    backoffSeconds = 30;
                    if (offline) { offline = false; Log.Write("Last.fm reachable again, sent " + batch.Count + " queued scrobble(s)"); }
                    Log.Verbose("Scrobbled " + batch.Count + ": " + string.Join(" | ", batch.Select(p => p.Track.ToString())));
                    if (Sent != null) Sent(batch.Count);
                }
                catch (LastFmException ex)
                {
                    if (ex.BadSession)
                    {
                        Log.Write("Scrobbling paused - Last.fm rejected the login (" + ex.Message + ")");
                        if (SessionProblem != null) SessionProblem();
                        nextAttempt = DateTime.UtcNow.AddMinutes(5);
                        return;
                    }
                    if (ex.Transient)
                    {
                        if (!offline) { offline = true; Log.Write("Last.fm unreachable (" + ex.Message + "), keeping scrobbles in the queue until it's back"); }
                        nextAttempt = DateTime.UtcNow.AddSeconds(backoffSeconds);
                        backoffSeconds = Math.Min(backoffSeconds * 2, 900);
                        return;
                    }
                    // A permanent error. Find the bad entry by sending one at a time.
                    Log.Write("Batch rejected (" + ex.Code + " " + ex.Message + "), retrying individually");
                    foreach (var p in batch)
                    {
                        try
                        {
                            var ign1 = api.ScrobbleBatch(new List<Pending> { p }, sk);
                            Remove(new[] { p });
                            if (ign1.Count > 0) History.Refused(p, ign1.Values.First()); else History.Sent(p);
                            Log.Verbose("Scrobbled: " + p.Track);
                            if (Sent != null) Sent(1);
                        }
                        catch (LastFmException e2)
                        {
                            if (e2.Transient || e2.BadSession) { nextAttempt = DateTime.UtcNow.AddSeconds(60); return; }
                            Remove(new[] { p });
                            History.Refused(p, e2.Message.ToLowerInvariant());
                            Log.Write("Dropped (Last.fm error " + e2.Code + " " + e2.Message + "): " + p.Track);
                        }
                    }
                }
            }
        }
    }

    // ================================================================ milestones
    // Checks your total Last.fm scrobble count (so Spotify & co. count too) and shows a
    // Windows notification at every multiple of MilestoneEvery (default 5000).
    class Milestones
    {
        readonly Func<Settings> settings;
        readonly Action<Toast> notify;
        DateTime nextCheck = DateTime.UtcNow.AddSeconds(20);
        volatile bool busy;

        public Milestones(Func<Settings> s, Action<Toast> notifyCb) { settings = s; notify = notifyCb; }

        // after one of our own scrobbles, check sooner
        public void Soon() { var t = DateTime.UtcNow.AddSeconds(15); if (t < nextCheck) nextCheck = t; }

        public void Tick()
        {
            var st = settings();
            if (busy || !st.IsConfigured || st.UserName == "" || DateTime.UtcNow < nextCheck) return;
            if (!(st.MilestonesEnabled && st.MilestoneEvery > 0) && !(st.ArtistMilestonesEnabled && st.ArtistMilestoneEvery > 0)) return;
            nextCheck = DateTime.UtcNow.AddMinutes(3);
            busy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Check(st); }
                catch (LastFmException ex) { if (!ex.Transient) Log.Write("Milestone check failed: " + ex.Message); }
                catch (Exception ex) { Log.Write("Milestone check error: " + ex.Message); }
                finally { busy = false; }
            });
        }

        static string N(long n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        void Check(Settings st)
        {
            var api = new LastFm(st.ApiKey, st.ApiSecret);
            try { ArtistMilestones.Check(st, api, notify); }
            catch (LastFmException ex) { if (!ex.Transient) Log.Write("Artist milestone check failed: " + ex.Message); }
            if (!st.MilestonesEnabled || st.MilestoneEvery <= 0) return;
            long count = api.GetPlayCount(st.UserName);
            long every = st.MilestoneEvery;
            long reached = count / every * every;

            if (st.LastMilestone < 0)
            {
                // first run: remember where you are, don't celebrate old milestones
                st.LastMilestone = reached; st.Save();
                Log.Write("Milestones: " + N(count) + " scrobbles so far, next notification at " + N(reached + every));
                return;
            }
            if (reached <= st.LastMilestone || reached == 0) return;

            string which = null, cover = null;
            try { which = api.GetScrobbleNumber(st.UserName, reached, out cover); } catch (Exception ex) { Log.Write("Couldn't look up milestone track: " + ex.Message); }

            st.LastMilestone = reached; st.Save();
            string title = N(reached) + " scrobbles!";
            string body = which != null
                ? "Your " + N(reached) + "th scrobble was " + which + "."
                : "You just passed " + N(reached) + " scrobbles on Last.fm.";
            Log.Write("Milestone reached: " + N(reached) + (which != null ? " - " + which : ""));
            string profile = "https://www.last.fm/user/" + Uri.EscapeDataString(st.UserName);
            var t = new Toast { Title = title, Body = body, Url = profile, Image = cover, Hero = ToastSender.MilestoneHero(reached), Attribution = "Last.fm milestone", DismissButton = true };
            notify(t.Button("View on Last.fm", profile));
        }
    }

    // ================================================================ Discord Rich Presence
    // Talks to the Discord desktop app over its local IPC pipe. Shows "Listening to <your app's name>"
    // with title, artist, album art and a progress bar. Only for what this app tracks (Apple Music / iTunes).
    class DiscordPresence
    {
        class Desired { public Track Track; public bool Playing; public long StartMs; }

        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Dictionary<string, string> artCache = new Dictionary<string, string>();
        Desired desired = new Desired();
        Desired sent;
        bool dirty;
        string clientId = "";
        volatile bool stopping;

        NamedPipeClientStream pipe;
        string connectedId;
        readonly object writeGate = new object();
        DateTime nextConnect = DateTime.MinValue;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        string apiKey = "", apiSecret = "";
        public static string StatusText = "off";

        public DiscordPresence()
        {
            var th = new Thread(Worker) { IsBackground = true, Name = "Discord" };
            th.Start();
        }

        static long NowMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        // Called from the UI thread every poll.
        public void Update(Settings st, Track t, bool playing, int position)
        {
            string id = st.DiscordEnabled ? (st.DiscordClientId ?? "").Trim() : "";
            bool show = id != "" && t != null && playing;
            long start = show ? NowMs() - position * 1000L : 0;
            lock (gate)
            {
                apiKey = st.ApiKey; apiSecret = st.ApiSecret;
                bool changed = id != clientId
                    || show != (desired.Track != null && desired.Playing)
                    || (show && (desired.Track == null || desired.Track.Id != t.Id || Math.Abs(desired.StartMs - start) > 5000));
                if (!changed) return;
                clientId = id;
                desired = new Desired { Track = show ? t : null, Playing = show, StartMs = start };
                dirty = true;
            }
            wake.Set();
        }

        public void Shutdown()
        {
            stopping = true;
            try { if (pipe != null && pipe.IsConnected) Send(1, ClearJson()); } catch { }
            Disconnect();
        }

        void Worker()
        {
            while (!stopping)
            {
                wake.WaitOne(15000);
                Desired want; string id;
                lock (gate) { if (!dirty) continue; want = desired; id = clientId; }

                try
                {
                    if (id == "")
                    {
                        if (pipe != null) { try { Send(1, ClearJson()); } catch { } Disconnect(); }
                        StatusText = "off";
                        lock (gate) { if (desired == want) dirty = false; }
                        continue;
                    }
                    if (pipe == null || !pipe.IsConnected || connectedId != id)
                    {
                        if (DateTime.UtcNow < nextConnect) continue;
                        Disconnect();
                        if (!Connect(id)) { nextConnect = DateTime.UtcNow.AddSeconds(20); continue; }
                    }

                    string json;
                    if (want.Track == null) json = ClearJson();
                    else json = ActivityJson(want.Track, want.StartMs, ArtFor(want.Track));
                    Send(1, json);
                    sent = want;
                    StatusText = want.Track == null ? "connected (nothing playing)" : "showing " + want.Track;
                    lock (gate) { if (desired == want) dirty = false; }
                }
                catch (Exception ex)
                {
                    Diag.Once("discord:" + ex.GetType().Name, "Discord update failed: " + ex.Message);
                    Disconnect();
                    nextConnect = DateTime.UtcNow.AddSeconds(20);
                }
            }
        }

        bool Connect(string id)
        {
            for (int i = 0; i < 10; i++)
            {
                NamedPipeClientStream p = null;
                try
                {
                    // Asynchronous mode is required: with a synchronous handle Windows serialises I/O,
                    // so the pending read in ReadLoop would block every write (presence never updates).
                    p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                    p.Connect(300);
                    pipe = p;
                    connectedId = id;
                    ready.Reset();
                    var reader = new Thread(() => ReadLoop(p)) { IsBackground = true, Name = "DiscordRead" };
                    reader.Start();
                    Send(0, "{\"v\":1,\"client_id\":" + J(id) + "}");
                    if (!ready.WaitOne(5000) || pipe != p)
                    {
                        Log.Write("Discord didn't accept the connection (check the Application ID in Settings).");
                        StatusText = "rejected - check the Application ID";
                        Disconnect();
                        return false;
                    }
                    Log.Write("Connected to Discord (pipe " + i + ")");
                    StatusText = "connected";
                    return true;
                }
                catch (TimeoutException) { Drop(p); }
                catch (IOException) { Drop(p); }
                catch (Exception ex) { Drop(p); Diag.Once("discord-connect", "Discord connect error: " + ex.Message); }
            }
            Diag.Once("discord-notrunning", "Discord desktop app not found (it has to be running for Rich Presence).");
            StatusText = "Discord app not found";
            return false;
        }

        void Drop(NamedPipeClientStream p)
        {
            if (p == null) return;
            if (pipe == p) { pipe = null; connectedId = null; }
            try { p.Dispose(); } catch { }
        }

        void Disconnect()
        {
            var p = pipe;
            pipe = null; connectedId = null; sent = null;
            if (p != null) try { p.Dispose(); } catch { }
            lock (gate) { if (desired.Track != null || clientId != "") dirty = true; }
        }

        void Send(int opcode, string json)
        {
            var p = pipe;
            if (p == null) throw new IOException("not connected");
            byte[] body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(opcode).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            lock (writeGate) { p.Write(frame, 0, frame.Length); p.Flush(); }
        }

        // Reads (and mostly discards) Discord's replies so the pipe never fills up; logs errors.
        void ReadLoop(NamedPipeClientStream p)
        {
            var header = new byte[8];
            try
            {
                while (p.IsConnected && !stopping)
                {
                    if (!ReadExact(p, header, 8)) break;
                    int op = BitConverter.ToInt32(header, 0), len = BitConverter.ToInt32(header, 4);
                    if (len < 0 || len > 1 << 20) break;
                    var body = new byte[len];
                    if (!ReadExact(p, body, len)) break;
                    string json = Encoding.UTF8.GetString(body);
                    if (op == 2) { Log.Write("Discord closed the connection: " + json); break; }   // e.g. invalid Application ID
                    if (json.Contains("\"evt\":\"READY\"")) { ready.Set(); continue; }
                    if (op == 3) { try { Send(4, json); } catch { } continue; }                    // ping -> pong
                    if (json.Contains("\"evt\":\"ERROR\"")) { Log.Write("Discord rejected the update: " + json); StatusText = "error - see log"; }
                }
            }
            catch { }
            if (pipe == p) { Disconnect(); wake.Set(); }
        }

        static bool ReadExact(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n) { int r = s.Read(buf, got, n - got); if (r <= 0) return false; got += r; }
            return true;
        }

        // ---- JSON
        static string J(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // Discord wants 2..128 characters
        static string Fit(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length > 128) s = s.Substring(0, 127) + "…";
            while (s.Length < 2) s += "⠀";
            return s;
        }

        static string Nonce() { return Guid.NewGuid().ToString(); }
        static int Pid() { using (var p = Process.GetCurrentProcess()) return p.Id; }

        static string ClearJson()
        {
            return "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + Pid() + "},\"nonce\":" + J(Nonce()) + "}";
        }

        static string ActivityJson(Track t, long startMs, string art)
        {
            var sb = new StringBuilder();
            sb.Append("{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":").Append(Pid()).Append(",\"activity\":{");
            sb.Append("\"type\":2,");   // "Listening to"
            sb.Append("\"details\":").Append(J(Fit(t.Name))).Append(',');
            sb.Append("\"state\":").Append(J(Fit("by " + t.Artist))).Append(',');
            sb.Append("\"timestamps\":{\"start\":").Append(startMs);
            if (t.Duration > 0) sb.Append(",\"end\":").Append(startMs + t.Duration * 1000L);
            sb.Append('}');
            if (art != null)
            {
                sb.Append(",\"assets\":{\"large_image\":").Append(J(art));
                if (!string.IsNullOrEmpty(t.Album)) sb.Append(",\"large_text\":").Append(J(Fit(t.Album)));
                sb.Append('}');
            }
            sb.Append("}},\"nonce\":").Append(J(Nonce())).Append('}');
            return sb.ToString();
        }

        // ---- album art: Last.fm first (track, then album), Apple's catalogue only if Last.fm has none
        string ArtFor(Track t)
        {
            string key = (t.Artist + "|" + t.Album + "|" + t.Name).ToLowerInvariant();
            lock (artCache) { string cached; if (artCache.TryGetValue(key, out cached)) return cached; }

            string url = null;
            string k, sec;
            lock (gate) { k = apiKey; sec = apiSecret; }
            if (k != "")
            {
                string how = "";
                try { url = new LastFm(k, sec).GetAlbumArt(t, out how); }
                catch (Exception ex) { how = "Last.fm lookup failed: " + ex.Message; }
                if (url != null) Log.Verbose("Album art for " + t + ": " + how + " -> " + url);
                else Log.Verbose("No Last.fm cover for " + t + " (artist '" + t.Artist + "', album '" + t.Album + "', album artist '" + t.AlbumArtist + "'): " + how);
            }
            if (url == null)
            {
                url = AppleArt(t);
                if (url != null) Log.Verbose("Album art for " + t + ": Apple catalogue -> " + url);
            }

            lock (artCache) { if (artCache.Count > 500) artCache.Clear(); artCache[key] = url; }
            Covers.Remember(t, url);   // the history window can reuse it
            return url;
        }

        internal static string AppleArt(Track t)
        {
            try
            {
                string term = t.Artist + " " + (t.Album != "" ? t.Album : t.Name);
                string entity = t.Album != "" ? "album" : "song";
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[HttpRequestHeader.UserAgent] = "VsLastfmScrobbler/2.0";
                    string json = wc.DownloadString("https://itunes.apple.com/search?media=music&limit=1&entity=" + entity +
                        "&country=" + RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant() + "&term=" + Uri.EscapeDataString(term));
                    var m = Regex.Match(json, "\"artworkUrl100\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value.Replace("\\/", "/").Replace("100x100bb", "512x512bb");
                }
            }
            catch (Exception ex) { Diag.Once("art", "Apple album art lookup failed: " + ex.Message); }
            return null;
        }
    }
}
