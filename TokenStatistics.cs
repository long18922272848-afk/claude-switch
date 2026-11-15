using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    class TokenTotals
    {
        public long Input, Output, CacheRead, CacheWrite;
        public long Total { get { return Input + Output + CacheRead + CacheWrite; } }
        internal void Add(TokenTotals other)
        { Input += other.Input; Output += other.Output; CacheRead += other.CacheRead; CacheWrite += other.CacheWrite; }
    }

    sealed class TokenDay : TokenTotals
    {
        public DateTime Date;
        public int SessionCount;
    }

    sealed class TokenStatisticsSnapshot
    {
        public DateTimeOffset UpdatedAt;
        public List<TokenDay> Days = new List<TokenDay>();
        public TokenTotals Totals = new TokenTotals();
        public int SessionCount, ActiveDays, ResponseCount, SourceFileCount, ProfileCount, SkippedRecords;
        public DateTime? FirstDay, LastDay, BusiestDay;
        public TimeSpan LongestSession;
        public string MostUsedModel, Warning;
        internal List<TokenRecord> Records;
        internal TimeZoneInfo Zone;

        public TokenStatisticsSnapshot ForRange(DateTime? start, DateTime? end)
        {
            if (Records != null) return Aggregate(Records, Zone, this, start, end);
            // Also supports the isolated drawing fixtures, without inventing per-range session counts.
            var copy = new TokenStatisticsSnapshot { UpdatedAt = UpdatedAt, SourceFileCount = SourceFileCount,
                ProfileCount = ProfileCount, Warning = Warning, SkippedRecords = SkippedRecords,
                SessionCount = SessionCount, MostUsedModel = MostUsedModel, LongestSession = LongestSession,
                ResponseCount = ResponseCount };
            copy.Days = Days.Where(d => (!start.HasValue || d.Date.Date >= start.Value.Date) &&
                (!end.HasValue || d.Date.Date <= end.Value.Date)).ToList();
            foreach (var day in copy.Days) copy.Totals.Add(day);
            SetDays(copy);
            return copy;
        }

        internal static TokenStatisticsSnapshot Aggregate(List<TokenRecord> records, TimeZoneInfo zone,
            TokenStatisticsSnapshot context, DateTime? start, DateTime? end)
        {
            var result = new TokenStatisticsSnapshot { Records = records, Zone = zone, UpdatedAt = context.UpdatedAt,
                SourceFileCount = context.SourceFileCount, ProfileCount = context.ProfileCount,
                Warning = context.Warning, SkippedRecords = context.SkippedRecords };
            var sessions = new Dictionary<string, List<DateTimeOffset>>();
            var days = new SortedDictionary<DateTime, TokenDay>();
            var daySessions = new Dictionary<DateTime, HashSet<string>>();
            var models = new Dictionary<string, long>();
            foreach (TokenRecord record in records)
            {
                DateTimeOffset at = DateTimeOffset.ParseExact(record.At, "o", CultureInfo.InvariantCulture);
                DateTime day = TimeZoneInfo.ConvertTime(at, zone).Date;
                if ((start.HasValue && day < start.Value.Date) || (end.HasValue && day > end.Value.Date)) continue;
                TokenDay value;
                if (!days.TryGetValue(day, out value))
                { days[day] = value = new TokenDay { Date = day }; daySessions[day] = new HashSet<string>(); }
                value.Add(record); result.Totals.Add(record); result.ResponseCount++;
                daySessions[day].Add(record.Session);
                List<DateTimeOffset> times;
                if (!sessions.TryGetValue(record.Session, out times)) sessions[record.Session] = times = new List<DateTimeOffset>();
                times.Add(at);
                long modelTotal; models.TryGetValue(record.Model, out modelTotal);
                models[record.Model] = modelTotal + record.Total;
            }
            foreach (var day in days.Values) day.SessionCount = daySessions[day.Date].Count;
            result.Days = days.Values.ToList(); result.SessionCount = sessions.Count;
            result.LongestSession = sessions.Count == 0 ? TimeSpan.Zero : sessions.Values.Max(t => t.Max() - t.Min());
            result.MostUsedModel = models.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault();
            SetDays(result);
            return result;
        }

        static void SetDays(TokenStatisticsSnapshot value)
        {
            value.ActiveDays = value.Days.Count(d => d.Total > 0);
            value.FirstDay = value.Days.Count == 0 ? (DateTime?)null : value.Days.Min(d => d.Date);
            value.LastDay = value.Days.Count == 0 ? (DateTime?)null : value.Days.Max(d => d.Date);
            value.BusiestDay = value.Days.Where(d => d.Total > 0).OrderByDescending(d => d.Total).ThenBy(d => d.Date)
                .Select(d => (DateTime?)d.Date).FirstOrDefault();
        }
    }

    // Only usage counters and hashed identifiers are persisted; never transcript content or paths.
    sealed class TokenRecord : TokenTotals
    {
        public string Key, Session, Model, At;
    }

    sealed class TokenFileCursor
    {
        public string Key, HeadHash, TailHash;
        public long Offset, Length, WriteTicks, CreatedTicks;
        public int Skipped;
    }

    sealed class TokenLedger
    {
        public int Version = 1;
        public List<TokenRecord> Records = new List<TokenRecord>();
        public List<TokenFileCursor> Files = new List<TokenFileCursor>();
    }

    sealed class TokenStatisticsService
    {
        const int MaxLineBytes = 16 * 1024 * 1024;
        const long MaxCounter = 1000000000000L;
        readonly object gate = new object();
        readonly string stateDir;
        readonly Func<IEnumerable<string>> roots;
        readonly Func<int> profileCount;
        readonly TimeZoneInfo zone;
        readonly Func<DateTimeOffset> clock;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 128 };
        static readonly Regex Assistant = new Regex("\"type\"\\s*:\\s*\"assistant\"", RegexOptions.Compiled);
        public string LedgerPath { get { return Path.Combine(stateDir, "token-statistics-v1.json"); } }

        public TokenStatisticsService(Engine engine) : this(engine.StateDir,
            () => new[] { Path.Combine(engine.ClaudeDir, "projects") }.Concat(engine.Profiles.ToArray().Select(p => Path.Combine(p.Dir, "claude-code", "projects"))),
            () => engine.Profiles.Count, TimeZoneInfo.Local, () => DateTimeOffset.UtcNow) { }

        internal TokenStatisticsService(string directory, Func<IEnumerable<string>> sourceRoots, Func<int> profiles,
            TimeZoneInfo timeZone, Func<DateTimeOffset> now)
        { stateDir = directory; roots = sourceRoots; profileCount = profiles; zone = timeZone; clock = now; }

        public TokenStatisticsSnapshot Refresh()
        {
            lock (gate)
            using (var mutex = new Mutex(false, "Local\\ClaudeAccountSync.TokenStatistics." + Hash(Path.GetFullPath(stateDir).ToUpperInvariant())))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("Token 统计正在由另一个窗口更新，请稍后刷新。");
                    return RefreshLocked();
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        TokenStatisticsSnapshot RefreshLocked()
        {
            TokenLedger ledger = Load();
            var records = ledger.Records.ToDictionary(r => r.Key, StringComparer.Ordinal);
            var cursors = ledger.Files.ToDictionary(f => f.Key, StringComparer.Ordinal);
            bool dirty = !File.Exists(LedgerPath);
            int inaccessible = 0;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots().Distinct(StringComparer.OrdinalIgnoreCase)) Enumerate(root, files, ref inaccessible);
            DateTimeOffset now = clock();
            foreach (string path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string key = Hash(Path.GetFullPath(path).ToUpperInvariant());
                    TokenFileCursor cursor;
                    if (!cursors.TryGetValue(key, out cursor)) cursors[key] = cursor = new TokenFileCursor { Key = key };
                    if (Scan(path, cursor, records, now)) dirty = true;
                }
                catch (IOException) { inaccessible++; }
                catch (UnauthorizedAccessException) { inaccessible++; }
            }
            if (dirty)
            {
                ledger.Records = records.Values.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
                ledger.Files = cursors.Values.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
                Save(ledger);
            }
            int skipped = cursors.Values.Sum(f => f.Skipped);
            var context = new TokenStatisticsSnapshot { UpdatedAt = now, SourceFileCount = files.Count,
                ProfileCount = profileCount(), SkippedRecords = skipped };
            var notes = new List<string>();
            if (inaccessible > 0) notes.Add(inaccessible.ToString(CultureInfo.InvariantCulture) + " 处来源暂时无法读取，已保留历史记录");
            if (skipped > 0) notes.Add(skipped.ToString(CultureInfo.InvariantCulture) + " 条异常或超大记录未计入");
            if (files.Count == 0) notes.Add("尚未发现本机 Claude Code 会话记录");
            context.Warning = string.Join("；", notes);
            return TokenStatisticsSnapshot.Aggregate(records.Values.ToList(), zone, context, null, null);
        }

        TokenLedger Load()
        {
            if (!File.Exists(LedgerPath)) return new TokenLedger();
            try
            {
                string text = File.ReadAllText(LedgerPath, Encoding.UTF8);
                var schema = json.Deserialize<Dictionary<string, object>>(text);
                if (schema == null || !schema.ContainsKey("Version") || !schema.ContainsKey("Records") || !schema.ContainsKey("Files")) throw new FormatException();
                var value = json.Deserialize<TokenLedger>(text);
                if (value == null || value.Version != 1 || value.Records == null || value.Files == null ||
                    value.Records.Any(r => !ValidRecord(r)) || value.Files.Any(f => f == null || !IsHash(f.Key) ||
                        f.Offset < 0 || f.Length < f.Offset || f.Skipped < 0) ||
                    value.Records.Select(r => r.Key).Distinct().Count() != value.Records.Count ||
                    value.Files.Select(f => f.Key).Distinct().Count() != value.Files.Count) throw new FormatException();
                return value;
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException || ex is InvalidOperationException || ex is FormatException ||
                    ex is IOException || ex is UnauthorizedAccessException || ex is OverflowException)) throw;
                throw new IOException("Token 历史账本无法读取，原文件已保留；请检查 token-statistics-v1.json 后再刷新。", ex);
            }
        }

        void Save(TokenLedger ledger)
        {
            Directory.CreateDirectory(stateDir);
            string temp = LedgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                { writer.Write(json.Serialize(ledger)); writer.Flush(); stream.Flush(true); }
                if (File.Exists(LedgerPath)) File.Replace(temp, LedgerPath, null); else File.Move(temp, LedgerPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static void Enumerate(string root, HashSet<string> files, ref int inaccessible)
        {
            try
            {
                if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
                foreach (string file in Directory.GetFiles(root, "*.jsonl"))
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) files.Add(Path.GetFullPath(file));
                foreach (string folder in Directory.GetDirectories(root)) Enumerate(folder, files, ref inaccessible);
            }
            catch (IOException) { inaccessible++; }
            catch (UnauthorizedAccessException) { inaccessible++; }
        }

        bool Scan(string path, TokenFileCursor cursor, Dictionary<string, TokenRecord> records, DateTimeOffset now)
        {
            var info = new FileInfo(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                long write = info.LastWriteTimeUtc.Ticks, created = info.CreationTimeUtc.Ticks;
                bool same = cursor.Length == length && cursor.WriteTicks == write && cursor.CreatedTicks == created;
                long offset = cursor.Offset;
                // Check both ends of consumed bytes before resuming; size alone cannot detect truncation/regrowth.
                bool reset = offset > length || cursor.CreatedTicks != created ||
                    (offset > 0 && (cursor.HeadHash != SegmentHash(stream, 0, (int)Math.Min(4096, offset)) ||
                     cursor.TailHash != SegmentHash(stream, Math.Max(0, offset - 4096), (int)Math.Min(4096, offset)))) ||
                    (!same && length == cursor.Length && offset == length);
                if (same && offset == length && !reset) return false;
                if (reset) { offset = 0; cursor.Skipped = 0; }
                stream.Position = offset;
                byte[] buffer = new byte[65536];
                using (var line = new MemoryStream())
                {
                    bool oversized = false;
                    long position = offset;
                    int read;
                    while ((read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - position))) > 0)
                    {
                        int segment = 0;
                        for (int i = 0; i < read; i++)
                        {
                            if (buffer[i] != 10) continue;
                            int count = i - segment;
                            if (!oversized && line.Length + count <= MaxLineBytes) line.Write(buffer, segment, count);
                            else oversized = true;
                            if (oversized) cursor.Skipped++;
                            else ProcessLine(line, records, now, ref cursor.Skipped);
                            offset = position + i + 1;
                            line.SetLength(0); oversized = false; segment = i + 1;
                        }
                        int remaining = read - segment;
                        if (!oversized && line.Length + remaining <= MaxLineBytes) line.Write(buffer, segment, remaining);
                        else oversized = true;
                        position += read;
                    }
                    // An unterminated tail can be an in-progress write, even when it currently parses.
                    // It remains pending until its newline arrives; offsets are never committed past it.
                }
                bool dirty = reset || !same || cursor.Offset != offset;
                cursor.Offset = offset; cursor.Length = length; cursor.WriteTicks = write; cursor.CreatedTicks = created;
                cursor.HeadHash = SegmentHash(stream, 0, (int)Math.Min(4096, offset));
                cursor.TailHash = SegmentHash(stream, Math.Max(0, offset - 4096), (int)Math.Min(4096, offset));
                return dirty;
            }
        }

        void ProcessLine(MemoryStream line, Dictionary<string, TokenRecord> records, DateTimeOffset now, ref int skipped)
        {
            if (line.Length == 0) return;
            string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim('\r', '\uFEFF');
            if (text.IndexOf("input_tokens", StringComparison.Ordinal) < 0 || !Assistant.IsMatch(text)) return;
            try
            {
                var data = json.Deserialize<Dictionary<string, object>>(text);
                if (data == null || String(data, "type") != "assistant") return;
                var message = Object(data, "message");
                if (message == null || (String(message, "role") != null && String(message, "role") != "assistant")) return;
                var usage = Object(message, "usage");
                if (usage == null) return;
                string model = String(message, "model");
                if (model == "<synthetic>") return;
                string id = String(message, "id");
                string session = String(data, "sessionId");
                DateTimeOffset at;
                string stamp = String(data, "timestamp");
                if (string.IsNullOrEmpty(id) || id.Length > 512 || string.IsNullOrEmpty(session) || session.Length > 512 ||
                    string.IsNullOrEmpty(model) || model.Length > 128 || model.Any(char.IsControl) ||
                    string.IsNullOrEmpty(stamp) || !Regex.IsMatch(stamp, @"(Z|[+\-]\d\d:\d\d)$") ||
                    !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out at) ||
                    at > now.AddMinutes(5) || at.Year < 2020) { skipped++; return; }
                var record = new TokenRecord { Key = Hash("message:" + id), Session = Hash("session:" + session),
                    Model = model, At = at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) };
                if (!Counter(usage, "input_tokens", true, out record.Input) || !Counter(usage, "output_tokens", true, out record.Output) ||
                    !Counter(usage, "cache_read_input_tokens", false, out record.CacheRead) ||
                    !Counter(usage, "cache_creation_input_tokens", false, out record.CacheWrite)) { skipped++; return; }
                TokenRecord prior;
                if (!records.TryGetValue(record.Key, out prior)) records.Add(record.Key, record);
                else
                {
                    // One model response can be written once per content block or streaming update.
                    // Cumulative usage is not summed, and a stale synced copy cannot reduce counters.
                    prior.Input = Math.Max(prior.Input, record.Input); prior.Output = Math.Max(prior.Output, record.Output);
                    prior.CacheRead = Math.Max(prior.CacheRead, record.CacheRead); prior.CacheWrite = Math.Max(prior.CacheWrite, record.CacheWrite);
                    if (string.CompareOrdinal(record.At, prior.At) < 0) prior.At = record.At;
                }
            }
            catch (ArgumentException) { skipped++; }
            catch (InvalidOperationException) { skipped++; }
        }

        static bool Counter(Dictionary<string, object> d, string key, bool required, out long value)
        {
            value = 0; object raw;
            if (!d.TryGetValue(key, out raw)) return !required;
            if (!(raw is int || raw is long || raw is decimal || raw is double)) return false;
            decimal number;
            try { number = Convert.ToDecimal(raw, CultureInfo.InvariantCulture); }
            catch (Exception ex) { if (ex is OverflowException || ex is FormatException) return false; throw; }
            if (number < 0 || number > MaxCounter || number != decimal.Truncate(number)) return false;
            value = (long)number; return true;
        }

        static string String(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) ? value as string : null; }
        static Dictionary<string, object> Object(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        static bool IsHash(string value) { return value != null && Regex.IsMatch(value, "^[0-9a-f]{64}$"); }
        static bool ValidRecord(TokenRecord r)
        {
            DateTimeOffset at;
            return r != null && IsHash(r.Key) && IsHash(r.Session) && !string.IsNullOrEmpty(r.Model) && r.Model.Length <= 128 &&
                !r.Model.Any(char.IsControl) && DateTimeOffset.TryParseExact(r.At, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out at) &&
                r.Input >= 0 && r.Input <= MaxCounter && r.Output >= 0 && r.Output <= MaxCounter &&
                r.CacheRead >= 0 && r.CacheRead <= MaxCounter && r.CacheWrite >= 0 && r.CacheWrite <= MaxCounter;
        }
        static string Hash(string text)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        static string SegmentHash(FileStream stream, long start, int count)
        {
            stream.Position = start;
            byte[] bytes = new byte[count]; int done = 0, read;
            while (done < count && (read = stream.Read(bytes, done, count - done)) > 0) done += read;
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes, 0, done)).Replace("-", "").ToLowerInvariant();
        }
    }
}
