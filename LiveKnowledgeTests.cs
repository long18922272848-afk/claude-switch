using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    // Synthetic filesystem fixtures only. No real Claude data, window or task is used.
    static class LiveKnowledgeTests
    {
        static int passed;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        sealed class Fixture
        {
            public string Root, Claude;
            public Engine Engine;
            public Fixture(string parent)
            {
                Root = Path.Combine(parent, "l" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Claude = Path.Combine(Root, "c"); Directory.CreateDirectory(Claude);
                Engine = new Engine(Path.Combine(Root, "p"), false, Claude, Path.Combine(Root, "s"));
            }
            public string Write(string relative, string text)
            {
                string path = Path.Combine(Claude, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, Utf8); return path;
            }
            public string Current { get { return LiveKnowledge.CurrentPath(Engine); } }
            public LiveKnowledgePointer ReadPointer() { return new JavaScriptSerializer().Deserialize<LiveKnowledgePointer>(File.ReadAllText(Current)); }
        }
        static void Check(bool ok, string name)
        { if (!ok) throw new Exception("LiveKnowledge fixture failed: " + name); passed++; }

        public static int Run(string temporaryRoot)
        {
            passed = 0;
            var f = new Fixture(temporaryRoot);
            string memory = f.Write(@"projects\first\memory\MEMORY.md", "first memory");
            string skill = f.Write(@"skills\sample\SKILL.md", "first skill");
            string sourceMemory = File.ReadAllText(memory), sourceSkill = File.ReadAllText(skill);
            DateTime memoryTime = File.GetLastWriteTimeUtc(memory);
            LiveKnowledgeResult first = LiveKnowledge.Refresh(f.Engine);
            Check(first.Ready && first.Changed && File.Exists(f.Current), "first valid publication creates current pointer");
            LiveKnowledgePointer pointer = f.ReadPointer();
            Check(pointer.Version == 1 && pointer.Fingerprint == first.Package.Fingerprint && pointer.IndexFile == first.Package.IndexFile, "pointer identifies exact verified immutable package");
            Check(pointer.SkillCount == 1 && pointer.MemoryCount == 1 && pointer.Files.Any(x => x.Source == memory), "pointer exposes current counts and source inventory");
            Check(File.ReadAllText(memory) == sourceMemory && File.ReadAllText(skill) == sourceSkill && File.GetLastWriteTimeUtc(memory) == memoryTime, "publishing preserves source contents and timestamps");
            string originalPointer = File.ReadAllText(f.Current), humanPath = LiveKnowledge.CurrentMarkdownPath(f.Engine);
            DateTime fixedTime = DateTime.UtcNow.AddDays(-1);
            File.SetLastWriteTimeUtc(f.Current, fixedTime); File.SetLastWriteTimeUtc(humanPath, fixedTime);
            LiveKnowledgeResult same = LiveKnowledge.Refresh(f.Engine);
            Check(same.Ready && !same.Changed && File.ReadAllText(f.Current) == originalPointer, "unchanged refresh keeps exact pointer bytes");
            Check(File.GetLastWriteTimeUtc(f.Current) == fixedTime && File.GetLastWriteTimeUtc(humanPath) == fixedTime, "unchanged refresh does not churn latest entry timestamps");
            Check(File.ReadAllText(humanPath).Contains("current.json") && !File.ReadAllText(humanPath).Contains(pointer.Fingerprint), "human entry always refers to authoritative live pointer instead of frozen version");

            f.Write(@"projects\first\memory\MEMORY.md", "updated memory");
            LiveKnowledgeResult updated = LiveKnowledge.Refresh(f.Engine);
            Check(updated.Ready && updated.Changed && updated.Package.Fingerprint != first.Package.Fingerprint, "changed memory publishes a newer package");
            KnowledgeFile memoryEntry = updated.Package.Files.Single(x => x.Source == memory);
            Check(File.ReadAllText(Path.Combine(updated.Package.Directory, memoryEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar))) == "updated memory", "new pointer leads to newest memory bytes");
            Check(File.Exists(first.Package.IndexFile), "old immutable package remains history without being latest");
            f.Write(@"skills\sample\SKILL.md", "updated skill");
            LiveKnowledgeResult newerSkill = LiveKnowledge.Refresh(f.Engine);
            Check(newerSkill.Ready && newerSkill.Changed && newerSkill.Package.Fingerprint != updated.Package.Fingerprint, "changed skill updates authoritative pointer");
            f.Write(@"skills\new-skill\SKILL.md", "new skill");
            string newMemory = f.Write(@"projects\first\memory\topic.md", "new note");
            LiveKnowledgeResult added = LiveKnowledge.Refresh(f.Engine);
            Check(added.Ready && added.Changed && f.ReadPointer().SkillCount == 2 && f.ReadPointer().MemoryCount == 2, "new memory and skill are discovered automatically");
            File.Delete(newMemory); File.Delete(Path.Combine(f.Claude, @"skills\new-skill\SKILL.md"));
            LiveKnowledgeResult removed = LiveKnowledge.Refresh(f.Engine);
            Check(removed.Ready && removed.Changed && f.ReadPointer().SkillCount == 1 && f.ReadPointer().MemoryCount == 1, "deleted source entries disappear from latest inventory");
            Check(!File.Exists(newMemory) && !File.Exists(Path.Combine(f.Claude, @"skills\new-skill\SKILL.md")), "publishing does not resurrect deleted original sources");

            string validPointer = File.ReadAllText(f.Current);
            string secret = f.Write(@"skills\sample\.credentials.json", "fixture excluded credentials");
            LiveKnowledgeResult incomplete = LiveKnowledge.Refresh(f.Engine);
            Check(!incomplete.Ready && !incomplete.Changed && incomplete.Package != null && !incomplete.Package.Complete, "incomplete source scan is not published");
            Check(File.ReadAllText(f.Current) == validPointer, "incomplete scan preserves previous pointer byte-for-byte");
            File.WriteAllText(f.Current, "invalid pointer");
            LiveKnowledgeResult badAndIncomplete = LiveKnowledge.Refresh(f.Engine);
            Check(!badAndIncomplete.Ready && File.ReadAllText(f.Current) == "invalid pointer", "corrupt pointer is not repaired using incomplete package");
            File.Delete(secret);
            LiveKnowledgeResult repaired = LiveKnowledge.Refresh(f.Engine);
            Check(repaired.Ready && repaired.Changed && f.ReadPointer().Fingerprint == removed.Package.Fingerprint, "corrupt pointer repairs only after full package verification");

            string repairedPointer = File.ReadAllText(f.Current);
            File.WriteAllText(repaired.Package.IndexFile, "fixture tampered package");
            LiveKnowledgeResult tampered = LiveKnowledge.Refresh(f.Engine);
            Check(!tampered.Ready && !tampered.Changed && File.ReadAllText(f.Current) == repairedPointer, "tampered immutable package never changes current pointer");

            f = new Fixture(temporaryRoot);
            f.Write(@"skills\sample\SKILL.md", "concurrent fixture skill");
            var firstConcurrent = Task.Factory.StartNew(() => LiveKnowledge.Refresh(f.Engine));
            var secondConcurrent = Task.Factory.StartNew(() => LiveKnowledge.Refresh(f.Engine));
            Task.WaitAll(firstConcurrent, secondConcurrent);
            Check(firstConcurrent.Result.Ready && secondConcurrent.Result.Ready, "concurrent publishers both obtain verified outcome");
            Check((firstConcurrent.Result.Changed ? 1 : 0) + (secondConcurrent.Result.Changed ? 1 : 0) == 1, "concurrent publishers serialize one authoritative update");
            Check(f.ReadPointer().Fingerprint == firstConcurrent.Result.Package.Fingerprint && Knowledge.VerifyPackage(secondConcurrent.Result.Package), "concurrent pointer identifies complete consistent package");
            Check(!Directory.GetFiles(Path.Combine(f.Engine.StateDir, "knowledge"), ".current-*.tmp").Any(), "publication staging files are cleaned up");
            return passed;
        }
    }
}
