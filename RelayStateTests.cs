using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    // Fixtures only. No real Claude data, processes, accounts, or windows are touched.
    static class RelayStateTests
    {
        const string Cli = "11111111-1111-4111-8111-111111111111";
        const string Local = "local_22222222-2222-4222-8222-222222222222";
        const string Account = "33333333-3333-4333-8333-333333333333";
        static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
        static Dictionary<string, object> D(params object[] values)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < values.Length; i += 2) d[(string)values[i]] = values[i + 1];
            return d;
        }
        static string Message(string role, object[] blocks, object detail = null)
        {
            var d = D("type", role, "sessionId", Cli, "message", D("role", role, "content", blocks));
            if (detail != null) d["toolUseResult"] = detail;
            return Json(d) + "\n";
        }
        static string Use(string id, string name, Dictionary<string, object> input)
        {
            return Message("assistant", new object[] { D("type", "tool_use", "id", id, "name", name, "input", input) });
        }
        static string Result(string id, object detail = null, bool error = false)
        {
            return Message("user", new object[] { D("type", "tool_result", "tool_use_id", id, "content", "fixture", "is_error", error) }, detail);
        }
        static void Check(bool ok, string name, ref int count)
        {
            if (!ok) throw new Exception("RelayState fixture failed: " + name);
            count++;
        }
        public static int Run(string temporaryRoot)
        {
            int count = 0;
            string idle = Message("assistant", new object[] { D("type", "text", "text", "fixture") });
            var state = RelayState.ParseTranscript(idle, Cli);
            Check(state.Known && state.Pending == 0 && !state.BackgroundUncertain, "simple saved completion", ref count);
            string call = Use("u1", "PowerShell", D("command", "fixture"));
            state = RelayState.ParseTranscript(call, Cli);
            Check(state.Known && state.Pending == 1, "unfinished foreground tool blocks idle", ref count);
            state = RelayState.ParseTranscript(call + Result("u1"), Cli);
            Check(state.Known && state.Pending == 0 && !state.BackgroundUncertain, "foreground result closes call", ref count);
            state = RelayState.ParseTranscript(call + Result("u1") + call, Cli);
            Check(state.Known && state.Pending == 0, "replayed call is not reopened", ref count);
            string bg = Use("u2", "PowerShell", D("command", "fixture", "run_in_background", true)) + Result("u2", D("backgroundTaskId", "b12345678", "interrupted", false));
            state = RelayState.ParseTranscript(bg, Cli);
            Check(state.Known && state.Pending == 0 && state.BackgroundUncertain, "background shell survives foreground result", ref count);
            state = RelayState.ParseTranscript(bg + Message("user", new object[] { D("type", "text", "text", "<task-notification><task-id>b12345678</task-id><status>completed</status></task-notification>") }), Cli);
            Check(state.BackgroundUncertain, "text notification cannot prove process exit", ref count);
            string stop = Use("u3", "TaskStop", D("task_id", "b12345678"));
            state = RelayState.ParseTranscript(bg + stop + Result("u3", D("task_id", "b12345678", "task_type", "local_bash")), Cli);
            Check(state.Known && !state.BackgroundUncertain && state.Pending == 0, "matching structural stop result", ref count);
            state = RelayState.ParseTranscript(bg + stop + Result("u3", D("task_id", "b12345678"), true), Cli);
            Check(state.BackgroundUncertain, "failed stop leaves background uncertain", ref count);
            state = RelayState.ParseTranscript(bg + stop + Result("u3", D("task_id", "b99999999")), Cli);
            Check(state.BackgroundUncertain, "different task stop cannot clear original", ref count);
            state = RelayState.ParseTranscript(Use("a1", "Agent", D("prompt", "fixture")) + Result("a1"), Cli);
            Check(state.BackgroundUncertain, "agent without lifecycle proof remains uncertain", ref count);
            state = RelayState.ParseTranscript(Use("a1", "Agent", D("prompt", "fixture", "run_in_background", true)) +
                Result("a1", D("status", "async_launched", "agentId", "a12345678")), Cli);
            Check(state.Known && state.Pending == 0 && state.BackgroundUncertain, "finished launch result cannot clear background agent", ref count);
            string cron = Use("cron1", "CronCreate", D("cron", "* * * * *", "prompt", "fixture")) +
                Result("cron1", D("id", "fixture-schedule", "status", "completed"));
            state = RelayState.ParseTranscript(cron, Cli);
            Check(state.Known && state.Pending == 0 && state.BackgroundUncertain, "completed schedule creation still leaves future work active", ref count);
            state = RelayState.ParseTranscript(cron + Message("assistant", new object[] { D("type", "text", "text", "fixture completion") }), Cli);
            Check(state.BackgroundUncertain, "later assistant completion cannot clear scheduled work", ref count);
            state = RelayState.ParseTranscript(Use("a1", "Workflow", D()) + Result("a1", D("status", "completed", "taskId", "w12345678")), Cli);
            Check(state.Known && !state.BackgroundUncertain, "workflow terminal result", ref count);
            Check(!RelayState.ParseTranscript(idle + "{", Cli).Known, "partial JSON refuses", ref count);
            Check(!RelayState.ParseTranscript(Result("missing"), Cli).Known, "orphan result refuses", ref count);
            Check(!RelayState.ParseTranscript("{\"type\":\"future-runtime-shape\"}\n", Cli).Known, "unknown record shape refuses", ref count);
            Check(!RelayState.ParseTranscript(idle.Replace(Cli, Account), Cli).Known, "other session refuses", ref count);
            Check(!RelayState.ParseTranscript(Use("x", "FutureTool", D()) + Message("assistant", new object[] { D("type", "future_tool_block") }), Cli).Known, "unknown content block refuses", ref count);

            string root = Path.Combine(temporaryRoot, "relay-state-fixtures");
            var engine = new Engine(Path.Combine(root, "profile"), false, Path.Combine(root, "claude"), Path.Combine(root, "state"));
            var profile = engine.Profiles[0];
            Directory.CreateDirectory(profile.Dir);
            File.WriteAllText(Path.Combine(profile.Dir, "config.json"), Json(D("lastKnownAccountUuid", Account, "windowSizeWasSignedIn", true)));
            string leaf = Path.Combine(profile.Registry, Account, "organization"), project = Path.Combine(engine.ClaudeDir, "projects", "project");
            Directory.CreateDirectory(leaf); Directory.CreateDirectory(project);
            string metadata = Path.Combine(leaf, Local + ".json");
            File.WriteAllText(metadata, Json(D("sessionId", Local, "cliSessionId", Cli, "cwd", root, "title", "fixture")), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(project, Cli + ".jsonl"), idle);
            var discovered = RelayState.Discover(engine, profile);
            Check(discovered.Count == 1 && discovered[0].RegistryPath == metadata, "active account discovery and UTF-8 BOM", ref count);
            string duplicate = Path.Combine(engine.ClaudeDir, "projects", "duplicate"); Directory.CreateDirectory(duplicate);
            string duplicateFile = Path.Combine(duplicate, Cli + ".jsonl"); File.WriteAllText(duplicateFile, idle);
            Check(RelayState.Discover(engine, profile).Count == 0, "ambiguous transcript excluded", ref count);
            File.Delete(duplicateFile);
            File.WriteAllText(metadata, Json(D("sessionId", Local, "cliSessionId", "..\\escape", "cwd", root)));
            Check(RelayState.Discover(engine, profile).Count == 0, "invalid CLI ID excluded", ref count);
            File.WriteAllText(metadata, Json(D("sessionId", Local, "cliSessionId", Cli, "cwd", root, "isArchived", true)));
            Check(RelayState.Discover(engine, profile).Count == 0, "archived task excluded", ref count);

            var task = discovered[0];
            var record = D("pid", 42, "sessionId", Cli, "hostSessionId", Local, "procStart", "123456", "entrypoint", "claude-desktop", "kind", "interactive", "cwd", root, "status", "idle");
            var records = new List<Dictionary<string, object>> { record };
            var processes = new List<RelayState.ProcessInfo> {
                new RelayState.ProcessInfo { Pid = 42, ParentPid = 41, Start = "123456", Name = "claude.exe", Exe = @"C:\fixture\claude-code\1\claude.exe", CommandLine = "claude.exe --resume " + Cli },
                new RelayState.ProcessInfo { Pid = 41, ParentPid = 1, Start = "123450", Name = "Claude.exe", Exe = @"C:\fixture\desktop\Claude.exe", CommandLine = "Claude.exe --user-data-dir=\"" + profile.Dir + "\"" }
            };
            state = RelayState.ParseTranscript(idle, Cli);
            Func<RelayRuntime> inspect = () => RelayState.Evaluate(profile, task, records, processes, state);
            Check(inspect().Known && inspect().Idle, "verified idle owned runtime", ref count);
            record["status"] = "busy"; Check(inspect().Known && !inspect().Idle, "busy runtime", ref count);
            record["status"] = "waiting"; Check(inspect().Known && !inspect().Idle, "permission or input wait", ref count);
            record["status"] = "shell"; Check(inspect().Known && !inspect().Idle, "shell runtime", ref count);
            record["status"] = "future"; Check(!inspect().Known, "unknown status refuses", ref count);
            record["status"] = "idle"; record["procStart"] = "stale"; Check(!inspect().Known, "PID reuse refuses", ref count);
            record["procStart"] = "123456";
            processes.Add(new RelayState.ProcessInfo { Pid = 43, ParentPid = 42, Name = "powershell.exe" });
            Check(inspect().Known && !inspect().Idle && inspect().BackgroundUncertain, "direct child process blocks resume", ref count);
            processes.RemoveAt(2);
            processes[1].CommandLine = "Claude.exe --user-data-dir=C:\\other";
            Check(!inspect().Known, "other account parent refuses", ref count);
            processes[1].CommandLine = "Claude.exe --user-data-dir=\"" + profile.Dir + "\"";
            state = RelayState.ParseTranscript(bg, Cli);
            Check(inspect().Known && !inspect().Idle && inspect().BackgroundUncertain, "idle registry cannot hide background shell", ref count);
            state = RelayState.ParseTranscript(call, Cli);
            Check(inspect().Known && !inspect().Idle, "idle registry cannot hide pending tool", ref count);
            records.Clear(); Check(!inspect().Known, "absent runtime remains unknown", ref count);
            return count;
        }
    }
}
