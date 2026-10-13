// Updates from GitHub: looks for a newer release of the app, downloads it, puts the new code
// into Source\ and runs build.bat, which closes the app, builds the new version and starts it.
// Only Source\, README.txt, Install.bat and LICENSE.txt are replaced: Data\ (settings, queue) and Wraps\ are never touched.
// C# 5 (built-in Windows compiler).

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ITunesScrobbler
{
    static class Updater
    {
        public const string Repo = "prodbyvista/vs-lastfm-scrobbler";
        public static string RepoUrl { get { return "https://github.com/" + Repo; } }

        // set by the tray app: Changed = a check found something (updates the tray menu),
        // InstallRequested = the settings tab's "update to vX.Y" button was clicked
        public static Action Changed, InstallRequested;

        // what the last check found (null until a check worked)
        public static string LatestTag, LatestPage, AssetUrl;

        public static string LatestVersion { get { return LatestTag == null ? null : LatestTag.TrimStart('v', 'V'); } }
        public static bool Available { get { return LatestTag != null && AssetUrl != null && IsNewer(LatestVersion, Program.Version); } }

        static Version Parse(string v)
        {
            Version r;
            return Version.TryParse((v ?? "").Trim().TrimStart('v', 'V'), out r) ? r : null;
        }

        public static bool IsNewer(string candidate, string current)
        {
            var a = Parse(candidate); var b = Parse(current);
            return a != null && b != null && a > b;
        }

        static WebClient Client()
        {
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "VsLastfmScrobbler/" + Program.Version;   // GitHub requires one
            wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return wc;
        }

        // asks GitHub for the newest published release (drafts and pre-releases don't count)
        public static void Check()
        {
            string json;
            using (var wc = Client()) json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            var page = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/releases/tag/[^\"]+)\"");
            var asset = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+\\.zip)\"");
            LatestTag = tag.Success ? tag.Groups[1].Value : null;
            LatestPage = page.Success ? page.Groups[1].Value : RepoUrl + "/releases";
            AssetUrl = asset.Success ? asset.Groups[1].Value : null;
        }

        // Downloads and unpacks the update, copies it over Source\, then starts build.bat.
        // Returns normally when build.bat is running; the caller should then exit the app.
        public static void Install()
        {
            if (!Available) throw new InvalidOperationException("no update available");
            string work = Path.Combine(Settings.Dir, "update");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, "update.zip"), unpacked = Path.Combine(work, "files");
            Log.Write("Update: downloading v" + LatestVersion + " from " + AssetUrl);
            using (var wc = Client()) wc.DownloadFile(AssetUrl, zip);
            ZipFile.ExtractToDirectory(zip, unpacked);

            // the release zip has one folder ("V's Last.fm Scrobbler\") with Source\build.bat inside
            var bat = Directory.GetFiles(unpacked, "build.bat", SearchOption.AllDirectories)
                               .FirstOrDefault(p => Path.GetFileName(Path.GetDirectoryName(p)).Equals("Source", StringComparison.OrdinalIgnoreCase));
            if (bat == null) throw new InvalidDataException("the download doesn't look like an app update (no Source\\build.bat)");
            string newSource = Path.GetDirectoryName(bat), newRoot = Path.GetDirectoryName(newSource);

            string app = Settings.AppDir, source = Path.Combine(app, "Source");
            Directory.CreateDirectory(source);
            foreach (var f in Directory.GetFiles(newSource))
                File.Copy(f, Path.Combine(source, Path.GetFileName(f)), true);
            foreach (var name in new[] { "README.txt", "Install.bat", "LICENSE.txt" })
            {
                string f = Path.Combine(newRoot, name);
                if (File.Exists(f)) File.Copy(f, Path.Combine(app, name), true);
            }
            Log.Write("Update: v" + LatestVersion + " unpacked, building it now (the app restarts by itself)");

            Process.Start(new ProcessStartInfo(Path.Combine(source, "build.bat")) { WorkingDirectory = source, UseShellExecute = true });
        }
    }
}
