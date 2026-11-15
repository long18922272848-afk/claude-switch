using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    class LiveKnowledgeResult
    {
        public KnowledgePackage Package;
        public bool Changed, Ready;
        public string Message;
    }

    // This file is the publication boundary, not another copy of the source knowledge. Both
    // accounts' native consumer reads one current.json; old immutable packages remain history.
    class LiveKnowledgePointer
    {
        public int Version = 1;
        public string Fingerprint, PackageDirectory, IndexFile, UpdatedUtc;
        public int SkillCount, MemoryCount, InstructionCount;
        public List<KnowledgeFile> Files;
    }

    static class LiveKnowledge
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }; }
        public static string CurrentPath(Engine engine) { return Path.Combine(engine.StateDir, "knowledge", "current.json"); }
        public static string CurrentMarkdownPath(Engine engine) { return Path.Combine(engine.StateDir, "knowledge", "current.md"); }

        static void SafePath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) throw new IOException("最新资料索引路径无效。");
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0 || full.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new IOException("最新资料索引必须保存在本机普通目录。");
            for (string part = full; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            {
                try
                {
                    if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("最新资料索引路径包含链接，未更新。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        static string Read(string path)
        {
            SafePath(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 8 * 1024 * 1024) throw new IOException("最新资料索引超过大小上限。");
                using (var reader = new StreamReader(stream, Utf8, true)) return reader.ReadToEnd();
            }
        }

        static void AtomicWrite(string path, string text)
        {
            SafePath(path);
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory); SafePath(directory);
            string temp = Path.Combine(directory, ".current-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                SafePath(temp);
                byte[] bytes = Utf8.GetBytes(text);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                SafePath(path);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally
            {
                // Only our newly generated single staging file is removed; no recursive cleanup.
                if (File.Exists(temp)) { SafePath(temp); File.Delete(temp); }
            }
        }

        static string HumanEntry()
        {
            return "# 小克的最新本地记忆与 Skills\n\n" +
                "[打开最新索引 current.json](current.json)\n\n" +
                "current.json 是唯一的最新版本入口，包含当前资料包的 IndexFile、内容指纹、实际数量和来源清单。" +
                "请每次重新读取它，再打开其中的 IndexFile；不要把某个历史资料包路径永久当成最新版。\n\n" +
                "本页保持固定链接，避免两个同时更新的文件指向不同版本。仅完整核验过的本地资料才会替换最新索引；" +
                "读取失败或资料不完整时保留上一次有效索引，并报告本次失败。\n\n" +
                "不同项目的记忆保留原作用域；当前任务只读取适用的项目资料。" +
                "源文件仍是本机 Claude Code 使用的原文件，云端账号记忆与 Skills 不在此索引的同步范围内。\n";
        }

        static LiveKnowledgePointer Pointer(KnowledgePackage package, string time)
        {
            return new LiveKnowledgePointer { Fingerprint = package.Fingerprint, PackageDirectory = package.Directory,
                IndexFile = package.IndexFile, UpdatedUtc = time, SkillCount = package.SkillCount,
                MemoryCount = package.MemoryCount, InstructionCount = package.InstructionCount, Files = package.Files };
        }

        public static LiveKnowledgeResult Refresh(Engine engine)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            var result = new LiveKnowledgeResult();
            try
            {
                string root = Path.GetFullPath(Path.Combine(engine.StateDir, "knowledge"));
                SafePath(root);
                string key = Knowledge.Hash(Utf8.GetBytes(root.TrimEnd('\\', '/').ToUpperInvariant()));
                using (var mutex = new Mutex(false, @"Local\ClaudeAccountSync-LiveKnowledge-" + key.Substring(0, 24)))
                {
                    bool held;
                    try { held = mutex.WaitOne(5000); } catch (AbandonedMutexException) { held = true; }
                    if (!held) { result.Message = "记忆与 Skills 正在更新，本次未重复发布。"; return result; }
                    try { return RefreshLocked(engine, root, result); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                // Do not expose serializer input or source contents. The last published pointer
                // is left intact when capture or verification fails.
                result.Message = "未更新最新记忆与 Skills，已保留上次索引：" + ex.Message;
                return result;
            }
        }

        static LiveKnowledgeResult RefreshLocked(Engine engine, string root, LiveKnowledgeResult result)
        {
            KnowledgePackage package = result.Package = Knowledge.Capture(engine, root);
            if (!package.Complete)
            {
                result.Message = "本地记忆与 Skills 未完整读取，保留上次最新索引。";
                return result;
            }
            if (!Knowledge.VerifyPackage(package) || !Knowledge.VerifySources(package))
            {
                result.Message = "本地记忆或 Skills 正在变化，保留上次最新索引，等待下次核验。";
                return result;
            }

            string current = CurrentPath(engine), markdown = CurrentMarkdownPath(engine), previousText = null;
            LiveKnowledgePointer previous = null;
            if (File.Exists(current))
            {
                // A corrupt pointer is repairable only after the fresh package above is verified.
                previousText = Read(current);
                try { previous = Json().Deserialize<LiveKnowledgePointer>(previousText); } catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            DateTime parsed;
            bool sameVersion = previous != null && previous.Version == 1 && previous.Fingerprint == package.Fingerprint &&
                DateTime.TryParse(previous.UpdatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed);
            string time = sameVersion ? previous.UpdatedUtc : DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string next = Json().Serialize(Pointer(package, time));

            string human = HumanEntry();
            if (!File.Exists(markdown) || Read(markdown) != human) AtomicWrite(markdown, human);
            // Verify membership as well as bytes immediately before replacing the authoritative
            // pointer. Newly created or deleted memories/Skills cannot be silently omitted.
            if (!Knowledge.VerifyPackage(package) || !Knowledge.VerifySources(package))
            {
                result.Message = "资料在发布前发生变化，最新索引未替换。";
                return result;
            }
            if (!string.Equals(previousText, next, StringComparison.Ordinal))
            { AtomicWrite(current, next); result.Changed = true; }
            result.Ready = true;
            result.Message = (result.Changed ? "最新本地资料已更新：" : "最新本地资料已核验：") +
                package.SkillCount + " 个 Skills、" + package.MemoryCount + " 份记忆、" + package.InstructionCount + " 份项目说明。";
            return result;
        }
    }
}
