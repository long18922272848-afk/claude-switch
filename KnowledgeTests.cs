using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    // Fixture-only knowledge snapshots. No Claude windows, tasks, profiles or user files are used.
    static class KnowledgeTests
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static int passed;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern bool CreateHardLinkW(string link, string existing, IntPtr securityAttributes);

        sealed class Fixture
        {
            public string Root, Out, Claude;
            public Engine Engine;
            public Fixture(string parent)
            {
                Root = Path.Combine(parent, "k" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Claude = Path.Combine(Root, "c"); Out = Path.Combine(Root, "o");
                Directory.CreateDirectory(Claude);
                Engine = new Engine(Path.Combine(Root, "p"), false, Claude, Path.Combine(Root, "s"));
            }
            public string Write(string relative, string text)
            {
                string path = Path.Combine(Claude, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, Utf8); return path;
            }
            public KnowledgePackage Capture() { return Knowledge.Capture(Engine, Out); }
        }

        static void Check(bool result, string name)
        {
            if (!result) throw new Exception("Knowledge fixture failed: " + name);
            passed++;
        }
        static KnowledgeFile Entry(KnowledgePackage package, string source)
        { return package.Files.SingleOrDefault(f => string.Equals(Path.GetFullPath(f.Source), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)); }
        static string Saved(KnowledgePackage package, KnowledgeFile file)
        { return Path.Combine(package.Directory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)); }
        static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        static bool AllFilesVerified(KnowledgePackage package)
        {
            return package.Files.All(f => File.Exists(Saved(package, f)) &&
                string.Equals(Hash(File.ReadAllBytes(Saved(package, f))), f.Hash, StringComparison.OrdinalIgnoreCase));
        }
        static bool RefusesOrMarksIncomplete(Func<KnowledgePackage> capture)
        {
            try { return !capture().Complete; }
            catch (IOException) { return true; }
            catch (InvalidOperationException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public static int Run(string temporaryRoot)
        {
            passed = 0;
            var f = new Fixture(temporaryRoot);
            string skill = f.Write(@"skills\sample\SKILL.md", "---\nname: fixture-skill\ndescription: Fixture only\n---\nRead resources/example.txt\n");
            string resource = f.Write(@"skills\sample\resources\example.txt", "fixture skill resource");
            string firstMemory = f.Write(@"projects\project-one\memory\MEMORY.md", "fixture project one memory");
            string secondMemory = f.Write(@"projects\project-two\memory\topic.md", "fixture project two memory");
            string userInstruction = f.Write("CLAUDE.md", "fixture global instruction");
            string rule = f.Write(@"rules\test.md", "fixture rule");
            string command = f.Write(@"commands\legacy.md", "fixture legacy skill");
            string credentials = f.Write(".credentials.json", "fixture credential file must not be copied");
            string config = f.Write("config.json", "fixture configuration must not be copied");
            string transcript = f.Write(@"projects\project-one\11111111-1111-4111-8111-111111111111.jsonl", "fixture transcript must not be copied");
            DateTime sourceWrite = File.GetLastWriteTimeUtc(skill);

            KnowledgePackage a = f.Capture();
            Check(a.Complete && a.Warnings.Count == 0, "ordinary local sources produce a complete package");
            Check(Entry(a, skill) != null && Entry(a, resource) != null, "skill package includes instruction and resource bytes");
            Check(Entry(a, firstMemory) != null && Entry(a, secondMemory) != null && a.MemoryCount == 2, "inventory includes both saved project memories");
            Check(Entry(a, userInstruction) != null && Entry(a, rule) != null && a.InstructionCount >= 2, "user instructions and rules are captured");
            Check(Entry(a, command) != null, "legacy command skill is captured");
            Check(Entry(a, credentials) == null && Entry(a, config) == null && Entry(a, transcript) == null, "credentials settings and transcripts are outside allowlist");
            string resourceRelative = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Saved(a, Entry(a, skill))), "resources", "example.txt"));
            Check(resourceRelative == Path.GetFullPath(Saved(a, Entry(a, resource))), "skill relative resource references are preserved");
            Check(File.Exists(a.IndexFile) && File.Exists(Path.Combine(a.Directory, "manifest.json")), "package exposes readable index and machine manifest");
            Check(AllFilesVerified(a), "all copied bytes match manifest hashes");
            Check(Knowledge.VerifyPackage(a), "newly captured immutable package passes full integrity verification");
            Check(Knowledge.VerifySources(a), "fresh capsule verifies against its current sources");
            Check(File.GetLastWriteTimeUtc(skill) == sourceWrite && File.ReadAllText(resource) == "fixture skill resource", "capture leaves source bytes and modified times untouched");
            KnowledgePackage again = f.Capture();
            Check(again.Fingerprint == a.Fingerprint && again.Directory == a.Directory && AllFilesVerified(again), "unchanged capture reuses a verified immutable package");
            File.WriteAllText(firstMemory, "fixture updated memory", Utf8);
            Check(!Knowledge.VerifySources(a), "source mutation invalidates the previous knowledge capsule");
            KnowledgePackage b = f.Capture();
            Check(b.Fingerprint != a.Fingerprint && b.Directory != a.Directory, "changed memory produces a new content version");
            Check(File.ReadAllText(Saved(a, Entry(a, firstMemory))) == "fixture project one memory", "old memory snapshot is frozen after source update");
            Check(File.ReadAllText(Saved(b, Entry(b, firstMemory))) == "fixture updated memory", "new snapshot contains the updated memory");

            // Corrupted immutable destinations must never silently be reported complete.
            string tampered = Saved(b, Entry(b, skill)); File.WriteAllText(tampered, "fixture corrupted snapshot", Utf8);
            Check(!Knowledge.VerifyPackage(b), "payload tampering fails full package integrity verification");
            bool tamperSafe;
            try { KnowledgePackage rebuilt = f.Capture(); tamperSafe = !rebuilt.Complete || AllFilesVerified(rebuilt); }
            catch (IOException) { tamperSafe = true; }
            catch (InvalidOperationException) { tamperSafe = true; }
            Check(tamperSafe, "cache tampering is rejected or repaired before reporting complete");

            f = new Fixture(temporaryRoot);
            f.Write(@"skills\sample\SKILL.md", "fixture complete skill");
            KnowledgePackage extraDestination = f.Capture();
            File.WriteAllText(Path.Combine(extraDestination.Directory, "unexpected.md"), "fixture unexpected snapshot content", Utf8);
            Check(RefusesOrMarksIncomplete(f.Capture), "unlisted snapshot files cannot pass cached-package validation");

            f = new Fixture(temporaryRoot);
            f.Write(@"projects\membership\memory\MEMORY.md", "fixture known memory");
            KnowledgePackage beforeNewMemory = f.Capture();
            f.Write(@"projects\membership\memory\new-topic.md", "fixture added memory");
            Check(!Knowledge.VerifySources(beforeNewMemory), "new memory membership invalidates a previous complete capsule");
            KnowledgePackage beforeNewSkill = f.Capture();
            f.Write(@"skills\new-skill\SKILL.md", "fixture newly installed skill");
            Check(!Knowledge.VerifySources(beforeNewSkill), "new skill membership invalidates a previous complete capsule");

            f = new Fixture(temporaryRoot);
            string lowerSkill = f.Write(@"skills\case-test\skill.md", "fixture case-insensitive skill entrypoint");
            string nestedSkillName = f.Write(@"skills\case-test\reference\SKILL.md", "fixture reference with a skill-like filename");
            KnowledgePackage casePackage = f.Capture();
            Check(casePackage.SkillCount == 1 && Entry(casePackage, lowerSkill) != null && Entry(casePackage, nestedSkillName) != null,
                "only the package root entrypoint counts as a skill regardless of filename case");

            f = new Fixture(temporaryRoot);
            f.Write(@"skills\normal\SKILL.md", "fixture normal skill");
            string synced = f.Write(@"skills\synced\account-skill\SKILL.md", "fixture account cache must not be promoted");
            KnowledgePackage filtered = f.Capture();
            Check(Entry(filtered, synced) == null && filtered.Files.All(x => x.Source.IndexOf("\\synced\\", StringComparison.OrdinalIgnoreCase) < 0), "account-bound synced skills are excluded");
            string secret = f.Write(@"skills\normal\.credentials.json", "fixture secret");
            KnowledgePackage guarded = f.Capture();
            Check(Entry(guarded, secret) == null && !guarded.Complete && guarded.Warnings.Count > 0, "credential-like skill resource is excluded with an incomplete warning");

            f = new Fixture(temporaryRoot);
            f.Write(@"skills\normal\SKILL.md", "fixture normal skill");
            string secretFolderFile = f.Write(@"skills\normal\credentials\session.json", "fixture secret stored in a sensitive directory");
            KnowledgePackage guardedDirectory = f.Capture();
            Check(Entry(guardedDirectory, secretFolderFile) == null && !guardedDirectory.Complete, "credential directory contents are excluded with an incomplete warning");

            f = new Fixture(temporaryRoot);
            f.Write(@"skills\linked\SKILL.md", "fixture skill with a hardlink resource");
            string hardlinkSource = Path.Combine(f.Root, "fixture-credentials.json");
            File.WriteAllText(hardlinkSource, "fixture secret content outside the skill", Utf8);
            string hardlinkDest = Path.Combine(f.Claude, "skills", "linked", "disguised.md");
            if (CreateHardLinkW(hardlinkDest, hardlinkSource, IntPtr.Zero))
            {
                KnowledgePackage hardlinkPackage = f.Capture();
                Check(!hardlinkPackage.Complete && Entry(hardlinkPackage, hardlinkDest) == null, "hardlinked skill content cannot bypass source exclusions");
                Check(File.ReadAllText(hardlinkSource) == "fixture secret content outside the skill", "hardlink rejection preserves the original source");
            }
            else Console.WriteLine("SKIP: fixture filesystem does not allow hardlink creation (" + Marshal.GetLastWin32Error() + ").");

            f = new Fixture(temporaryRoot);
            string custom = Path.Combine(f.Root, "custom-memory"); Directory.CreateDirectory(custom);
            string customFile = Path.Combine(custom, "MEMORY.md"); File.WriteAllText(customFile, "fixture custom memory", Utf8);
            string settings = f.Write("settings.json", new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "autoMemoryDirectory", custom } }));
            KnowledgePackage customPackage = f.Capture();
            Check(Entry(customPackage, customFile) != null && Entry(customPackage, settings) == null, "absolute custom auto-memory directory is honored without copying settings");
            File.WriteAllText(settings, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "autoMemoryDirectory", custom } }), new UTF8Encoding(true));
            KnowledgePackage bomPackage = f.Capture();
            Check(bomPackage.Complete && Entry(bomPackage, customFile) != null, "UTF-8 BOM settings retain the custom memory directory");
            File.Delete(customFile);
            Check(!Knowledge.VerifySources(customPackage), "deleted source invalidates an existing capsule");
            f.Write("settings.json", "{\"autoMemoryDirectory\":\"relative-memory\"}");
            Check(RefusesOrMarksIncomplete(f.Capture), "unsupported relative custom memory directory cannot claim complete sync");

            f = new Fixture(temporaryRoot);
            string emptyMemoryA = Path.Combine(f.Root, "memory-a"), emptyMemoryB = Path.Combine(f.Root, "memory-b");
            Directory.CreateDirectory(emptyMemoryA); Directory.CreateDirectory(emptyMemoryB);
            f.Write("settings.json", new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "autoMemoryDirectory", emptyMemoryA } }));
            KnowledgePackage beforeOverrideChange = f.Capture();
            f.Write("settings.json", new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "autoMemoryDirectory", emptyMemoryB } }));
            Check(!Knowledge.VerifySources(beforeOverrideChange), "changing an empty custom memory location invalidates the capsule");
            KnowledgePackage beforeOverrideRemoval = f.Capture();
            f.Write("settings.json", "{}");
            Check(!Knowledge.VerifySources(beforeOverrideRemoval), "removing an empty custom memory override invalidates the capsule");

            f = new Fixture(temporaryRoot);
            f.Write(@"skills\oversized\SKILL.md", "fixture skill");
            string large = f.Write(@"skills\oversized\large.txt", "");
            using (var stream = new FileStream(large, FileMode.Open, FileAccess.Write)) stream.SetLength(8L * 1024 * 1024 + 1);
            Check(RefusesOrMarksIncomplete(f.Capture), "oversized relevant resource stops complete reporting");

            f = new Fixture(temporaryRoot);
            string locked = f.Write(@"skills\locked\SKILL.md", "fixture locked skill");
            using (var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(RefusesOrMarksIncomplete(f.Capture), "unreadable relevant source cannot be reported completely copied");
            Check(File.ReadAllText(locked) == "fixture locked skill", "failed snapshot does not alter locked source");

            f = new Fixture(temporaryRoot);
            string selectedMemory = f.Write(@"projects\selected\memory\MEMORY.md", "fixture selected project memory");
            string unrelatedMemory = f.Write(@"projects\unrelated\memory\MEMORY.md", "fixture unrelated project memory");
            string taskLog = f.Write(@"projects\selected\11111111-1111-4111-8111-111111111111.jsonl", "fixture saved task log");
            string workspace = Path.Combine(f.Root, "workspace"), cwd = Path.Combine(workspace, "subproject");
            Directory.CreateDirectory(cwd);
            string parentInstruction = Path.Combine(workspace, "CLAUDE.md"); File.WriteAllText(parentInstruction, "fixture parent project instructions", Utf8);
            string localInstruction = Path.Combine(cwd, "CLAUDE.local.md"); File.WriteAllText(localInstruction, "fixture local project instructions", Utf8);
            string projectSkillDir = Path.Combine(workspace, ".claude", "skills", "project-skill"); Directory.CreateDirectory(projectSkillDir);
            string projectSkill = Path.Combine(projectSkillDir, "SKILL.md"); File.WriteAllText(projectSkill, "fixture project skill", Utf8);
            var task = new RelayTask { Cwd = cwd, TranscriptPath = taskLog, CliId = "11111111-1111-4111-8111-111111111111", LocalId = "local_fixture" };
            // The explicit boundary keeps all ancestor discovery inside this synthetic fixture.
            KnowledgePackage scoped = Knowledge.Capture(f.Engine, f.Out, task, f.Root);
            Check(Entry(scoped, selectedMemory) != null && Entry(scoped, unrelatedMemory) == null, "task capsule carries only matching project auto-memory");
            Check(Entry(scoped, parentInstruction) != null && Entry(scoped, localInstruction) != null, "task capsule preserves ancestor and local project instructions");
            Check(Entry(scoped, projectSkill) != null, "task capsule includes ancestor project skills");
            Check(scoped.Files.All(x => Path.GetFullPath(x.Source).StartsWith(Path.GetFullPath(f.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "task fixture boundary prevents real ancestor file reads");
            Check(Entry(scoped, taskLog) == null && AllFilesVerified(scoped), "task knowledge capsule excludes transcript and verifies all knowledge bytes");

            f = new Fixture(temporaryRoot);
            string defaultScopedMemory = f.Write(@"projects\selected\memory\MEMORY.md", "fixture default memory should not mix with an effective custom location");
            taskLog = f.Write(@"projects\selected\11111111-1111-4111-8111-111111111111.jsonl", "fixture selected log");
            workspace = Path.Combine(f.Root, "workspace"); cwd = Path.Combine(workspace, "subproject");
            string parentConfig = Path.Combine(workspace, ".claude"), nearestConfig = Path.Combine(cwd, ".claude");
            Directory.CreateDirectory(parentConfig); Directory.CreateDirectory(nearestConfig);
            string userMemory = Path.Combine(f.Root, "um"), checkedInMemory = Path.Combine(f.Root, "cm"), parentMemory = Path.Combine(f.Root, "pm"), nearestMemory = Path.Combine(f.Root, "nm");
            foreach (string dir in new[] { userMemory, checkedInMemory, parentMemory, nearestMemory })
            { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "MEMORY.md"), "fixture memory from " + Path.GetFileName(dir), Utf8); }
            Func<string, string> memorySetting = dir => new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "autoMemoryDirectory", dir } });
            f.Write("settings.json", memorySetting(userMemory));
            File.WriteAllText(Path.Combine(parentConfig, "settings.json"), memorySetting(checkedInMemory), Utf8);
            task = new RelayTask { Cwd = cwd, TranscriptPath = taskLog, CliId = "11111111-1111-4111-8111-111111111111", LocalId = "local_fixture" };
            KnowledgePackage checkedInSettings = Knowledge.Capture(f.Engine, f.Out, task, f.Root);
            Check(Entry(checkedInSettings, Path.Combine(userMemory, "MEMORY.md")) != null && Entry(checkedInSettings, Path.Combine(checkedInMemory, "MEMORY.md")) == null,
                "checked-in project settings cannot override personal auto-memory location");
            File.WriteAllText(Path.Combine(parentConfig, "settings.local.json"), memorySetting(parentMemory), Utf8);
            File.WriteAllText(Path.Combine(nearestConfig, "settings.local.json"), memorySetting(nearestMemory), Utf8);
            KnowledgePackage nearestSettings = Knowledge.Capture(f.Engine, f.Out, task, f.Root);
            Check(Entry(nearestSettings, Path.Combine(nearestMemory, "MEMORY.md")) != null && Entry(nearestSettings, Path.Combine(parentMemory, "MEMORY.md")) == null && Entry(nearestSettings, Path.Combine(userMemory, "MEMORY.md")) == null,
                "nearest local project auto-memory overrides ancestor local and user settings");
            Check(Entry(nearestSettings, defaultScopedMemory) == null && nearestSettings.MemoryCount == 1,
                "task capsule carries only effective custom memory without default-project mixing");

            return passed;
        }
    }
}
