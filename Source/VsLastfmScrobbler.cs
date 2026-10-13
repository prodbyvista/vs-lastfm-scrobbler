// V's Last.fm Scrobbler: Apple Music / iTunes -> Last.fm (tray app) for Windows 10.
// Language level: C# 5 so it builds with the csc.exe that ships with Windows (.NET Framework 4.x).
// Build: run build.bat

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("V's Last.fm Scrobbler")]
[assembly: System.Reflection.AssemblyProduct("V's Last.fm Scrobbler")]
[assembly: System.Reflection.AssemblyDescription("Scrobbles Apple Music and iTunes to Last.fm")]
[assembly: System.Reflection.AssemblyCompany("V")]
[assembly: System.Reflection.AssemblyCopyright("V, 2026")]
[assembly: System.Reflection.AssemblyVersion("4.2.0.0")]

namespace ITunesScrobbler
{
    // ---------------------------------------------------------------- settings
    class Settings
    {
        public string ApiKey = "";
        public string ApiSecret = "";
        public string SessionKey = "";
        public string UserName = "";
        public bool Paused = false;
        public long LastMilestone = -1;       // highest milestone already announced (-1 = not initialised yet)
        public long MilestoneEvery = 5000;
        public bool DiscordEnabled = false;
        public string DiscordClientId = "";
        public bool MilestonesEnabled = true;
        public bool ArtistMilestonesEnabled = true;
        public long ArtistMilestoneEvery = 500;
        public long LastArtistCheck = 0;      // unix time up to which artist milestones were checked
        public bool FriendsEnabled = true;
        public bool WrapsEnabled = true;
        public string LastWrapMonth = "";     // yyyy-MM of the last automatic wrap
        public string LastWrapYear = "";      // yyyy of the last automatic yearly wrap
        public bool VerboseLog = false;       // log every routine event, for troubleshooting
        public bool CheckUpdates = true;      // look for new versions on GitHub
        public string UpdateNotified = "";    // the newest version already announced
        public string LastRunVersion = "";    // the version that ran last (for the "updated to ..." note)

        // Folder layout:  <app>\VsLastfmScrobbler.exe, <app>\Data\ (settings, queue, cache, log), <app>\Wraps\, <app>\Source\
        public static string AppDir { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } }
        public static string Dir { get { return Path.Combine(AppDir, "Data"); } }

        // Called once at startup: create Data\ and move files left in the app folder by older versions.
        public static void PrepareFolders()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                foreach (var name in new[] { "settings.ini", "queue.txt", "durations.txt" })
                {
                    string src = Path.Combine(AppDir, name), dst = Path.Combine(Dir, name);
                    if (File.Exists(src) && !File.Exists(dst)) File.Move(src, dst);
                }
                // the old, very chatty log isn't worth keeping: start a clean one in Data\
                string oldLog = Path.Combine(AppDir, "log.txt");
                if (File.Exists(oldLog)) File.Delete(oldLog);
            }
            catch { }
        }
        static string OldDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iTunesScrobbler"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }
        public static string LogPath { get { return Path.Combine(Dir, "log.txt"); } }

        public bool IsConfigured { get { return ApiKey != "" && ApiSecret != "" && SessionKey != ""; } }

        // Secret and session key are encrypted with Windows DPAPI (only your Windows account can read them).
        static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser));
        }
        static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        public static Settings Load()
        {
            var st = new Settings();
            try
            {
                // one-time move from the old %APPDATA% location
                string old = Path.Combine(OldDir, "settings.ini");
                if (!File.Exists(FilePath) && File.Exists(old))
                {
                    File.Copy(old, FilePath);
                    try { Directory.Delete(OldDir, true); } catch { }
                }
                if (!File.Exists(FilePath)) return st;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    if (k == "ApiKey") st.ApiKey = v;
                    else if (k == "ApiSecret") st.ApiSecret = Unprotect(v);
                    else if (k == "SessionKey") st.SessionKey = Unprotect(v);
                    else if (k == "UserName") st.UserName = v;
                    else if (k == "Paused") st.Paused = v == "1";
                    else if (k == "LastMilestone") long.TryParse(v, out st.LastMilestone);
                    else if (k == "MilestoneEvery") long.TryParse(v, out st.MilestoneEvery);
                    else if (k == "DiscordEnabled") st.DiscordEnabled = v == "1";
                    else if (k == "DiscordClientId") st.DiscordClientId = v;
                    else if (k == "MilestonesEnabled") st.MilestonesEnabled = v == "1";
                    else if (k == "ArtistMilestonesEnabled") st.ArtistMilestonesEnabled = v == "1";
                    else if (k == "ArtistMilestoneEvery") long.TryParse(v, out st.ArtistMilestoneEvery);
                    else if (k == "LastArtistCheck") long.TryParse(v, out st.LastArtistCheck);
                    else if (k == "FriendsEnabled") st.FriendsEnabled = v == "1";
                    else if (k == "WrapsEnabled") st.WrapsEnabled = v == "1";
                    else if (k == "LastWrapMonth") st.LastWrapMonth = v;
                    else if (k == "LastWrapYear") st.LastWrapYear = v;
                    else if (k == "VerboseLog") st.VerboseLog = v == "1";
                    else if (k == "CheckUpdates") st.CheckUpdates = v == "1";
                    else if (k == "UpdateNotified") st.UpdateNotified = v;
                    else if (k == "LastRunVersion") st.LastRunVersion = v;
                }
            }
            catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
            Log.VerboseEnabled = st.VerboseLog;
            return st;
        }

        static readonly object SaveGate = new object();

        public void Save()
        {
            lock (SaveGate)
            File.WriteAllLines(FilePath, new[] {
                "ApiKey=" + ApiKey,
                "ApiSecret=" + Protect(ApiSecret),
                "SessionKey=" + Protect(SessionKey),
                "UserName=" + UserName,
                "Paused=" + (Paused ? "1" : "0"),
                "LastMilestone=" + LastMilestone,
                "MilestoneEvery=" + MilestoneEvery,
                "DiscordEnabled=" + (DiscordEnabled ? "1" : "0"),
                "DiscordClientId=" + DiscordClientId,
                "MilestonesEnabled=" + (MilestonesEnabled ? "1" : "0"),
                "ArtistMilestonesEnabled=" + (ArtistMilestonesEnabled ? "1" : "0"),
                "ArtistMilestoneEvery=" + ArtistMilestoneEvery,
                "LastArtistCheck=" + LastArtistCheck,
                "FriendsEnabled=" + (FriendsEnabled ? "1" : "0"),
                "WrapsEnabled=" + (WrapsEnabled ? "1" : "0"),
                "LastWrapMonth=" + LastWrapMonth,
                "LastWrapYear=" + LastWrapYear,
                "VerboseLog=" + (VerboseLog ? "1" : "0"),
                "CheckUpdates=" + (CheckUpdates ? "1" : "0"),
                "UpdateNotified=" + UpdateNotified,
                "LastRunVersion=" + LastRunVersion
            });
        }
    }

    static class Log
    {
        static readonly object Gate = new object();
        public static bool VerboseEnabled;   // VerboseLog=1 in Data\settings.ini

        // routine events (each scrobble, now playing, album art...): only logged when VerboseLog=1
        public static void Verbose(string msg) { if (VerboseEnabled) Write("\u00b7 " + msg); }

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Settings.LogPath));
                    var fi = new FileInfo(Settings.LogPath);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(Settings.LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- last.fm API
    class LastFmException : Exception
    {
        public int Code;
        public LastFmException(int code, string msg) : base(msg) { Code = code; }
        // Codes that mean "try again later" rather than "this request is bad"
        public bool Transient { get { return Code == 0 || Code == 11 || Code == 16 || Code == 29; } }
        public bool BadSession { get { return Code == 9 || Code == 4 || Code == 14; } }
    }

    partial class LastFm
    {
        const string Endpoint = "https://ws.audioscrobbler.com/2.0/";
        readonly string key, secret;
        public LastFm(string apiKey, string apiSecret) { key = apiKey; secret = apiSecret; }

        string Sign(SortedDictionary<string, string> p)
        {
            var sb = new StringBuilder();
            foreach (var kv in p) sb.Append(kv.Key).Append(kv.Value);   // ordinal key order
            sb.Append(secret);
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return string.Concat(hash.Select(b => b.ToString("x2")));
            }
        }

        public XmlDocument Call(string method, Dictionary<string, string> args, bool post)
        {
            var p = new SortedDictionary<string, string>(StringComparer.Ordinal);
            p["method"] = method;
            p["api_key"] = key;
            foreach (var kv in args) if (!string.IsNullOrEmpty(kv.Value)) p[kv.Key] = kv.Value;
            p["api_sig"] = Sign(p);

            string body = string.Join("&", p.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
            string text;
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[HttpRequestHeader.UserAgent] = "VsLastfmScrobbler/1.0 (Windows)";
                    if (post)
                    {
                        wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded; charset=utf-8";
                        text = wc.UploadString(Endpoint, "POST", body);
                    }
                    else text = wc.DownloadString(Endpoint + "?" + body);
                }
            }
            catch (WebException ex)
            {
                // Last.fm returns API errors with HTTP 4xx and an XML body
                if (ex.Response != null)
                {
                    try
                    {
                        using (var r = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd();
                    }
                    catch { throw new LastFmException(0, ex.Message); }
                }
                else throw new LastFmException(0, ex.Message);
            }

            var doc = new XmlDocument();
            try { doc.LoadXml(text); } catch { throw new LastFmException(0, "Unreadable response from Last.fm"); }
            var lfm = doc.DocumentElement;
            if (lfm == null || lfm.GetAttribute("status") != "ok")
            {
                var err = doc.SelectSingleNode("/lfm/error") as XmlElement;
                int code = 0;
                if (err != null) int.TryParse(err.GetAttribute("code"), out code);
                throw new LastFmException(code, err != null ? err.InnerText.Trim() : "Unknown Last.fm error");
            }
            return doc;
        }

        public string GetToken()
        {
            return Call("auth.getToken", new Dictionary<string, string>(), false).SelectSingleNode("/lfm/token").InnerText;
        }

        public void GetSession(string token, out string sessionKey, out string user)
        {
            var d = Call("auth.getSession", new Dictionary<string, string> { { "token", token } }, false);
            sessionKey = d.SelectSingleNode("/lfm/session/key").InnerText;
            user = d.SelectSingleNode("/lfm/session/name").InnerText;
        }

        public string AuthUrl(string token)
        {
            return "https://www.last.fm/api/auth/?api_key=" + key + "&token=" + token;
        }

        static Dictionary<string, string> TrackArgs(Track t, string sk)
        {
            t.Normalize();
            var a = new Dictionary<string, string>();
            a["artist"] = t.Artist;
            a["track"] = t.Name;
            a["album"] = t.Album;
            if (!string.IsNullOrEmpty(t.AlbumArtist) && t.AlbumArtist != t.Artist) a["albumArtist"] = t.AlbumArtist;
            if (t.Duration > 0) a["duration"] = t.Duration.ToString();
            if (t.TrackNumber > 0) a["trackNumber"] = t.TrackNumber.ToString();
            a["sk"] = sk;
            return a;
        }

        public void UpdateNowPlaying(Track t, string sk)
        {
            Call("track.updateNowPlaying", TrackArgs(t, sk), true);
        }

        // returns the items Last.fm accepted but ignored: index -> reason
        public Dictionary<int, string> ScrobbleBatch(List<Pending> items, string sk)
        {
            var a = new Dictionary<string, string>();
            for (int i = 0; i < items.Count; i++)
            {
                var t = items[i].Track;
                t.Normalize();
                string n = "[" + i + "]";
                a["artist" + n] = t.Artist;
                a["track" + n] = t.Name;
                a["timestamp" + n] = items[i].Timestamp.ToString();
                a["album" + n] = t.Album;
                if (!string.IsNullOrEmpty(t.AlbumArtist) && t.AlbumArtist != t.Artist) a["albumArtist" + n] = t.AlbumArtist;
                if (t.Duration > 0) a["duration" + n] = t.Duration.ToString();
                if (t.TrackNumber > 0) a["trackNumber" + n] = t.TrackNumber.ToString();
            }
            a["sk"] = sk;
            var d = Call("track.scrobble", a, true);
            var ignored = new Dictionary<int, string>();
            var nodes = d.SelectNodes("/lfm/scrobbles/scrobble");
            for (int i = 0; i < nodes.Count; i++)
            {
                var ign = nodes[i].SelectSingleNode("ignoredMessage") as XmlElement;
                if (ign == null || ign.GetAttribute("code") == "0" || ign.GetAttribute("code") == "") continue;
                string code = ign.GetAttribute("code");
                string why = code == "1" ? "artist ignored by last.fm" : code == "2" ? "track ignored by last.fm" : code == "3" ? "too old"
                           : code == "4" ? "timestamp in the future" : code == "5" ? "daily scrobble limit reached" : "ignored (code " + code + ")";
                ignored[i] = why;
                Log.Write("Last.fm refused a scrobble (" + why + "): " + nodes[i].SelectSingleNode("artist").InnerText + " - " + nodes[i].SelectSingleNode("track").InnerText);
            }
            return ignored;
        }

        // Album cover URL from Last.fm (track.getInfo, then album.getInfo); null if Last.fm has none.
        // 'how' says where it came from, or why nothing was found (for the log).
        public string GetAlbumArt(Track t, out string how)
        {
            string url = null;
            how = "";
            try
            {
                var d = Call("track.getInfo", new Dictionary<string, string> {
                    { "artist", t.Artist }, { "track", t.Name }, { "autocorrect", "1" } }, false);
                url = BestImage(d.SelectNodes("/lfm/track/album/image"));
                if (url != null) { how = "Last.fm track"; return url; }
                how = "track has no album on Last.fm";
            }
            catch (LastFmException ex) { if (ex.Transient) throw; how = "track not on Last.fm (" + ex.Message + ")"; }

            if (t.Album == "") return null;
            var artists = new List<string>();
            if (!string.IsNullOrEmpty(t.AlbumArtist)) artists.Add(t.AlbumArtist);
            if (!artists.Contains(t.Artist)) artists.Add(t.Artist);
            foreach (var artist in artists)
            {
                try
                {
                    var d = Call("album.getInfo", new Dictionary<string, string> {
                        { "artist", artist }, { "album", t.Album }, { "autocorrect", "1" } }, false);
                    url = BestImage(d.SelectNodes("/lfm/album/image"));
                    if (url != null) { how = "Last.fm album \"" + artist + " - " + t.Album + "\""; return url; }
                    how += "; album \"" + artist + " - " + t.Album + "\" has no cover";
                }
                catch (LastFmException ex)
                {
                    if (ex.Transient) throw;
                    how += "; album \"" + artist + " - " + t.Album + "\" not found (" + ex.Message + ")";
                }
            }
            return null;
        }

        static string BestImage(XmlNodeList images)
        {
            string best = null;
            foreach (XmlElement img in images)
            {
                string u = img.InnerText.Trim();
                // skip empty entries and Last.fm's grey star placeholder
                if (u == "" || u.Contains("2a96cbd8b46e442fc41c2b86b821562f")) continue;
                best = u;   // sizes are listed small -> large, so the last one wins
            }
            return best;
        }

        public long GetPlayCount(string user)
        {
            var d = Call("user.getInfo", new Dictionary<string, string> { { "user", user } }, false);
            return long.Parse(d.SelectSingleNode("/lfm/user/playcount").InnerText.Trim());
        }

        // "Artist - Title" of the user's n-th scrobble ever (1 = first).
        public string GetScrobbleNumber(string user, long n, out string image)
        {
            long total;
            string r = RecentAt(user, 1, out total, out image);   // just to learn the exact total
            image = null;
            if (total < n) return null;
            r = RecentAt(user, total - n + 1, out total, out image);
            return r;
        }

        string RecentAt(string user, long page, out long total, out string image)
        {
            image = null;
            var d = Call("user.getRecentTracks", new Dictionary<string, string> {
                { "user", user }, { "limit", "1" }, { "page", page.ToString() } }, false);
            var rt = (XmlElement)d.SelectSingleNode("/lfm/recenttracks");
            total = long.Parse(rt.GetAttribute("total"));
            var tr = d.SelectSingleNode("/lfm/recenttracks/track[not(@nowplaying='true')]");
            if (tr == null) return null;
            var img = tr.SelectSingleNode("image[@size='extralarge']");
            if (img != null && img.InnerText.Trim() != "" && !img.InnerText.Contains("2a96cbd8b46e442fc41c2b86b821562f")) image = img.InnerText.Trim();
            return tr.SelectSingleNode("artist").InnerText + " – " + tr.SelectSingleNode("name").InnerText;
        }
    }

    // ---------------------------------------------------------------- iTunes
    class Track
    {
        public string Artist = "", Name = "", Album = "", AlbumArtist = "";
        public int Duration, TrackNumber;
        public string Id = "";
        public override string ToString() { return Artist + " - " + Name; }

        // Apple Music sometimes packs "Artist — Album" into the artist (and album artist) field,
        // and switches between that and the separate fields mid-song. Always split it apart.
        const string Sep = " \u2014 ";   // " — "
        public void Normalize()
        {
            Artist = (Artist ?? "").Trim(); Name = (Name ?? "").Trim();
            Album = (Album ?? "").Trim(); AlbumArtist = (AlbumArtist ?? "").Trim();

            int i = Artist.IndexOf(Sep, StringComparison.Ordinal);
            if (i > 0)
            {
                string rest = Artist.Substring(i + Sep.Length).Trim();
                Artist = Artist.Substring(0, i).Trim();
                if (Album == "") Album = rest;
            }
            i = AlbumArtist.IndexOf(Sep, StringComparison.Ordinal);
            if (i > 0) AlbumArtist = AlbumArtist.Substring(0, i).Trim();
            if (AlbumArtist == Album || AlbumArtist == Artist) AlbumArtist = "";
        }
    }

    class ITunesReader
    {
        public const int StateStopped = 0, StatePlaying = 1;

        // Which method produced the last successful read ("iTunes", "Windows media controls")
        public static string Source = "";

        static bool ITunesProcessRunning()
        {
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.ProcessName.Equals("iTunes", StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        // Returns false when no supported player is running / reachable.
        public static bool TryRead(out Track track, out int state, out int position)
        {
            track = null; state = StateStopped; position = 0;
            if (ITunesProcessRunning() && TryReadCom(out track, out state, out position)) { Source = "iTunes"; return true; }
#if SMTC
            if (SmtcReader.TryRead(out track, out state, out position)) { Source = "Windows media controls"; return true; }
#endif
            Source = "";
            return false;
        }

        // Reads the current state without keeping iTunes' COM object alive between polls.
        // Holding it would make iTunes show "applications are using the scripting interface" on quit.
        static bool TryReadCom(out Track track, out int state, out int position)
        {
            track = null; state = StateStopped; position = 0;
            object app = null, cur = null;
            try
            {
                var type = Type.GetTypeFromProgID("iTunes.Application");
                if (type == null) { Diag.Once("com-missing", "iTunes COM interface (iTunes.Application) is not registered on this PC."); return false; }
                app = Activator.CreateInstance(type);
                dynamic it = app;
                state = (int)it.PlayerState;
                cur = it.CurrentTrack;
                if (cur == null) return true;
                dynamic t = cur;
                track = new Track();
                track.Artist = S(t.Artist);
                track.Name = S(t.Name);
                track.Album = S(t.Album);
                try { track.AlbumArtist = S(t.AlbumArtist); } catch { }   // only on file/CD tracks
                track.Duration = (int)t.Duration;
                track.TrackNumber = (int)t.TrackNumber;
                try { track.Id = ((int)t.TrackDatabaseID).ToString(); } catch { track.Id = track.ToString() + "|" + track.Duration; }
                position = (int)it.PlayerPosition;
                return true;
            }
            catch (Exception ex)
            {
                // iTunes busy (modal dialog open, starting up, quitting) -> skip this poll
                Diag.Once("com-error:" + ex.GetType().Name, "iTunes COM read failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            finally
            {
                if (cur != null) try { Marshal.FinalReleaseComObject(cur); } catch { }
                if (app != null) try { Marshal.FinalReleaseComObject(app); } catch { }
            }
        }

        static string S(object o) { return o == null ? "" : o.ToString().Trim(); }
    }

    static class Diag
    {
        static readonly HashSet<string> seen = new HashSet<string>();
        public static void Once(string key, string msg)
        {
            lock (seen) { if (!seen.Add(key)) return; }
            Log.Write(msg);
        }

        public static void WriteReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== Diagnostics (v" + Program.Version + ") ====");
            sb.AppendLine("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " 64-bit" : " 32-bit")
                + ", app is " + (Environment.Is64BitProcess ? "64-bit" : "32-bit"));
            sb.AppendLine("App folder: " + Settings.AppDir + "  (data: " + Settings.Dir + ")");
            sb.AppendLine("Discord Rich Presence: " + DiscordPresence.StatusText);
            sb.AppendLine("iTunes COM registered: " + (Type.GetTypeFromProgID("iTunes.Application") != null ? "yes" : "no"));
            sb.AppendLine("Music-related processes:");
            int n = 0;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string name = p.ProcessName, low = name.ToLowerInvariant();
                    if (low.Contains("tunes") || low.Contains("music") || low.Contains("apple"))
                    {
                        string path = "";
                        try { path = p.MainModule.FileName; } catch { path = "(path not accessible)"; }
                        sb.AppendLine("   " + name + "  [" + path + "]");
                        n++;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (n == 0) sb.AppendLine("   (none found)");
#if SMTC
            sb.AppendLine("Windows media sessions:");
            sb.Append(SmtcReader.Describe());
#else
            sb.AppendLine("Windows media controls support: not compiled in (see build output).");
#endif
            Track t; int state, pos;
            bool ok = ITunesReader.TryRead(out t, out state, out pos);
            sb.AppendLine("Read result: " + (ok ? ("via " + ITunesReader.Source + ", state=" + state + ", pos=" + pos + ", track=" + (t == null ? "(none)" : t + " | " + t.Album + " | " + t.Duration + "s")) : "nothing readable"));
            sb.Append("=====================");
            Log.Write(sb.ToString());
        }
    }

    // ---------------------------------------------------------------- scrobble logic
    // Where the current song is, for the hover popup's progress line (a snapshot, replaced every poll)
    class PlaybackInfo
    {
        public string TrackId;
        public double Pos;            // seconds into the song when the poll ran
        public DateTime At;           // when that was (UTC)
        public int Length;            // song length in seconds (0 = unknown)
        public double ScrobbleAt = -1;   // where in the song it gets scrobbled (-1 = it won't)
        public bool Scrobbled;
        public string Note = "";      // instead of a scrobble mark: "scrobbling paused" etc.
    }

    class Engine
    {
        readonly Func<Settings> settings;
        readonly Action<string> status;
        readonly Action<string> balloon;
        public readonly ScrobbleQueue Queue = new ScrobbleQueue();
        public readonly Milestones Milestones;
        public readonly DiscordPresence Discord = new DiscordPresence();
        public readonly FriendWatch Friends;
        public readonly MonthlyWrap Wraps;
        Track playingNow;                       // what's playing right now (null when paused/stopped)
        public Track NowPlaying { get { return playingNow; } }
        public PlaybackInfo Progress;           // null when nothing plays
        double scrobbleAt = -1;

        Track current;
        long startedUnix;
        double playedSeconds;
        bool scrobbled, nowPlayingSent;
        int lastPos = -1;
        DateTime lastTick = DateTime.UtcNow;
        bool sessionWarned;
        bool notFoundLogged;

        public Engine(Func<Settings> s, Action<string> statusCb, Action<string> balloonCb, Action<Toast> notifyCb)
        {
            settings = s; status = statusCb; balloon = balloonCb;
            Milestones = new Milestones(s, notifyCb);
            Friends = new FriendWatch(s, () => playingNow, notifyCb);
            Wraps = new MonthlyWrap(s, notifyCb);
            Queue.SessionProblem = SessionProblem;
            Queue.Sent = n => Milestones.Soon();
        }

        public static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        // Last.fm rule: track longer than 30 s, played for half its length or 4 minutes, whichever comes first.
        static double Threshold(Track t)
        {
            if (t.Duration <= 0) return 60.0;            // length unknown (some media-control sources): scrobble after 1 min
            if (t.Duration <= 30) return double.MaxValue;
            return Math.Min(t.Duration / 2.0, 240.0);
        }

        string Queued()
        {
            int n = Queue.Count;
            return n > 0 ? " (" + n + " queued)" : "";
        }

        public void Tick()
        {
            var st = settings();
            try { PlaybackTick(st); }
            finally
            {
                Queue.Tick(st);
                Milestones.Tick();
                Friends.Tick();
                Wraps.Tick();
            }
        }

        void PlaybackTick(Settings st)
        {
            var nowUtc = DateTime.UtcNow;
            double dt = (nowUtc - lastTick).TotalSeconds;
            lastTick = nowUtc;
            if (dt > 15) dt = 15;   // PC slept, etc.

            Track t; int state, pos;
            bool ok = ITunesReader.TryRead(out t, out state, out pos);

            if (!ok || t == null || state != ITunesReader.StatePlaying)
            {
                playingNow = null;
                Progress = null;
                Discord.Update(st, null, false, 0);
                if (!ok)
                {
                    if (!notFoundLogged) { notFoundLogged = true; Diag.WriteReport(); }   // explain why, once
                    status("No player found (iTunes / Apple Music)" + Queued()); ResetCurrent();
                }
                else status((current != null ? "Paused: " + current : "Not playing") + Queued());
                return;
            }

            notFoundLogged = false;
            if (t.Artist == "" || t.Name == "") { status("Playing (no artist/title tag)"); Discord.Update(st, null, false, 0); return; }

            bool newTrack = current == null || current.Id != t.Id;
            // Same track restarted (repeat-one or jumped back to start after it had been playing)
            bool restarted = !newTrack && lastPos > 10 && pos < 5 && pos < lastPos - 10;

            if (newTrack || restarted)
            {
                current = t;
                startedUnix = Now() - Math.Max(0, pos);
                playedSeconds = 0;
                scrobbled = false;
                nowPlayingSent = false;
                scrobbleAt = -1;
            }
            else
            {
                playedSeconds += dt;
            }
            lastPos = pos;

            Discord.Update(st, current, true, pos);
            playingNow = current;

            string prefix = st.Paused ? "Scrobbling paused — " : "";
            status(prefix + "Playing: " + current + Queued());
            Diag.Once("src:" + ITunesReader.Source, "Reading playback via " + ITunesReader.Source);

            if (st.Paused || !st.IsConfigured) { SetProgress(st, pos); return; }

            if (!nowPlayingSent)
            {
                nowPlayingSent = true;
                var snapshot = current;
                var api = new LastFm(st.ApiKey, st.ApiSecret);
                string sk = st.SessionKey;
                ThreadPool.QueueUserWorkItem(_ => Safe(() => api.UpdateNowPlaying(snapshot, sk), "Now playing: " + snapshot));
            }

            if (!scrobbled && playedSeconds >= Threshold(current))
            {
                scrobbled = true;
                Queue.Add(new Pending { Track = current, Timestamp = startedUnix });   // saved to disk first, then sent
                if (current.Duration > 0) Durations.Set(current.Artist, current.Name, current.Duration);
            }
            SetProgress(st, pos);
        }

        // the popup's progress line: where we are, and where in the song the scrobble happens
        // (Last.fm counts time listened, so after a skip the mark moves with it)
        void SetProgress(Settings st, int pos)
        {
            var p = new PlaybackInfo { TrackId = current.Id, Pos = pos, At = DateTime.UtcNow, Length = current.Duration, Scrobbled = scrobbled };
            double need = Threshold(current);
            if (st.Paused) p.Note = "scrobbling paused";
            else if (!st.IsConfigured) p.Note = "not connected to last.fm";
            else if (need == double.MaxValue) p.Note = "too short to scrobble";
            else
            {
                if (!scrobbled || scrobbleAt < 0) scrobbleAt = scrobbled ? pos : pos + Math.Max(0, need - playedSeconds);
                p.ScrobbleAt = scrobbleAt;
            }
            Progress = p;
        }

        void ResetCurrent() { current = null; lastPos = -1; }

        public void SessionOk() { sessionWarned = false; }
        public bool SessionBad { get { return sessionWarned; } }

        void SessionProblem()
        {
            if (sessionWarned) return;
            sessionWarned = true;
            balloon("Last.fm rejected the login. Open Settings and click 'Connect to Last.fm' again. Scrobbles are kept until then.");
        }

        void Safe(Action a, string what)
        {
            try { a(); Log.Verbose(what); }
            catch (LastFmException ex)
            {
                Log.Write(what + " failed: " + ex.Code + " " + ex.Message);
                if (ex.BadSession) SessionProblem();
            }
            catch (Exception ex) { Log.Write(what + " error: " + ex.Message); }
        }
    }

    // ---------------------------------------------------------------- settings window
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "VsLastfmScrobbler";
        static readonly string[] OldNames = { "iTunesScrobbler", "AppleMusicScrobbler" };
        public static bool IsEnabled
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Name) != null; }
        }
        // Called at startup: replace autostart entries left by older versions (iTunesScrobbler / VsLastfmScrobbler)
        public static void MigrateOldEntry()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    bool had = false;
                    foreach (var o in OldNames)
                        if (k.GetValue(o) != null) { k.DeleteValue(o); had = true; }
                    string want = "\"" + Application.ExecutablePath + "\"";
                    var cur = k.GetValue(Name) as string;
                    if (had || (cur != null && !string.Equals(cur, want, StringComparison.OrdinalIgnoreCase))) k.SetValue(Name, want);
                }
            }
            catch { }
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else if (k.GetValue(Name) != null) k.DeleteValue(Name);
            }
        }
    }

    // ---------------------------------------------------------------- tray app
    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem statusItem = new ToolStripMenuItem("    starting\u2026") { Enabled = false };
        readonly ToolStripMenuItem pauseItem = new ToolStripMenuItem();
        readonly DarkMenuRenderer menuRenderer = new DarkMenuRenderer();
        readonly System.Windows.Forms.Timer marquee = new System.Windows.Forms.Timer { Interval = 30 };
        int marqueeHold;                 // ticks to wait before (re)starting the scroll
        ContextMenuStrip menu;
        ToolStripMenuItem lastYearItem;
        readonly ToolStripMenuItem updateItem = new ToolStripMenuItem("  update") { Tag = "update", Visible = false };
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 15 * 1000 };   // one look, shortly after start (network up)
        volatile bool updating;
        TrayHoverCard hover;

        static ToolStripMenuItem Item(ContextMenuStrip menu, string text, Action click)
        {
            var it = new ToolStripMenuItem("  " + text);
            it.Click += (s, e) => click();
            menu.Items.Add(it);
            return it;
        }

        // "──[ WRAPS ]────": a section title inside the menu (not clickable)
        static void Head(ContextMenuStrip menu, string title)
        {
            menu.Items.Add(new ToolStripMenuItem("\u2500\u2500[ " + title.ToUpperInvariant() + " ]" + new string('\u2500', Math.Max(4, 26 - title.Length))) { Enabled = false, Tag = "head" });
        }

        void UpdatePauseText() { pauseItem.Text = (st.Paused ? "  [x]" : "  [ ]") + " pause scrobbling"; }
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Engine engine;
        Settings st;
        Icon icon;

        readonly Control invoker = new Control();

        public TrayApp()
        {
            var h = invoker.Handle;   // create handle on the UI thread so BeginInvoke works
            st = Settings.Load();
            icon = LoadIcon();
            Covers.Init(() => st);
            engine = new Engine(() => st, SetStatus, Balloon, Notify);

            // dark, terminal-style menu (see DarkMenuRenderer); all labels start with two spaces for the hover ">"
            menu = new ContextMenuStrip { Renderer = menuRenderer, ShowImageMargin = false, ShowCheckMargin = false, Font = Theme.Mono(9.75f), Padding = new Padding(0, 4, 0, 4) };
            statusItem.Tag = "status";
            menuRenderer.StatusLine = "starting…";
            menu.Items.Add(statusItem);
            updateItem.Click += (s, e) => InstallUpdate();
            menu.Items.Add(updateItem);   // only shows when a new version is out
            menu.Items.Add(new ToolStripSeparator());
            UpdatePauseText();
            pauseItem.Click += (s, e) => { st.Paused = !st.Paused; UpdatePauseText(); st.Save(); };
            menu.Items.Add(pauseItem);
            Item(menu, "open my last.fm profile", () =>
                SettingsForm.OpenUrl(st.UserName != "" ? "https://www.last.fm/user/" + Uri.EscapeDataString(st.UserName) : "https://www.last.fm"));
            Head(menu, "wraps");
            Item(menu, "make a wrap of this month so far", () =>
                StartWrap("Monthly wrap", "Making it now. This can take a few minutes the first time.", () => engine.Wraps.MakeNow()));
            Item(menu, "make a wrap of this year so far", () =>
                StartWrap("Yearly wrap", "Making it now. A whole year takes a while the first time: every song's length is looked up once.", () => engine.Wraps.MakeYearSoFar()));
            lastYearItem = Item(menu, "make a wrap of last year", () =>
            {
                int year = DateTime.Now.Year - 1;
                StartWrap("Yearly wrap", "Making your " + year + " wrap now. A whole year takes a while the first time.", () => engine.Wraps.MakeYear(year));
            });
            Item(menu, "open wraps shelf", () =>
            {
                try { Process.Start(WrapsShelf.Build()); }
                catch (Exception ex) { Log.Write("Couldn't open the wraps shelf: " + ex.Message); }
            });
            Head(menu, "app");
            Item(menu, "open folder", () => { try { Process.Start("explorer.exe", "\"" + Settings.AppDir + "\""); } catch { } });
            Item(menu, "friends\u2026", () => ShowTab("friends"));
            Item(menu, "stats\u2026", () => ShowTab("stats"));
            Item(menu, "scrobble history\u2026", () => ShowTab("history"));
            Item(menu, "log\u2026", () => ShowTab("log"));
            Item(menu, "settings\u2026", () => ShowTab("settings"));
            Item(menu, "about\u2026", () => ShowTab("about"));
            menu.Items.Add(new ToolStripSeparator());
            Item(menu, "exit", ExitThread).Tag = "danger";

            // the status line scrolls while the menu is open (only if it's too long to fit)
            menu.Opening += (s, e) => { hover.HideCard(); UpdateWrapItems(); FitStatusWidth(); };
            menu.Opened += (s, e) => { menuRenderer.StatusScroll = 0; marqueeHold = 45; marquee.Start(); };
            menu.Closed += (s, e) => marquee.Stop();
            marquee.Tick += (s, e) => ScrollStatus();
            FitStatusWidth();

            ToastSender.Init(icon);
            tray.Icon = icon;
            tray.Text = "";   // no grey Windows tooltip: the themed hover card shows instead
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            hover = new TrayHoverCard(tray) { Suppressed = () => menu.Visible, Progress = () => engine != null ? engine.Progress : null };
            tray.DoubleClick += (s, e) => ShowTab("settings");
            tray.BalloonTipClicked += (s, e) => { if (!string.IsNullOrEmpty(clickUrl)) SettingsForm.OpenUrl(clickUrl); };
            noteTimer.Tick += (s, e) => ShowNextNote();

            // GitHub is checked once when the app starts; Settings has a button to check again
            updateTimer.Tick += (s, e) => { updateTimer.Stop(); CheckForUpdate(); };
            updateTimer.Start();
            Updater.Changed = () => { try { invoker.BeginInvoke((Action)UpdateFound); } catch { } };
            Updater.InstallRequested = () => { try { invoker.BeginInvoke((Action)InstallUpdate); } catch { } };

            timer.Interval = 5000;
            timer.Tick += (s, e) => { try { engine.Tick(); } catch (Exception ex) { Log.Write("Tick error: " + ex); } };
            timer.Start();

            Log.Write("Started v" + Program.Version + " (folder: " + Settings.AppDir + ")");
            if (!st.IsConfigured) ShowTab("settings");
            AnnounceNewVersion();
        }

        // First start of a new version: one notification with the patch notes (not on a fresh install)
        void AnnounceNewVersion()
        {
            if (st.LastRunVersion == Program.Version) return;
            bool updated = st.LastRunVersion != "" || st.IsConfigured;   // 4.1 and older didn't save this yet
            st.LastRunVersion = Program.Version; st.Save();
            if (!updated) return;
            string page = Updater.RepoUrl + "/releases/tag/" + Program.Version;
            Notify(new Toast
            {
                Title = "Updated to v" + Program.Version,
                Body = PatchNotes.Summary + " All notes: tray menu > about.",
                Url = page, Attribution = "What's new"
            }.Button("Release notes", page));
        }

        // ---- updates from GitHub (see Updater.cs)
        void CheckForUpdate()
        {
            if (!st.CheckUpdates || updating) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Updater.Check(); }
                catch (Exception ex) { Diag.Once("update-check:" + ex.Message, "Update check didn't work (will try again later): " + ex.Message); return; }
                try { invoker.BeginInvoke((Action)UpdateFound); } catch { }
            });
        }

        void UpdateFound()
        {
            updateItem.Visible = Updater.Available;
            if (!Updater.Available) return;
            string v = Updater.LatestVersion;
            updateItem.Text = "  update to v" + v + " \u2193";
            if (st.UpdateNotified == v) return;   // announce each version once
            st.UpdateNotified = v; st.Save();
            Log.Write("Update available: v" + v);
            Notify(new Toast
            {
                Title = "V's Last.fm Scrobbler " + v + " is out",
                Body = "Right-click the tray icon > \"update to v" + v + "\" to install it. It takes about a minute.",
                Url = Updater.LatestPage, Attribution = "Update"
            }.Button("What's new", Updater.LatestPage));
        }

        void InstallUpdate()
        {
            if (updating || !Updater.Available) return;
            updating = true;
            string v = Updater.LatestVersion;
            Notify(new Toast { Title = "Updating to v" + v, Body = "Downloading it now. The app closes, builds the new version and starts again by itself." });
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Updater.Install();
                    invoker.BeginInvoke((Action)ExitThread);   // build.bat takes it from here
                }
                catch (Exception ex)
                {
                    updating = false;
                    Log.Write("Update failed: " + ex.Message);
                    Notify(new Toast { Title = "The update didn't work", Body = ex.Message + ". You can download it from GitHub instead.", Url = Updater.LatestPage }
                        .Button("Open GitHub", Updater.LatestPage));
                }
            });
        }

        void StartWrap(string title, string body, Action make)
        {
            if (engine.Wraps.Busy) { Notify(new Toast { Title = title, Body = "A wrap is already being made, hang on." }); return; }
            if (!st.IsConfigured || st.UserName == "") { Notify(new Toast { Title = title, Body = "Connect to Last.fm in Settings first." }); return; }
            Notify(new Toast { Title = title, Body = body });
            make();
        }

        // "make a wrap of 2025" only shows while that year has no finished wrap yet
        void UpdateWrapItems()
        {
            int year = DateTime.Now.Year - 1;
            lastYearItem.Text = "  make a wrap of " + year;
            lastYearItem.Visible = !File.Exists(Path.Combine(MonthlyWrap.Folder, year + ".html"));
        }

        // The menu sizes itself from the items' text. The status item gets the widest other item's text
        // (never shown, the renderer draws the real status), so a long song title can't stretch the menu.
        void FitStatusWidth()
        {
            string widest = ""; int widestW = 0;
            foreach (ToolStripItem it in menu.Items)
            {
                if (it == statusItem || it is ToolStripSeparator) continue;
                int w = TextRenderer.MeasureText(it.Text ?? "", menu.Font).Width;
                if (w > widestW) { widestW = w; widest = it.Text; }
            }
            statusItem.Text = widest;
        }

        void ScrollStatus()
        {
            if (!menuRenderer.StatusOverflows || menuRenderer.StatusCycle <= 0)
            {
                if (menuRenderer.StatusScroll != 0) { menuRenderer.StatusScroll = 0; statusItem.Invalidate(); }
                return;
            }
            if (marqueeHold > 0) { marqueeHold--; return; }
            menuRenderer.StatusScroll++;
            if (menuRenderer.StatusScroll >= menuRenderer.StatusCycle) { menuRenderer.StatusScroll = 0; marqueeHold = 60; }   // a pause at the start of each loop
            statusItem.Invalidate();
        }

        static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
            catch { return SystemIcons.Application; }
        }

        // ---- the app window: friends, history, log, settings and about are tabs in one window
        AppWindow appWin;

        void ShowTab(string name)
        {
            if (appWin != null)
            {
                appWin.ShowTab(name);
                if (appWin.WindowState == FormWindowState.Minimized) appWin.WindowState = FormWindowState.Normal;
                appWin.Activate();
                return;
            }
            var pages = new Dictionary<string, Func<AsciiForm>>
            {
                { "friends", () => new FriendsForm(st, icon) },
                { "stats", () => new StatsForm(st, icon) },
                { "history", () => new HistoryForm(st, icon, h => engine.Queue.Add(new Pending
                    {
                        Timestamp = h.Timestamp,
                        Track = new Track { Artist = h.Artist, Name = h.Track, Album = h.Album, Duration = h.Duration }
                    })) },
                { "log", () => new LogForm(st, icon) },
                { "settings", () => new SettingsForm(st, icon) },
                { "about", () => new AboutForm(st, icon) },
            };
            appWin = new AppWindow(icon, pages);
            // settings may have changed (saved on the settings tab, or the log's verbose switch)
            appWin.FormClosed += (s, e) => { appWin = null; st = Settings.Load(); UpdatePauseText(); engine.SessionOk(); };
            appWin.ShowTab(name);
            appWin.Show();
        }

        TrayState shown = (TrayState)(-1);

        void SetStatus(string text)
        {
            // what the scrobbler is doing decides the icon badge and the tooltip
            TrayState state;
            int queued = engine != null ? engine.Queue.Count : 0;
            if (!st.IsConfigured || (engine != null && engine.SessionBad)) { state = TrayState.NotLoggedIn; text = "Not connected · open Settings"; }
            else if (st.Paused) { state = TrayState.Paused; text = "Scrobbling paused"; }
            else if (engine != null && engine.Queue.Offline && queued > 0) { state = TrayState.Offline; text = "Offline · " + queued + (queued == 1 ? " scrobble" : " scrobbles") + " queued"; }
            else if (engine != null && engine.NowPlaying != null) state = TrayState.Playing;
            else state = TrayState.Idle;

            var dot = state == TrayState.Playing ? Color.FromArgb(35, 197, 82) : state == TrayState.Paused ? Color.FromArgb(154, 160, 166)
                : state == TrayState.Offline ? Color.FromArgb(255, 176, 32) : state == TrayState.NotLoggedIn ? Color.FromArgb(229, 55, 43) : Color.Empty;
            if (state != shown)
            {
                shown = state; tray.Icon = TrayIcons.For(state, icon);
                menuRenderer.StatusDot = dot;
                statusItem.Invalidate();
            }
            // full text: the menu scrolls it when it's too long, the hover card wraps it
            string line = text.ToLowerInvariant();
            if (line != menuRenderer.StatusLine)
            {
                menuRenderer.StatusLine = line;
                menuRenderer.StatusScroll = 0; marqueeHold = 45;
                statusItem.AccessibleName = line;
                statusItem.Invalidate();
            }
            if (hover != null) hover.SetStatus(line, dot, engine != null ? engine.NowPlaying : null, state == TrayState.Playing);
        }

        void Balloon(string msg)
        {
            // may come from a worker thread
            if (invoker.InvokeRequired) { invoker.BeginInvoke((Action)(() => Balloon(msg))); return; }
            try { tray.ShowBalloonTip(8000, "V's Last.fm Scrobbler", msg, ToolTipIcon.Warning); } catch { }
        }

        // Real Windows toast first (cover, buttons, app name); a tray balloon if that isn't possible.
        // Balloons replace each other, so they're queued and shown a few seconds apart.
        readonly Queue<Toast> notes = new Queue<Toast>();
        readonly System.Windows.Forms.Timer noteTimer = new System.Windows.Forms.Timer { Interval = 7000 };
        string clickUrl;

        void Notify(Toast t)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (ToastSender.TryShow(t)) return;
                try { invoker.BeginInvoke((Action)(() => QueueBalloon(t))); } catch { }
            });
        }

        void QueueBalloon(Toast t)
        {
            notes.Enqueue(t);
            if (!noteTimer.Enabled) { ShowNextNote(); noteTimer.Start(); }
        }

        void ShowNextNote()
        {
            if (notes.Count == 0) { noteTimer.Stop(); return; }
            var n = notes.Dequeue();
            clickUrl = n.Url;
            try { tray.ShowBalloonTip(15000, n.Title, n.Body, ToolTipIcon.None); } catch { }
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            noteTimer.Stop();
            marquee.Stop();
            updateTimer.Stop();
            try { hover.Close(); } catch { }
            try { engine.Discord.Shutdown(); } catch { }
            tray.Visible = false;
            tray.Dispose();
            Log.Write("Exited");
            base.ExitThreadCore();
        }
    }

    // What's new in this version: shown in the About tab and, once after an update, as a notification.
    // Update both with every release (lower case, like the rest of the app).
    static class PatchNotes
    {
        public const string Summary = "New: a level meter and progress line in the hover popup, smoother history, LED milestone banners.";
        public static readonly string[] Lines =
        {
            "level meter in the hover popup: green, yellow and red blocks that follow apple music's sound (not other apps)",
            "progress line in the hover popup, with a mark where the song gets scrobbled; it lights up once it's sent",
            "new scrobbles slide into the history tab",
            "milestone notifications show the number on an led display",
            "this note: after each update the app tells you what's new",
        };
    }

    static class Program
    {
        public const string Version = "4.2";

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "VsLastfmScrobbler_SingleInstance", out created))
            {
                if (!created) { MessageBox.Show("V's Last.fm Scrobbler is already running (see the tray)."); return; }
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                Settings.PrepareFolders();
                Startup.MigrateOldEntry();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
