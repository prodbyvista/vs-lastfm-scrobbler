// Real Windows 10 toast notifications: app name + icon, album cover, hero banner, buttons.
// Falls back to a tray balloon when toasts aren't available. C# 5.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace ITunesScrobbler
{
    class Toast
    {
        public string Title = "", Body = "", Attribution, Url, Image, Hero;
        public bool CircleImage, DismissButton;
        public readonly List<KeyValuePair<string, string>> Buttons = new List<KeyValuePair<string, string>>();
        public Toast Button(string label, string target) { if (!string.IsNullOrEmpty(target)) Buttons.Add(new KeyValuePair<string, string>(label, target)); return this; }
    }

    static class ToastSender
    {
        const string Aumid = "V.LastfmScrobbler";
        static bool ready, warned;

        static string CacheDir { get { return Path.Combine(Settings.Dir, "cache"); } }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

        // Registers the app's name and icon with Windows so toasts show "V's Last.fm Scrobbler".
        public static void Init(Icon appIcon)
        {
#if SMTC
            try
            {
                Directory.CreateDirectory(CacheDir);
                string iconPng = Path.Combine(CacheDir, "app-icon.png");
                using (var big = new Icon(appIcon, 256, 256)) using (var bmp = big.ToBitmap()) bmp.Save(iconPng, ImageFormat.Png);
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
                {
                    k.SetValue("DisplayName", "V's Last.fm Scrobbler");
                    k.SetValue("IconUri", iconPng);
                }
                SetCurrentProcessExplicitAppUserModelID(Aumid);
                ready = true;
            }
            catch (Exception ex) { Log.Write("Windows notifications unavailable, using tray balloons: " + ex.Message); }
#endif
        }

        static string X(string s) { return SecurityElement.Escape(s ?? ""); }

        static string FileUri(string path) { return new Uri(path).AbsoluteUri; }

        // Desktop apps can only show local images in toasts, so web covers are downloaded first.
        static string LocalImage(string img)
        {
            if (string.IsNullOrEmpty(img)) return null;
            if (!img.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return File.Exists(img) ? FileUri(img) : null;
            try
            {
                Directory.CreateDirectory(CacheDir);
                string name = "img-" + ((uint)img.GetHashCode()).ToString("x8") + ".jpg";
                string path = Path.Combine(CacheDir, name);
                if (!File.Exists(path))
                {
                    using (var wc = new WebClient()) wc.DownloadFile(img, path);
                    // keep the cache small
                    var old = new DirectoryInfo(CacheDir).GetFiles("img-*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(60).ToList();
                    foreach (var f in old) try { f.Delete(); } catch { }
                }
                return FileUri(path);
            }
            catch { return null; }
        }

        // Returns false when a real toast couldn't be shown (caller falls back to a balloon).
        public static bool TryShow(Toast t)
        {
#if SMTC
            if (!ready) return false;
            try
            {
                var x = new StringBuilder();
                x.Append("<toast");
                if (!string.IsNullOrEmpty(t.Url)) x.Append(" activationType=\"protocol\" launch=\"").Append(X(t.Url)).Append('"');
                x.Append("><visual><binding template=\"ToastGeneric\">");
                x.Append("<text>").Append(X(t.Title)).Append("</text>");
                if (!string.IsNullOrEmpty(t.Body)) x.Append("<text>").Append(X(t.Body)).Append("</text>");
                if (!string.IsNullOrEmpty(t.Attribution)) x.Append("<text placement=\"attribution\">").Append(X(t.Attribution)).Append("</text>");
                string img = LocalImage(t.Image);
                if (img != null) x.Append("<image placement=\"appLogoOverride\" src=\"").Append(X(img)).Append('"').Append(t.CircleImage ? " hint-crop=\"circle\"" : "").Append("/>");
                string hero = LocalImage(t.Hero);
                if (hero != null) x.Append("<image placement=\"hero\" src=\"").Append(X(hero)).Append("\"/>");
                x.Append("</binding></visual>");
                if (t.Buttons.Count > 0 || t.DismissButton)
                {
                    x.Append("<actions>");
                    foreach (var b in t.Buttons.Take(t.DismissButton ? 4 : 5))
                        x.Append("<action content=\"").Append(X(b.Key)).Append("\" activationType=\"protocol\" arguments=\"").Append(X(b.Value)).Append("\"/>");
                    if (t.DismissButton) x.Append("<action content=\"Dismiss\" activationType=\"system\" arguments=\"dismiss\"/>");
                    x.Append("</actions>");
                }
                x.Append("</toast>");

                var doc = new Windows.Data.Xml.Dom.XmlDocument();
                doc.LoadXml(x.ToString());
                var n = new Windows.UI.Notifications.ToastNotification(doc);
                Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(Aumid).Show(n);
                return true;
            }
            catch (Exception ex)
            {
                if (!warned) { warned = true; Log.Write("Couldn't show a Windows notification, using tray balloons: " + ex.Message); }
                return false;
            }
#else
            return false;
#endif
        }

        // Banner image for milestone toasts: the number on a green seven-segment display, unlit segments faintly visible.
        public static string MilestoneHero(long number)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string path = Path.Combine(CacheDir, "hero-milestone-" + number + ".png");
                if (File.Exists(path)) return path;
                string digits = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                const float W = 44, H = 80, Gap = 14;
                float total = digits.Length * W + (digits.Length - 1) * Gap;
                float scale = Math.Min(560f / total, 150f / H);
                using (var bmp = new Bitmap(728, 360))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.FromArgb(5, 8, 6));

                    var lit = new List<PointF[]>(); var ghost = new List<PointF[]>();
                    for (int i = 0; i < digits.Length; i++)
                    {
                        string on = SegmentsOf(digits[i]);
                        float x0 = i * (W + Gap);
                        foreach (var seg in "abcdefg")
                            (on.IndexOf(seg) >= 0 ? lit : ghost).Add(Segment(seg, x0, W, H));
                    }

                    // centred, slightly slanted like a real display
                    float drawnW = total * scale, drawnH = H * scale;
                    g.TranslateTransform((728 - drawnW) / 2 + drawnH * 0.05f, 150 - drawnH / 2);
                    using (var slant = new Matrix(1, 0, -0.105f, 1, 0, 0)) g.MultiplyTransform(slant);
                    g.ScaleTransform(scale, scale);
                    using (var b = new SolidBrush(Color.FromArgb(15, 42, 26))) foreach (var p in ghost) g.FillPolygon(b, p);
                    using (var glow = new Pen(Color.FromArgb(50, 70, 255, 142), 5f) { LineJoin = LineJoin.Round })
                        foreach (var p in lit) g.DrawPolygon(glow, p);
                    using (var b = new SolidBrush(Color.FromArgb(70, 255, 142))) foreach (var p in lit) g.FillPolygon(b, p);
                    g.ResetTransform();

                    using (var small = Theme.Mono(17f))
                    using (var dim = new SolidBrush(Color.FromArgb(59, 110, 74)))
                    {
                        string label = "\u2500\u2500[ SCROBBLES ]\u2500\u2500";
                        var size = g.MeasureString(label, small);
                        g.DrawString(label, small, dim, (728 - size.Width) / 2, 150 + drawnH / 2 + 34);
                    }
                    bmp.Save(path, ImageFormat.Png);
                }
                return path;
            }
            catch { return null; }
        }

        static string SegmentsOf(char d)
        {
            switch (d)
            {
                case '0': return "abcdef"; case '1': return "bc"; case '2': return "abged"; case '3': return "abgcd";
                case '4': return "fgbc"; case '5': return "afgcd"; case '6': return "afgedc"; case '7': return "abc";
                case '8': return "abcdefg"; case '9': return "abcdfg"; default: return "";
            }
        }

        // one segment as a six-sided bar: a/g/d across (top, middle, bottom), f/b/e/c upright
        static PointF[] Segment(char seg, float x0, float w, float h)
        {
            const float T = 9, G = 2;
            float m = h / 2;
            switch (seg)
            {
                case 'a': return Across(x0, w, T / 2, T, G);
                case 'g': return Across(x0, w, m, T, G);
                case 'd': return Across(x0, w, h - T / 2, T, G);
                case 'f': return Upright(x0 + T / 2, T / 2, m, T, G);
                case 'b': return Upright(x0 + w - T / 2, T / 2, m, T, G);
                case 'e': return Upright(x0 + T / 2, m, h - T / 2, T, G);
                default: return Upright(x0 + w - T / 2, m, h - T / 2, T, G);   // 'c'
            }
        }

        static PointF[] Across(float x0, float w, float y, float t, float gap)
        {
            return new[] { new PointF(x0 + t / 2 + gap, y), new PointF(x0 + t + gap, y - t / 2), new PointF(x0 + w - t - gap, y - t / 2),
                           new PointF(x0 + w - t / 2 - gap, y), new PointF(x0 + w - t - gap, y + t / 2), new PointF(x0 + t + gap, y + t / 2) };
        }

        static PointF[] Upright(float x, float y1, float y2, float t, float gap)
        {
            return new[] { new PointF(x, y1 + gap), new PointF(x + t / 2, y1 + t / 2 + gap), new PointF(x + t / 2, y2 - t / 2 - gap),
                           new PointF(x, y2 - gap), new PointF(x - t / 2, y2 - t / 2 - gap), new PointF(x - t / 2, y1 + t / 2 + gap) };
        }

        // Banner image for the "wrap is ready" toast, in the app's green terminal look.
        // kind: "MONTHLY WRAP" / "YEARLY WRAP"; title: "OCT 2026" / "2026"
        public static string WrapHero(string key, string kind, string title, string line)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string path = Path.Combine(CacheDir, "hero-" + key + ".png");
                using (var bmp = new Bitmap(728, 360))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    var r = new Rectangle(0, 0, bmp.Width, bmp.Height);
                    using (var b = new LinearGradientBrush(r, Color.FromArgb(10, 22, 14), Color.FromArgb(4, 7, 5), 90f)) g.FillRectangle(b, r);
                    var green = Color.FromArgb(70, 255, 142);
                    using (var big = Theme.Mono(58f, FontStyle.Bold))
                    using (var small = Theme.Mono(17f))
                    {
                        string head = "──[ " + kind + " ]──";
                        using (var dim = new SolidBrush(Color.FromArgb(59, 110, 74))) g.DrawString(head, small, dim, 40, 40);
                        var size = g.MeasureString(title, big);
                        float tx = 40, ty = 120;
                        using (var halo = new SolidBrush(Color.FromArgb(45, green)))
                            foreach (var d in new[] { new PointF(-3, 0), new PointF(3, 0), new PointF(0, -3), new PointF(0, 3) }) g.DrawString(title, big, halo, tx + d.X, ty + d.Y);
                        using (var b = new SolidBrush(green)) g.DrawString(title, big, b, tx, ty);
                        using (var b = new SolidBrush(Color.FromArgb(143, 207, 156))) g.DrawString(line, small, b, 42, ty + size.Height + 12);
                    }
                    using (var p = new Pen(Color.FromArgb(60, 0, 0, 0))) for (int y = 0; y < bmp.Height; y += 3) g.DrawLine(p, 0, y, bmp.Width, y);
                    bmp.Save(path, ImageFormat.Png);
                }
                return path;
            }
            catch { return null; }
        }
    }
}
