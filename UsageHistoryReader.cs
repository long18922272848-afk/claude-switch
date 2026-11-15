using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    sealed class UsageHistoryResult
    {
        public UsageSnapshot Snapshot;
        public string Message;
    }

    // Reads only the desktop's public-shaped local usage history and directory names.
    // This is a historical observation, never a live API request or a reset-time estimate.
    // No cookies, bearer tokens, credential files or task transcripts are accessed.
    static class UsageHistoryReader
    {
        const int MaxBytes = 4 * 1024 * 1024;
        static readonly DateTimeOffset Epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public static UsageHistoryResult Read(Engine engine, Profile profile)
        {
            return Read(profile, () => engine.ActiveAccount(profile), DateTimeOffset.UtcNow);
        }

        internal static UsageHistoryResult Read(Profile profile, Func<string> activeAccount, DateTimeOffset now)
        {
            try
            {
                string account = activeAccount();
                Guid id;
                if (string.IsNullOrEmpty(account) || !Guid.TryParseExact(account, "D", out id))
                    return Unavailable("账号尚未登录，无法关联额度记录");
                string org = UniqueOrganization(profile.Dir, account);
                if (org == null) return Unavailable("无法唯一关联当前账号与额度记录");
                string path = Path.Combine(profile.Dir, "plan-usage-history.json");
                if (!File.Exists(path)) return Unavailable("Claude 尚未保存此账号的额度记录");
                EnsureNoLinks(profile.Dir, path);
                string text = ReadBounded(path);
                var json = new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 32 };
                var root = json.DeserializeObject(text) as Dictionary<string, object>;
                object value;
                if (root == null || !root.TryGetValue("version", out value) || !(value is int) || (int)value != 2)
                    return Unavailable("Claude 额度记录格式不兼容");
                if (!root.TryGetValue("samples", out value) || !(value is object[]))
                    return Unavailable("Claude 额度记录缺少采样数据");
                object[] samples = (object[])value;
                if (samples.Length > 20000) return Unavailable("Claude 额度记录过大，暂不读取");
                Dictionary<string, object> latest = null;
                double latestTimestamp = -1;
                bool conflictingTimestamp = false;
                foreach (object item in samples)
                {
                    var sample = item as Dictionary<string, object>;
                    if (sample == null || !sample.TryGetValue("org", out value) ||
                        !string.Equals(value as string, org, StringComparison.OrdinalIgnoreCase)) continue;
                    double timestamp;
                    if (!sample.TryGetValue("t", out value) || !Number(value, out timestamp) ||
                        timestamp <= 0 || timestamp != Math.Floor(timestamp) || timestamp > (now - Epoch).TotalMilliseconds)
                        return Unavailable("Claude 额度记录时间无效，等待有效采样");
                    if (timestamp > latestTimestamp)
                    {
                        latestTimestamp = timestamp; latest = sample; conflictingTimestamp = false;
                    }
                    else if (timestamp == latestTimestamp && json.Serialize(sample) != json.Serialize(latest)) conflictingTimestamp = true;
                }
                if (latest == null) return Unavailable("暂无可确认属于当前账号的额度记录");
                if (conflictingTimestamp) return Unavailable("Claude 同一时刻额度记录冲突，暂不读取");
                if (!latest.TryGetValue("u", out value) || !(value is Dictionary<string, object>))
                    return Unavailable("Claude 最新额度记录不完整");
                var utilization = (Dictionary<string, object>)value;
                var normalized = new Dictionary<string, object>();
                // Do not backfill missing windows from older observations: a window may have reset.
                AddWindow(utilization, normalized, "fh", "five_hour");
                AddWindow(utilization, normalized, "sd", "seven_day");
                AddWindow(utilization, normalized, "sn", "seven_day_sonnet");
                AddWindow(utilization, normalized, "so", "seven_day_opus");
                if (normalized.Count == 0) return Unavailable("Claude 最新记录没有可读取的额度百分比");
                UsageSnapshot snapshot = UsageParser.Parse(json.Serialize(normalized), profile.Id, account,
                    UsageSource.DesktopHistory, Epoch.AddMilliseconds(latestTimestamp));
                // A sign-in or directory association change invalidates the whole observation.
                if (!string.Equals(activeAccount(), account, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(UniqueOrganization(profile.Dir, account), org, StringComparison.OrdinalIgnoreCase))
                    return Unavailable("账号正在变更，请稍后重新读取额度");
                return new UsageHistoryResult { Snapshot = snapshot,
                    Message = "已读取 Claude 本地历史采样；重置时间未保存在此记录中" };
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException) &&
                    !(ex is InvalidOperationException) && !(ex is FormatException) && !(ex is OverflowException)) throw;
                // Do not expose raw paths, identities or JSON/parser exception contents.
                return Unavailable("Claude 额度记录暂时无法读取");
            }
        }

        static UsageHistoryResult Unavailable(string message)
        { return new UsageHistoryResult { Message = message }; }

        static void AddWindow(Dictionary<string, object> from, Dictionary<string, object> to, string shortKey, string key)
        {
            object value; double number;
            if (!from.TryGetValue(shortKey, out value)) return;
            if (!Number(value, out number) || number < 0 || number > 100)
                throw new FormatException("Invalid usage percentage");
            to[key] = new Dictionary<string, object> { { "utilization", number }, { "resets_at", null } };
        }

        static bool Number(object value, out double number)
        {
            number = 0;
            if (!(value is int) && !(value is long) && !(value is decimal) && !(value is double)) return false;
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }

        static string UniqueOrganization(string profileDirectory, string account)
        {
            string root = Path.Combine(profileDirectory, @"local-agent-mode-sessions\skills-plugin");
            if (!Directory.Exists(root)) return null;
            EnsureNoLinks(profileDirectory, root);
            string[] orgDirectories = Directory.EnumerateDirectories(root).Take(65).ToArray();
            if (orgDirectories.Length > 64) return null;
            string found = null;
            foreach (string directory in orgDirectories)
            {
                Guid orgId;
                if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out orgId)) continue;
                EnsureNoLinks(profileDirectory, directory);
                string[] accounts = Directory.EnumerateDirectories(directory).Take(33).ToArray();
                if (accounts.Length > 32) return null;
                var ids = accounts.Where(p => { Guid a; return Guid.TryParseExact(Path.GetFileName(p), "D", out a); }).ToArray();
                string match = ids.FirstOrDefault(p => string.Equals(Path.GetFileName(p), account, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                // A shared organization could represent another account's historical usage.
                if (ids.Length != 1 || found != null) return null;
                EnsureNoLinks(profileDirectory, match);
                string manifest = Path.Combine(match, "manifest.json");
                if (!File.Exists(manifest)) return null;
                EnsureNoLinks(profileDirectory, manifest);
                found = Path.GetFileName(directory);
            }
            return found;
        }

        static void EnsureNoLinks(string profileDirectory, string path)
        {
            string root = Path.GetFullPath(profileDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string item = Path.GetFullPath(path);
            if (root.StartsWith(@"\\", StringComparison.Ordinal) ||
                !(string.Equals(item, root, StringComparison.OrdinalIgnoreCase) ||
                  item.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Invalid history location");
            while (item != null)
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked history path");
                if (string.Equals(item, root, StringComparison.OrdinalIgnoreCase)) break;
                item = Path.GetDirectoryName(item);
            }
        }

        static string ReadBounded(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxBytes) throw new IOException("History is too large");
                byte[] bytes = new byte[MaxBytes + 1];
                int total = 0, read;
                while (total < bytes.Length && (read = stream.Read(bytes, total, bytes.Length - total)) > 0) total += read;
                if (total > MaxBytes) throw new IOException("History grew too large");
                return new UTF8Encoding(false, true).GetString(bytes, 0, total).TrimStart('\uFEFF');
            }
        }
    }
}
