using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeAccountSync
{
    static class UsageMonitorTests
    {
        public static int Run(string temporaryRoot)
        {
            int passed = 0;
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception("Usage monitor: " + name); passed++; };
            var time = DateTimeOffset.UtcNow.AddMinutes(-10);
            var old = UsageParser.Parse("{\"five_hour\":{\"utilization\":80},\"seven_day\":{\"utilization\":22}}", "p", "account-a", UsageSource.DesktopUi, time);
            var same = UsageParser.Parse("{\"five_hour\":{\"utilization\":80}}", "p", "account-a", UsageSource.DesktopUi, time.AddMinutes(10));
            var merged = UsageMonitor.Merge(old, same);
            check(merged.Weekly.UsedPercent == 22 && UsageDisplay.WindowObservedAt(merged, merged.Weekly) == time, "partial footer preserves weekly observation date");
            check(UsageDisplay.WindowObservedAt(merged, merged.FiveHour) == same.ObservedAt, "actual visible footer observation gets its own capture time");
            check(!UsageDisplay.IsCurrent(merged, merged.Weekly, time.AddMinutes(10)), "uncaptured weekly value stays stale");
            same.FiveHour.UsedPercent = 100;
            merged = UsageMonitor.Merge(old, same);
            check(UsageDisplay.WindowObservedAt(merged, merged.FiveHour) == same.ObservedAt && UsageDisplay.WindowObservedAt(merged, merged.Weekly) == time, "new five-hour value does not rejuvenate old weekly value");
            check(old.FiveHour.UsedPercent == 80 && old.Windows.All(w => !w.ObservedAt.HasValue), "merging never mutates previous shared snapshot");
            same.AccountId = "account-b";
            check(UsageMonitor.Merge(old, same).Weekly == null, "another account cannot inherit weekly usage");
            same.AccountId = "account-a"; same.ProfileId = "other-profile";
            check(UsageMonitor.Merge(old, same).Weekly == null, "another profile cannot inherit weekly usage");
            var newerHistory = UsageParser.Parse("{\"five_hour\":{\"utilization\":40}}", "p", "account-a", UsageSource.DesktopHistory, time.AddMinutes(5));
            var preferred = UsageMonitor.PreferNewest(old, newerHistory);
            check(preferred.FiveHour.UsedPercent == 40 && preferred.Weekly.UsedPercent == 22, "newer native history does not get hidden by old page cache");
            check(preferred.FiveHour.Source == UsageSource.DesktopHistory && preferred.Weekly.Source == UsageSource.DesktopUi && preferred.FiveHour.ResetText == null, "mixed observations keep individual source and never borrow a reset");
            check(UsageDisplay.RemainingPercent(preferred, preferred.FiveHour, time.AddMinutes(5)) == null, "native history is never promoted to current remaining quota");
            newerHistory.AccountId = "different";
            check(UsageMonitor.PreferNewest(old, newerHistory) == old, "newer record for another identity is not merged");
            var gathered = UsageParser.Parse("{\"five_hour\":{\"utilization\":71},\"seven_day\":{\"utilization\":82}}", "p", "account-a", UsageSource.DesktopUi, time);
            var published = UsageParser.Parse("{\"five_hour\":{\"utilization\":87},\"seven_day\":{\"utilization\":84}}", "p", "account-a", UsageSource.DesktopUi, time.AddMinutes(10));
            var latest = UsageMonitor.PreferNewest(gathered, published);
            check(latest.FiveHour.UsedPercent == 87 && latest.Weekly.UsedPercent == 84 && latest.ObservedAt == published.ObservedAt,
                "late old Gather cannot overwrite newer same-account published percentages");
            gathered.Source = UsageSource.DesktopHistory;
            latest = UsageMonitor.PreferNewest(gathered, published);
            check(latest.Source == UsageSource.DesktopUi && latest.Windows.All(w => w.Source == UsageSource.DesktopUi),
                "all newer UI windows restore snapshot and per-window UI provenance");
            CheckRefresh(temporaryRoot, check);
            return passed;
        }

        static void CheckRefresh(string root, Action<bool, string> check)
        {
            const string account = "11111111-1111-4111-8111-111111111111";
            var engine = new Engine(Path.Combine(root, "profile"), false, Path.Combine(root, "claude"), Path.Combine(root, "state"));
            Profile profile = engine.Profiles[0];
            Directory.CreateDirectory(profile.Dir);
            File.WriteAllText(Path.Combine(profile.Dir, "config.json"), "{\"windowSizeWasSignedIn\":true,\"lastKnownAccountUuid\":\"" + account + "\"}");
            var old = UsageParser.Parse("{\"five_hour\":{\"utilization\":71},\"seven_day\":{\"utilization\":82}}", profile.Id, account, UsageSource.DesktopUi, DateTimeOffset.UtcNow.AddMinutes(-10));
            UsageStore.Save(engine, profile, old);
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                int calls = 0, notifications = 0;
                UsageHistoryResult published = null;
                var monitor = new UsageMonitor(engine, p =>
                {
                    Interlocked.Increment(ref calls);
                    entered.Set();
                    if (!release.WaitOne(5000)) throw new TimeoutException("Fixture sampler was not released");
                    return new UsageHistoryResult { Snapshot = UsageParser.Parse("{\"five_hour\":{\"utilization\":87},\"seven_day\":{\"utilization\":84}}", p.Id, account, UsageSource.DesktopUi, DateTimeOffset.UtcNow), Message = "fixture observation" };
                });
                // A failed display subscriber must not suppress later subscribers or leave sampling busy.
                monitor.Updated += (p, value) => { throw new InvalidOperationException("fixture display failure"); };
                monitor.Updated += (p, value) => { published = value; Interlocked.Increment(ref notifications); };
                Task first = monitor.RefreshAsync();
                try
                {
                    check(entered.WaitOne(3000), "refresh starts injected sampler off caller thread");
                    Task second = monitor.RefreshAsync();
                    monitor.RequestRefresh();
                    check(object.ReferenceEquals(first, second) && calls == 1, "concurrent timer and manual refresh share one sample");
                    check(!first.IsCompleted && monitor.Read(profile).Snapshot.FiveHour.UsedPercent == 71 && notifications == 0,
                        "refresh task remains pending while only the old value is available");
                }
                finally { release.Set(); }
                check(first.Wait(3000), "refresh task completes after sampler publication");
                var latest = monitor.Read(profile).Snapshot;
                check(latest.FiveHour.UsedPercent == 87 && latest.Weekly.UsedPercent == 84,
                    "awaited refresh returns current percentages instead of the old cache");
                check(notifications == 1 && published.Snapshot.FiveHour.UsedPercent == 87 && published.Snapshot.Weekly.UsedPercent == 84,
                    "update event publishes the merged current reading despite another subscriber failure");
                check(UsageStore.Read(engine, profile).FiveHour.UsedPercent == 87, "refresh completes only after updated observation is saved");
                Task next = monitor.RefreshAsync();
                check(!object.ReferenceEquals(first, next) && next.Wait(3000) && calls == 2 && notifications == 2,
                    "subscriber failures do not leave future refreshes blocked");
            }
        }
    }
}
