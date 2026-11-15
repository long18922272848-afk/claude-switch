using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeAccountSync
{
    // UIA stays off the form thread and the task/sync work semaphore. A slow provider can
    // occupy at most this single background sampler; repeated timer ticks never queue more.
    sealed class UsageMonitor
    {
        readonly Engine engine;
        readonly Func<Profile, UsageHistoryResult> observe;
        readonly object gate = new object();
        readonly Dictionary<string, UsageHistoryResult> readings = new Dictionary<string, UsageHistoryResult>();
        Task activeRefresh = Task.FromResult(true);
        public event Action<Profile, UsageHistoryResult> Updated;

        public UsageMonitor(Engine engine) : this(engine, p => UsageDesktopReader.Read(engine, p)) { }
        internal UsageMonitor(Engine engine, Func<Profile, UsageHistoryResult> observe)
        { this.engine = engine; this.observe = observe; }

        public void RequestRefresh() { RefreshAsync(); }

        // All callers share the same in-flight read. A manual refresh can await this task
        // instead of displaying the cached value while accessibility sampling is unfinished.
        public Task RefreshAsync()
        {
            TaskCompletionSource<bool> completion;
            List<Profile> profiles;
            lock (gate)
            {
                if (!activeRefresh.IsCompleted) return activeRefresh;
                completion = new TaskCompletionSource<bool>();
                activeRefresh = completion.Task;
                try { profiles = engine.Profiles.ToList(); }
                catch { completion.SetResult(false); return activeRefresh; }
            }
            try
            {
                if (!ThreadPool.QueueUserWorkItem(_ => Sample(profiles, completion))) completion.TrySetResult(false);
            }
            catch { completion.TrySetResult(false); }
            return completion.Task;
        }

        void Sample(List<Profile> profiles, TaskCompletionSource<bool> completion)
        {
            try
            {
                foreach (var profile in profiles)
                {
                    UsageHistoryResult result;
                    try { result = observe(profile); }
                    catch { result = new UsageHistoryResult { Message = "当前用量页面暂时无法读取" }; }
                    if (result.Snapshot != null)
                    {
                        UsageSnapshot previous = UsageStore.Read(engine, profile);
                        UsageSnapshot merged = Merge(previous, result.Snapshot);
                        try { UsageStore.Save(engine, profile, merged); }
                        catch (IOException) { result = new UsageHistoryResult { Message = "用量采样未保存，等待重新核验账号" }; }
                        catch (UnauthorizedAccessException) { result = new UsageHistoryResult { Message = "用量采样未保存，无法写入缓存" }; }
                        if (result.Snapshot != null) result.Snapshot = merged;
                    }
                    lock (gate) readings[profile.Id] = result;
                    Publish(profile, Read(profile));
                }
            }
            catch { engine.Log("usage sampler failed; previous observations retained"); }
            finally { completion.TrySetResult(true); }
        }

        void Publish(Profile profile, UsageHistoryResult result)
        {
            var handlers = Updated;
            if (handlers == null) return;
            foreach (Action<Profile, UsageHistoryResult> handler in handlers.GetInvocationList())
                try { handler(profile, result); }
                catch { engine.Log("usage display update failed; observation retained"); }
        }

        public UsageHistoryResult Read(Profile profile)
        {
            string account = engine.ActiveAccount(profile);
            UsageHistoryResult last;
            lock (gate) readings.TryGetValue(profile.Id, out last);
            UsageSnapshot value = last == null ? null : last.Snapshot;
            if (value != null && !string.Equals(value.AccountId, account, StringComparison.OrdinalIgnoreCase)) value = null;
            if (value == null) value = UsageStore.Read(engine, profile);
            UsageHistoryResult history = UsageHistoryReader.Read(engine, profile);
            if (value != null && value.Source == UsageSource.DesktopUi &&
                string.Equals(value.AccountId, engine.ActiveAccount(profile), StringComparison.OrdinalIgnoreCase))
                return new UsageHistoryResult { Snapshot = PreferNewest(value, history.Snapshot), Message = last != null && last.Snapshot != null ? last.Message :
                    "保留最近采样；打开 Claude 的用量详情可自动更新（本地记录较新时按其原采样时间显示）" };
            history.Message = (last == null ? "打开 Claude 的用量详情可自动读取" : last.Message) +
                "。" + history.Message;
            return history;
        }

        internal static UsageSnapshot PreferNewest(UsageSnapshot page, UsageSnapshot history)
        {
            if (history == null || page.ProfileId != history.ProfileId || !string.Equals(page.AccountId, history.AccountId, StringComparison.OrdinalIgnoreCase)) return page;
            var result = new UsageSnapshot { ProfileId = page.ProfileId, AccountId = page.AccountId, Source = page.Source, ObservedAt = page.ObservedAt };
            foreach (var key in page.Windows.Select(w => w.Key).Union(history.Windows.Select(w => w.Key)))
            {
                var p = page.Find(key); var h = history.Find(key);
                bool useHistory = h != null && (p == null || UsageDisplay.WindowObservedAt(history, h) > UsageDisplay.WindowObservedAt(page, p));
                var source = useHistory ? history : page; var window = useHistory ? h : p;
                result.Windows.Add(Copy(window, UsageDisplay.WindowObservedAt(source, window), UsageDisplay.WindowSource(source, window)));
            }
            var sources = result.Windows.Select(w => w.Source.Value).Distinct().ToArray();
            if (sources.Length == 1) result.Source = sources[0];
            result.ObservedAt = result.Windows.Max(w => w.ObservedAt.Value);
            return result;
        }

        internal static UsageSnapshot Merge(UsageSnapshot previous, UsageSnapshot incoming)
        {
            if (incoming == null) return previous;
            bool sameAccount = previous != null && previous.ProfileId == incoming.ProfileId &&
                string.Equals(previous.AccountId, incoming.AccountId, StringComparison.OrdinalIgnoreCase) && previous.Source == UsageSource.DesktopUi;
            var merged = new UsageSnapshot { ProfileId = incoming.ProfileId, AccountId = incoming.AccountId,
                Source = incoming.Source, ObservedAt = incoming.ObservedAt };
            foreach (var window in incoming.Windows)
            {
                // This timestamp records an actual accessibility observation, not a server fetch.
                // Only windows present in this capture receive the new observation time.
                merged.Windows.Add(Copy(window, UsageDisplay.WindowObservedAt(incoming, window), UsageDisplay.WindowSource(incoming, window)));
            }
            // A compact Code footer may provide only one limit. Keep the other observation's
            // original date/source rather than silently refreshing it or replacing it with 0%.
            if (sameAccount)
                foreach (var old in previous.Windows.Where(w => merged.Find(w.Key) == null))
                    merged.Windows.Add(Copy(old, UsageDisplay.WindowObservedAt(previous, old), UsageDisplay.WindowSource(previous, old)));
            merged.ObservedAt = merged.Windows.Count == 0 ? incoming.ObservedAt : merged.Windows.Max(w => w.ObservedAt.Value);
            return merged;
        }

        static UsageWindow Copy(UsageWindow w, DateTimeOffset observed, UsageSource source)
        { return new UsageWindow { Key = w.Key, Label = w.Label, UsedPercent = w.UsedPercent,
            ResetsAt = w.ResetsAt, ResetText = w.ResetText, ObservedAt = observed, Source = source }; }
    }
}
