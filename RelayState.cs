using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    class RelayTask
    {
        public string LocalId, CliId, Title, Cwd, TranscriptPath, AccountId, RegistryPath;
    }

    class RelayRuntime
    {
        public bool Known, Idle, BackgroundUncertain;
        public string Reason;
        public int Pid;
        public string ProcStart;
    }

    // Read-only observations. No transcript, registry, process or credential is modified here.
    static class RelayState
    {
        const int MaxRecord = 2 * 1024 * 1024;
        const long MaxTranscript = 64L * 1024 * 1024;
        internal class ProcessInfo
        {
            public int Pid, ParentPid;
            public string Name, Exe, CommandLine, Start;
        }
        internal class TranscriptState
        {
            public bool Known = true, BackgroundUncertain;
            public int Pending;
            public string Reason;
        }
        sealed class Tool
        {
            public string Name, TaskId;
            public bool Background;
        }
        static readonly HashSet<string> RecordTypes = new HashSet<string>(StringComparer.Ordinal) {
            "assistant", "user", "system", "progress", "summary", "attachment", "agent-name", "mode",
            "atis-latch", "bridge-session", "api-request", "api-request-blob", "api-request-shape",
            "custom-title", "last-prompt", "file-history-delta", "file-history-snapshot", "queue-operation",
            "relocated", "tools-used", "cost-state", "pr-link", "tag", "agent-color", "agent-setting"
        };

        static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaxRecord, RecursionLimit = 128 };
        }
        static Dictionary<string, object> Dict(object o) { return o as Dictionary<string, object>; }
        static object Value(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? v : null;
        }
        static string Str(Dictionary<string, object> d, string k) { return Value(d, k) as string; }
        static bool True(Dictionary<string, object> d, string k) { return Equals(Value(d, k), true); }
        internal static bool Uuid(string s)
        {
            Guid g; return s != null && s.Length == 36 && Guid.TryParseExact(s, "D", out g);
        }
        internal static bool LocalId(string s) { return s != null && s.StartsWith("local_", StringComparison.Ordinal) && Uuid(s.Substring(6)); }
        static string Full(string p) { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        static bool SamePath(string a, string b)
        {
            try { return a != null && b != null && string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        static bool Inside(string path, string root)
        {
            try
            {
                string r = Full(root), p = Full(path);
                if (!p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                string cursor = p;
                while (cursor != null)
                {
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return false;
                    if (SamePath(cursor, r)) return true;
                    cursor = Path.GetDirectoryName(cursor);
                }
            }
            catch { }
            return false;
        }
        static string ReadStable(string path, long limit)
        {
            var before = new FileInfo(path);
            long length = before.Length; DateTime modified = before.LastWriteTimeUtc;
            if (length > limit) throw new IOException("State exceeds supported size.");
            string text;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(f, new UTF8Encoding(false, true), true))
            {
                var builder = new StringBuilder(); var buffer = new char[8192]; int count;
                while ((count = r.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (builder.Length + (long)count > limit) throw new IOException("State exceeds supported size.");
                    builder.Append(buffer, 0, count);
                }
                text = builder.ToString();
            }
            var after = new FileInfo(path);
            if (after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("State changed while reading.");
            return text;
        }

        public static List<RelayTask> Discover(Engine e, Profile p)
        {
            var tasks = new List<RelayTask>();
            string account = e.ActiveAccount(p);
            if (!Uuid(account)) return tasks;
            string registry = Path.Combine(p.Registry, account), projects = Path.Combine(e.ClaudeDir, "projects");
            if (!Directory.Exists(registry) || !Directory.Exists(projects)) return tasks;
            try
            {
                var transcripts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string folder in Directory.GetDirectories(projects))
                {
                    if (!Inside(folder, projects)) continue;
                    foreach (string path in Directory.GetFiles(folder, "*.jsonl"))
                    {
                        string id = Path.GetFileNameWithoutExtension(path);
                        if (!Uuid(id) || !Inside(path, projects)) continue;
                        List<string> paths;
                        if (!transcripts.TryGetValue(id, out paths)) transcripts[id] = paths = new List<string>();
                        paths.Add(path);
                    }
                }
                foreach (string leaf in Directory.GetDirectories(registry))
                {
                    if (!Inside(leaf, registry)) continue;
                    foreach (string path in Directory.GetFiles(leaf, "local_*.json"))
                    {
                        try
                        {
                            if (!Inside(path, registry)) continue;
                            var d = Serializer().Deserialize<Dictionary<string, object>>(ReadStable(path, MaxRecord));
                            string id = Str(d, "sessionId"), cli = Str(d, "cliSessionId"), cwd = Str(d, "cwd");
                            if (!LocalId(id) || !Uuid(cli) || True(d, "isArchived") || id != Path.GetFileNameWithoutExtension(path)) continue;
                            if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd) || !Directory.Exists(cwd)) continue;
                            List<string> candidates;
                            if (!transcripts.TryGetValue(cli, out candidates) || candidates.Count != 1) continue;
                            string title = Str(d, "title") ?? "Claude Code 任务";
                            tasks.Add(new RelayTask { LocalId = id, CliId = cli, Title = title.Length > 512 ? title.Substring(0, 512) : title,
                                Cwd = cwd, TranscriptPath = candidates[0], AccountId = account, RegistryPath = path });
                        }
                        catch { /* An unreadable or changing entry is not an eligible task. */ }
                    }
                }
            }
            catch { return new List<RelayTask>(); }
            return tasks.GroupBy(t => t.LocalId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
                .Select(g => g.Single()).OrderBy(t => t.Title, StringComparer.CurrentCulture).ToList();
        }

        static RelayRuntime Unknown(string reason) { return new RelayRuntime { Reason = reason, BackgroundUncertain = true }; }

        internal class CandidateRecord
        {
            public int Pid;
            public Dictionary<string, object> Data;
        }

        // Selection only: do not parse the transcript or require a recognized runtime status here.
        // An unsupported or unfinished task must reach Inspect and stop the relay, not disappear
        // from the candidate list and let an account switch skip its still-running work.
        public static List<RelayTask> LiveCandidates(Engine e, Profile p)
        {
            List<RelayTask> tasks = Discover(e, p);
            if (tasks.Count == 0) return tasks;
            if (tasks.Count > 4096) throw new IOException("任务记录过多，无法安全选择自动接力任务。");
            string directory = Path.Combine(e.ClaudeDir, "sessions");
            if (!Directory.Exists(directory)) return new List<RelayTask>();
            List<ProcessInfo> processes = ReadProcesses();
            if (processes.Count > 32768) throw new IOException("进程记录过多，无法核验接力任务。");
            var livePids = new HashSet<int>(processes.Select(process => process.Pid));
            var records = new List<CandidateRecord>();
            int count = 0;
            foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (++count > 4096) throw new IOException("任务运行记录过多，无法安全选择自动接力任务。");
                int pid;
                string name = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0 ||
                    pid.ToString(CultureInfo.InvariantCulture) != name || !livePids.Contains(pid)) continue;
                if (!Inside(file, directory)) throw new IOException("任务运行记录位置无法核验。");
                Dictionary<string, object> data = null;
                try { data = Serializer().Deserialize<Dictionary<string, object>>(ReadStable(file, 262144)); }
                catch (IOException) { } // Include an identifiable live worker; Inspect supplies the error.
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                records.Add(new CandidateRecord { Pid = pid, Data = data });
            }
            return SelectLiveCandidates(p, tasks, records, processes);
        }

        internal static List<RelayTask> SelectLiveCandidates(Profile profile, List<RelayTask> tasks,
            List<CandidateRecord> records, List<ProcessInfo> processes)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CandidateRecord record in records)
            {
                ProcessInfo worker = processes.FirstOrDefault(process => process.Pid == record.Pid);
                if (worker == null || KnownOtherProfile(profile, worker, processes)) continue;
                string cli = Str(record.Data, "sessionId"), local = Str(record.Data, "hostSessionId");
                var matches = tasks.Where(task =>
                    (!string.IsNullOrEmpty(cli) && string.Equals(task.CliId, cli, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(local) && string.Equals(task.LocalId, local, StringComparison.OrdinalIgnoreCase))).ToList();
                bool cliWorker = string.Equals(worker.Name, "claude.exe", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(worker.Exe) && worker.Exe.IndexOf("\\claude-code\\", StringComparison.OrdinalIgnoreCase) >= 0;
                if (matches.Count == 0 && cliWorker)
                {
                    string[] args = Args(worker.CommandLine);
                    string resume = Option(args, "--resume"), assigned = Option(args, "--session-id");
                    matches = tasks.Where(task => string.Equals(task.CliId, resume, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(task.CliId, assigned, StringComparison.OrdinalIgnoreCase)).ToList();
                    // A live Claude worker with an unreadable registry and no usable session ID
                    // cannot be safely ignored. Ambiguity is left for the caller/strict inspection.
                    if (matches.Count == 0 && (record.Data == null || (string.IsNullOrEmpty(cli) && string.IsNullOrEmpty(local))))
                        matches = tasks;
                }
                foreach (RelayTask task in matches) candidates.Add(task.LocalId);
            }
            return tasks.Where(task => candidates.Contains(task.LocalId)).ToList();
        }

        static bool KnownOtherProfile(Profile profile, ProcessInfo worker, List<ProcessInfo> processes)
        {
            ProcessInfo parent = processes.FirstOrDefault(process => process.Pid == worker.ParentPid);
            if (parent == null || !string.Equals(parent.Name, "claude.exe", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(parent.Exe) || string.IsNullOrEmpty(parent.CommandLine) ||
                parent.Exe.IndexOf("\\claude-code\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (parent.Exe.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                 parent.Exe.IndexOf("\\AnthropicClaude\\", StringComparison.OrdinalIgnoreCase) < 0)) return false;
            string[] args = Args(parent.CommandLine);
            if (args.Length == 0 || !SamePath(args[0], parent.Exe) ||
                args.Any(arg => arg == "--type" || arg.StartsWith("--type=", StringComparison.Ordinal))) return false;
            int directoryOptions = args.Count(arg => arg == "--user-data-dir" || arg.StartsWith("--user-data-dir=", StringComparison.Ordinal));
            if (directoryOptions > 1 || args[args.Length - 1] == "--user-data-dir") return false;
            string userData = Option(args, "--user-data-dir");
            if (userData == null) return !profile.IsRealDefault;
            if (string.IsNullOrWhiteSpace(userData) || !Path.IsPathRooted(userData)) return false;
            // App.Belongs also treats an explicit default-profile path as belonging to that
            // profile. Its stricter runtime checks remain Inspect's responsibility.
            return !SamePath(userData, profile.Dir);
        }

        public static RelayRuntime Inspect(Engine e, Profile p, RelayTask task)
        {
            try
            {
                if (task == null || !LocalId(task.LocalId) || !Uuid(task.CliId) ||
                    !string.Equals(e.ActiveAccount(p), task.AccountId, StringComparison.OrdinalIgnoreCase))
                    return Unknown("账号或任务身份发生变化。");
                RelayTask current = Discover(e, p).FirstOrDefault(t => t.LocalId == task.LocalId && t.CliId == task.CliId);
                if (current == null || !SamePath(current.TranscriptPath, task.TranscriptPath) || !SamePath(current.Cwd, task.Cwd))
                    return Unknown("任务保存位置发生变化，尚不能确认状态。");
                var transcript = ParseTranscript(ReadStable(task.TranscriptPath, MaxTranscript), task.CliId);
                if (!transcript.Known) return Unknown(transcript.Reason);
                string directory = Path.Combine(e.ClaudeDir, "sessions");
                if (!Directory.Exists(directory)) return Unknown("没有可核验的任务运行记录。");
                var records = new List<Dictionary<string, object>>();
                foreach (string file in Directory.GetFiles(directory, "*.json"))
                {
                    int pid;
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out pid) || pid <= 0 ||
                        pid.ToString(CultureInfo.InvariantCulture) != Path.GetFileNameWithoutExtension(file)) continue;
                    if (!Inside(file, directory)) return Unknown("运行记录位置无法核验。");
                    var record = Serializer().Deserialize<Dictionary<string, object>>(ReadStable(file, 262144));
                    if (!string.Equals(Str(record, "sessionId"), task.CliId, StringComparison.OrdinalIgnoreCase)) continue;
                    int declared;
                    if (!int.TryParse(Convert.ToString(Value(record, "pid"), CultureInfo.InvariantCulture), out declared) || declared != pid)
                        return Unknown("运行记录的进程身份不一致。");
                    records.Add(record);
                }
                var processes = ReadProcesses();
                return Evaluate(p, task, records, processes, transcript);
            }
            catch { return Unknown("暂时无法完整读取任务状态，已保留进度并停止自动接力。"); }
        }

        static List<ProcessInfo> ReadProcesses()
        {
            var list = new List<ProcessInfo>();
            using (var query = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine, ExecutablePath FROM Win32_Process"))
            using (var objects = query.Get())
                foreach (ManagementObject o in objects)
                {
                    var info = new ProcessInfo { Pid = Convert.ToInt32(o["ProcessId"]), ParentPid = Convert.ToInt32(o["ParentProcessId"]),
                        Name = o["Name"] as string, Exe = o["ExecutablePath"] as string, CommandLine = o["CommandLine"] as string };
                    if (string.Equals(info.Name, "claude.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        try { using (var process = Process.GetProcessById(info.Pid)) info.Start = process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture); }
                        catch { }
                    }
                    list.Add(info);
                }
            return list;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
        static string[] Args(string command)
        {
            if (string.IsNullOrEmpty(command)) return new string[0];
            int count; IntPtr pointer = CommandLineToArgvW(command, out count);
            if (pointer == IntPtr.Zero) return new string[0];
            try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))).ToArray(); }
            finally { LocalFree(pointer); }
        }
        static string Option(string[] args, string key)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith(key + "=", StringComparison.Ordinal)) return args[i].Substring(key.Length + 1);
                if (args[i] == key && i + 1 < args.Length) return args[i + 1];
            }
            return null;
        }
        internal static RelayRuntime Evaluate(Profile profile, RelayTask task, List<Dictionary<string, object>> records,
            List<ProcessInfo> processes, TranscriptState transcript)
        {
            if (!transcript.Known) return Unknown(transcript.Reason);
            var live = records.Where(r => processes.Any(p => p.Pid == Convert.ToInt32(Value(r, "pid")))).ToList();
            if (live.Count != 1) return Unknown(live.Count == 0 ? "任务进程未能核验，需要重新打开该任务后再接力。" : "同一任务存在多个运行实例，已停止接力。");
            var record = live[0]; int pid = Convert.ToInt32(Value(record, "pid"));
            var process = processes.First(p => p.Pid == pid);
            string start = Str(record, "procStartFt") ?? Str(record, "procStart");
            if (string.IsNullOrEmpty(start) || process.Start != start || !string.Equals(Str(record, "sessionId"), task.CliId, StringComparison.OrdinalIgnoreCase) || Str(record, "entrypoint") != "claude-desktop" ||
                Str(record, "kind") != "interactive" || Str(record, "hostSessionId") != task.LocalId || !SamePath(Str(record, "cwd"), task.Cwd))
                return Unknown("运行任务的身份或进程启动时间无法核验。");
            var args = Args(process.CommandLine);
            string resumed = Option(args, "--resume"), assigned = Option(args, "--session-id");
            if (!string.Equals(process.Name, "claude.exe", StringComparison.OrdinalIgnoreCase) || process.Exe == null ||
                process.Exe.IndexOf("\\claude-code\\", StringComparison.OrdinalIgnoreCase) < 0 ||
                !(string.Equals(resumed, task.CliId, StringComparison.OrdinalIgnoreCase) || string.Equals(assigned, task.CliId, StringComparison.OrdinalIgnoreCase)))
                return Unknown("无法确认任务进程对应当前会话。");
            var parent = processes.FirstOrDefault(p => p.Pid == process.ParentPid);
            if (parent == null || !string.Equals(parent.Name, "claude.exe", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(parent.Exe) || parent.Exe.IndexOf("\\claude-code\\", StringComparison.OrdinalIgnoreCase) >= 0 || string.IsNullOrEmpty(parent.CommandLine))
                return Unknown("无法确认任务属于这个账号窗口。");
            var parentArgs = Args(parent.CommandLine);
            string userData = Option(parentArgs, "--user-data-dir");
            if (Option(parentArgs, "--type") != null || (profile.IsRealDefault ? userData != null : !SamePath(userData, profile.Dir)))
                return Unknown("任务进程属于其他账号窗口。");
            string status = Str(record, "status");
            if (status != "idle" && status != "busy" && status != "waiting" && status != "shell") return Unknown("任务状态版本暂不受支持。");
            bool background = transcript.BackgroundUncertain || processes.Any(p => p.ParentPid == pid);
            return new RelayRuntime { Known = true, Idle = status == "idle" && transcript.Pending == 0 && !background,
                BackgroundUncertain = background, Pid = pid, ProcStart = start,
                Reason = background ? "仍有后台任务或子进程，暂不启动下一个账号。" : transcript.Pending > 0 ? "尚有工具调用未保存结束记录。" :
                    status == "idle" ? "原任务已空闲，未发现未结束的本地工具调用。" : status == "waiting" ? "原任务正在等待输入或授权。" : "原任务仍在运行。" };
        }

        static bool Terminal(string status) { return status == "completed" || status == "failed" || status == "killed"; }
        internal static TranscriptState ParseTranscript(string text, string cliId)
        {
            var state = new TranscriptState();
            var tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
            var done = new HashSet<string>(StringComparer.Ordinal);
            var background = new HashSet<string>(StringComparer.Ordinal);
            bool schedulerSeen = false;
            try
            {
                if (text == null || text.Length > MaxTranscript || !Uuid(cliId)) throw new FormatException();
                int lines = 0, messages = 0;
                using (var reader = new StringReader(text))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        if (++lines > 200000 || line.Length > MaxRecord) throw new FormatException();
                        var d = Serializer().Deserialize<Dictionary<string, object>>(line);
                        string type = Str(d, "type"), session = Str(d, "sessionId");
                        if (type == null || !RecordTypes.Contains(type) || (session != null && !string.Equals(session, cliId, StringComparison.OrdinalIgnoreCase))) throw new FormatException();
                        if (type != "assistant" && type != "user") continue;
                        messages++;
                        var message = Dict(Value(d, "message"));
                        if (message == null) throw new FormatException();
                        object content = Value(message, "content");
                        if (content is string) continue;
                        var blocks = content as IEnumerable;
                        if (blocks == null) throw new FormatException();
                        var results = new List<Dictionary<string, object>>();
                        foreach (object item in blocks)
                        {
                            var block = Dict(item); string blockType = Str(block, "type");
                            if (blockType == "tool_use")
                            {
                                string id = Str(block, "id"), name = Str(block, "name"); var input = Dict(Value(block, "input"));
                                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || input == null) throw new FormatException();
                                string schedulerName = name.Substring(name.LastIndexOf("__", StringComparison.Ordinal) < 0 ? 0 : name.LastIndexOf("__", StringComparison.Ordinal) + 2);
                                if (string.Equals(schedulerName, "CronCreate", StringComparison.OrdinalIgnoreCase) ||
                                    schedulerName.StartsWith("Schedule", StringComparison.OrdinalIgnoreCase)) schedulerSeen = true;
                                Tool previous;
                                if (tools.TryGetValue(id, out previous)) { if (previous.Name != name) throw new FormatException(); continue; }
                                bool bg = True(input, "run_in_background") || True(input, "background") || True(input, "isAsync") || name == "Agent" || name == "Task" || name == "Workflow";
                                tools[id] = new Tool { Name = name, TaskId = Str(input, "task_id"), Background = bg };
                                if (bg) background.Add(id);
                            }
                            else if (blockType == "tool_result") results.Add(block);
                            else if (blockType != "text" && blockType != "thinking" && blockType != "redacted_thinking" && blockType != "image" && blockType != "document") throw new FormatException();
                        }
                        foreach (var result in results)
                        {
                            string id = Str(result, "tool_use_id"); Tool tool;
                            if (id == null || !tools.TryGetValue(id, out tool)) throw new FormatException();
                            done.Add(id);
                            // Top-level structured results are trusted only for their single matching tool result.
                            var detail = results.Count == 1 ? Dict(Value(d, "toolUseResult")) : null;
                            string taskId = Str(detail, "backgroundTaskId") ?? Str(detail, "taskId") ?? Str(detail, "task_id");
                            if (!string.IsNullOrEmpty(taskId) && tool.Name != "TaskStop" && tool.Name != "TaskOutput")
                            {
                                background.Remove(id); background.Add(taskId); tool.TaskId = taskId;
                            }
                            if (Terminal(Str(detail, "status")))
                            {
                                background.Remove(id);
                                if (taskId != null) background.Remove(taskId);
                                if (tool.TaskId != null) background.Remove(tool.TaskId);
                            }
                            if (tool.Name == "TaskStop" && !True(result, "is_error") && detail != null &&
                                tool.TaskId != null && Str(detail, "task_id") == tool.TaskId) background.Remove(tool.TaskId);
                            // Error text or a task-notification string is not proof that an async process exited.
                        }
                    }
                }
                if (messages == 0) throw new FormatException();
                state.Pending = tools.Keys.Count(id => !done.Contains(id));
                // A successful scheduling request means the scheduler may still fire later. Its
                // terminal tool result or later assistant prose does not prove scheduled work ended.
                state.BackgroundUncertain = schedulerSeen || background.Count != 0;
            }
            catch { state.Known = false; state.BackgroundUncertain = true; state.Reason = "任务记录不完整或包含尚不支持的格式，已停止自动接力。"; }
            return state;
        }
    }
}
