using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    // Only synthetic files and direct handler calls. Never launches Claude, a hook, or any task.
    static class CodeKnowledgeHookTests
    {
        const string Session = "11111111-1111-4111-8111-111111111111";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static int passed;
        static void Check(bool ok, string name) { if (!ok) throw new Exception("Knowledge hook fixture failed: " + name); passed++; }
        static Dictionary<string, object> D(params object[] values)
        { var d = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) d[(string)values[i]] = values[i + 1]; return d; }
        static Dictionary<string, object> Obj(string text) { return Json.Deserialize<Dictionary<string, object>>(text); }
        static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
        static IEnumerable<object> Items(object value) { return ((IEnumerable)value).Cast<object>(); }
        sealed class Fixture
        {
            public string Root, Claude, State, Cwd, Transcript, Settings, Executable;
            public Engine Engine;
            public Fixture(string parent)
            {
                Root = Path.Combine(parent, "h" + Guid.NewGuid().ToString("N").Substring(0, 5));
                Claude = Path.Combine(Root, "c"); State = Path.Combine(Root, "s"); Cwd = Path.Combine(Root, "w");
                Directory.CreateDirectory(Claude); Directory.CreateDirectory(Cwd);
                Engine = new Engine(Path.Combine(Root, "p"), false, Claude, State);
                Transcript = Write("projects\\fixture\\" + Session + ".jsonl", "fixture transcript not consumed");
                Settings = Path.Combine(Claude, "settings.json"); Executable = Path.Combine(Root, "ClaudeAccountSync.exe");
                File.WriteAllText(Executable, "not an executable; this fixture never launches it");
            }
            public string Write(string relative, string content)
            { string path = Path.Combine(Claude, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, content, Utf8); return path; }
            public string Input(string eventName = "UserPromptSubmit")
            { return Json.Serialize(D("hook_event_name", eventName, "cwd", Cwd, "session_id", Session, "transcript_path", Transcript, "prompt", "PRIVATE FIXTURE PROMPT MUST NOT ECHO")); }
            public string Run(string input = null) { return CodeKnowledgeHook.Handle(Engine, input ?? Input(), Root); }
            public string Context(string result)
            { var parsed = Obj(result); return parsed.ContainsKey("hookSpecificOutput") ? (string)Map(parsed["hookSpecificOutput"])["additionalContext"] : null; }
        }
        public static int Run(string temporaryRoot)
        {
            passed = 0;
            var f = new Fixture(temporaryRoot);
            string externalCommand = Path.Combine(f.Root, "existing-helper.exe");
            byte[] original = Utf8.GetBytes(Json.Serialize(D("model", "fixture-model", "unknownSetting", D("keep", true), "hooks", D(
                "UserPromptSubmit", new object[] { D("matcher", "", "hooks", new object[] { D("type", "command", "command", externalCommand, "args", new[] { "--existing" }) }) },
                "PostToolUse", new object[] { D("matcher", "Write", "hooks", new object[] { D("type", "command", "command", "fixture-existing") }) }))));
            File.WriteAllBytes(f.Settings, original);
            string status = CodeKnowledgeHook.Install(f.Engine, f.Executable);
            var config = Obj(File.ReadAllText(f.Settings));
            var hooks = Map(config["hooks"]);
            Check(status.Contains("已启用") && (string)config["model"] == "fixture-model" && (bool)Map(config["unknownSetting"])["keep"], "installation preserves unrelated settings");
            Check(Items(hooks["PostToolUse"]).Count() == 1 && Items(hooks["UserPromptSubmit"]).Count() == 2, "installation preserves other hooks and event rules");
            var own = Map(Items(Map(Items(hooks["SessionStart"]).Single())["hooks"]).Single());
            Check((string)own["command"] == f.Executable && Items(own["args"]).First().Equals("--knowledge-context"), "hook uses exact executable and structured arguments");
            Check((string)own["onFailure"] == "continue" && Convert.ToInt32(own["timeout"]) == 15 && !own.ContainsKey("async"), "hook is bounded synchronous and never blocks on failure");
            string[] backups = Directory.GetFiles(Path.Combine(f.State, "hook-settings-backups"), "*.json");
            Check(backups.Length == 1 && original.SequenceEqual(File.ReadAllBytes(backups[0])), "original settings backed up byte for byte");
            byte[] installed = File.ReadAllBytes(f.Settings); CodeKnowledgeHook.Install(f.Engine, f.Executable);
            Check(installed.SequenceEqual(File.ReadAllBytes(f.Settings)) && Directory.GetFiles(Path.Combine(f.State, "hook-settings-backups"), "*.json").Length == 1, "reinstallation is idempotent without backup churn");

            f = new Fixture(temporaryRoot); File.WriteAllText(f.Settings, "{\"disableAllHooks\":true,\"model\":\"keep\"}");
            original = File.ReadAllBytes(f.Settings); status = CodeKnowledgeHook.Install(f.Engine, f.Executable);
            Check(status.Contains("未启用") && original.SequenceEqual(File.ReadAllBytes(f.Settings)), "disabled hooks remain disabled without setting rewrites");
            f = new Fixture(temporaryRoot); File.WriteAllText(f.Settings, "not json"); original = File.ReadAllBytes(f.Settings);
            bool failed = false; try { CodeKnowledgeHook.Install(f.Engine, f.Executable); } catch { failed = true; }
            Check(failed && original.SequenceEqual(File.ReadAllBytes(f.Settings)), "malformed settings are preserved");

            f = new Fixture(temporaryRoot);
            string memory = f.Write("projects\\fixture\\memory\\MEMORY.md", "fixture memory version ONE");
            string unrelated = f.Write("projects\\other\\memory\\MEMORY.md", "UNRELATED PROJECT PRIVATE MEMORY");
            string skill = f.Write("skills\\example\\SKILL.md", "fixture skill version ONE");
            string first = f.Run(), context = f.Context(first);
            Check(context != null && context.Contains("fixture memory version ONE"), "current scoped memory enters the next prompt context");
            Check(!first.Contains("UNRELATED PROJECT PRIVATE MEMORY") && !first.Contains(unrelated), "another project memory does not enter prompt context");
            Check(context.Contains(skill) && context.Contains("SHA256=") && context.Contains("旧上下文中同名 Skill 可能过期"), "skill catalogue includes current path hash and stale context guidance");
            Check(!first.Contains("fixture skill version ONE") && !first.Contains("PRIVATE FIXTURE PROMPT"), "handler neither injects executable skill bodies nor echoes private user prompts");
            File.WriteAllText(memory, "fixture memory version TWO", Utf8); File.WriteAllText(skill, "fixture skill version TWO", Utf8);
            string second = f.Run(), secondContext = f.Context(second);
            Check(secondContext.Contains("fixture memory version TWO") && !secondContext.Contains("fixture memory version ONE"), "updated memory automatically replaces the next hook's old excerpt");
            Check(context != secondContext && secondContext.Contains(Knowledge.Hash(File.ReadAllBytes(skill))), "edited skill hash updates on the next handler call");
            string added = f.Write("skills\\new-skill\\SKILL.md", "new fixture skill");
            Check(f.Context(f.Run()).Contains(added), "new skill appears without an old snapshot cache");
            File.Delete(skill);
            Check(!f.Context(f.Run()).Contains(skill), "deleted skill disappears from the next catalogue");
            var anotherAccount = f.Engine.AddProfile("Second fixture account");
            Check(anotherAccount != null && f.Context(f.Run()).Contains("fixture memory version TWO"), "another profile uses the same latest local source without account caches");
            var startup = Obj(f.Run(f.Input("SessionStart")));
            Check((string)Map(startup["hookSpecificOutput"])["hookEventName"] == "SessionStart", "SessionStart output names its own event");

            var request = Obj(f.Input()); request["transcript_path"] = Path.Combine(f.Root, Session + ".jsonl");
            string rejected = f.Run(Json.Serialize(request));
            Check(Obj(rejected).ContainsKey("systemMessage") && !rejected.Contains("fixture memory"), "outside-config transcript rejected before source loading");
            request = Obj(f.Input()); request["session_id"] = "22222222-2222-4222-8222-222222222222";
            Check(Obj(f.Run(Json.Serialize(request))).ContainsKey("systemMessage"), "mismatched session identity rejects");
            Check(Obj(f.Run("{")).ContainsKey("systemMessage"), "invalid input becomes a nonblocking visible warning");
            request = Obj(f.Input()); request["hook_event_name"] = "PreToolUse";
            Check(Obj(f.Run(Json.Serialize(request))).ContainsKey("systemMessage"), "unconfigured event cannot use the context reader");
            Directory.CreateDirectory(Path.Combine(f.Cwd, ".git"));
            Check(Obj(f.Run()).ContainsKey("systemMessage"), "unresolved Git memory identity refuses transcript-folder guessing");

            f = new Fixture(temporaryRoot); f.Write("projects\\fixture\\memory\\MEMORY.md", string.Join("\n", Enumerable.Range(0, 400).Select(i => "fixture long memory line " + i)));
            context = f.Context(f.Run());
            Check(context != null && context.Length < 10000 && context.Contains("记忆摘录已截断") && context.Contains("完整索引"), "large memory remains bounded with explicit truncation and full index path");
            return passed;
        }
    }
}
