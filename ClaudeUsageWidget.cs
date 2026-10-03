// Claude Usage Widget - desktop widget showing Claude plan limits and token usage.
// Build: build.cmd (uses the C# compiler that ships with Windows / .NET Framework 4.x)
//
// Everything here is deterministic: no model is ever called, so refreshing costs zero tokens.
//  - Limits: one GET to the free /api/oauth/usage endpoint (the same one Claude Code's /usage uses),
//            falling back to the snapshot file the Claude desktop app writes.
//  - Tokens: summed from local session transcripts (*.jsonl), read incrementally.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeUsageWidget
{
    // ------------------------------------------------------------------ settings
    class Settings
    {
        public double Left = double.NaN, Top = double.NaN, Width = 300, Height = 200;
        public bool Locked = false;
        public int TintAlpha = 0x70;
        public bool FirstRunDone = false;

        public static string Dir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget"); } }
        static string FilePath { get { return System.IO.Path.Combine(Dir, "settings.json"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath));
                object v;
                if (d.TryGetValue("Left", out v) && v != null) s.Left = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (d.TryGetValue("Top", out v) && v != null) s.Top = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (d.TryGetValue("Width", out v)) s.Width = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (d.TryGetValue("Height", out v)) s.Height = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (d.TryGetValue("Locked", out v)) s.Locked = Convert.ToBoolean(v);
                if (d.TryGetValue("TintAlpha", out v)) s.TintAlpha = Convert.ToInt32(v);
                if (d.TryGetValue("FirstRunDone", out v)) s.FirstRunDone = Convert.ToBoolean(v);
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var d = new Dictionary<string, object>();
                d["Left"] = double.IsNaN(Left) ? (object)null : Left;
                d["Top"] = double.IsNaN(Top) ? (object)null : Top;
                d["Width"] = Width; d["Height"] = Height;
                d["Locked"] = Locked; d["TintAlpha"] = TintAlpha; d["FirstRunDone"] = FirstRunDone;
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ data: plan limits
    class LimitInfo
    {
        public double? FiveHourUsed, WeeklyUsed;     // percent used, 0..100
        public DateTime? FiveHourReset, WeeklyReset; // local time
        public DateTime? AsOf;                       // local time
        public bool Live;                            // true = from the usage API
        public string Source = "";
        public string Error;
    }

    static class LimitFetcher
    {
        const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        static LimitInfo lastLive;

        public static string CredentialsPath
        {
            get
            {
                var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                var dir = string.IsNullOrEmpty(cfg) ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : cfg;
                return System.IO.Path.Combine(dir, ".credentials.json");
            }
        }

        static DateTime? ParseIso(object o)
        {
            var s = o as string;
            if (string.IsNullOrEmpty(s)) return null;
            DateTime dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt)) return dt.ToLocalTime();
            return null;
        }

        static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }

        static double? Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { }
            }
            return null;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as string : null;
        }

        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs)) return sr.ReadToEnd();
        }

        static string Http(string method, string url, string bearer, string jsonBody)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 15000;
            req.UserAgent = "claude-usage-widget/2.0";
            req.Accept = "application/json";
            if (bearer != null)
            {
                req.Headers["Authorization"] = "Bearer " + bearer;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            }
            if (jsonBody != null)
            {
                var bytes = Encoding.UTF8.GetBytes(jsonBody);
                req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd();
        }

        public static bool HasLogin()
        {
            try
            {
                if (!File.Exists(CredentialsPath)) return false;
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadShared(CredentialsPath));
                var o = Obj(root, "claudeAiOauth");
                return o != null && (Str(o, "refreshToken") != null || Str(o, "accessToken") != null);
            }
            catch { return false; }
        }

        // Returns a valid access token from Claude Code's credentials file, refreshing it (and writing
        // the rotated tokens back, exactly like Claude Code does) when it is about to expire.
        static string GetAccessToken(bool forceRefresh)
        {
            var path = CredentialsPath;
            if (!File.Exists(path)) return null;
            var js = new JavaScriptSerializer();
            var root = js.Deserialize<Dictionary<string, object>>(ReadShared(path));
            var oauth = Obj(root, "claudeAiOauth");
            if (oauth == null) return null;
            var token = Str(oauth, "accessToken");
            var refresh = Str(oauth, "refreshToken");
            var exp = Num(oauth, "expiresAt");
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool fresh = token != null && (!exp.HasValue || exp.Value > nowMs + 5 * 60 * 1000);
            if (fresh && !forceRefresh) return token;
            if (refresh == null) return fresh ? token : null;

            var body = js.Serialize(new Dictionary<string, object> { { "grant_type", "refresh_token" }, { "refresh_token", refresh }, { "client_id", ClientId } });
            var resp = js.Deserialize<Dictionary<string, object>>(Http("POST", TokenUrl, null, body));
            var newAccess = Str(resp, "access_token");
            if (newAccess == null) throw new Exception("token refresh returned no access token");
            oauth["accessToken"] = newAccess;
            var newRefresh = Str(resp, "refresh_token");
            if (newRefresh != null) oauth["refreshToken"] = newRefresh;
            var expiresIn = Num(resp, "expires_in");
            if (expiresIn.HasValue) oauth["expiresAt"] = nowMs + (long)(expiresIn.Value * 1000);

            var tmp = path + ".widget.tmp";
            File.WriteAllText(tmp, js.Serialize(root));
            File.Copy(tmp, path, true);
            File.Delete(tmp);
            return newAccess;
        }

        static LimitInfo TryApi()
        {
            var token = GetAccessToken(false);
            if (token == null) return null;
            string body;
            try { body = Http("GET", UsageUrl, token, null); }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null || r.StatusCode != HttpStatusCode.Unauthorized) throw;
                token = GetAccessToken(true); // token revoked/expired early: refresh once and retry
                body = Http("GET", UsageUrl, token, null);
            }
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
            var fh = Obj(d, "five_hour");
            var sd = Obj(d, "seven_day");
            var info = new LimitInfo { Source = "Claude usage API", AsOf = DateTime.Now, Live = true };
            info.FiveHourUsed = Num(fh, "utilization") ?? 0;
            info.WeeklyUsed = Num(sd, "utilization") ?? 0;
            if (fh != null && fh.ContainsKey("resets_at")) info.FiveHourReset = ParseIso(fh["resets_at"]);
            if (sd != null && sd.ContainsKey("resets_at")) info.WeeklyReset = ParseIso(sd["resets_at"]);
            return info;
        }

        // Fallback: samples the Claude desktop app occasionally writes to plan-usage-history.json
        static LimitInfo TryDesktopHistory()
        {
            var candidates = new[] {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "plan-usage-history.json")
            };
            Dictionary<string, object> best = null; double bestT = -1;
            foreach (var p in candidates)
            {
                if (!File.Exists(p)) continue;
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadShared(p));
                object samplesObj; root.TryGetValue("samples", out samplesObj);
                var samples = samplesObj as ArrayList;
                if (samples == null) continue;
                foreach (var so in samples)
                {
                    var s = so as Dictionary<string, object>;
                    var t = Num(s, "t");
                    if (t.HasValue && t.Value > bestT) { bestT = t.Value; best = s; }
                }
            }
            if (best == null) return null;
            var u = Obj(best, "u") ?? best; // v2: { t, org, u: { fh, sd } }   v1: { t, fh, sd }
            return new LimitInfo
            {
                Source = "Claude app snapshot",
                AsOf = DateTimeOffset.FromUnixTimeMilliseconds((long)bestT).LocalDateTime,
                FiveHourUsed = Num(u, "fh"),
                WeeklyUsed = Num(u, "sd")
            };
        }

        public static LimitInfo Fetch()
        {
            string err = null;
            try
            {
                var r = TryApi();
                if (r != null) { lastLive = r; return r; }
            }
            catch (Exception ex)
            {
                var we = ex as WebException;
                var resp = we != null ? we.Response as HttpWebResponse : null;
                err = resp != null ? "Usage API: HTTP " + (int)resp.StatusCode : "Usage API: " + ex.Message;
                // transient failure (rate limit / offline): keep showing the last live reading
                if (lastLive != null) { lastLive.Error = err; return lastLive; }
            }
            try
            {
                var r = TryDesktopHistory();
                if (r != null) { r.Error = err; return r; }
            }
            catch (Exception ex) { err = (err == null ? "" : err + "; ") + "snapshot: " + ex.Message; }
            return new LimitInfo { Error = err ?? "No usage data yet" };
        }
    }

    // ------------------------------------------------------------------ data: token totals
    class TokenTotals
    {
        public long Input, Output, CacheWrite, CacheRead;
        public long Total { get { return Input + Output + CacheWrite + CacheRead; } }
        public void Add(TokenTotals o) { Input += o.Input; Output += o.Output; CacheWrite += o.CacheWrite; CacheRead += o.CacheRead; }
        public TokenTotals Clone() { return new TokenTotals { Input = Input, Output = Output, CacheWrite = CacheWrite, CacheRead = CacheRead }; }
    }

    // Incrementally scans Claude session transcripts (*.jsonl) and sums the usage block of each
    // assistant message, de-duplicated by message id + request id. Only newly appended bytes are read.
    class TokenScanner
    {
        readonly Dictionary<string, long> offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> seen = new HashSet<string>();
        readonly Dictionary<string, TokenTotals> perDay = new Dictionary<string, TokenTotals>();
        readonly TokenTotals total = new TokenTotals();
        readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };

        static IEnumerable<string> Roots()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var list = new List<string> {
                System.IO.Path.Combine(home, ".claude", "projects"),
                System.IO.Path.Combine(home, ".config", "claude", "projects"),
                System.IO.Path.Combine(appData, "Claude", "local-agent-mode-sessions")
            };
            if (!string.IsNullOrEmpty(cfg)) list.Insert(0, System.IO.Path.Combine(cfg, "projects"));
            return list.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        static IEnumerable<string> SafeEnumerate(string dir)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                string[] files = null, subs = null;
                try { files = Directory.GetFiles(d, "*.jsonl"); subs = Directory.GetDirectories(d); } catch { }
                if (files != null) foreach (var f in files) yield return f;
                if (subs != null) foreach (var s in subs) stack.Push(s);
            }
        }

        void Reset() { offsets.Clear(); seen.Clear(); perDay.Clear(); total.Input = total.Output = total.CacheWrite = total.CacheRead = 0; }

        public void Scan()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool needReset = false;
                foreach (var root in Roots())
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in SafeEnumerate(root))
                        if (!ScanFile(f)) { needReset = true; break; }
                    if (needReset) break;
                }
                if (!needReset) return;
                Reset();
            }
        }

        // returns false if the file shrank (rewritten) and a full rescan is needed
        bool ScanFile(string path)
        {
            long off; offsets.TryGetValue(path, out off);
            long len;
            try { len = new FileInfo(path).Length; } catch { return true; }
            if (len < off) return false;
            if (len == off) return true;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(off, SeekOrigin.Begin);
                    var buf = new byte[len - off];
                    int read = 0;
                    while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
                    if (read == 0) return true;
                    int lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
                    if (lastNl < 0) return true; // no complete line yet
                    var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
                    offsets[path] = off + lastNl + 1;
                    foreach (var line in text.Split('\n'))
                        ProcessLine(line);
                }
            }
            catch { }
            return true;
        }

        static long L(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null) { try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { } }
            return 0;
        }

        void ProcessLine(string line)
        {
            if (line.Length < 20 || line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) return;
            Dictionary<string, object> d;
            try { d = js.Deserialize<Dictionary<string, object>>(line); } catch { return; }
            if (d == null) return;
            object mo; if (!d.TryGetValue("message", out mo)) return;
            var msg = mo as Dictionary<string, object>;
            if (msg == null) return;
            object uo; if (!msg.TryGetValue("usage", out uo)) return;
            var usage = uo as Dictionary<string, object>;
            if (usage == null) return;

            object id, rid;
            msg.TryGetValue("id", out id);
            d.TryGetValue("requestId", out rid);
            if (id != null || rid != null)
            {
                if (!seen.Add((id ?? "") + ":" + (rid ?? ""))) return;
            }

            var t = new TokenTotals
            {
                Input = L(usage, "input_tokens"),
                Output = L(usage, "output_tokens"),
                CacheWrite = L(usage, "cache_creation_input_tokens"),
                CacheRead = L(usage, "cache_read_input_tokens")
            };
            if (t.Total == 0) return;

            DateTime when = DateTime.Now;
            object ts;
            if (d.TryGetValue("timestamp", out ts) && ts is string)
            {
                DateTime p;
                if (DateTime.TryParse((string)ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out p)) when = p.ToLocalTime();
            }
            var dayKey = when.ToString("yyyy-MM-dd");
            TokenTotals day;
            if (!perDay.TryGetValue(dayKey, out day)) { day = new TokenTotals(); perDay[dayKey] = day; }
            day.Add(t);
            total.Add(t);
        }

        public TokenTotals Today()
        {
            TokenTotals t;
            return perDay.TryGetValue(DateTime.Now.ToString("yyyy-MM-dd"), out t) ? t.Clone() : new TokenTotals();
        }
        public TokenTotals AllTime() { return total.Clone(); }
    }

    // ------------------------------------------------------------------ native helpers
    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        public const int WM_WINDOWPOSCHANGING = 0x46, WM_SYSCOMMAND = 0x112;
        public const int SC_MINIMIZE = 0xF020;
        public const int SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    }

    // ------------------------------------------------------------------ the widget
    class WidgetWindow : Window
    {
        [Flags] enum Edge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

        readonly Settings settings;
        IntPtr hwnd;
        readonly Border chrome;
        readonly TextBlock statusText, lockGlyph;
        readonly TextBlock fivePct, fiveSub, weekPct, weekSub, todayVal, totalVal;
        readonly ColumnDefinition fiveFill, fiveRest, weekFill, weekRest;
        readonly Border fiveBar, weekBar;
        readonly Ellipse statusDot, statusRing;
        readonly System.Windows.Shapes.Path grip;
        readonly Grid tokensGrid;
        readonly TokenScanner scanner = new TokenScanner();
        LimitInfo lastLimits;
        DateTime lastLimitFetch = DateTime.MinValue;
        TokenTotals lastToday, lastTotal;
        int refreshing = 0;
        bool settingsDirty;
        System.Windows.Forms.NotifyIcon tray;
        System.Windows.Forms.ToolStripMenuItem trayLock;
        MenuItem lockItem, startupItem, signInItem;
        readonly List<MenuItem> tintItems = new List<MenuItem>();

        // resize state
        Edge resizeEdge = Edge.None;
        Point resizeStartMouse;
        Rect resizeStartRect;

        // palette matched to the 47-lab desktop widgets
        static readonly Color Green = Color.FromRgb(0x4C, 0xA8, 0x74); // dimmed to match how the 47-lab widgets render on the desktop
        static readonly Color Amber = Color.FromRgb(0xC4, 0xA0, 0x5C);
        static readonly Color Red = Color.FromRgb(0xD8, 0x82, 0x8A);
        static readonly Brush Ink = Brushes.White;
        static readonly Brush InkSoft = new SolidColorBrush(Color.FromArgb(0xD1, 0xFF, 0xFF, 0xFF));
        static readonly Brush InkFaint = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        static readonly Brush TrackBrush = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
        static readonly FontFamily SansFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily TechFont = new FontFamily("Bahnschrift, Segoe UI Variable Display, Segoe UI");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        const int LimitPollSeconds = 120; // usage API is rate limited; 2 minutes is plenty
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ClaudeUsageWidget";

        public WidgetWindow(Settings s)
        {
            settings = s;
            Title = "Claude Usage";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            // alpha 1 (invisible) instead of fully transparent, so the rounded corners still catch the mouse for resizing
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            MinWidth = 170; MinHeight = 110;
            Width = Math.Max(MinWidth, s.Width); Height = Math.Max(MinHeight, s.Height);
            PlaceOnScreen();

            // ---- content (designed at 300 px wide, scaled by a Viewbox)
            var panel = new StackPanel { Width = 300, Margin = new Thickness(18, 14, 18, 16) };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            statusDot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Green), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            // dot with a faint ring that pulses like the other desktop widgets (opacity 1 -> 0.5, 2.6s)
            statusRing = new Ellipse { Width = 13, Height = 13, Fill = new SolidColorBrush(Color.FromArgb(0x38, Green.R, Green.G, Green.B)) };
            statusDot.Margin = new Thickness(0);
            var dotHolder = new Grid { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            dotHolder.Children.Add(statusRing);
            dotHolder.Children.Add(statusDot);
            statusDot.HorizontalAlignment = HorizontalAlignment.Center;
            var pulse = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.5, new Duration(TimeSpan.FromSeconds(1.3)))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            };
            dotHolder.BeginAnimation(UIElement.OpacityProperty, pulse);
            title.Children.Add(dotHolder);
            var titleText = Text("CLAUDE USAGE", 12.5, new SolidColorBrush(Green), FontWeights.SemiBold, TechFont);
            title.Children.Add(titleText);
            header.Children.Add(title);
            lockGlyph = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 10, Foreground = InkFaint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
            Grid.SetColumn(lockGlyph, 1);
            header.Children.Add(lockGlyph);
            statusText = Text("loading…", 10.5, InkFaint, FontWeights.Normal, SansFont);
            statusText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(statusText, 2);
            header.Children.Add(statusText);
            panel.Children.Add(header);

            panel.Children.Add(BuildLimitRow("Current session · 5h", out fivePct, out fiveSub, out fiveBar, out fiveFill, out fiveRest, 14));
            panel.Children.Add(BuildLimitRow("Weekly · all models", out weekPct, out weekSub, out weekBar, out weekFill, out weekRest, 12));

            panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 14, 0, 10) });

            tokensGrid = new Grid { Background = Brushes.Transparent };
            tokensGrid.ColumnDefinitions.Add(new ColumnDefinition());
            tokensGrid.ColumnDefinitions.Add(new ColumnDefinition());
            tokensGrid.Children.Add(BuildStat("Tokens today", out todayVal, 0));
            tokensGrid.Children.Add(BuildStat("Tokens all time", out totalVal, 1));
            panel.Children.Add(tokensGrid);

            var view = new Viewbox { Stretch = Stretch.Uniform, Child = panel, VerticalAlignment = VerticalAlignment.Center };
            view.Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black };

            // resize grip (bottom-right), hidden while locked
            grip = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 10,1 L 1,10 M 10,5 L 5,10 M 10,9 L 9,10"),
                Stroke = InkFaint, StrokeThickness = 1.2,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 6), IsHitTestVisible = false
            };

            var layer = new Grid();
            layer.Children.Add(view);
            layer.Children.Add(grip);

            chrome = new Border
            {
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
                Child = layer
            };
            ApplyTint();
            Content = chrome;

            BuildContextMenu();
            UpdateLockVisual();

            PreviewMouseLeftButtonDown += OnMouseDown;
            PreviewMouseMove += OnMouseMove;
            PreviewMouseLeftButtonUp += OnMouseUp;
            LostMouseCapture += delegate { EndResize(); };
            MouseLeave += delegate { if (resizeEdge == Edge.None) Cursor = null; };
            LocationChanged += delegate { SaveBounds(); };

            SourceInitialized += OnSourceInitialized;
            Loaded += delegate { SendToBottom(); Refresh(true); };

            var refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            refreshTimer.Tick += delegate { Refresh(false); };
            refreshTimer.Start();

            // keeps "x min ago" / reset countdowns fresh, persists bounds, and un-hides if anything hid us
            var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            tick.Tick += delegate
            {
                Render();
                if (settingsDirty) { settingsDirty = false; settings.Save(); }
                if (hwnd != IntPtr.Zero && Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 4 /*SW_SHOWNOACTIVATE*/);
                if (!IsVisible) Show();
                EnsureDesktopOwner();
            };
            tick.Start();

            SetupTray();
        }

        void PlaceOnScreen()
        {
            var wa = SystemParameters.WorkArea;
            var vl = SystemParameters.VirtualScreenLeft; var vt = SystemParameters.VirtualScreenTop;
            var vw = SystemParameters.VirtualScreenWidth; var vh = SystemParameters.VirtualScreenHeight;
            bool valid = !double.IsNaN(settings.Left) && !double.IsNaN(settings.Top)
                && settings.Left > vl - Width + 40 && settings.Left < vl + vw - 40
                && settings.Top > vt - 10 && settings.Top < vt + vh - 40;
            if (valid) { Left = settings.Left; Top = settings.Top; }
            else { Left = wa.Right - Width - 24; Top = wa.Top + 24; }
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        static TextBlock Text(string s, double size, Brush b, FontWeight w, FontFamily f)
        {
            return new TextBlock { Text = s, FontSize = size, Foreground = b, FontWeight = w, FontFamily = f };
        }

        UIElement BuildLimitRow(string label, out TextBlock pct, out TextBlock sub, out Border bar, out ColumnDefinition fill, out ColumnDefinition rest, double topMargin)
        {
            var sp = new StackPanel { Margin = new Thickness(0, topMargin, 0, 0) };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = Text(label, 11.5, InkSoft, FontWeights.Normal, SansFont);
            lbl.VerticalAlignment = VerticalAlignment.Bottom;
            top.Children.Add(lbl);
            pct = Text("—", 14, Ink, FontWeights.SemiBold, TechFont);
            Grid.SetColumn(pct, 1);
            top.Children.Add(pct);
            sp.Children.Add(top);

            var track = new Grid { Height = 6, Margin = new Thickness(0, 5, 0, 4) };
            fill = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
            rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            track.ColumnDefinitions.Add(fill);
            track.ColumnDefinitions.Add(rest);
            var bg = new Border { CornerRadius = new CornerRadius(3), Background = TrackBrush };
            Grid.SetColumnSpan(bg, 2);
            track.Children.Add(bg);
            bar = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Green) };
            track.Children.Add(bar);
            sp.Children.Add(track);

            sub = Text(" ", 10, InkFaint, FontWeights.Normal, SansFont);
            sp.Children.Add(sub);
            return sp;
        }

        UIElement BuildStat(string label, out TextBlock val, int col)
        {
            var sp = new StackPanel();
            sp.Children.Add(Text(label, 10.5, InkFaint, FontWeights.Normal, SansFont));
            val = Text("—", 21, Ink, FontWeights.SemiBold, TechFont);
            val.Margin = new Thickness(0, 1, 0, 0);
            sp.Children.Add(val);
            Grid.SetColumn(sp, col);
            return sp;
        }

        // ---------------------------------------------------------------- move & resize
        Edge HitEdge(Point p)
        {
            const double band = 10, corner = 18;
            double w = ActualWidth, h = ActualHeight;
            var e = Edge.None;
            if (p.X < band) e |= Edge.Left; else if (p.X > w - band) e |= Edge.Right;
            if (p.Y < band) e |= Edge.Top; else if (p.Y > h - band) e |= Edge.Bottom;
            if (p.X > w - corner && p.Y > h - corner) e = Edge.Right | Edge.Bottom; // generous grip corner
            return e;
        }

        static Cursor CursorFor(Edge e)
        {
            if (e == (Edge.Left | Edge.Top) || e == (Edge.Right | Edge.Bottom)) return Cursors.SizeNWSE;
            if (e == (Edge.Right | Edge.Top) || e == (Edge.Left | Edge.Bottom)) return Cursors.SizeNESW;
            if (e == Edge.Left || e == Edge.Right) return Cursors.SizeWE;
            if (e == Edge.Top || e == Edge.Bottom) return Cursors.SizeNS;
            return null;
        }

        Point CursorDip()
        {
            Native.POINT p; Native.GetCursorPos(out p);
            var src = PresentationSource.FromVisual(this);
            var pt = new Point(p.X, p.Y);
            return src != null ? src.CompositionTarget.TransformFromDevice.Transform(pt) : pt;
        }

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (settings.Locked) return;
            var edge = HitEdge(e.GetPosition(this));
            if (edge != Edge.None)
            {
                resizeEdge = edge;
                resizeStartMouse = CursorDip();
                resizeStartRect = new Rect(Left, Top, ActualWidth, ActualHeight);
                CaptureMouse();
                e.Handled = true;
                return;
            }
            try { DragMove(); } catch { }
            SaveBounds();
            e.Handled = true;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (resizeEdge == Edge.None)
            {
                Cursor = settings.Locked ? null : (CursorFor(HitEdge(e.GetPosition(this))) ?? Cursors.SizeAll);
                return;
            }
            var cur = CursorDip();
            double dx = cur.X - resizeStartMouse.X, dy = cur.Y - resizeStartMouse.Y;
            var r = resizeStartRect;
            double left = r.Left, top = r.Top, w = r.Width, h = r.Height;
            if ((resizeEdge & Edge.Right) != 0) w = Math.Max(MinWidth, r.Width + dx);
            if ((resizeEdge & Edge.Bottom) != 0) h = Math.Max(MinHeight, r.Height + dy);
            if ((resizeEdge & Edge.Left) != 0) { w = Math.Max(MinWidth, r.Width - dx); left = r.Right - w; }
            if ((resizeEdge & Edge.Top) != 0) { h = Math.Max(MinHeight, r.Height - dy); top = r.Bottom - h; }
            if (left != Left) Left = left;
            if (top != Top) Top = top;
            Width = w; Height = h;
        }

        void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (resizeEdge != Edge.None) { ReleaseMouseCapture(); e.Handled = true; }
        }

        void EndResize()
        {
            if (resizeEdge == Edge.None) return;
            resizeEdge = Edge.None;
            SaveBounds();
        }

        // ---------------------------------------------------------------- window plumbing
        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            // tool window: no taskbar button, not in Alt+Tab
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        }

        IntPtr desktopOwner = IntPtr.Zero;
        public bool ExitRequested;

        public void AttachToDesktop()
        {
            // Being owned by the desktop window (Progman) keeps us visible through Win+D / "Show desktop".
            var progman = Native.FindWindow("Progman", null);
            desktopOwner = progman;
            if (progman != IntPtr.Zero) new WindowInteropHelper(this).Owner = progman;
        }

        // Explorer can recreate the desktop window (at sign-in or when explorer.exe restarts).
        // Re-own ourselves to the new one so we keep surviving Win+D.
        void EnsureDesktopOwner()
        {
            if (hwnd == IntPtr.Zero) return;
            var progman = Native.FindWindow("Progman", null);
            if (progman == IntPtr.Zero || progman == desktopOwner) return;
            Native.SetWindowLongPtr(hwnd, -8 /*GWLP_HWNDPARENT*/, progman);
            desktopOwner = progman;
            Program.Log("re-attached to new desktop window");
            SendToBottom();
        }

        void SendToBottom()
        {
            if (hwnd != IntPtr.Zero)
                Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_WINDOWPOSCHANGING)
            {
                // stay pinned to the desktop layer: never rise above other windows
                var wp = (Native.WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(Native.WINDOWPOS));
                if ((wp.flags & Native.SWP_NOZORDER) == 0)
                {
                    wp.hwndInsertAfter = Native.HWND_BOTTOM;
                    Marshal.StructureToPtr(wp, lParam, false);
                }
            }
            else if (msg == Native.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == Native.SC_MINIMIZE)
            {
                handled = true; // refuse to minimize
            }
            return IntPtr.Zero;
        }

        void SaveBounds()
        {
            if (WindowState != WindowState.Normal) return;
            settings.Left = Left; settings.Top = Top;
            settings.Width = ActualWidth > 0 ? ActualWidth : Width;
            settings.Height = ActualHeight > 0 ? ActualHeight : Height;
            settingsDirty = true;
        }

        void ApplyTint()
        {
            chrome.Background = new SolidColorBrush(Color.FromArgb((byte)settings.TintAlpha, 10, 13, 18));
        }

        void UpdateLockVisual()
        {
            lockGlyph.Visibility = settings.Locked ? Visibility.Visible : Visibility.Collapsed;
            grip.Visibility = settings.Locked ? Visibility.Collapsed : Visibility.Visible;
            if (settings.Locked) Cursor = null;
            if (lockItem != null) lockItem.IsChecked = settings.Locked;
            if (trayLock != null) trayLock.Checked = settings.Locked;
        }

        void SetLocked(bool locked)
        {
            settings.Locked = locked;
            settings.Save();
            UpdateLockVisual();
        }

        // ---------------------------------------------------------------- menus
        void BuildContextMenu()
        {
            var menu = new ContextMenu();
            lockItem = new MenuItem { Header = "Lock position & size", IsCheckable = true, IsChecked = settings.Locked };
            lockItem.Click += delegate { SetLocked(lockItem.IsChecked); };
            menu.Items.Add(lockItem);

            var refresh = new MenuItem { Header = "Refresh now" };
            refresh.Click += delegate { Refresh(true); };
            menu.Items.Add(refresh);

            signInItem = new MenuItem { Header = "Sign in for live limits…" };
            signInItem.Click += delegate { LaunchSignIn(); };
            menu.Items.Add(signInItem);

            var tint = new MenuItem { Header = "Background tint" };
            foreach (var opt in new[] { new KeyValuePair<string, int>("Clear", 0x30), new KeyValuePair<string, int>("Light", 0x70), new KeyValuePair<string, int>("Medium", 0x9E), new KeyValuePair<string, int>("Dark", 0xD0) })
            {
                var alpha = opt.Value;
                var mi = new MenuItem { Header = opt.Key, IsCheckable = true, IsChecked = settings.TintAlpha == alpha, Tag = alpha };
                mi.Click += delegate
                {
                    settings.TintAlpha = alpha; settings.Save(); ApplyTint();
                    foreach (var t in tintItems) t.IsChecked = (int)t.Tag == alpha;
                };
                tintItems.Add(mi);
                tint.Items.Add(mi);
            }
            menu.Items.Add(tint);

            var resetSize = new MenuItem { Header = "Reset size" };
            resetSize.Click += delegate { Width = 300; Height = 200; SaveBounds(); };
            menu.Items.Add(resetSize);

            startupItem = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = IsStartupEnabled() };
            startupItem.Click += delegate { SetStartup(startupItem.IsChecked); startupItem.IsChecked = IsStartupEnabled(); };
            menu.Items.Add(startupItem);

            menu.Items.Add(new Separator());
            var exit = new MenuItem { Header = "Exit" };
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);

            menu.Opened += delegate
            {
                startupItem.IsChecked = IsStartupEnabled();
                signInItem.Header = LimitFetcher.HasLogin() ? "Sign in again (live limits)…" : "Sign in for live limits…";
            };
            ContextMenu = menu;
        }

        static string FindClaudeCli()
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(';'))
            {
                try
                {
                    foreach (var name in new[] { "claude.exe", "claude.cmd" })
                    {
                        var p = System.IO.Path.Combine(dir.Trim(), name);
                        if (File.Exists(p)) return p;
                    }
                }
                catch { }
            }
            var bundled = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code");
            if (Directory.Exists(bundled))
            {
                var exe = Directory.GetFiles(bundled, "claude.exe", SearchOption.AllDirectories)
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
                if (exe != null) return exe;
            }
            return null;
        }

        void LaunchSignIn()
        {
            var cli = FindClaudeCli();
            if (cli == null)
            {
                MessageBox.Show("Couldn't find Claude Code. Install it, then run:  claude auth login", "Claude usage widget");
                return;
            }
            try
            {
                // One-time browser sign-in; Claude Code stores the login in ~/.claude/.credentials.json,
                // which this widget then uses for the free usage endpoint.
                Process.Start(new ProcessStartInfo("cmd.exe", "/k \"\"" + cli + "\" auth login && echo. && echo Signed in. You can close this window.\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Claude usage widget"); }
        }

        void SetupTray()
        {
            var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using (var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x5E, 0xE7, 0x9B)))
                    g.FillEllipse(br, 3, 3, 26, 26);
            }
            tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon()),
                Text = "Claude usage widget",
                Visible = true,
                ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
            };
            trayLock = new System.Windows.Forms.ToolStripMenuItem("Lock position & size") { Checked = settings.Locked };
            trayLock.Click += delegate { Dispatcher.Invoke(new Action(() => SetLocked(!settings.Locked))); };
            tray.ContextMenuStrip.Items.Add(trayLock);
            tray.ContextMenuStrip.Items.Add("Refresh now", null, delegate { Dispatcher.Invoke(new Action(() => Refresh(true))); });
            tray.ContextMenuStrip.Items.Add("Sign in for live limits…", null, delegate { Dispatcher.Invoke(new Action(LaunchSignIn)); });
            tray.ContextMenuStrip.Items.Add("Move to top-right corner", null, delegate
            {
                Dispatcher.Invoke(new Action(() => { var wa = SystemParameters.WorkArea; Left = wa.Right - ActualWidth - 24; Top = wa.Top + 24; SaveBounds(); }));
            });
            tray.ContextMenuStrip.Items.Add("-");
            tray.ContextMenuStrip.Items.Add("Exit", null, delegate { Dispatcher.Invoke(new Action(ExitApp)); });
        }

        void ExitApp()
        {
            ExitRequested = true;
            SaveBounds(); settings.Save();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Application.Current.Shutdown();
        }

        // Autostart uses a shortcut in the user's Startup folder (same as the other desktop widgets);
        // the Run registry entry proved unreliable.
        static string StartupShortcut
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Claude Usage Widget.lnk"); }
        }

        public static bool IsStartupEnabled()
        {
            return File.Exists(StartupShortcut);
        }

        static void RemoveLegacyRunEntry()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null) k.DeleteValue(RunName, false);
            }
            catch { }
        }

        public static void SetStartup(bool on)
        {
            RemoveLegacyRunEntry();
            try
            {
                if (!on) { if (File.Exists(StartupShortcut)) File.Delete(StartupShortcut); return; }
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(shellType);
                var lnk = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { StartupShortcut });
                var t = lnk.GetType();
                t.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { exe });
                t.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "--startup" });
                t.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { System.IO.Path.GetDirectoryName(exe) });
                t.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "Claude usage desktop widget" });
                t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.FinalReleaseComObject(lnk);
                Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception ex) { Program.Log("startup shortcut failed: " + ex.Message); }
        }

        // true if an older build registered startup via the Run key
        public static bool HasLegacyRunEntry()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- data refresh & rendering
        void Refresh(bool force)
        {
            if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
            bool fetchLimits = force || (DateTime.Now - lastLimitFetch).TotalSeconds >= LimitPollSeconds - 5;
            if (fetchLimits) lastLimitFetch = DateTime.Now;
            if (force) statusText.Text = "refreshing…";
            Task.Run(() =>
            {
                LimitInfo limits = null; TokenTotals today = null, all = null;
                if (fetchLimits)
                {
                    try { limits = LimitFetcher.Fetch(); } catch (Exception ex) { limits = new LimitInfo { Error = ex.Message }; }
                }
                try { scanner.Scan(); today = scanner.Today(); all = scanner.AllTime(); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (limits != null) lastLimits = limits;
                    if (today != null) { lastToday = today; lastTotal = all; }
                    Render();
                    Interlocked.Exchange(ref refreshing, 0);
                }));
            });
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + "h ago";
            return (int)d.TotalDays + "d ago";
        }

        static string Until(DateTime t)
        {
            var d = t - DateTime.Now;
            if (d.TotalSeconds <= 0) return "Resetting now";
            if (d.TotalHours < 24) return "Resets in " + (int)d.TotalHours + " hr " + d.Minutes + " min";
            return "Resets " + t.ToString("ddd h:mm tt", CultureInfo.CurrentCulture);
        }

        static string Fmt(long n)
        {
            if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
            if (n < 1000000) return (n / 1000.0).ToString(n < 10000 ? "0.0" : "0", CultureInfo.InvariantCulture) + "K";
            if (n < 1000000000) return (n / 1e6).ToString(n < 100000000 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + "M";
            return (n / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + "B";
        }

        static string Breakdown(string title, TokenTotals t)
        {
            return title + "\n" +
                "Input:        " + t.Input.ToString("N0") + "\n" +
                "Output:       " + t.Output.ToString("N0") + "\n" +
                "Cache write:  " + t.CacheWrite.ToString("N0") + "\n" +
                "Cache read:   " + t.CacheRead.ToString("N0") + "\n" +
                "Total:        " + t.Total.ToString("N0");
        }

        static void SetBar(double? used, TextBlock pct, Border bar, ColumnDefinition fill, ColumnDefinition rest)
        {
            if (!used.HasValue)
            {
                pct.Text = "—";
                fill.Width = new GridLength(0, GridUnitType.Star); rest.Width = new GridLength(1, GridUnitType.Star);
                return;
            }
            double left = Math.Max(0, Math.Min(100, 100 - used.Value));
            pct.Text = Math.Round(left).ToString(CultureInfo.InvariantCulture) + "% left";
            fill.Width = new GridLength(left, GridUnitType.Star);
            rest.Width = new GridLength(100 - left, GridUnitType.Star);
            // green until under 30% left, then amber, red under 15%
            Color c = left >= 30 ? Green : left >= 15 ? Amber : Red;
            bar.Background = new SolidColorBrush(c);
            pct.Foreground = left >= 30 ? Ink : new SolidColorBrush(c);
        }

        static string SubLine(double? used, DateTime? reset)
        {
            if (!used.HasValue) return " ";
            var s = Math.Round(used.Value).ToString(CultureInfo.InvariantCulture) + "% used";
            return reset.HasValue ? s + "  ·  " + Until(reset.Value) : s;
        }

        void Render()
        {
            var l = lastLimits;
            if (l != null)
            {
                SetBar(l.FiveHourUsed, fivePct, fiveBar, fiveFill, fiveRest);
                SetBar(l.WeeklyUsed, weekPct, weekBar, weekFill, weekRest);
                fiveSub.Text = SubLine(l.FiveHourUsed, l.FiveHourReset);
                weekSub.Text = SubLine(l.WeeklyUsed, l.WeeklyReset);

                bool stale = !l.Live || !l.AsOf.HasValue || (DateTime.Now - l.AsOf.Value).TotalMinutes > 10;
                if (l.Live && l.AsOf.HasValue) statusText.Text = "live · " + Ago(l.AsOf.Value);
                else if (l.AsOf.HasValue) statusText.Text = "app snapshot · " + Ago(l.AsOf.Value);
                else statusText.Text = "no limit data";
                statusText.Foreground = stale ? new SolidColorBrush(Amber) : InkFaint;
                var dc = stale ? Amber : Green; statusDot.Fill = new SolidColorBrush(dc); statusRing.Fill = new SolidColorBrush(Color.FromArgb(0x38, dc.R, dc.G, dc.B));
                if (!l.Live) fiveSub.Text = "Right-click → Sign in for live limits";

                var tip = "Limits source: " + (string.IsNullOrEmpty(l.Source) ? "none" : l.Source);
                if (l.AsOf.HasValue) tip += "\nAs of: " + l.AsOf.Value.ToString("g");
                if (!l.Live) tip += "\nThe Claude app only saves snapshots occasionally, so this may be out of date.\nRight-click → \"Sign in for live limits…\" for live numbers and reset times.";
                if (!string.IsNullOrEmpty(l.Error)) tip += "\n" + l.Error;
                tip += "\nNo model calls are made: refreshing costs 0 tokens.";
                statusText.ToolTip = tip;
            }
            if (lastToday != null)
            {
                todayVal.Text = Fmt(lastToday.Total);
                totalVal.Text = Fmt(lastTotal.Total);
                tokensGrid.ToolTip = Breakdown("Today", lastToday) + "\n\n" + Breakdown("All time", lastTotal) + "\n\nCounted from local Claude Code / Cowork session logs.";
            }
        }
    }

    // ------------------------------------------------------------------ entry point
    static class Program
    {
        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var path = System.IO.Path.Combine(Settings.Dir, "widget.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        static void Relaunch()
        {
            try { Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, "--restart") { UseShellExecute = false }); }
            catch (Exception ex) { Log("relaunch failed: " + ex.Message); }
        }

        // At sign-in, Run-key apps can start before Explorer has built the desktop and taskbar.
        static void WaitForShell(int maxSeconds)
        {
            var until = DateTime.Now.AddSeconds(maxSeconds);
            while (DateTime.Now < until)
            {
                if (Native.FindWindow("Progman", null) != IntPtr.Zero && Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) break;
                Thread.Sleep(1000);
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool atStartup = args.Contains("--startup");
            bool isRestart = args.Contains("--restart");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log("FATAL: " + e.ExceptionObject);
                Relaunch();
            };

            bool created;
            using (var mutex = new Mutex(true, "Local\\ClaudeUsageWidget_SingleInstance", out created))
            {
                if (!created)
                {
                    if (!isRestart) return;
                    try { if (!mutex.WaitOne(15000)) return; } catch (AbandonedMutexException) { }
                }
                Log("starting" + (atStartup ? " (sign-in)" : "") + (isRestart ? " (restart)" : ""));

                WaitForShell(atStartup || isRestart ? 120 : 5);
                if (atStartup) Thread.Sleep(4000); // let Explorer finish settling the desktop

                var settings = Settings.Load();
                if (!settings.FirstRunDone)
                {
                    WidgetWindow.SetStartup(true); // start with Windows by default
                    settings.FirstRunDone = true;
                    settings.Save();
                }
                else if (WidgetWindow.IsStartupEnabled() || WidgetWindow.HasLegacyRunEntry())
                {
                    WidgetWindow.SetStartup(true); // (re)create the Startup-folder shortcut, drop the old Run entry
                }

                bool sessionEnding = false;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { Log("UI error: " + e.Exception); e.Handled = true; };
                app.SessionEnding += (s, e) => { sessionEnding = true; Log("Windows is shutting down / signing out"); };
                var w = new WidgetWindow(settings);
                // If Windows/Explorer destroys our window (e.g. the desktop it was attached to went away),
                // start a fresh copy instead of silently vanishing - but never while Windows is shutting down.
                w.Closed += delegate
                {
                    if (w.ExitRequested) return;
                    if (sessionEnding || Native.GetSystemMetrics(0x2000 /*SM_SHUTTINGDOWN*/) != 0)
                    {
                        app.Shutdown();
                        return;
                    }
                    Log("window was closed externally; relaunching");
                    Relaunch();
                    app.Shutdown();
                };
                w.AttachToDesktop();
                w.Show();
                app.Run();
                Log("exited");
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }
}
