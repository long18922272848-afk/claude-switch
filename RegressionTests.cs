using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ClaudeAccountSync
{
    // Uses synthetic account IDs and a unique, disposable URL scheme. Never touches real Claude data.
    static class RegressionTests
    {
        static int passed;
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name); passed++;
        }
        static void Config(Profile p, string id, string signedIn)
        {
            Directory.CreateDirectory(p.Dir);
            File.WriteAllText(Path.Combine(p.Dir, "config.json"), "{\"lastKnownAccountUuid\":\"" + id + "\",\"windowSizeWasSignedIn\":" + signedIn + "}");
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string cmd, out int count);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        static string[] Parse(string cmd)
        {
            int count; IntPtr ptr = CommandLineToArgvW(cmd, out count);
            try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size))).ToArray(); }
            finally { LocalFree(ptr); }
        }

        static void WriteSession(string path, string cli, string title, long activity, string[] bridges, DateTime modified)
        {
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "cliSessionId", cli }, { "title", title }, { "lastActivityAt", activity }, { "bridgeSessionIds", bridges }
            }));
            File.SetLastWriteTimeUtc(path, modified);
        }

        static void TestHandoff(string root)
        {
            root = Path.Combine(root, "handoff");
            var e = new Engine(Path.Combine(root, "first"), true, Path.Combine(root, "cli"), Path.Combine(root, "state"));
            Profile first = e.Profiles[0], second = e.AddProfile("Destination");
            const string id1 = "11111111-1111-4111-8111-111111111111", id2 = "22222222-2222-4222-8222-222222222222";
            Config(first, id1, "true"); Config(second, id2, "true");
            string sourceLeaf = Path.Combine(first.Registry, id1, "organization"), targetLeaf = Path.Combine(second.Registry, id2, "organization");
            string transcripts = Path.Combine(e.ClaudeDir, "projects", "fixture");
            Directory.CreateDirectory(sourceLeaf); Directory.CreateDirectory(targetLeaf); Directory.CreateDirectory(transcripts);
            File.WriteAllText(Path.Combine(transcripts, "cli-one.jsonl"), "{\"type\":\"test\"}\n");
            string source = Path.Combine(sourceLeaf, "local_one.json"), target = Path.Combine(targetLeaf, "local_one.json");
            DateTime start = DateTime.UtcNow.AddMinutes(-10);
            WriteSession(source, "cli-one", "First progress", 1000, new[] { "source-account-server-session" }, start);
            var phases = new List<string>();
            SyncResult r = App.PrepareSwitch(e, second, true, phases.Add);
            Check(r.Actions.Count == 1 && File.Exists(target), "handoff commits metadata even when target is already running");
            Check(r.RequiresRefresh && phases.SequenceEqual(new[] { "syncing", "knowledge" }), "running target exposes refresh requirement and real sync phase");
            var json = new JavaScriptSerializer();
            var saved = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(target));
            Check(((System.Collections.IEnumerable)saved["bridgeSessionIds"]).Cast<object>().Count() == 0, "cross-account handoff clears source server session IDs");
            r = App.PrepareSwitch(e, second, true);
            Check(r.Actions.Count == 0 && !r.RequiresRefresh, "unchanged handoff is idempotent without unnecessary refresh");
            WriteSession(target, "cli-one", "First progress", 1000, new[] { "target-account-server-session" }, start.AddMinutes(1));
            WriteSession(source, "cli-one", "Latest saved progress", 2000, new[] { "source-account-server-session" }, start.AddMinutes(2));
            r = App.PrepareSwitch(e, second, true);
            saved = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(target));
            Check(r.Actions.Count == 1 && Equals(saved["title"], "Latest saved progress"), "new saved progress reaches running target before focus");
            Check(((System.Collections.IEnumerable)saved["bridgeSessionIds"]).Cast<object>().Single().ToString() == "target-account-server-session", "updating preserves the destination account server session");
            WriteSession(source, "cli-one", "Source changing again", 3000, new string[0], start.AddMinutes(3));
            r = App.PrepareSwitch(e, second, true, phase =>
                WriteSession(target, "cli-one", "Destination concurrent progress", 5000, new[] { "new-target-session" }, start.AddMinutes(4)));
            saved = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(target));
            Check(r.Actions.Count == 0 && r.SkipConcurrent == 1 && !r.RequiresRefresh && r.Waiting != null, "concurrent target edit is skipped and never counted as committed");
            Check(Equals(saved["title"], "Destination concurrent progress"), "concurrent destination progress is preserved");
            string sourceTwo = Path.Combine(sourceLeaf, "local_two.json"), targetTwo = Path.Combine(targetLeaf, "local_two.json");
            File.WriteAllText(Path.Combine(transcripts, "cli-two.jsonl"), "{}\n");
            WriteSession(sourceTwo, "cli-two", "Source new session", 6000, new string[0], start.AddMinutes(5));
            r = App.PrepareSwitch(e, second, true, phase =>
                WriteSession(targetTwo, "cli-two", "Destination created meanwhile", 7000, new string[0], start.AddMinutes(6)));
            Check(r.SkipConcurrent == 1 && File.ReadAllText(targetTwo).Contains("Destination created meanwhile"), "new-entry race never overwrites a destination-created session");
            Check(Directory.GetFiles(targetLeaf, "*.tmp").Length == 0, "skipped writes clean up their temporary files");
            File.Delete(target);
            r = App.PrepareSwitch(e, second, true);
            Check(!File.Exists(target) && r.SkipDeleted == 1, "handoff does not resurrect a deliberately removed session");
            string sourceThree = Path.Combine(sourceLeaf, "local_three.json");
            File.WriteAllText(Path.Combine(transcripts, "cli-three.jsonl"), "{}\n");
            WriteSession(sourceThree, "cli-three", "Closed target progress", 8000, new string[0], start.AddMinutes(7));
            r = App.PrepareSwitch(e, second, false);
            Check(r.Actions.Count == 1 && !r.RequiresRefresh, "closed target receives progress without refresh warning");
            string longName = "local_" + new string('x', 244 - targetLeaf.Length - 12) + ".json";
            string longSource = Path.Combine(sourceLeaf, longName), longTarget = Path.Combine(targetLeaf, longName);
            File.WriteAllText(Path.Combine(transcripts, "cli-long.jsonl"), "{}\n");
            WriteSession(longSource, "cli-long", "Long destination name", 9000, new string[0], start.AddMinutes(8));
            r = App.PrepareSwitch(e, second, false);
            Check(r.Actions.Count == 1 && File.Exists(longTarget) && Directory.GetFiles(targetLeaf, "*.tmp").Length == 0,
                "valid long session destination syncs without an oversized temporary filename");
            Profile notReady = e.AddProfile("Not signed in");
            r = App.PrepareSwitch(e, notReady, false);
            Check(r.Waiting != null && r.Actions.Count == 0, "handoff without sign-in reports waiting, not success");
        }
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("--user-data-dir="))
            {
                File.WriteAllText(Path.Combine(args[0].Substring("--user-data-dir=".Length), "forwarded-args.json"), new JavaScriptSerializer().Serialize(args));
                return 0;
            }
            string root = Path.Combine(Path.GetTempPath(), "cas-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            string scheme = "claude-sync-test-" + Guid.NewGuid().ToString("N");
            string key = @"Software\Classes\" + scheme;
            CallbackRoute route = null;
            try
            {
                var e = new Engine(Path.Combine(root, "default"), true, Path.Combine(root, "cli"), Path.Combine(root, "state"));
                Profile first = e.Profiles[0], second = e.AddProfile("Second");
                const string id1 = "11111111-1111-4111-8111-111111111111", id2 = "22222222-2222-4222-8222-222222222222";
                Check(e.ActiveAccount(second) == null, "new profile is not signed in");
                Config(first, id1, "true");
                Check(e.ActiveAccount(first) == id1, "verified sign-in is recognized");
                Config(first, id1, "false");
                Check(e.ActiveAccount(first) == null, "logout with a retained UUID is NOT signed in");
                File.WriteAllText(Path.Combine(first.Dir, "config.json"), "{\"lastKnownAccountUuid\":\"" + id1 + "\"}");
                Check(e.ActiveAccount(first) == null, "historical UUID alone cannot complete login");
                Config(first, id1, "true"); Config(second, id1, "true");
                Check(e.IsDuplicate(second), "duplicate accounts detected by UUID");
                Config(second, id2, "true");
                Check(!e.IsDuplicate(second), "different accounts remain distinct");
                var loaded = new Engine(null, false, Path.Combine(root, "cli"), e.StateDir);
                Check(loaded.Find("default").IsRealDefault, "default-profile launch mode survives save/reload");
                Check(loaded.Find(second.Id).Dir == second.Dir, "profile directories survive save/reload");
                foreach (string arg in new[] { "", "C:\\a b\\", "a\"b", "a\\\"b", "中文 路径", "claude://test?code=fake&state=fake", "a  " })
                    Check(Parse("test.exe " + App.Quote(arg))[1] == arg, "Windows argument quoting round-trip " + passed);
                Check(App.ValidCallback("claude://test?code=fake&state=fake", "claude"), "valid callback accepted");
                Check(Program.ValidCallbackArguments(new[] { "--state-dir", root, "--scheme", "claude", "--protocol-callback", "claude://test" }), "registered protocol invocation accepted");
                Check(!Program.ValidCallbackArguments(new[] { "--state-dir", root, "--scheme", "claude", "--protocol-callback", "claude://test", "--default-dir", "injected" }), "callback cannot inject directory overrides");
                Check(!Program.ValidCallbackArguments(new[] { "--state-dir", root, "--scheme", "claude", "--protocol-callback", "claude://test", "--scheme", "injected" }), "callback cannot inject duplicate options");
                foreach (string bad in new[] { "https://claude.ai", "claude://test\" --user-data-dir=x", "claude://test\r\n", "claude://test\\x", "--inspect", "claude:relative" })
                    Check(!App.ValidCallback(bad, "claude"), "invalid/injected callback rejected " + passed);
                Check(!App.Belongs(new Instance { Dir = second.Dir }, first), "custom process never belongs to default profile");
                Check(App.Belongs(new Instance { Dir = second.Dir + "\\" }, second), "normalized custom process belongs to selected profile");
                var capture = new Profile { Dir = Path.Combine(root, "profile with spaces 中文") };
                Directory.CreateDirectory(capture.Dir);
                App.ForwardCallback(capture, System.Windows.Forms.Application.ExecutablePath, "claude://test?code=fake&state=fake", "claude");
                string captured = Path.Combine(capture.Dir, "forwarded-args.json");
                for (int i = 0; i < 30 && !File.Exists(captured); i++) System.Threading.Thread.Sleep(100);
                string[] actualArgs = new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(captured));
                Check(actualArgs.SequenceEqual(new[] { "--user-data-dir=" + capture.Dir, "--", "claude://test?code=fake&state=fake" }), "real child process receives exact profile and callback arguments");
                string original = "\"C:\\original-handler.exe\" \"%1\"";
                using (var k = Registry.CurrentUser.CreateSubKey(key + @"\shell\open\command")) k.SetValue("", original);
                route = new CallbackRoute(e, scheme);
                Config(second, id1, "false");
                Check(route.Start(second, @"C:\fake\Claude.exe", true, 20), "route arms before launch");
                Check(route.RouteVerified, "Windows resolves the temporary scheme to the unpackaged broker");
                Check(CallbackRoute.CanDispatch(e, second, scheme), "selected profile callback allowed");
                Check(CallbackRoute.DispatchTarget(e, scheme) == second, "permanent protocol handler honors pending profile");
                Check(!CallbackRoute.CanDispatch(e, first, scheme), "wrong profile callback refused");
                Check(!CallbackRoute.CanDispatch(e, second, "different"), "wrong scheme callback refused");
                Check(route.Tick() == null && route.Active, "stale UUID cannot prematurely restore callback");
                Config(second, id2, "true");
                Check(route.Tick() == null && route.Tick() == null && route.Tick() == "signed-in", "login completes only after stable verified identity");
                using (var k = Registry.CurrentUser.OpenSubKey(key + @"\shell\open\command"))
                    Check((string)k.GetValue("") == original, "original callback restored exactly");
                Check(!CallbackRoute.CanDispatch(e, second, scheme), "completed callback ticket revoked");
                Check(CallbackRoute.DispatchTarget(e, scheme) == first, "ordinary links fall back to default after login");
                Config(second, id1, "false"); route.Start(second, @"C:\fake\Claude.exe", true, 20); Config(second, id1, "true");
                route.Tick(); route.Tick();
                Check(route.Tick() == "duplicate", "duplicate login is not reported as a second account");
                route.Start(second, @"C:\fake\Claude.exe", false, -1);
                Check(!CallbackRoute.CanDispatch(e, second, scheme), "expired callback rejected");
                Check(CallbackRoute.DispatchTarget(e, scheme) == first, "expired pending profile cannot hijack later ordinary links");
                Check(route.Tick() == "expired", "expired route restored");
                route.Start(second, @"C:\fake\Claude.exe", false, 20);
                var recovery = new CallbackRoute(e, scheme); recovery.RecoverStale();
                using (var k = Registry.CurrentUser.OpenSubKey(key + @"\shell\open\command"))
                    Check((string)k.GetValue("") == original, "crash recovery restores original callback");
                Check(!CallbackRoute.CanDispatch(e, second, scheme), "crash recovery revokes ticket");
                route.Stop();
                route.Start(first, @"C:\fake\Claude.exe", false, 20);
                Check(CallbackRoute.CanDispatch(e, first, scheme), "default profile also supports re-login routing");
                using (var k = Registry.CurrentUser.CreateSubKey(key + @"\shell\open\command")) k.SetValue("", "external change");
                route.Stop();
                using (var k = Registry.CurrentUser.OpenSubKey(key + @"\shell\open\command"))
                    Check((string)k.GetValue("") == "external change", "stop preserves later external handler changes");
                TestHandoff(root);
                passed += RelayStateTests.Run(Path.Combine(root, "relay-state"));
                passed += RelayTests.Run(Path.Combine(root, "relay-flow"));
                passed += KnowledgeTests.Run(Path.Combine(root, "knowledge"));
                passed += LiveKnowledgeTests.Run(Path.Combine(root, "live"));
                passed += CodeKnowledgeHookTests.Run(Path.Combine(root, "hooks"));
                passed += IdleActivityTests.Run();
                passed += EmailPrivacyTests.Run(root);
                passed += UsageTests.Run(Path.Combine(root, "usage"));
                passed += UsageHistoryReaderTests.Run(Path.Combine(root, "usage-history"));
                passed += UsageDesktopReaderTests.Run(Path.Combine(root, "usage-desktop"));
                passed += UsageMonitorTests.Run(Path.Combine(root, "usage-monitor"));
                passed += TokenStatisticsTests.Run(Path.Combine(root, "token-statistics"));
                passed += PetInteractionTests.Run();
                const string localTask = "local_11111111-1111-4111-8111-111111111111";
                Check(RelayUi.UrlIdentifiesSession("https://claude.ai/epitaxy/" + localTask, localTask), "relay UI accepts exact Code session URL");
                Check(!RelayUi.UrlIdentifiesSession("https://claude.ai/chat/" + localTask, localTask), "relay UI rejects Chat session URL");
                Check(!RelayUi.UrlIdentifiesSession("https://example.com/epitaxy/" + localTask, localTask), "relay UI rejects another origin");
                Check(RelayUi.CanUseTitleFallback("https://claude.ai/epitaxy") && RelayUi.CanUseTitleFallback(null), "relay heading fallback limited to Code shell or absent URL");
                Check(!RelayUi.CanUseTitleFallback("https://claude.ai/chat/example"), "relay heading cannot identify an unrelated Chat");
                Check(!RelayUi.CanUseTitleFallback("https://claude.ai/epitaxy/local_other"), "relay heading cannot override another Code task URL");
                Console.WriteLine("All " + passed + " regression checks passed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally
            {
                if (route != null) route.Stop();
                Registry.CurrentUser.DeleteSubKeyTree(key, false);
                // root is a newly created unique directory, never a real account path.
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
