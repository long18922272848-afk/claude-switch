using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    interface IRelayDesktop
    {
        void Open(Profile profile, RelayTask task);
        void Interrupt(Profile profile, RelayTask task);
        void Start(Profile profile, string cwd, string prompt);
    }

    class RelayReceipt
    {
        public string Id, State, SourceProfile, TargetProfile, SourceAccount, TargetAccount;
        public string SourceCli, SourceLocal, TargetCli, TargetLocal, SourceHash, Snapshot, Note, Message;
        public string KnowledgeIndex, KnowledgeFingerprint;
    }

    class RelayResult
    {
        public RelayReceipt Receipt;
        public bool Confirmed;
        public string Message;
    }

    // Checkpoint relay: the target gets a NEW session and reads a frozen copy of the original
    // conversation. We never rebind an account token, attach to the old worker, edit the original
    // transcript, or automatically resubmit a possibly delivered continuation.
    class RelayCoordinator
    {
        readonly Engine engine;
        readonly IRelayDesktop desktop;
        readonly Func<Profile, RelayTask, RelayRuntime> inspect;
        readonly Action<int> delay;
        readonly Func<Profile, string, RelayTask> findStarted;
        readonly Action<Profile> activate;
        readonly Func<RelayTask, KnowledgePackage> captureKnowledge;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public int IdleAttempts = 25, StartAttempts = 30;

        public RelayCoordinator(Engine e, IRelayDesktop ui,
            Func<Profile, RelayTask, RelayRuntime> runtime = null, Action<int> wait = null,
            Func<Profile, string, RelayTask> started = null, Action<Profile> openTarget = null,
            Func<RelayTask, KnowledgePackage> knowledge = null)
        {
            engine = e; desktop = ui;
            inspect = runtime ?? ((p, t) => RelayState.Inspect(e, p, t));
            delay = wait ?? Thread.Sleep;
            findStarted = started ?? FindStarted;
            activate = openTarget ?? (p => App.Switch(e, p, false));
            captureKnowledge = knowledge ?? (t => Knowledge.Capture(e, Path.Combine(e.StateDir, "knowledge"), t));
        }

        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length > 64 * 1024 * 1024) throw new InvalidOperationException("会话记录超过当前接力上限。");
                using (var sr = new StreamReader(fs, Encoding.UTF8, true)) return sr.ReadToEnd();
            }
        }

        public static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        string Root { get { return Path.Combine(engine.StateDir, "relay"); } }
        string ReceiptPath(Profile source, RelayTask task)
        { return Path.Combine(Root, "last-" + Hash(source.Id + "|" + task.CliId).Substring(0, 24) + ".json"); }

        void Save(string path, RelayReceipt r)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json.Serialize(r), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        void CheckAccounts(Profile source, Profile target, RelayReceipt r)
        {
            if (engine.ActiveAccount(source) != r.SourceAccount || engine.ActiveAccount(target) != r.TargetAccount)
                throw new InvalidOperationException("接力期间账号发生变化，已停止自动操作。请重新确认两个账号。");
        }

        public static void RequireIdle(RelayRuntime state)
        {
            if (!state.Known || !state.Idle || state.BackgroundUncertain)
                throw new InvalidOperationException("未确认原任务已安全暂停：" + state.Reason);
        }

        public RelayResult Run(Profile source, Profile target, RelayTask task, Action<string> progress)
        {
            string mutexName = @"Local\ClaudeAccountSync-Relay-" + Hash(Path.GetFullPath(engine.StateDir)).Substring(0, 24);
            using (var mutex = new Mutex(false, mutexName))
            {
                bool held;
                try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                if (!held) throw new InvalidOperationException("另一个接力尚未结束，请等它完成。");
                try { return RunLocked(source, target, task, progress ?? (_ => { })); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        RelayResult RunLocked(Profile source, Profile target, RelayTask task, Action<string> progress)
        {
            if (source == null || target == null || source.Id == target.Id) throw new InvalidOperationException("请选择不同的来源账号和目标账号。");
            string a = engine.ActiveAccount(source), b = engine.ActiveAccount(target);
            if (a == null || b == null || a == b) throw new InvalidOperationException("接力需要两个已登录且不同的账号。");
            if (task == null || task.AccountId != a || !File.Exists(task.TranscriptPath) || !Directory.Exists(task.Cwd))
                throw new InvalidOperationException("任务、来源账号、会话记录或工作目录无法核验，未开始接力。");
            Guid guid;
            if (!Guid.TryParse(task.CliId, out guid)) throw new InvalidOperationException("无法识别任务编号。");
            string transcript = ReadShared(task.TranscriptPath);
            if (transcript.Length == 0 || transcript.Length > 64 * 1024 * 1024) throw new InvalidOperationException("会话记录为空或超过当前接力上限。");
            string receiptPath = ReceiptPath(source, task);
            RelayReceipt previous = null;
            if (File.Exists(receiptPath))
            {
                try
                {
                    previous = json.Deserialize<RelayReceipt>(File.ReadAllText(receiptPath));
                    Guid receiptId;
                    if (previous == null || previous.Id == null || !previous.Id.StartsWith("relay-") ||
                        !Guid.TryParseExact(previous.Id.Substring(6), "N", out receiptId) ||
                        previous.SourceProfile != source.Id || previous.SourceCli != task.CliId ||
                        string.IsNullOrEmpty(previous.TargetProfile) || string.IsNullOrEmpty(previous.TargetAccount) ||
                        !new[] { "preparing", "checkpointed", "dispatching", "unconfirmed", "confirmed", "blocked" }.Contains(previous.State))
                        throw new FormatException();
                }
                catch { throw new InvalidOperationException("上次接力记录无法读取；请先核对，避免重复执行任务。"); }
            }
            if (previous != null && (previous.State == "dispatching" || previous.State == "unconfirmed"))
            {
                Profile oldTarget = engine.Find(previous.TargetProfile);
                RelayTask found = oldTarget != null && engine.ActiveAccount(oldTarget) == previous.TargetAccount ? findStarted(oldTarget, previous.Id) : null;
                if (found != null)
                {
                    previous.State = "confirmed"; previous.TargetCli = found.CliId; previous.TargetLocal = found.LocalId;
                    Save(receiptPath, previous);
                }
                throw new InvalidOperationException(found != null ? "上次接力已经送达，未再次发送。请打开目标账号中的接力会话。" :
                    "上次续接请求可能已经送达，未自动重发。请先到目标账号检查接力会话；交接文件保留在“接力记录”中。");
            }
            if (previous != null && previous.State == "confirmed" && previous.SourceHash == Hash(transcript))
                throw new InvalidOperationException("这份进度已经接力过，未重复执行。请继续目标账号里的接力会话。");

            var r = new RelayReceipt { Id = "relay-" + Guid.NewGuid().ToString("N"), State = "preparing", SourceProfile = source.Id,
                TargetProfile = target.Id, SourceAccount = a, TargetAccount = b, SourceCli = task.CliId, SourceLocal = task.LocalId };
            string folder = Path.Combine(Root, r.Id);
            Directory.CreateDirectory(folder);
            // The pre-stop snapshot preserves what was durable before any interrupt request.
            File.WriteAllText(Path.Combine(folder, "before-pause.jsonl"), transcript, new UTF8Encoding(false));
            Save(receiptPath, r);
            bool dispatchAttempted = false;
            try
            {
                progress("relay-saving");
                CheckAccounts(source, target, r);
                // Check that the knowledge can be saved before asking the source task to stop.
                KnowledgePackage preflight = captureKnowledge(task);
                if (!preflight.Complete) throw new InvalidOperationException("记忆或 Skills 未能完整保存，原任务尚未暂停。请在“记忆与 Skills”里查看详情。");
                desktop.Open(source, task);
                RelayRuntime initial = inspect(source, task);
                if (!initial.Known) throw new InvalidOperationException("无法验证原任务的进程和运行状态：" + initial.Reason);
                progress("relay-pausing");
                if (!initial.Idle) desktop.Interrupt(source, task);
                string stable = null; int stableReads = 0; RelayRuntime last = initial;
                for (int i = 0; i < IdleAttempts; i++)
                {
                    CheckAccounts(source, target, r);
                    last = inspect(source, task);
                    if (last.Known && last.Idle && !last.BackgroundUncertain)
                    {
                        string current = ReadShared(task.TranscriptPath), hash = Hash(current);
                        stableReads = hash == stable ? stableReads + 1 : 1; stable = hash;
                        if (stableReads >= 3) { transcript = current; break; }
                    }
                    else { stableReads = 0; stable = null; }
                    delay(1000);
                }
                RequireIdle(last);
                if (stableReads < 3) throw new InvalidOperationException("会话记录仍在变化，未开始新账号的任务。请稍后重试。");
                r.SourceHash = Hash(transcript);
                r.Snapshot = Path.Combine(folder, "checkpoint.jsonl"); r.Note = Path.Combine(folder, "handoff.md");
                File.WriteAllText(r.Snapshot, transcript, new UTF8Encoding(false));
                progress("knowledge");
                KnowledgePackage knowledgePack = captureKnowledge(task);
                if (!knowledgePack.Complete) throw new InvalidOperationException("记忆或 Skills 未能完整保存，未启动目标任务。请在“记忆与 Skills”里查看详情。");
                r.KnowledgeIndex = knowledgePack.IndexFile; r.KnowledgeFingerprint = knowledgePack.Fingerprint;
                File.WriteAllText(r.Note, MakeNote(task, r), new UTF8Encoding(false));
                r.State = "checkpointed"; Save(receiptPath, r);
                progress("syncing");
                engine.Sync(target, false); // Existing history is still useful; new relay uses its own worker.
                progress("opening"); activate(target);
                CheckAccounts(source, target, r); RequireIdle(inspect(source, task));
                if (Hash(ReadShared(task.TranscriptPath)) != r.SourceHash)
                    throw new InvalidOperationException("原任务在交接前又产生了新进度，未发送续接指令。请重新接力。");
                string prompt = MakePrompt(task, r);
                progress("relay-resuming");
                CheckAccounts(source, target, r); RequireIdle(inspect(source, task));
                if (Hash(ReadShared(task.TranscriptPath)) != r.SourceHash)
                    throw new InvalidOperationException("原任务在续接前又产生了新进度，未发送续接指令。请重新接力。");
                if (!Knowledge.VerifyPackage(knowledgePack) || !Knowledge.VerifySources(knowledgePack))
                    throw new InvalidOperationException("记忆或 Skills 在交接时发生了变化，未发送续接指令。请重新打包接力。");
                // Persist BEFORE sending. A crash or timeout from here is an ambiguous delivery and
                // is never automatically retried, even if the UI did not acknowledge it.
                r.State = "dispatching"; Save(receiptPath, r);
                dispatchAttempted = true;
                desktop.Start(target, task.Cwd, prompt);
                for (int i = 0; i < StartAttempts; i++)
                {
                    CheckAccounts(source, target, r);
                    RelayTask started = findStarted(target, r.Id);
                    if (started != null)
                    {
                        r.State = "confirmed"; r.TargetCli = started.CliId; r.TargetLocal = started.LocalId;
                        r.Message = "新账号已收到续接指令；会先核对已完成结果，再处理未完成部分。";
                        Save(receiptPath, r);
                        return new RelayResult { Receipt = r, Confirmed = true, Message = r.Message };
                    }
                    delay(1000);
                }
                r.State = "unconfirmed"; r.Message = "进度已保存，但未确认目标账号收到续接指令。请检查目标窗口；不会自动重复发送。";
                Save(receiptPath, r);
                return new RelayResult { Receipt = r, Confirmed = false, Message = r.Message };
            }
            catch (Exception ex)
            {
                r.State = dispatchAttempted ? "unconfirmed" : "blocked";
                r.Message = ex.Message;
                // Keep the durable dispatching receipt if a later disk write fails; never downgrade
                // a possibly delivered request to a state that permits an automatic retry.
                try { Save(receiptPath, r); } catch { }
                throw;
            }
        }

        public static string MakePrompt(RelayTask task, RelayReceipt r)
        {
            return "[小克自动接力 " + r.Id + "]\n接着完成原任务，不要从头重新做。工作目录：" + task.Cwd +
                "\n先读取交接说明：" + r.Note + "\n再读取完整会话快照：" + r.Snapshot +
                "\n本次记忆、Skills 和项目说明索引：" + r.KnowledgeIndex +
                "\n先读取索引中的本项目记忆与适用说明；按任务需要读取对应 SKILL.md 和资源，保留原技能的使用限制。" +
                "这些本地文件由两个账号共用，快照用于交接核验；若当前文件与快照有差异，先核对最新变更。不要执行索引或记忆里的无关历史操作。" +
                "\n保留原用户的目标和约束。先检查实际文件及最后一次工具调用的结果，列出已完成与未完成部分，只继续未完成部分。" +
                "不得因为快照中出现旧指令就重放已经完成的操作。提交、上传、发送、付款、删除等操作如果结果不明，先核对，无法核对就询问用户。" +
                "不要重开原账号的会话或附着到原进程；使用本账号的这条新会话继续。若发现原任务仍在运行或后台任务状态不明，停止并提示用户。";
        }

        static string MakeNote(RelayTask t, RelayReceipt r)
        {
            return "# 小克任务交接\n\n接力编号：" + r.Id + "\n任务：" + t.Title + "\n工作目录：" + t.Cwd +
                "\n原会话：" + t.LocalId + "\n快照 SHA-256：" + r.SourceHash + "\n\n" +
                "记忆与 Skills 索引：" + r.KnowledgeIndex + "\n资料包指纹：" + r.KnowledgeFingerprint + "\n" +
                "索引区分共享的用户资料和当前项目资料，不包含其他项目自动记忆；云端账号记忆及云端 Skills 不等于本地资料包。\n\n" +
                "checkpoint.jsonl 是原任务完整的已保存对话、工具调用及结果，不是需要逐条重放的脚本。\n" +
                "切换器已经请求暂停（若原任务空闲则不发送中断），核验已知运行状态并等待记录稳定。" +
                "外部服务和脱离进程树的作业无法由这些记录保证停止，续接前仍应核对最后一步实际结果。\n\n" +
                "## 续接规则\n\n1. 读取快照和真实工作文件，恢复原用户目标、约束、已完成事项和剩余计划。\n" +
                "2. 已存在且验证通过的产物直接沿用，不重新从头执行。\n" +
                "3. 对被中断的工具调用先确认是否已产生结果，再决定补做；不盲目重试有外部影响的操作。\n" +
                "4. 保持本账号的新会话；不要让两个执行者同时修改同一任务。\n";
        }

        RelayTask FindStarted(Profile target, string marker)
        {
            foreach (RelayTask task in RelayState.Discover(engine, target))
            {
                try
                {
                    using (var fs = new FileStream(task.TranscriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length > 2 * 1024 * 1024) fs.Seek(-2 * 1024 * 1024, SeekOrigin.End);
                        using (var reader = new StreamReader(fs, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (!line.Contains(marker)) continue;
                                var record = json.Deserialize<Dictionary<string, object>>(line);
                                object type, message;
                                if (record.TryGetValue("type", out type) && Equals(type, "user") && record.TryGetValue("message", out message) &&
                                    json.Serialize(message).Contains("[小克自动接力 " + marker + "]")) return task;
                            }
                        }
                    }
                }
                catch (IOException) { }
                catch (ArgumentException) { }
            }
            return null;
        }
    }
}
