// Fallback reader: Windows "System Media Transport Controls" (the media overlay that
// appears when you press a volume key). Apple Music for Windows - and many other
// players - publish what they're playing here, even though they have no COM API.
// Compiled only when build.bat defines SMTC (needs Windows 10 1809 or newer).

#if SMTC
using System;
using System.Text;
using System.Diagnostics;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

namespace ITunesScrobbler
{
    static class SmtcReader
    {
        static GlobalSystemMediaTransportControlsSessionManager manager;
        static DateTime nextManagerTry = DateTime.MinValue;

        // Waits for a Windows async call by polling its status
        // (avoids System.Runtime.WindowsRuntime, which the built-in compiler can't link).
        static T Wait<T>(IAsyncOperation<T> op)
        {
            var sw = Stopwatch.StartNew();
            while (op.Status == AsyncStatus.Started)
            {
                if (sw.ElapsedMilliseconds > 3000)
                {
                    try { op.Cancel(); } catch { }
                    throw new TimeoutException("Windows media controls did not respond");
                }
                Thread.Sleep(10);
            }
            if (op.Status == AsyncStatus.Completed) return op.GetResults();
            if (op.Status == AsyncStatus.Error && op.ErrorCode != null) throw op.ErrorCode;
            throw new InvalidOperationException("Windows media request " + op.Status);
        }

        static GlobalSystemMediaTransportControlsSessionManager Manager()
        {
            if (manager == null && DateTime.UtcNow >= nextManagerTry)
            {
                try { manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync()); }
                catch (Exception ex)
                {
                    nextManagerTry = DateTime.UtcNow.AddMinutes(1);
                    Diag.Once("smtc-mgr", "Windows media controls unavailable: " + ex.Message);
                }
            }
            return manager;
        }

        // Apple's apps only; other players (browsers, Spotify...) are ignored.
        static bool IsApplePlayer(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;
            string a = appId.ToLowerInvariant();
            return a.Contains("itunes") || a.Contains("applemusic") || a.Contains("appleinc.");
        }

        public static bool TryRead(out Track track, out int state, out int position)
        {
            track = null; state = ITunesReader.StateStopped; position = 0;
            try
            {
                var mgr = Manager();
                if (mgr == null) return false;

                GlobalSystemMediaTransportControlsSession session = null;
                foreach (var s in mgr.GetSessions())
                {
                    if (!IsApplePlayer(s.SourceAppUserModelId)) continue;
                    // prefer a session that's actually playing
                    if (session == null || s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        session = s;
                }
                if (session == null) return false;

                var info = session.GetPlaybackInfo();
                state = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    ? ITunesReader.StatePlaying : ITunesReader.StateStopped;

                var props = Wait(session.TryGetMediaPropertiesAsync());
                if (props == null) return true;

                track = new Track();
                track.Artist = (props.Artist ?? "").Trim();
                track.Name = (props.Title ?? "").Trim();
                track.Album = (props.AlbumTitle ?? "").Trim();
                track.AlbumArtist = (props.AlbumArtist ?? "").Trim();
                track.TrackNumber = props.TrackNumber;

                track.Normalize();   // split "Artist \u2014 Album" apart

                var tl = session.GetTimelineProperties();
                double len = (tl.EndTime - tl.StartTime).TotalSeconds;
                track.Duration = len > 0 && len < 24 * 3600 ? (int)Math.Round(len) : 0;

                double pos = (tl.Position - tl.StartTime).TotalSeconds;
                if (state == ITunesReader.StatePlaying && tl.LastUpdatedTime.Year > 2000)
                {
                    double since = (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
                    if (since > 0 && since < 3600) pos += since;   // position is only refreshed occasionally
                }
                if (track.Duration > 0 && pos > track.Duration) pos = track.Duration;
                position = pos > 0 ? (int)pos : 0;

                track.Id = track.Artist + "|" + track.Name + "|" + track.Duration;
                return true;
            }
            catch (Exception ex)
            {
                Diag.Once("smtc-err:" + ex.GetType().Name, "Windows media controls read failed: " + ex.GetType().Name + ": " + ex.Message);
                manager = null;
                return false;
            }
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            try
            {
                var mgr = Manager();
                if (mgr == null) { sb.AppendLine("   (media controls unavailable)"); return sb.ToString(); }
                int n = 0;
                foreach (var s in mgr.GetSessions())
                {
                    n++;
                    string line = "   " + s.SourceAppUserModelId + "  status=" + s.GetPlaybackInfo().PlaybackStatus
                        + (IsApplePlayer(s.SourceAppUserModelId) ? "  <- used" : "  (ignored, not an Apple player)");
                    try
                    {
                        var p = Wait(s.TryGetMediaPropertiesAsync());
                        if (p != null) line += "  [" + p.Artist + " - " + p.Title + " | " + p.AlbumTitle + "]";
                    }
                    catch { }
                    sb.AppendLine(line);
                }
                if (n == 0) sb.AppendLine("   (no apps are reporting media to Windows)");
            }
            catch (Exception ex) { sb.AppendLine("   error: " + ex.Message); }
            return sb.ToString();
        }
    }
}
#endif
