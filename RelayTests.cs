using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    // All dependencies are fake and all files are under the supplied temporary root.
    // In particular these tests cannot open, interrupt, or submit to the installed Claude app.
    static class RelayTests
    {
        const string SourceAccount = "11111111-1111-4111-8111-111111111111";
        const string TargetAccount = "22222222-2222-4222-8222-222222222222";
        const string ThirdAccount = "33333333-3333-4333-8333-333333333333";
        const string SourceCli = "44444444-4444-4444-8444-444444444444";
        const string SourceLocal = "local_55555555-5555-4555-8555-555555555555";
        const string TargetCli = "66666666-6666-4666-8666-666666666666";
        const string TargetLocal = "local_77777777-7777-4777-8777-777777777777";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static Dictionary<string, object> D(params object[] pairs)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }
        static string Transcript(string text)
        {
            return Json.Serialize(D("type", "assistant", "sessionId", SourceCli, "message",
                D("role", "assistant", "content", new object[] { D("type", "text", "text", text) }))) + "\n";
        }
        static RelayRuntime Idle() { return new RelayRuntime { Known = true, Idle = true, Reason = "fixture idle" }; }
        static void Config(Profile p, string account)
        {
            Directory.CreateDirectory(p.Dir);
            File.WriteAllText(Path.Combine(p.Dir, "config.json"), Json.Serialize(D("lastKnownAccountUuid", account, "windowSizeWasSignedIn", true)));
        }
        sealed class Desktop : IRelayDesktop
        {
            public readonly List<string> Events = new List<string>();
            public int Opens, Interrupts, Starts;
            public string Prompt, Cwd;
            public Action OnOpen, OnInterrupt, OnStart;
            public void Open(Profile profile, RelayTask task) { Opens++; Events.Add("open"); if (OnOpen != null) OnOpen(); }
            public void Interrupt(Profile profile, RelayTask task) { Interrupts++; Events.Add("interrupt"); if (OnInterrupt != null) OnInterrupt(); }
            public void Start(Profile profile, string cwd, string prompt)
            {
                Starts++; Cwd = cwd; Prompt = prompt; Events.Add("start"); if (OnStart != null) OnStart();
            }
        }
        sealed class Fixture
        {
            public readonly Engine Engine;
            public readonly Profile Source, Target;
            public readonly RelayTask Task, Started;
            public readonly Desktop Ui = new Desktop();
            public readonly RelayCoordinator Coordinator;
            public RelayRuntime Runtime = Idle();
            public Func<Profile, RelayTask, RelayRuntime> RuntimeOverride;
            public Action OnWait, OnActivate;
            public bool Acknowledge = true;
            public int Waits, Activations, Finds;
            public readonly string Initial;
            public Fixture(string parent, string name)
            {
                // Keep synthetic registry paths below .NET Framework's MAX_PATH limit.
                string root = Path.Combine(parent, "f-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Engine = new Engine(Path.Combine(root, "source"), false, Path.Combine(root, "claude"), Path.Combine(root, "state"));
                Source = Engine.Profiles[0]; Target = Engine.AddProfile("Fixture target");
                Config(Source, SourceAccount); Config(Target, TargetAccount);
                string leaf = Path.Combine(Source.Registry, SourceAccount, "organization");
                string targetLeaf = Path.Combine(Target.Registry, TargetAccount, "organization");
                string projects = Path.Combine(Engine.ClaudeDir, "projects", "fixture");
                Directory.CreateDirectory(leaf); Directory.CreateDirectory(targetLeaf); Directory.CreateDirectory(projects);
                Task = new RelayTask { LocalId = SourceLocal, CliId = SourceCli, AccountId = SourceAccount, Cwd = root,
                    Title = "Synthetic task", RegistryPath = Path.Combine(leaf, SourceLocal + ".json"), TranscriptPath = Path.Combine(projects, SourceCli + ".jsonl") };
                Initial = Transcript("already completed fixture work");
                File.WriteAllText(Task.TranscriptPath, Initial, new UTF8Encoding(false));
                File.WriteAllText(Task.RegistryPath, Json.Serialize(D("sessionId", SourceLocal, "cliSessionId", SourceCli,
                    "cwd", root, "title", Task.Title, "lastActivityAt", 1000, "bridgeSessionIds", new object[0])));
                Started = new RelayTask { LocalId = TargetLocal, CliId = TargetCli, AccountId = TargetAccount, Cwd = root, Title = "Synthetic resumed task" };
                Coordinator = new RelayCoordinator(Engine, Ui,
                    (p, t) => RuntimeOverride == null ? Runtime : RuntimeOverride(p, t),
                    milliseconds => { Waits++; Ui.Events.Add("wait"); if (OnWait != null) OnWait(); },
                    (p, marker) => { Finds++; return Acknowledge ? Started : null; },
                    p => { Activations++; Ui.Events.Add("activate"); if (OnActivate != null) OnActivate(); },
                    t => Knowledge.Capture(Engine, Path.Combine(Engine.StateDir, "knowledge"), t, t.Cwd));
                Coordinator.IdleAttempts = 4; Coordinator.StartAttempts = 2;
            }
            public RelayResult Run(Action<string> progress = null) { return Coordinator.Run(Source, Target, Task, progress); }
            public RelayReceipt Receipt()
            {
                string root = Path.Combine(Engine.StateDir, "relay");
                return Json.Deserialize<RelayReceipt>(File.ReadAllText(Directory.GetFiles(root, "last-*.json").Single()));
            }
        }
        static void Check(bool result, string label, ref int count)
        {
            if (!result) throw new Exception("Relay coordinator fixture failed: " + label);
            count++;
        }
        static bool Fails(Action action)
        {
            try { action(); return false; }
            catch (InvalidOperationException) { return true; }
        }
        public static int Run(string temporaryRoot)
        {
            int count = 0;
            var f = new Fixture(temporaryRoot, "busy");
            f.Runtime = new RelayRuntime { Known = true, Idle = false, Reason = "fixture busy" };
            string final = f.Initial + Transcript("last saved step after pause");
            bool beforePauseWasDurable = false;
            f.Ui.OnInterrupt = () =>
            {
                string path = Directory.GetFiles(Path.Combine(f.Engine.StateDir, "relay"), "before-pause.jsonl", SearchOption.AllDirectories).Single();
                beforePauseWasDurable = File.ReadAllText(path) == f.Initial;
                File.WriteAllText(f.Task.TranscriptPath, final); f.Runtime = Idle();
            };
            var phases = new List<string>();
            RelayResult result = f.Run(phases.Add);
            Check(beforePauseWasDurable, "pre-pause checkpoint precedes interrupt", ref count);
            Check(result.Confirmed && f.Ui.Interrupts == 1 && f.Ui.Starts == 1 && f.Ui.Events.IndexOf("interrupt") < f.Ui.Events.IndexOf("start"), "busy task pauses before target start", ref count);
            Check(f.Waits >= 2 && f.Ui.Events.IndexOf("activate") > f.Ui.Events.IndexOf("interrupt"), "three stable observations precede activation", ref count);
            Check(File.ReadAllText(result.Receipt.Snapshot) == final && result.Receipt.SourceHash == RelayCoordinator.Hash(final), "checkpoint contains final durable progress", ref count);
            Check(File.ReadAllText(f.Task.TranscriptPath) == final, "coordinator preserves source transcript", ref count);
            Check(phases.SequenceEqual(new[] { "relay-saving", "relay-pausing", "knowledge", "syncing", "opening", "relay-resuming" }), "ordered progress phases", ref count);
            Check(File.Exists(result.Receipt.KnowledgeIndex) && f.Ui.Prompt.Contains(result.Receipt.KnowledgeIndex), "continuation includes durable scoped memory and skills index", ref count);
            Check(f.Ui.Prompt.Contains(result.Receipt.Id) && f.Ui.Prompt.Contains(result.Receipt.Snapshot) && f.Ui.Prompt.Contains(result.Receipt.Note) && f.Ui.Cwd == f.Task.Cwd, "continuation carries exact snapshot and work directory", ref count);
            Check(result.Receipt.State == "confirmed" && result.Receipt.TargetCli == TargetCli && result.Receipt.TargetLocal == TargetLocal, "confirmation records new session identity", ref count);
            Check(Fails(() => f.Run()) && f.Ui.Starts == 1, "confirmed same checkpoint never resends", ref count);

            f = new Fixture(temporaryRoot, "immutable");
            byte[] original = File.ReadAllBytes(f.Task.TranscriptPath), metadata = File.ReadAllBytes(f.Task.RegistryPath);
            result = f.Run();
            Check(f.Ui.Interrupts == 0 && original.SequenceEqual(File.ReadAllBytes(f.Task.TranscriptPath)) && metadata.SequenceEqual(File.ReadAllBytes(f.Task.RegistryPath)), "idle task original files remain byte-for-byte unchanged", ref count);
            string frozen = File.ReadAllText(result.Receipt.Snapshot);
            File.AppendAllText(f.Task.TranscriptPath, Transcript("later source change"));
            Check(File.ReadAllText(result.Receipt.Snapshot) == frozen && RelayCoordinator.Hash(frozen) == result.Receipt.SourceHash, "snapshot remains frozen after later source edits", ref count);

            f = new Fixture(temporaryRoot, "unknown");
            f.Runtime = new RelayRuntime { Known = false, Reason = "unsupported fixture" };
            Check(Fails(() => f.Run()) && f.Ui.Starts == 0 && f.Ui.Interrupts == 0 && f.Activations == 0, "unknown source state blocks actions", ref count);
            Check(f.Receipt().State == "blocked", "unknown source persists blocked receipt", ref count);

            f = new Fixture(temporaryRoot, "background");
            f.Runtime = new RelayRuntime { Known = true, Idle = true, BackgroundUncertain = true, Reason = "fixture child still active" };
            Check(Fails(() => f.Run()) && f.Ui.Starts == 0 && f.Activations == 0, "idle registry cannot bypass background uncertainty", ref count);

            f = new Fixture(temporaryRoot, "still-busy");
            f.Runtime = new RelayRuntime { Known = true, Idle = false, Reason = "fixture busy" };
            Check(Fails(() => f.Run()) && f.Ui.Interrupts == 1 && f.Ui.Starts == 0, "unacknowledged pause cannot resume target", ref count);

            f = new Fixture(temporaryRoot, "unstable");
            int edits = 0;
            f.OnWait = () => File.AppendAllText(f.Task.TranscriptPath, Transcript("fixture edit " + ++edits));
            Check(Fails(() => f.Run()) && f.Ui.Starts == 0, "changing transcript cannot produce checkpoint", ref count);

            f = new Fixture(temporaryRoot, "source-account-change");
            f.OnActivate = () => Config(f.Source, ThirdAccount);
            Check(Fails(() => f.Run()) && f.Ui.Starts == 0 && f.Receipt().State == "blocked", "source account mutation before send blocks", ref count);

            f = new Fixture(temporaryRoot, "target-account-change");
            f.OnActivate = () => Config(f.Target, ThirdAccount);
            Check(Fails(() => f.Run()) && f.Ui.Starts == 0, "target account mutation before send blocks", ref count);

            f = new Fixture(temporaryRoot, "source-change-before-send");
            f.OnActivate = () => File.AppendAllText(f.Task.TranscriptPath, Transcript("new progress before send"));
            Check(Fails(() => f.Run()) && f.Ui.Starts == 0 && f.Receipt().State == "blocked", "source progress mutation before send blocks", ref count);
            Check(File.ReadAllText(f.Receipt().Snapshot) == f.Initial, "blocked pre-send attempt retains original checkpoint", ref count);

            f = new Fixture(temporaryRoot, "progress-callback-source-change");
            Check(Fails(() => f.Run(phase =>
            {
                if (phase == "relay-resuming") File.AppendAllText(f.Task.TranscriptPath, Transcript("new progress in final callback"));
            })) && f.Ui.Starts == 0 && f.Receipt().State == "blocked", "source mutation in final progress callback blocks dispatch", ref count);

            f = new Fixture(temporaryRoot, "source-restarted");
            f.OnActivate = () => f.Runtime = new RelayRuntime { Known = true, Idle = false, Reason = "fixture resumed elsewhere" };
            Check(Fails(() => f.Run()) && f.Ui.Starts == 0, "source resumed before send blocks", ref count);

            f = new Fixture(temporaryRoot, "unconfirmed");
            f.Acknowledge = false;
            result = f.Run();
            Check(!result.Confirmed && result.Receipt.State == "unconfirmed" && f.Ui.Starts == 1, "unacknowledged send preserves ambiguous delivery", ref count);
            Check(Fails(() => f.Run()) && f.Ui.Starts == 1, "unconfirmed attempt never auto-resends", ref count);
            f.Acknowledge = true;
            Check(Fails(() => f.Run()) && f.Ui.Starts == 1 && f.Receipt().State == "confirmed", "late confirmation resolves receipt without resend", ref count);

            f = new Fixture(temporaryRoot, "start-throws");
            bool dispatchWrittenFirst = false;
            f.Acknowledge = false;
            f.Ui.OnStart = () => { dispatchWrittenFirst = f.Receipt().State == "dispatching"; throw new InvalidOperationException("fixture delivery uncertainty"); };
            Check(Fails(() => f.Run()) && dispatchWrittenFirst && f.Receipt().State == "unconfirmed", "dispatch receipt is durable before potentially throwing send", ref count);
            Check(Fails(() => f.Run()) && f.Ui.Starts == 1, "throwing send never auto-resends", ref count);

            f = new Fixture(temporaryRoot, "account-change-after-send");
            f.Ui.OnStart = () => Config(f.Target, ThirdAccount);
            Check(Fails(() => f.Run()) && f.Ui.Starts == 1 && f.Receipt().State == "unconfirmed", "post-send account mutation remains ambiguous", ref count);
            Config(f.Target, TargetAccount); f.Acknowledge = false;
            Check(Fails(() => f.Run()) && f.Ui.Starts == 1, "restoring target account does not resend ambiguous delivery", ref count);

            f = new Fixture(temporaryRoot, "interrupt-throws");
            f.Runtime = new RelayRuntime { Known = true, Idle = false, Reason = "fixture busy" };
            f.Ui.OnInterrupt = () => { throw new InvalidOperationException("fixture pause failed"); };
            Check(Fails(() => f.Run()) && f.Ui.Starts == 0 && f.Receipt().State == "blocked", "pause failure never starts destination", ref count);
            Check(File.ReadAllText(f.Task.TranscriptPath) == f.Initial, "failed pause preserves original progress", ref count);

            f = new Fixture(temporaryRoot, "wrong-task-account");
            f.Task.AccountId = TargetAccount;
            Check(Fails(() => f.Run()) && f.Ui.Opens == 0 && f.Ui.Starts == 0, "mismatched task account rejects before desktop actions", ref count);

            f = new Fixture(temporaryRoot, "null-prior-receipt");
            string receiptRoot = Path.Combine(f.Engine.StateDir, "relay"); Directory.CreateDirectory(receiptRoot);
            string receiptPath = Path.Combine(receiptRoot, "last-" + RelayCoordinator.Hash(f.Source.Id + "|" + f.Task.CliId).Substring(0, 24) + ".json");
            File.WriteAllText(receiptPath, "null");
            Check(Fails(() => f.Run()) && f.Ui.Opens == 0 && f.Ui.Starts == 0, "null prior receipt fails closed before desktop actions", ref count);
            Check(File.ReadAllText(receiptPath) == "null", "invalid prior receipt is preserved for review", ref count);

            f = new Fixture(temporaryRoot, "knowledge-after-pause");
            string memoryDir = Path.Combine(Path.GetDirectoryName(f.Task.TranscriptPath), "memory"); Directory.CreateDirectory(memoryDir);
            string memoryFile = Path.Combine(memoryDir, "MEMORY.md"); File.WriteAllText(memoryFile, "before pause");
            f.Runtime = new RelayRuntime { Known = true, Idle = false, Reason = "fixture busy" };
            f.Ui.OnInterrupt = () => { File.WriteAllText(memoryFile, "latest memory after pause"); f.Runtime = Idle(); };
            result = f.Run();
            string knowledgeManifest = Path.Combine(Path.GetDirectoryName(result.Receipt.KnowledgeIndex), "manifest.json");
            var knowledgeFiles = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(knowledgeManifest))["files"] as System.Collections.IEnumerable;
            var memoryEntry = knowledgeFiles.Cast<Dictionary<string, object>>().Single(x => (string)x["Source"] == memoryFile);
            string memorySnapshot = Path.Combine(Path.GetDirectoryName(result.Receipt.KnowledgeIndex), ((string)memoryEntry["RelativePath"]).Replace('/', Path.DirectorySeparatorChar));
            Check(result.Confirmed && File.ReadAllText(memorySnapshot) == "latest memory after pause", "relay carries final memory saved after pause", ref count);
            Check(File.ReadAllText(memoryFile) == "latest memory after pause", "memory capture never rewrites shared originals", ref count);

            f = new Fixture(temporaryRoot, "knowledge-change-before-send");
            memoryDir = Path.Combine(Path.GetDirectoryName(f.Task.TranscriptPath), "memory"); Directory.CreateDirectory(memoryDir);
            memoryFile = Path.Combine(memoryDir, "MEMORY.md"); File.WriteAllText(memoryFile, "saved memory");
            Check(Fails(() => f.Run(phase => { if (phase == "relay-resuming") File.WriteAllText(memoryFile, "concurrent new memory"); })) && f.Ui.Starts == 0,
                "memory change immediately before sending blocks target execution", ref count);

            f = new Fixture(temporaryRoot, "new-skill-before-send");
            string newSkill = Path.Combine(f.Engine.ClaudeDir, "skills", "new-skill");
            Check(Fails(() => f.Run(phase => { if (phase == "relay-resuming") { Directory.CreateDirectory(newSkill); File.WriteAllText(Path.Combine(newSkill, "SKILL.md"), "new skill"); } })) && f.Ui.Starts == 0,
                "new skill created after packaging blocks stale target execution", ref count);
            return count;
        }
    }
}
