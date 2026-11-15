using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    // Isolated history/identity fixtures only. Never opens Claude or reads real account files.
    static class UsageHistoryReaderTests
    {
        const string Account = "11111111-1111-4111-8111-111111111111";
        const string OtherAccount = "22222222-2222-4222-8222-222222222222";
        const string Org = "33333333-3333-4333-8333-333333333333";
        const string OtherOrg = "44444444-4444-4444-8444-444444444444";
        static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero);
        static readonly DateTimeOffset Epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        static int passed;

        sealed class Fixture
        {
            public Profile Profile;
            public string Active = Account;
            public string FilePath { get { return Path.Combine(Profile.Dir, "plan-usage-history.json"); } }
            public Fixture(string parent)
            {
                Profile = new Profile { Id = "fixture", Dir = Path.Combine(parent, "uh" + Guid.NewGuid().ToString("N").Substring(0, 8)) };
                Directory.CreateDirectory(Profile.Dir);
                Binding(Org, Account);
            }
            public void Binding(string org, string account)
            {
                string path = Path.Combine(Profile.Dir, @"local-agent-mode-sessions\skills-plugin", org, account);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "manifest.json"), "{}");
            }
            public UsageHistoryResult Read() { return UsageHistoryReader.Read(Profile, () => Active, Now); }
            public void Save(params object[] samples)
            {
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "version", 2 }, { "samples", samples }
                }), new UTF8Encoding(false));
            }
        }

        static object Sample(string org, DateTimeOffset at, params object[] values)
        {
            var u = new Dictionary<string, object>();
            for (int i = 0; i < values.Length; i += 2) u[(string)values[i]] = values[i + 1];
            return new Dictionary<string, object> { { "org", org }, { "t", (long)(at - Epoch).TotalMilliseconds }, { "u", u } };
        }
        static void Check(bool ok, string message)
        { if (!ok) throw new Exception("Usage history fixture failed: " + message); passed++; }

        public static int Run(string temporaryRoot)
        {
            passed = 0;
            var f = new Fixture(temporaryRoot);
            Check(f.Read().Snapshot == null, "missing history unknown, not zero");
            f.Save(Sample(Org, Now.AddMinutes(-10), "fh", 61, "sd", 49));
            string before = File.ReadAllText(f.FilePath);
            var r = f.Read();
            Check(r.Snapshot != null && r.Snapshot.FiveHour.UsedPercent == 61 && r.Snapshot.Weekly.UsedPercent == 49, "bound native history percent values");
            Check(r.Snapshot.ProfileId == "fixture" && r.Snapshot.AccountId == Account && r.Snapshot.Source == UsageSource.DesktopHistory, "source and account preserved");
            Check(r.Snapshot.ObservedAt == Now.AddMinutes(-10) && !r.Snapshot.FiveHour.ResetsAt.HasValue && !r.Snapshot.Weekly.ResetsAt.HasValue, "original timestamp and unknown resets");
            Check(File.ReadAllText(f.FilePath) == before, "read preserves source history");
            File.SetLastWriteTimeUtc(f.FilePath, Now.UtcDateTime);
            Check(f.Read().Snapshot.ObservedAt == Now.AddMinutes(-10), "mtime never refreshes observation age");
            f.Save(Sample(OtherOrg, Now.AddMinutes(-1), "fh", 99, "sd", 99), Sample(Org, Now.AddMinutes(-20), "fh", 12, "sd", 32), Sample(Org, Now.AddMinutes(-10), "fh", 25));
            r = f.Read();
            Check(r.Snapshot.FiveHour.UsedPercent == 25 && r.Snapshot.Weekly == null, "newest matching org and no older weekly backfill");
            f.Save(Sample(Org, Now.AddDays(-4), "fh", 3, "sd", 0));
            Check(f.Read().Snapshot.ObservedAt == Now.AddDays(-4), "old native data remains an old observation");
            f.Active = null;
            Check(f.Read().Snapshot == null, "signed out does not expose cached quota");
            f.Active = OtherAccount;
            Check(f.Read().Snapshot == null, "different account without association does not get old quota");
            f.Active = Account;
            int calls = 0;
            Check(UsageHistoryReader.Read(f.Profile, () => ++calls == 1 ? Account : OtherAccount, Now).Snapshot == null, "account change during read discards sample");

            var ambiguous = new Fixture(temporaryRoot);
            ambiguous.Save(Sample(Org, Now.AddMinutes(-1), "fh", 1));
            ambiguous.Binding(Org, OtherAccount);
            Check(ambiguous.Read().Snapshot == null, "reverse organization to account ambiguity rejected");
            var several = new Fixture(temporaryRoot);
            several.Save(Sample(Org, Now.AddMinutes(-1), "fh", 1));
            several.Binding(OtherOrg, Account);
            Check(several.Read().Snapshot == null, "account in multiple organizations rejected");
            var absent = new Fixture(temporaryRoot);
            File.Delete(Path.Combine(absent.Profile.Dir, @"local-agent-mode-sessions\skills-plugin", Org, Account, "manifest.json"));
            absent.Save(Sample(Org, Now.AddMinutes(-1), "fh", 1));
            Check(absent.Read().Snapshot == null, "empty fabricated directory alone is insufficient");

            File.WriteAllText(f.FilePath, "{\"version\":1,\"samples\":[{\"t\":1,\"fh\":2}]}");
            Check(f.Read().Snapshot == null, "legacy unbound history rejected");
            File.WriteAllText(f.FilePath, "{\"version\":2,\"samples\":");
            Check(f.Read().Snapshot == null, "partial concurrent write rejected");
            f.Save(Sample(Org, Now.AddSeconds(1), "fh", 2));
            Check(f.Read().Snapshot == null, "future timestamp rejected");
            f.Save(Sample(Org, Now.AddMinutes(-1), "fh", 1), Sample(Org, Now.AddMinutes(-1), "fh", 2));
            Check(f.Read().Snapshot == null, "conflicting same timestamp rejected");
            f.Save(Sample(Org, Now.AddMinutes(-1), "fh", -1, "sd", 49));
            Check(f.Read().Snapshot == null, "invalid percent rejects observation");
            f.Save(Sample(Org, Now.AddMinutes(-1), "fh", "12", "sd", 49));
            Check(f.Read().Snapshot == null, "string percent not silently converted");
            f.Save(Sample(Org, Now.AddMinutes(-1), "xu", 17));
            Check(f.Read().Snapshot == null, "extra usage alone not mistaken for plan limit");
            f.Save(Sample(Org, Now.AddMinutes(-1), "fh", 0, "sd", 100, "sn", 42, "so", 16));
            r = f.Read();
            Check(r.Snapshot.FiveHour.UsedPercent == 0 && r.Snapshot.Weekly.UsedPercent == 100 && r.Snapshot.Find("seven_day_sonnet").UsedPercent == 42, "boundary values and distinct model windows");
            File.WriteAllText(f.FilePath, new string(' ', 4 * 1024 * 1024 + 1));
            Check(f.Read().Snapshot == null, "oversized source rejected");
            return passed;
        }
    }
}
