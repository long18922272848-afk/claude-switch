using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeAccountSync
{
    sealed class OfficeDesk
    {
        public string Key, Name, ProfileId;
        public DateTime? Started, Ended;
        public bool Archived { get { return Ended.HasValue; } }
        internal OfficeDesk Copy() { return (OfficeDesk)MemberwiseClone(); }

        public int? UsageDays(DateTime today)
        {
            if (!Started.HasValue) return null;
            int days = ((Ended ?? today).Date - Started.Value.Date).Days + 1;
            return days > 0 ? (int?)days : null;
        }
    }

    static class OfficeHistory
    {
        public const string Field = "officeDesks";
        const string DayFormat = "yyyy-MM-dd";
        static readonly Regex LegacyEvent = new Regex(
            @"^(?<day>\d{4}-\d{2}-\d{2}) \d{2}:\d{2}:\d{2}  (?:added account (?<name>.+) at (?<path>.+)|removed from list: (?<name>.+) \(folder kept: (?<path>.+)\))$",
            RegexOptions.CultureInvariant);
        static readonly Regex GeneratedId = new Regex(@"^p-[0-9a-f]{8}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static string Hash(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        static string DeskKey(string profileId) { return Hash("office-desk:" + profileId); }
        static string Label(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "未命名账号";
            if (EmailPrivacy.ContainsEmail(text) || text.Contains("@")) return EmailPrivacy.Mask;
            return text.Trim();
        }

        static DateTime? ReadDay(string value)
        {
            DateTime day;
            return DateTime.TryParseExact(value, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day) ? (DateTime?)day : null;
        }

        static OfficeDesk Get(Dictionary<string, OfficeDesk> desks, string id)
        {
            string key = DeskKey(id);
            OfficeDesk desk;
            if (!desks.TryGetValue(key, out desk)) desks.Add(key, desk = new OfficeDesk { Key = key });
            return desk;
        }

        // Accept only the application's lifecycle messages and its own generated child paths.
        // A desk keeps an opaque local key, a label and dates; never copy account UUIDs, paths or sessions.
        static void ReadLegacy(string logPath, string profilesDir, Dictionary<string, OfficeDesk> desks)
        {
            if (!File.Exists(logPath)) return;
            string root = Path.GetFullPath(profilesDir).TrimEnd(Path.DirectorySeparatorChar);
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    Match match = LegacyEvent.Match(line);
                    if (!match.Success) continue;
                    DateTime? day = ReadDay(match.Groups["day"].Value);
                    string path = match.Groups["path"].Value;
                    if (!day.HasValue || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    string id = path.Substring(root.Length + 1);
                    if (!GeneratedId.IsMatch(id)) continue;
                    var desk = Get(desks, id.ToLowerInvariant());
                    desk.Name = Label(match.Groups["name"].Value);
                    if (line.IndexOf("  added account ", StringComparison.Ordinal) == 19)
                    {
                        if (!desk.Started.HasValue || day < desk.Started) desk.Started = day;
                        desk.Ended = null;
                    }
                    else desk.Ended = day;
                }
            }
        }

        public static List<OfficeDesk> Read(Dictionary<string, object> store, string logPath, string profilesDir,
                                            List<Profile> active, Profile added, Profile removed, DateTime today, DateTime? defaultStarted)
        {
            var desks = new Dictionary<string, OfficeDesk>(StringComparer.Ordinal);
            object rows;
            if (store.TryGetValue(Field, out rows))
            {
                // A malformed ledger must not be silently replaced with an empty history.
                foreach (Dictionary<string, object> row in (IEnumerable)rows)
                {
                    var desk = new OfficeDesk { Key = (string)row["key"], Name = Label((string)row["name"]),
                        Started = ReadDay((string)row["start"]), Ended = ReadDay((string)row["end"]) };
                    desks.Add(desk.Key, desk);
                }
            }
            else ReadLegacy(logPath, profilesDir, desks);

            if (removed != null)
            {
                OfficeDesk desk = Get(desks, removed.Id);
                desk.Name = Label(removed.Name);
                desk.Ended = today;
            }
            foreach (var p in active)
            {
                OfficeDesk desk = Get(desks, p.Id);
                desk.ProfileId = p.Id;
                desk.Name = Label(p.Name);
                desk.Ended = null;
                if (added != null && added.Id == p.Id) desk.Started = today;
                if (p.Id == "default" && !desk.Started.HasValue) desk.Started = defaultStarted;
            }
            return desks.Values.Where(x => x.ProfileId != null || x.Archived)
                .OrderBy(x => x.Archived).ThenBy(x => x.Archived ? x.Ended : x.Started).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        }

        public static object Encode(IEnumerable<OfficeDesk> desks)
        {
            return desks.Select(d => new Dictionary<string, object> {
                { "key", d.Key }, { "name", d.Name },
                { "start", d.Started.HasValue ? d.Started.Value.ToString(DayFormat, CultureInfo.InvariantCulture) : null },
                { "end", d.Ended.HasValue ? d.Ended.Value.ToString(DayFormat, CultureInfo.InvariantCulture) : null }
            }).ToList();
        }
    }
}
