using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    static class TokenStatisticsTests
    {
        static int passed;
        static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("fixture+09", TimeSpan.FromHours(9), "fixture", "fixture");
        static void Check(bool valid, string message)
        { if (!valid) throw new Exception("Token statistics fixture failed: " + message); passed++; }

        static string Record(string id, string session, string stamp, long input, long output, long read, long write, string model)
        {
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "type", "assistant" }, { "sessionId", session }, { "timestamp", stamp }, { "requestId", "request-" + id },
                { "message", new Dictionary<string, object> { { "id", id }, { "role", "assistant" }, { "model", model },
                    { "content", new [] { new Dictionary<string, object> { { "type", "text" }, { "text", "PRIVATE_TRANSCRIPT_MARKER" } } } },
                    { "usage", new Dictionary<string, object> { { "input_tokens", input }, { "output_tokens", output },
                        { "cache_read_input_tokens", read }, { "cache_creation_input_tokens", write },
                        { "cache_creation", new Dictionary<string, object> { { "ephemeral_1h_input_tokens", write } } } } } } }
            });
        }

        public static int Run(string root)
        {
            passed = 0;
            string projects = Path.Combine(root, "projects"), state = Path.Combine(root, "state");
            Directory.CreateDirectory(projects);
            Func<TokenStatisticsService> create = () => new TokenStatisticsService(state, () => new[] { projects, projects }, () => 3, Zone, () => Now);
            var service = create();
            var empty = service.Refresh();
            Check(empty.Totals.Total == 0 && empty.SessionCount == 0 && empty.FirstDay == null, "empty sources stay empty");
            Check(empty.ProfileCount == 3 && !string.IsNullOrEmpty(empty.Warning), "three profiles and absent source warning");
            string a = Record("response-a", "parent-a", "2026-11-13T15:01:00Z", 10, 20, 30, 40, "claude-opus-fixture");
            string stream = Record("response-a", "parent-a", "2026-11-13T15:01:05Z", 10, 50, 30, 40, "claude-opus-fixture");
            string b = Record("response-b", "parent-a", "2026-11-14T15:02:00Z", 11, 21, 31, 41, "claude-sonnet-fixture");
            string path = Path.Combine(projects, "session.jsonl");
            File.WriteAllText(path, a + "\n" + stream + "\n" + b + "\n", new UTF8Encoding(false));
            var first = service.Refresh();
            Check(first.Totals.Input == 21 && first.Totals.Output == 71 && first.Totals.CacheRead == 61 && first.Totals.CacheWrite == 81, "streaming response counted once using cumulative maxima");
            Check(first.Totals.Total == 234, "cache creation subfields not double counted");
            Check(first.Days.Count == 2 && first.FirstDay == new DateTime(2026, 11, 14) && first.LastDay == new DateTime(2026, 11, 15), "UTC to local day boundary");
            Check(first.SessionCount == 1 && first.ResponseCount == 2 && first.Days.All(d => d.SessionCount == 1), "parent session distinct across days");
            Check(first.LongestSession == TimeSpan.FromMinutes(1441), "session elapsed span from first to last response");
            Check(first.MostUsedModel == "claude-opus-fixture" && first.BusiestDay == new DateTime(2026, 11, 14), "model and busiest day by total tokens");
            var range = first.ForRange(new DateTime(2026, 11, 15), new DateTime(2026, 11, 15));
            Check(range.Totals.Total == 104 && range.SessionCount == 1 && range.ResponseCount == 1 && range.MostUsedModel == "claude-sonnet-fixture", "range reaggregates all metrics inclusively");
            var nothing = first.ForRange(new DateTime(2026, 11, 16), null);
            Check(nothing.Totals.Total == 0 && nothing.SessionCount == 0 && nothing.ActiveDays == 0, "empty selected range");
            string ledger = File.ReadAllText(service.LedgerPath);
            Check(!ledger.Contains("PRIVATE_TRANSCRIPT_MARKER") && !ledger.Contains("response-a") && !ledger.Contains("parent-a") && !ledger.Contains(projects.Replace("\\", "\\\\")), "ledger contains no conversation content, raw identifiers, or paths");
            Check(create().Refresh().Totals.Total == 234, "restart preserves history and offsets");
            string subagents = Path.Combine(projects, "session", "subagents");
            Directory.CreateDirectory(subagents);
            File.WriteAllText(Path.Combine(subagents, "agent-child.jsonl"), Record("response-child", "parent-a", "2026-11-15T03:00:00Z", 1, 2, 3, 4, "claude-sonnet-fixture") + "\n");
            File.WriteAllText(Path.Combine(projects, "synced-copy.jsonl"), a + "\n" + stream + "\n" + b + "\n");
            var children = service.Refresh();
            Check(children.Totals.Total == 244 && children.ResponseCount == 3 && children.SessionCount == 1, "subagents included; synchronized duplicate transcripts not multiplied");
            Check(children.SourceFileCount == 3, "overlapping roots scanned once");
            string pending = Record("response-tail", "parent-b", "2026-11-15T04:00:00Z", 2, 3, 4, 5, "claude-opus-fixture");
            File.AppendAllText(path, pending.Substring(0, pending.Length / 2));
            Check(service.Refresh().Totals.Total == 244, "incomplete final line deferred");
            File.AppendAllText(path, pending.Substring(pending.Length / 2));
            Check(service.Refresh().Totals.Total == 244, "complete but unterminated line remains pending");
            File.AppendAllText(path, "\n");
            Check(service.Refresh().Totals.Total == 258 && service.Refresh().SessionCount == 2, "finished line counted exactly once");
            File.AppendAllText(path, Record("bad-negative", "parent-a", "2026-11-15T01:00:00Z", -1, 2, 3, 4, "model") + "\n" +
                Record("bad-future", "parent-a", "2027-01-01T00:00:00Z", 1, 2, 3, 4, "model") + "\n" +
                a.Replace("response-a", "bad-string").Replace("\"input_tokens\":10", "\"input_tokens\":\"10\"") + "\n" +
                a.Replace("response-a", "bad-fraction").Replace("\"input_tokens\":10", "\"input_tokens\":1.5") + "\n" +
                a.Replace("response-a", "bad-naive").Replace("2026-11-13T15:01:00Z", "2026-11-13T15:01:00") + "\n" +
                a.Replace("response-a", "synthetic").Replace("claude-opus-fixture", "<synthetic>") + "\n" +
                "{\"type\":\"assistant\",\"input_tokens\":bad}\n");
            var rejected = service.Refresh();
            Check(rejected.Totals.Total == 258 && rejected.SkippedRecords == 6, "invalid counters/timestamp/malformed record rejected; synthetic omitted");
            Check(!string.IsNullOrEmpty(rejected.Warning) && service.Refresh().SkippedRecords == 6, "data gaps visible without repeatedly counting rejected records");
            File.WriteAllText(path, Record("new-after-truncate", "parent-c", "2026-11-15T05:00:00Z", 1, 1, 1, 1, "model") + "\n");
            Check(service.Refresh().Totals.Total == 262, "truncated transcript rescanned while prior ledger survives");
            File.Delete(path);
            Check(create().Refresh().Totals.Total == 262, "deleted source does not erase collected history");
            File.WriteAllText(path, a.Replace("response-a", "same-size-first") + "\n");
            service.Refresh();
            var writeAt = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, a.Replace("response-a", "same-size-other") + "\n");
            File.SetLastWriteTimeUtc(path, writeAt.AddSeconds(2));
            Check(service.Refresh().Totals.Total == 462, "same size rewrite detected");
            writeAt = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, a.Replace("response-a", "same-size-third") + "\n");
            File.SetLastWriteTimeUtc(path, writeAt);
            Check(service.Refresh().Totals.Total == 562, "fingerprints detect rewrite even with preserved timestamp and size");
            string validLedger = File.ReadAllText(service.LedgerPath);
            File.WriteAllText(service.LedgerPath, "{\"Version\":1,broken");
            bool failed = false;
            try { service.Refresh(); } catch (IOException) { failed = true; }
            Check(failed && File.ReadAllText(service.LedgerPath) == "{\"Version\":1,broken", "corrupt ledger preserved and clearly rejected");
            File.WriteAllText(service.LedgerPath, "{}"); failed = false;
            try { service.Refresh(); } catch (IOException) { failed = true; }
            Check(failed && File.ReadAllText(service.LedgerPath) == "{}", "schema-less ledger never silently resets history");
            File.WriteAllText(service.LedgerPath, validLedger);
            Check(create().Refresh().Totals.Total == 562, "repairing ledger restores readable historical totals");
            return passed;
        }
    }
}
