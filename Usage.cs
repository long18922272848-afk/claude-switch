using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    enum UsageSource { Unknown, ClaudeApi, CodeStatusline, DesktopUi, DesktopHistory }

    sealed class UsageWindow
    {
        public string Key, Label, ResetText;
        public double? UsedPercent;
        public DateTimeOffset? ResetsAt;
        // A partial live read may refresh one limit while another remains an older observation.
        public DateTimeOffset? ObservedAt;
        public UsageSource? Source;
    }

    sealed class UsageSnapshot
    {
        public string ProfileId, AccountId;
        public UsageSource Source;
        public DateTimeOffset ObservedAt;
        public List<UsageWindow> Windows = new List<UsageWindow>();
        public UsageWindow Find(string key) { return Windows.FirstOrDefault(w => w.Key == key); }
        public UsageWindow FiveHour { get { return Find("five_hour"); } }
        public UsageWindow Weekly { get { return Find("seven_day"); } }
    }

    // Parsers keep only usage counters and reset times. Raw responses, prompts, email and tokens
    // are never stored here. The collector must independently verify the source account identity.
    static class UsageParser
    {
        internal const int MaximumJsonLength = 128 * 1024;
        static readonly Regex WindowKey = new Regex(@"\A(?:five_hour|seven_day(?:_[a-z0-9_]{1,40})?)\z");
        static readonly Regex ZonedIso = new Regex(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.IgnoreCase);
        static readonly DateTimeOffset Epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = MaximumJsonLength, RecursionLimit = 20 }; }
        internal static object Get(Dictionary<string, object> obj, string key)
        { object value; return obj != null && obj.TryGetValue(key, out value) ? value : null; }
        internal static bool Numeric(object value)
        { return value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal; }
        internal static double? Percent(object value)
        {
            if (!Numeric(value)) return null;
            double n = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsNaN(n) || double.IsInfinity(n) || n < 0 || n > 100 ? (double?)null : n;
        }
        internal static DateTimeOffset? Timestamp(object value)
        {
            if (Numeric(value))
            {
                double seconds = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                // Public statusline times use epoch seconds. Do not silently reinterpret milliseconds.
                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 946684800 || seconds >= 4102444800) return null;
                return Epoch.AddSeconds(seconds);
            }
            string s = value as string;
            DateTimeOffset parsed;
            if (s == null || !ZonedIso.IsMatch(s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed) || parsed.Year < 2000 || parsed.Year >= 2100) return null;
            return parsed.ToUniversalTime();
        }
        internal static string ResetPhrase(object value)
        {
            string text = value as string;
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.Length > 120 || text.IndexOf('@') >= 0 || text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)) return null;
            if (text == "从您发送第一条消息时开始计算") return text;
            if (text.IndexOf("reset", StringComparison.OrdinalIgnoreCase) < 0 && !text.Contains("重置") && !text.Contains("重設") && !text.Contains("重设")) return null;
            return text;
        }
        internal static string Label(string key)
        {
            if (key == "five_hour") return "5 小时";
            if (key == "seven_day") return "周额度";
            if (key == "seven_day_opus") return "Opus 周额度";
            if (key == "seven_day_sonnet") return "Sonnet 周额度";
            if (key == "seven_day_cowork") return "Cowork 周额度";
            if (key == "seven_day_oauth_apps") return "应用周额度";
            return key.Substring("seven_day_".Length) + " 周额度";
        }

        public static UsageSnapshot Parse(string json, string profileId, string accountId, UsageSource source, DateTimeOffset observedAt)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(accountId)) throw new ArgumentException("额度来源缺少账号标识。");
            if (source == UsageSource.Unknown || !Enum.IsDefined(typeof(UsageSource), source)) throw new ArgumentException("额度来源无效。");
            if (string.IsNullOrEmpty(json) || json.Length > MaximumJsonLength) throw new FormatException("额度数据为空或过大。");
            Dictionary<string, object> root;
            try { root = Json().DeserializeObject(json) as Dictionary<string, object>; }
            catch (Exception ex) { if (ex is ArgumentException || ex is InvalidOperationException) throw new FormatException("额度数据格式无效。"); throw; }
            if (root == null) throw new FormatException("额度数据格式无效。");
            if (root.ContainsKey("rate_limits")) root = Get(root, "rate_limits") as Dictionary<string, object>;
            if (root == null || !root.Keys.Any(k => WindowKey.IsMatch(k))) throw new FormatException("未找到可识别的额度窗口。");
            var snapshot = new UsageSnapshot { ProfileId = profileId, AccountId = accountId, Source = source, ObservedAt = observedAt.ToUniversalTime() };
            foreach (string key in root.Keys.Where(k => WindowKey.IsMatch(k)).OrderBy(k => k == "five_hour" ? 0 : k == "seven_day" ? 1 : 2).ThenBy(k => k))
            {
                var data = root[key] as Dictionary<string, object>;
                double? used = Percent(Get(data, "utilization")), statusline = Percent(Get(data, "used_percentage"));
                if (data != null && data.ContainsKey("utilization") && data.ContainsKey("used_percentage"))
                    used = used.HasValue && statusline.HasValue && used.Value == statusline.Value ? used : null;
                else if (data != null && !data.ContainsKey("utilization")) used = statusline;
                DateTimeOffset? windowTime = Timestamp(Get(data, "observed_at"));
                if (data != null && data.ContainsKey("observed_at") && Get(data, "observed_at") != null &&
                    (!windowTime.HasValue || windowTime.Value > snapshot.ObservedAt.AddSeconds(5))) throw new FormatException("额度窗口观测时间无效。");
                UsageSource? windowSource = null;
                object sourceValue = Get(data, "source");
                if (sourceValue != null)
                {
                    if (!(sourceValue is int) || !Enum.IsDefined(typeof(UsageSource), (int)sourceValue) || (int)sourceValue == 0) throw new FormatException("额度窗口来源无效。");
                    windowSource = (UsageSource)(int)sourceValue;
                }
                snapshot.Windows.Add(new UsageWindow { Key = key, Label = Label(key), UsedPercent = used,
                    ResetsAt = Timestamp(Get(data, "resets_at")), ResetText = ResetPhrase(Get(data, "reset_text")), ObservedAt = windowTime, Source = windowSource });
            }
            return snapshot;
        }
    }

    static class UsageDisplay
    {
        public const int FreshForSeconds = 120;
        public static bool IsFresh(UsageSnapshot snapshot, DateTimeOffset now)
        {
            if (snapshot == null) return false;
            double age = (now - snapshot.ObservedAt).TotalSeconds;
            return age >= -5 && age <= FreshForSeconds;
        }
        public static DateTimeOffset WindowObservedAt(UsageSnapshot snapshot, UsageWindow window)
        { return window != null && window.ObservedAt.HasValue ? window.ObservedAt.Value : snapshot == null ? DateTimeOffset.MinValue : snapshot.ObservedAt; }
        public static UsageSource WindowSource(UsageSnapshot snapshot, UsageWindow window)
        { return window != null && window.Source.HasValue ? window.Source.Value : snapshot == null ? UsageSource.Unknown : snapshot.Source; }
        public static bool IsWindowFresh(UsageSnapshot snapshot, UsageWindow window, DateTimeOffset now)
        {
            if (snapshot == null || window == null) return false;
            double age = (now - WindowObservedAt(snapshot, window)).TotalSeconds;
            return age >= -5 && age <= FreshForSeconds;
        }
        public static bool IsCurrent(UsageSnapshot snapshot, UsageWindow window, DateTimeOffset now)
        { return IsWindowFresh(snapshot, window, now) && window.UsedPercent.HasValue && UsageParser.Percent(window.UsedPercent.Value).HasValue && (!window.ResetsAt.HasValue || window.ResetsAt.Value > now); }
        // The main number and meter both use Claude's consumed percentage. Availability is
        // supplementary and can only be derived from the same current observation.
        public static double? CurrentUsedPercent(UsageSnapshot snapshot, UsageWindow window, DateTimeOffset now)
        { return IsCurrent(snapshot, window, now) && WindowSource(snapshot, window) != UsageSource.DesktopHistory ? window.UsedPercent : null; }
        public static double? RemainingPercent(UsageSnapshot snapshot, UsageWindow window, DateTimeOffset now)
        { double? used = CurrentUsedPercent(snapshot, window, now); return used.HasValue ? 100 - used.Value : (double?)null; }
        public static bool IsLimitReached(UsageSnapshot snapshot, UsageWindow window, DateTimeOffset now)
        { double? remaining = RemainingPercent(snapshot, window, now); return remaining.HasValue && remaining.Value == 0; }
        public static string ResetDescription(UsageWindow window, DateTimeOffset now)
        {
            if (window == null) return "重置未知";
            if (window.ResetsAt.HasValue) return window.ResetsAt.Value <= now ? "已到重置时间 · 待核验" :
                window.ResetsAt.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 重置";
            return UsageParser.ResetPhrase(window.ResetText) ?? "重置未知";
        }

        public static string MeterLine(UsageSnapshot snapshot, string key, DateTimeOffset now)
        {
            string label = UsageParser.Label(key);
            if (snapshot == null) return label + " · 未读取";
            UsageWindow window = snapshot.Find(key);
            if (window == null) return label + " · 未提供";
            if (window.ResetsAt.HasValue && window.ResetsAt.Value <= now) return label + " · 已到重置时间，等待刷新";
            double? used = CurrentUsedPercent(snapshot, window, now);
            string amount = window.UsedPercent.HasValue ? (used.HasValue ? "已用 " : "上次已用 ") +
                window.UsedPercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "未知";
            if (used.HasValue) amount += " · 剩余 " + (100 - used.Value).ToString("0.#", CultureInfo.InvariantCulture) + "%";
            string reset = ResetDescription(window, now);
            return label + " · " + amount + " · " + reset;
        }
        public static string Freshness(UsageSnapshot snapshot, DateTimeOffset now)
        {
            if (snapshot == null) return "尚未读取额度";
            string source = snapshot.Source == UsageSource.DesktopHistory ? "Claude 本地记录" :
                snapshot.Source == UsageSource.CodeStatusline ? "Code 状态栏" : snapshot.Source == UsageSource.DesktopUi ? "Claude 用量页面" : "Claude 用量接口";
            if (snapshot.Windows.Select(w => WindowSource(snapshot, w)).Distinct().Count() > 1) source = "Claude 页面 / 本地记录";
            DateTimeOffset oldest = snapshot.Windows.Count == 0 ? snapshot.ObservedAt : snapshot.Windows.Min(w => WindowObservedAt(snapshot, w));
            if (snapshot.ObservedAt > now.AddSeconds(5) || snapshot.Windows.Any(w => WindowObservedAt(snapshot, w) > now.AddSeconds(5))) return source + " · 时间异常，等待刷新";
            string when = oldest.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            bool resetPassed = snapshot.Windows.Any(w => w.ResetsAt.HasValue && w.ResetsAt.Value <= now);
            return source + " · " + when + (IsFresh(snapshot, now) && snapshot.Windows.All(w => IsWindowFresh(snapshot, w, now)) && !resetPassed ? " 读取" : " · 待刷新");
        }
    }

    static class UsageStore
    {
        static readonly object Gate = new object();
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static string CachePath(string stateDir, string profileId, string accountId)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(accountId)) throw new ArgumentException("额度缓存缺少账号标识。");
            // IDs never become path fragments. Keying by both avoids carrying old account usage into a re-login.
            using (var hash = SHA256.Create())
            {
                string key = BitConverter.ToString(hash.ComputeHash(Utf8.GetBytes(profileId + "\n" + accountId.ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
                return Path.Combine(stateDir, "usage", key + ".json");
            }
        }
        static void SafePath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0) throw new IOException("额度缓存路径无效。");
            for (string part = full; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            {
                try { if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("额度缓存路径包含链接。"); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        static string Serialize(UsageSnapshot snapshot)
        {
            var windows = new Dictionary<string, object>();
            foreach (UsageWindow w in snapshot.Windows)
                windows.Add(w.Key, new Dictionary<string, object> { { "utilization", w.UsedPercent },
                    { "resets_at", w.ResetsAt.HasValue ? w.ResetsAt.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : null },
                    { "reset_text", w.ResetText }, { "observed_at", w.ObservedAt.HasValue ? w.ObservedAt.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : null },
                    { "source", w.Source.HasValue ? (int?)w.Source.Value : null } });
            return UsageParser.Json().Serialize(new Dictionary<string, object> {
                { "version", 1 }, { "profile", snapshot.ProfileId }, { "account", snapshot.AccountId }, { "source", (int)snapshot.Source },
                { "observed_at", snapshot.ObservedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) }, { "windows", windows } });
        }
        public static UsageSnapshot Read(Engine engine, Profile profile)
        {
            if (profile == null) return null;
            string account = engine.ActiveAccount(profile);
            if (account == null) return null;
            UsageSnapshot snapshot = Read(engine.StateDir, profile.Id, account);
            return string.Equals(engine.ActiveAccount(profile), account, StringComparison.OrdinalIgnoreCase) ? snapshot : null;
        }
        internal static UsageSnapshot Read(string stateDir, string profileId, string accountId)
        {
            try
            {
                string path = CachePath(stateDir, profileId, accountId); SafePath(path);
                lock (Gate)
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > UsageParser.MaximumJsonLength) return null;
                    string text; using (var reader = new StreamReader(stream, Utf8, true)) text = reader.ReadToEnd();
                    var obj = UsageParser.Json().DeserializeObject(text) as Dictionary<string, object>;
                    if (obj == null || !Equals(UsageParser.Get(obj, "version"), 1) ||
                        !Equals(UsageParser.Get(obj, "profile"), profileId) ||
                        !string.Equals(UsageParser.Get(obj, "account") as string, accountId, StringComparison.OrdinalIgnoreCase)) return null;
                    var source = UsageParser.Get(obj, "source");
                    DateTimeOffset? time = UsageParser.Timestamp(UsageParser.Get(obj, "observed_at"));
                    if (!(source is int) || !time.HasValue) return null;
                    return UsageParser.Parse(UsageParser.Json().Serialize(UsageParser.Get(obj, "windows")), profileId, accountId, (UsageSource)(int)source, time.Value);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (FormatException) { return null; }
        }
        public static void Save(Engine engine, Profile profile, UsageSnapshot snapshot)
        {
            if (profile == null || snapshot == null || profile.Id != snapshot.ProfileId ||
                !string.Equals(engine.ActiveAccount(profile), snapshot.AccountId, StringComparison.OrdinalIgnoreCase))
                throw new IOException("额度所属账号已改变，未保存。");
            Save(engine.StateDir, snapshot);
        }
        internal static void Save(string stateDir, UsageSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            string text = Serialize(snapshot);
            // Reparse before publishing so malformed/invented fields never poison an existing valid snapshot.
            string path = CachePath(stateDir, snapshot.ProfileId, snapshot.AccountId); SafePath(path);
            var obj = UsageParser.Json().DeserializeObject(text) as Dictionary<string, object>;
            if (!UsageParser.Timestamp(UsageParser.Get(obj, "observed_at")).HasValue) throw new IOException("额度观测时间无效。");
            UsageSnapshot clean = UsageParser.Parse(UsageParser.Json().Serialize(UsageParser.Get(obj, "windows")), snapshot.ProfileId, snapshot.AccountId, snapshot.Source, snapshot.ObservedAt);
            text = Serialize(clean);
            lock (Gate)
            {
                UsageSnapshot previous = Read(stateDir, snapshot.ProfileId, snapshot.AccountId);
                if (previous != null && previous.ObservedAt > snapshot.ObservedAt) return;
                string directory = Path.GetDirectoryName(path); Directory.CreateDirectory(directory); SafePath(directory);
                string temp = Path.Combine(directory, ".usage-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    SafePath(temp);
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { byte[] bytes = Utf8.GetBytes(text); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    SafePath(path);
                    if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) { SafePath(temp); File.Delete(temp); } }
            }
        }
    }
}
