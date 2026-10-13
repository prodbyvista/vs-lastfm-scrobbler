// Monthly wrap page: black & green terminal look with real cover art.
// C# 5 (built-in Windows compiler).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace ITunesScrobbler
{
    static class WrapPage
    {
        static readonly CultureInfo En = CultureInfo.InvariantCulture;

        public static string Render(WrapData w)
        {
            var h = new StringBuilder();
            string handle = (w.User ?? "user").ToLowerInvariant();
            DateTime lastDay = w.Partial ? DateTime.Now : w.To.AddDays(-1);
            var top = w.Artists.Count > 0 ? w.Artists[0] : null;
            string unit = w.Year ? "year" : "month";

            h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            h.Append("<title>").Append(E(w.Month)).Append(" · wrap</title><style>").Append(Css).Append("</style></head><body><main>");

            // ---- header: prompt, block-letter month, period, flags
            h.Append("<header><div class=\"prompt mono\">").Append(E(handle)).Append("@last.fm:~$ ./wrap ").Append(w.Year ? "--year " + w.From.Year.ToString(En) : "--month " + w.From.ToString("yyyy-MM", En))
             .Append(w.Partial ? " --so-far" : "").Append("<span class=\"cursor\">█</span></div>");
            h.Append("<pre class=\"banner\" aria-label=\"").Append(E(w.Month)).Append("\">").Append(Banner(w.Month.ToUpperInvariant())).Append("</pre>");
            h.Append("<div class=\"period mono\">").Append(E(w.User)).Append(" · ").Append(E(w.From.ToString("d MMM", En).ToLowerInvariant()))
             .Append(" – ").Append(E(lastDay.ToString("d MMM yyyy", En).ToLowerInvariant())).Append(w.Partial ? " · so far" : "").Append("</div>");
            h.Append("<div class=\"flags mono\">");
            Flag(h, "NEW", w.NewArtists.Count > 0, w.NewArtists.Count > 0 ? "new this " + unit + ": " + string.Join(", ", w.NewArtists) : "no new artists in your top 10");
            if (!w.Year)
                Flag(h, "LOUD", w.Record, w.Record ? "your biggest month of the past year" : "biggest month of the past year: " + w.PrevBest.ToString("N0", En));
            else
                Flag(h, "LOUD", w.Record, w.Record ? "your biggest year yet" + (w.Partial ? ", and it isn't even over" : "")
                    : w.PrevYears > 0 ? "biggest earlier year: " + w.PrevBest.ToString("N0", En) + " scrobbles" : "your first year on last.fm, nothing to compare yet");
            Flag(h, "RPT", w.RepeatRun >= 3, w.RepeatRun >= 3 ? "on repeat: " + w.RepeatTrack + " (" + w.RepeatRun + "x in a row)" : "nothing on repeat");
            h.Append("</div></header>");

            // ---- stat cards
            h.Append("<section class=\"stats\">");
            Stat(h, w.Scrobbles.ToString("N0", En), "scrobbles");
            Stat(h, (w.Estimated ? "~" : "") + Hours(w.TotalSec), "listening time");
            Stat(h, w.ArtistCount.ToString("N0", En), "artists");
            Stat(h, w.BusiestDay.ToString("ddd d MMM", En), "busiest day · " + w.BusiestDayCount + " plays");
            Stat(h, w.BusiestHour.ToString("00") + ":00", "busiest hour · " + w.PerHour[w.BusiestHour] + " plays");
            if (w.AllTime > 0) Stat(h, w.AllTime.ToString("N0", En), "all-time scrobbles");
            h.Append("</section>");

            // ---- artist of the month: real cover, its ASCII version fading in behind
            if (top != null)
            {
                h.Append("<section class=\"card hero\">");
                if (top.Image != null) h.Append("<pre class=\"ascii-bg\" aria-hidden=\"true\">").Append(AsciiCover(top.Image, 72, 36)).Append("</pre>");
                Thumb(h, top.Image, top.Name, "cover");
                h.Append("<div class=\"info\"><div class=\"label mono\">// artist of the ").Append(unit).Append("</div>");
                h.Append("<h1>").Append(E(top.Name)).Append("</h1>");
                h.Append("<div class=\"mono hi\">").Append(top.N.ToString("N0", En)).Append(" scrobbles <span class=\"muted\">· ")
                 .Append(((double)top.N * 100 / Math.Max(1, w.Scrobbles)).ToString("0.0", En)).Append("% of your ").Append(unit).Append("</span></div>");
                Meter(h, top.N, Math.Max(top.N, w.Scrobbles / 5), 28);
                if (w.Artists.Count > 1)
                    h.Append("<div class=\"runner mono\"><span class=\"muted\">runner-up →</span> ").Append(E(w.Artists[1].Name))
                     .Append(" <span class=\"muted\">[").Append(w.Artists[1].N).Append("]</span></div>");
                h.Append("</div></section>");
            }

            // ---- top lists with covers
            h.Append("<div class=\"cols\">");
            ListCard(h, "top_artists", w.Artists, false);
            ListCard(h, "top_albums", w.Albums, true);
            ListCard(h, "top_tracks", w.Tracks, true);
            h.Append("</div>");

            // ---- charts (the readable layout from the first design)
            if (w.Year) YearCharts(h, w);
            else DayChart(h, w, lastDay);
            TimeOfDay(h, w);

            // ---- footer
            h.Append("<footer class=\"mono\">// generated ").Append(E(DateTime.Now.ToString("d MMM yyyy HH:mm", En).ToLowerInvariant()))
             .Append(" by v's last.fm scrobbler");
            if (w.Estimated) h.Append("<br>// ~ listening time partly estimated: last.fm doesn't know every song's length");
            h.Append("</footer></main></body></html>");
            return h.ToString();
        }

        static void DayChart(StringBuilder h, WrapData w, DateTime lastDay)
        {
            var days = new List<int>(); var dayLabels = new List<DateTime>();
            for (var d = w.From.Date; d < w.From.AddMonths(1) && d <= lastDay.Date; d = d.AddDays(1))
            {
                int n; w.PerDay.TryGetValue(d, out n);
                days.Add(n); dayLabels.Add(d);
            }
            h.Append("<section class=\"card chartcard\"><h3 class=\"mono\"><span class=\"muted\">$</span> every_day</h3><div class=\"bars\">");
            int maxDay = Math.Max(1, days.Count > 0 ? days.Max() : 1);
            for (int i = 0; i < days.Count; i++)
            {
                var d = dayLabels[i];
                h.Append("<div class=\"col").Append(d.Date == w.BusiestDay.Date ? " hot" : "").Append("\" title=\"").Append(E(d.ToString("ddd d MMM", En))).Append(": ")
                 .Append(days[i]).Append(" scrobbles\"><i style=\"height:").Append((days[i] * 100.0 / maxDay).ToString("0.#", En)).Append("%\"></i><span>")
                 .Append(d.Day).Append("</span></div>");
            }
            h.Append("</div></section>");
        }

        // year wraps: a bar per month, then each month's #1 artist with its cover
        static void YearCharts(StringBuilder h, WrapData w)
        {
            var n = new int[12];
            foreach (var m in w.Months) if (m.Month.Year == w.From.Year) n[m.Month.Month - 1] = m.N;
            int last = w.Partial && w.From.Year == DateTime.Now.Year ? DateTime.Now.Month : 12;
            int max = Math.Max(1, n.Max()), hot = Array.IndexOf(n, n.Max());
            h.Append("<section class=\"card chartcard\"><h3 class=\"mono\"><span class=\"muted\">$</span> every_month</h3><div class=\"bars\">");
            for (int i = 0; i < last; i++)
            {
                var d = new DateTime(w.From.Year, i + 1, 1);
                h.Append("<div class=\"col").Append(i == hot ? " hot" : "").Append("\" title=\"").Append(E(d.ToString("MMMM", En))).Append(": ")
                 .Append(n[i].ToString("N0", En)).Append(" scrobbles\"><i style=\"height:").Append((n[i] * 100.0 / max).ToString("0.#", En)).Append("%\"></i><span>")
                 .Append(E(d.ToString("MMM", En).ToLowerInvariant())).Append("</span></div>");
            }
            h.Append("</div></section>");

            if (w.Months.Count == 0) return;
            int best = w.Months.Max(m => m.N);
            h.Append("<section class=\"card chartcard\"><h3 class=\"mono\"><span class=\"muted\">$</span> month_by_month</h3><div class=\"months\">");
            foreach (var m in w.Months)
            {
                h.Append("<div class=\"mcell").Append(m.N == best ? " hot" : "").Append("\">");
                Thumb(h, m.Image, m.Artist, "thumb");
                h.Append("<div class=\"t\"><div class=\"mname mono\">").Append(E(m.Month.ToString("MMM", En).ToLowerInvariant())).Append(" · ")
                 .Append(m.N.ToString("N0", En)).Append("</div><div class=\"n\">").Append(E(m.Artist)).Append("</div><div class=\"s\">#1 that month</div></div></div>");
            }
            h.Append("</div></section>");
        }

        static void TimeOfDay(StringBuilder h, WrapData w)
        {
            h.Append("<section class=\"card chartcard\"><h3 class=\"mono\"><span class=\"muted\">$</span> time_of_day</h3><div class=\"bars\">");
            int maxHour = Math.Max(1, w.PerHour.Max());
            for (int i = 0; i < 24; i++)
                h.Append("<div class=\"col").Append(i == w.BusiestHour ? " hot" : "").Append("\" title=\"").Append(i.ToString("00")).Append(":00–")
                 .Append(((i + 1) % 24).ToString("00")).Append(":00: ").Append(w.PerHour[i]).Append(" scrobbles\"><i style=\"height:")
                 .Append((w.PerHour[i] * 100.0 / maxHour).ToString("0.#", En)).Append("%\"></i><span>").Append(i % 3 == 0 ? i.ToString("00") : "").Append("</span></div>");
            h.Append("</div></section>");
        }

        // ------------------------------------------------------------ pieces
        static string E(string s) { return WebUtility.HtmlEncode(s ?? ""); }

        static string Hours(double sec)
        {
            long min = (long)Math.Round(sec / 60);
            return (min / 60).ToString("N0", En) + " h " + (min % 60) + " min";
        }

        // [NEW] [LOUD] [RPT]: lit when true, dim otherwise; hover explains why
        static void Flag(StringBuilder h, string text, bool on, string title)
        {
            h.Append("<span class=\"flag").Append(on ? " on" : "").Append("\" title=\"").Append(E(title)).Append("\">[").Append(text).Append("]</span>");
        }

        static void Stat(StringBuilder h, string value, string label)
        {
            h.Append("<div class=\"card stat\"><div class=\"v mono\">").Append(E(value)).Append("</div><div class=\"l\">").Append(E(label)).Append("</div></div>");
        }

        static void Thumb(StringBuilder h, string img, string alt, string cls)
        {
            if (!string.IsNullOrEmpty(img))
                h.Append("<img class=\"").Append(cls).Append("\" src=\"").Append(E(img)).Append("\" alt=\"\" loading=\"lazy\" referrerpolicy=\"no-referrer\">");
            else
                h.Append("<div class=\"").Append(cls).Append(" ph mono\">").Append(E(alt.Length > 0 ? alt.Substring(0, 1).ToUpperInvariant() : "?")).Append("</div>");
        }

        static void Meter(StringBuilder h, int value, int max, int width)
        {
            int filled = (int)Math.Round(value * (double)width / Math.Max(1, max));
            filled = Math.Max(1, Math.Min(width, filled));
            h.Append("<div class=\"meter mono\"><span class=\"bar\">").Append(new string('█', filled)).Append("</span><span class=\"faint\">")
             .Append(new string('░', width - filled)).Append("</span></div>");
        }

        static void ListCard(StringBuilder h, string title, List<MonthlyWrap.Entry> items, bool withSub)
        {
            h.Append("<section class=\"card\"><h3 class=\"mono\"><span class=\"muted\">$</span> ").Append(E(title)).Append("</h3><ol>");
            if (items.Count == 0) h.Append("<li class=\"muted mono\">(nothing yet)</li>");
            int max = items.Count > 0 ? items[0].N : 1;
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                h.Append("<li><span class=\"rank mono\">").Append((i + 1).ToString("00")).Append("</span>");
                Thumb(h, c.Image, c.Name, "thumb");
                h.Append("<div class=\"t\"><div class=\"n\">").Append(E(c.Name)).Append("</div>");
                if (withSub && c.Sub != "") h.Append("<div class=\"s\">").Append(E(c.Sub)).Append("</div>");
                Meter(h, c.N, max, 16);
                h.Append("</div><span class=\"c mono\">").Append(c.N).Append("</span></li>");
            }
            h.Append("</ol></section>");
        }

        // ------------------------------------------------------------ banner (3x5 pixel font, each pixel = 2 chars)
        static readonly Dictionary<char, string[]> Font = new Dictionary<char, string[]>
        {
            { 'A', new[] { ".#.", "#.#", "###", "#.#", "#.#" } }, { 'B', new[] { "##.", "#.#", "##.", "#.#", "##." } },
            { 'C', new[] { ".##", "#..", "#..", "#..", ".##" } }, { 'D', new[] { "##.", "#.#", "#.#", "#.#", "##." } },
            { 'E', new[] { "###", "#..", "##.", "#..", "###" } }, { 'F', new[] { "###", "#..", "##.", "#..", "#.." } },
            { 'G', new[] { ".##", "#..", "#.#", "#.#", ".##" } }, { 'H', new[] { "#.#", "#.#", "###", "#.#", "#.#" } },
            { 'I', new[] { "###", ".#.", ".#.", ".#.", "###" } }, { 'J', new[] { "..#", "..#", "..#", "#.#", ".#." } },
            { 'K', new[] { "#.#", "#.#", "##.", "#.#", "#.#" } }, { 'L', new[] { "#..", "#..", "#..", "#..", "###" } },
            { 'M', new[] { "#.#", "###", "###", "#.#", "#.#" } }, { 'N', new[] { "##.", "#.#", "#.#", "#.#", "#.#" } },
            { 'O', new[] { ".#.", "#.#", "#.#", "#.#", ".#." } }, { 'P', new[] { "##.", "#.#", "##.", "#..", "#.." } },
            { 'Q', new[] { ".#.", "#.#", "#.#", "##.", ".##" } }, { 'R', new[] { "##.", "#.#", "##.", "#.#", "#.#" } },
            { 'S', new[] { ".##", "#..", ".#.", "..#", "##." } }, { 'T', new[] { "###", ".#.", ".#.", ".#.", ".#." } },
            { 'U', new[] { "#.#", "#.#", "#.#", "#.#", "###" } }, { 'V', new[] { "#.#", "#.#", "#.#", "#.#", ".#." } },
            { 'W', new[] { "#.#", "#.#", "###", "###", "#.#" } }, { 'X', new[] { "#.#", "#.#", ".#.", "#.#", "#.#" } },
            { 'Y', new[] { "#.#", "#.#", ".#.", ".#.", ".#." } }, { 'Z', new[] { "###", "..#", ".#.", "#..", "###" } },
            { '0', new[] { "###", "#.#", "#.#", "#.#", "###" } }, { '1', new[] { ".#.", "##.", ".#.", ".#.", "###" } },
            { '2', new[] { "##.", "..#", ".#.", "#..", "###" } }, { '3', new[] { "##.", "..#", ".#.", "..#", "##." } },
            { '4', new[] { "#.#", "#.#", "###", "..#", "..#" } }, { '5', new[] { "###", "#..", "##.", "..#", "##." } },
            { '6', new[] { ".##", "#..", "###", "#.#", "###" } }, { '7', new[] { "###", "..#", ".#.", ".#.", ".#." } },
            { '8', new[] { "###", "#.#", "###", "#.#", "###" } }, { '9', new[] { "###", "#.#", "###", "..#", "##." } },
            { '\'', new[] { "#", "#", ".", ".", "." } }, { '.', new[] { ".", ".", ".", ".", "#" } },
            { ' ', new[] { "..", "..", "..", "..", ".." } },
        };

        public static string Banner(string text)
        {
            var lines = new StringBuilder[5];
            for (int r = 0; r < 5; r++) lines[r] = new StringBuilder();
            foreach (char ch in text)
            {
                string[] g;
                if (!Font.TryGetValue(ch, out g)) continue;
                for (int r = 0; r < 5; r++)
                {
                    foreach (char px in g[r]) lines[r].Append(px == '#' ? "██" : "  ");
                    lines[r].Append("  ");
                }
            }
            var sb = new StringBuilder();
            for (int r = 0; r < 5; r++) sb.Append(lines[r].ToString().TrimEnd()).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        // ------------------------------------------------------------ cover -> ASCII art (faint background of the hero card)
        static string AsciiCover(string url, int cols, int rows)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try
            {
                byte[] data;
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "VsLastfmScrobbler/3";
                    data = wc.DownloadData(url);
                }
                using (var ms = new MemoryStream(data))
                using (var src = new Bitmap(ms))
                using (var small = new Bitmap(cols, rows))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, cols, rows);
                    }
                    var lum = new double[cols, rows];
                    double lo = 1, hi = 0;
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < cols; x++)
                        {
                            var c = small.GetPixel(x, y);
                            double l = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                            lum[x, y] = l; lo = Math.Min(lo, l); hi = Math.Max(hi, l);
                        }
                    const string ramp = " .:-=+*#%@";
                    var sb = new StringBuilder();
                    for (int y = 0; y < rows; y++)
                    {
                        for (int x = 0; x < cols; x++)
                        {
                            double l = hi - lo > 0.05 ? (lum[x, y] - lo) / (hi - lo) : lum[x, y];
                            int idx = Math.Max(0, Math.Min(ramp.Length - 1, (int)(l * ramp.Length)));
                            sb.Append(E(ramp[idx].ToString()));
                        }
                        sb.Append('\n');
                    }
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Verbose("Wrap: couldn't turn the cover into ASCII art: " + ex.Message);
                return "";
            }
        }

        // ------------------------------------------------------------ style (shared with the wraps shelf)
        internal const string Css = @"
:root{--bg:#040705;--card:#09110c;--card2:#0c1610;--line:#163222;--text:#dcf7e4;--muted:#6c9a7a;--hi:#4dff91;--bar:#2fbf68;--faint:#183a27;
  --mono:'Cascadia Mono','Cascadia Code',Consolas,'Lucida Console',monospace;--sans:'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif}
*{box-sizing:border-box}
html{background:var(--bg)}
body{margin:0;color:var(--text);font:15px/1.45 var(--sans);
  background:radial-gradient(1100px 500px at 50% -120px,rgba(77,255,145,.10),transparent 70%) no-repeat,var(--bg)}
main{max-width:1120px;margin:0 auto;padding:36px 20px 56px}
.mono{font-family:var(--mono)}
.muted{color:var(--muted)}
.hi{color:var(--hi)}
.bar{color:var(--bar)}
.faint{color:var(--faint)}
pre{margin:0;font-family:var(--mono);white-space:pre}

header{margin-bottom:22px}
.prompt{color:var(--muted);font-size:13px}
.cursor{color:var(--hi);margin-left:4px;animation:blink 1.1s steps(1) infinite}
@keyframes blink{50%{opacity:0}}
.banner{color:var(--hi);font-size:12px;line-height:1.02;margin:18px 0 12px;overflow:visible;text-shadow:0 0 10px rgba(77,255,145,.35)}
.period{color:var(--muted);font-size:13px}
.flags{display:flex;gap:12px;margin-top:8px;font-size:13px}
.flag{color:var(--faint);cursor:default}
.flag.on{color:var(--hi);text-shadow:0 0 8px rgba(77,255,145,.45)}

.card{background:linear-gradient(180deg,var(--card2),var(--card));border:1px solid var(--line);border-radius:14px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin-bottom:14px}
.stat{padding:16px 18px}
.stat .v{font-size:24px;font-weight:600;color:var(--hi);letter-spacing:-.5px;white-space:nowrap}
.stat .l{color:var(--muted);font-size:13px;margin-top:2px}

.hero{position:relative;overflow:hidden;display:flex;gap:26px;align-items:center;padding:22px;margin-bottom:14px}
.ascii-bg{position:absolute;right:-10px;top:50%;transform:translateY(-50%);font-size:8px;line-height:1;color:var(--hi);opacity:.22;
  pointer-events:none;-webkit-mask-image:linear-gradient(to left,#000 35%,transparent 95%);mask-image:linear-gradient(to left,#000 35%,transparent 95%)}
.cover{width:170px;height:170px;border-radius:10px;object-fit:cover;flex:none;position:relative;
  box-shadow:0 0 0 1px var(--line),0 12px 40px rgba(0,0,0,.6),0 0 40px rgba(77,255,145,.12)}
.info{position:relative;min-width:0}
.label{color:var(--hi);font-size:12px;letter-spacing:.5px}
.hero h1{font-size:40px;line-height:1.05;margin:6px 0 10px;letter-spacing:-1px;font-weight:700}
.hero .meter{margin-top:10px;font-size:13px}
.runner{margin-top:12px;font-size:13px}

.cols{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:14px;margin-bottom:14px}
.cols>.card{padding:16px 16px 8px}
h3{font-size:13px;font-weight:600;margin:0 0 10px;color:var(--hi)}
ol{list-style:none;margin:0;padding:0}
li{display:flex;align-items:center;gap:11px;padding:7px 0;border-top:1px solid var(--line)}
li:first-child{border-top:0}
.rank{color:var(--muted);font-size:12px;width:18px;flex:none}
.thumb{width:46px;height:46px;border-radius:6px;object-fit:cover;flex:none;box-shadow:0 0 0 1px var(--line)}
.ph{display:flex;align-items:center;justify-content:center;background:var(--faint);color:var(--hi);font-weight:700}
.cover.ph{font-size:56px}
.t{flex:1;min-width:0}
.n{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.s{color:var(--muted);font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.meter{font-size:10px;line-height:1;margin-top:5px;letter-spacing:-.5px;white-space:nowrap}
.c{color:var(--muted);font-size:13px}

.chartcard{padding:16px 18px 14px;margin-bottom:14px}
.bars{display:flex;align-items:flex-end;gap:4px;height:180px;padding-bottom:22px}
.col{flex:1;height:100%;display:flex;flex-direction:column;justify-content:flex-end;position:relative;min-width:0}
.col i{display:block;min-height:2px;border-radius:3px 3px 0 0;background:repeating-linear-gradient(to top,var(--bar) 0 4px,rgba(47,191,104,.55) 4px 5px)}
.col:hover i{filter:brightness(1.25)}
.col.hot i{background:repeating-linear-gradient(to top,var(--hi) 0 4px,rgba(77,255,145,.6) 4px 5px);box-shadow:0 0 14px rgba(77,255,145,.4)}
.col span{position:absolute;bottom:-21px;left:-6px;right:-6px;text-align:center;font:11px var(--mono);color:var(--muted)}
.col.hot span{color:var(--hi)}

.months{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:10px}
.mcell{display:flex;gap:11px;align-items:center;padding:8px 10px;border:1px solid var(--line);border-radius:10px;background:rgba(0,0,0,.18);min-width:0}
.mcell.hot{border-color:var(--hi);box-shadow:0 0 14px rgba(77,255,145,.18)}
.mname{color:var(--muted);font-size:12px}
.mcell.hot .mname{color:var(--hi)}

footer{color:var(--muted);font-size:12px;margin-top:22px}
::selection{background:var(--hi);color:var(--bg)}
@media (max-width:900px){.banner{font-size:9px}}
@media (max-width:640px){.hero{flex-direction:column;align-items:flex-start}.hero h1{font-size:30px}.cover{width:130px;height:130px}.banner{font-size:6px}.bars{gap:2px}}
@media (prefers-reduced-motion:reduce){.cursor{animation:none}}
";
    }
}
