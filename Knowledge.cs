using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace ClaudeAccountSync
{
    class KnowledgeFile
    {
        public string Kind, Scope, Source, RelativePath, Hash;
    }

    class KnowledgePackage
    {
        public string Directory, IndexFile, Fingerprint;
        public List<KnowledgeFile> Files = new List<KnowledgeFile>();
        public List<string> Warnings = new List<string>();
        internal Func<string> SourceIdentity;
        public int SkillCount { get { return Files.Count(f => f.Kind == "skill" || f.Kind == "command"); } }
        public int MemoryCount { get { return Files.Count(f => f.Kind == "memory"); } }
        public int InstructionCount { get { return Files.Count(f => f.Kind == "instruction" || f.Kind == "rule"); } }
        public bool Complete { get { return Warnings.Count == 0; } }
        public string Summary { get { return "本地 " + SkillCount + " 个 Skills、" + MemoryCount + " 份记忆、" + InstructionCount + " 份项目说明" + (Complete ? "已打包" : "打包不完整"); } }
    }

    // Read-only source capture. The two Desktop profiles already share Claude Code's user/project
    // paths. A capsule gives the next worker an explicit, immutable map of that knowledge; it does
    // not install anything into either account or pretend to synchronize Claude cloud settings.
    static class Knowledge
    {
        const int MaxFile = 8 * 1024 * 1024, MaxFiles = 4096, MaxDepth = 32;
        const long MaxTotal = 64L * 1024 * 1024;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        [StructLayout(LayoutKind.Sequential)]
        struct NativeFileTime { public uint Low, High; }
        [StructLayout(LayoutKind.Sequential)]
        struct FileInformation
        {
            public uint Attributes;
            public NativeFileTime Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }; }
        sealed class Item { public KnowledgeFile Info; public byte[] Bytes; }
        sealed class Plan
        {
            public readonly List<Item> Items = new List<Item>();
            public readonly List<string> Warnings = new List<string>();
            public readonly HashSet<string> Sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Inputs = new List<string>();
            public long Bytes;
            public int Visited;
            public string OutputRoot;
            public bool LimitReached;
            public void Warn(string message) { if (!Warnings.Contains(message)) Warnings.Add(message); }
        }

        internal static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string Hash(string value) { return Hash(Utf8.GetBytes(value)); }
        static string Full(string path)
        {
            string p = Path.GetFullPath(path), root = Path.GetPathRoot(p);
            return p.Length == root.Length ? p : p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        static bool Same(string a, string b) { return string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase); }
        static bool Inside(string path, string root)
        { return Same(path, root) || Full(path).StartsWith(Full(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        static void SafePath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new IOException("资料路径无效。");
            string full = Path.GetFullPath(path);
            // No ADS, UNC/device namespaces, junctions or symbolic links. Do not follow a link to
            // credentials or another workspace even when the final path itself appears innocuous.
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
                throw new IOException("资料路径不是受支持的本机普通路径。");
            for (string p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if (File.Exists(p) || System.IO.Directory.Exists(p))
                    if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("资料路径包含链接或重解析点。");
        }
        static byte[] Read(string path)
        {
            SafePath(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                FileInformation information;
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out information) || information.Links != 1)
                    throw new IOException("无法核验资料文件的独立身份，硬链接文件不参与同步。");
                if (stream.Length > MaxFile) throw new IOException("资料文件超过 8 MB 上限。");
                byte[] bytes = new byte[checked((int)stream.Length)];
                int at = 0, count;
                while (at < bytes.Length && (count = stream.Read(bytes, at, bytes.Length - at)) > 0) at += count;
                if (at != bytes.Length || stream.ReadByte() != -1) throw new IOException("资料文件在读取时发生变化。");
                return bytes;
            }
        }
        static bool Sensitive(string name)
        {
            string n = name.ToLowerInvariant(), ext = Path.GetExtension(n);
            return n == ".env" || n.StartsWith(".env.") || n.Contains("credential") || n == "auth.json" ||
                n == ".claude.json" || n == "config.json" || n == "settings.json" || n == "settings.local.json" ||
                n == "installed_plugins.json" || n == "known_marketplaces.json" || n.StartsWith("id_rsa") || n.StartsWith("id_ed25519") ||
                n == "secrets" || n.StartsWith("secrets.") || n == "token" || n == "tokens.json" ||
                ext == ".pem" || ext == ".key" || ext == ".pfx" || ext == ".p12" || ext == ".jsonl";
        }
        static void Add(Plan plan, string source, string relative, string kind, string scope)
        {
            try
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(source); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                SafePath(source);
                if ((attributes & FileAttributes.Directory) != 0) throw new IOException("预期文件却发现目录。");
                if (Sensitive(Path.GetFileName(source))) { plan.Warn("已排除可能含凭据或账号配置的文件：" + source); return; }
                string full = Path.GetFullPath(source);
                if (!plan.Sources.Add(full)) return;
                if (plan.Items.Count >= MaxFiles || plan.LimitReached) { plan.LimitReached = true; plan.Warn("资料数量超过 4096 个文件上限，未完整打包。"); return; }
                if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p == ".." || p == "." || p.Length == 0))
                    throw new IOException("资料目标路径无效。");
                if (!plan.Destinations.Add(relative)) throw new IOException("资料目标路径重名。");
                byte[] bytes = Read(source);
                if (plan.Bytes + bytes.Length > MaxTotal) { plan.LimitReached = true; plan.Warn("资料总量超过 64 MB 上限，未完整打包。"); return; }
                plan.Bytes += bytes.Length;
                plan.Items.Add(new Item { Bytes = bytes, Info = new KnowledgeFile { Kind = kind, Scope = scope, Source = full, RelativePath = relative, Hash = Hash(bytes) } });
            }
            catch (Exception ex) { plan.Warn(ex.Message + " 文件：" + source); }
        }

        static bool Folder(Plan plan, string path)
        {
            try
            {
                FileAttributes attrs;
                try { attrs = File.GetAttributes(path); }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
                SafePath(path);
                if ((attrs & FileAttributes.Directory) == 0) throw new IOException("预期资料目录却发现文件。");
                if (Inside(plan.OutputRoot, path)) throw new IOException("快照输出目录不能位于资料源目录内部。");
                return true;
            }
            catch (Exception ex) { plan.Warn(ex.Message + " 目录：" + path); return false; }
        }
        static void Tree(Plan plan, string source, string relative, string kind, string scope, bool markdownOnly, int depth)
        {
            if (!Folder(plan, source)) return;
            if (depth > MaxDepth) { plan.Warn("资料目录超过 32 层，未完整打包：" + source); return; }
            try
            {
                foreach (string entry in System.IO.Directory.EnumerateFileSystemEntries(source).Take(MaxFiles * 4 + 1).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (++plan.Visited > MaxFiles * 4) { plan.LimitReached = true; plan.Warn("资料目录过大，未完整打包。"); return; }
                    string name = Path.GetFileName(entry);
                    if (name == ".git" || name == ".svn" || name == ".hg") continue;
                    if (Sensitive(name)) { plan.Warn("已排除可能含凭据或账号配置的资料：" + entry); continue; }
                    SafePath(entry);
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                        Tree(plan, entry, relative + "/" + name, kind == "skill" ? "resource" : kind, scope, markdownOnly, depth + 1);
                    else if (!markdownOnly || string.Equals(Path.GetExtension(name), ".md", StringComparison.OrdinalIgnoreCase))
                        Add(plan, entry, relative + "/" + name, kind == "skill" && !name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ? "resource" : kind, scope);
                    if (plan.LimitReached) return;
                }
            }
            catch (Exception ex) { plan.Warn(ex.Message + " 目录：" + source); }
        }
        static void Skills(Plan plan, string root, string relative, string scope)
        {
            if (!Folder(plan, root)) return;
            try
            {
                foreach (string package in System.IO.Directory.EnumerateDirectories(root).Take(MaxFiles * 4 + 1).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (++plan.Visited > MaxFiles * 4) { plan.LimitReached = true; plan.Warn("资料目录过大，未完整打包。"); return; }
                    string name = Path.GetFileName(package);
                    if (name.Equals("synced", StringComparison.OrdinalIgnoreCase) || name == ".git") continue;
                    if (!Folder(plan, package)) continue;
                    string skill = Path.Combine(package, "SKILL.md");
                    if (!File.Exists(skill)) continue;
                    Tree(plan, package, relative + "/" + name, "skill", scope + "/" + name, false, 0);
                    if (plan.LimitReached) return;
                }
            }
            catch (Exception ex) { plan.Warn(ex.Message + " 目录：" + root); }
        }
        static void Layer(Plan plan, string root, string relative, string scope)
        {
            Add(plan, Path.Combine(root, "CLAUDE.md"), relative + "/CLAUDE.md", "instruction", scope);
            Tree(plan, Path.Combine(root, "rules"), relative + "/rules", "rule", scope, true, 0);
            Tree(plan, Path.Combine(root, "commands"), relative + "/commands", "command", scope, true, 0);
            Skills(plan, Path.Combine(root, "skills"), relative + "/skills", scope);
        }
        static string CustomMemory(Plan plan, string settings, string scope)
        {
            if (!File.Exists(settings)) { plan.Inputs.Add(settings + "|absent"); return null; }
            try
            {
                var values = Json().Deserialize<Dictionary<string, object>>(Utf8.GetString(Read(settings)).TrimStart('\uFEFF'));
                object value;
                if (values == null || !values.TryGetValue("autoMemoryDirectory", out value) || value == null)
                { plan.Inputs.Add(settings + "|unconfigured"); return null; }
                string path = value as string;
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                    throw new IOException("自定义记忆目录需要明确的绝对路径。");
                SafePath(path);
                plan.Inputs.Add(settings + "|memory=" + Path.GetFullPath(path));
                return Path.GetFullPath(path);
            }
            catch (Exception ex) { plan.Warn("无法核验 " + scope + " 自定义记忆配置：" + ex.Message); return null; }
        }
        static Plan Gather(Engine engine, string outputRoot, RelayTask task, string workspaceBoundary)
        {
            var plan = new Plan { OutputRoot = outputRoot };
            string user = Path.GetFullPath(engine.ClaudeDir);
            Layer(plan, user, "user", "用户");
            // Settings are inspected only for this path property; no settings bytes enter the capsule.
            string custom = CustomMemory(plan, Path.Combine(user, "settings.json"), "用户");
            string projects = Path.Combine(user, "projects");
            if (task == null)
            {
                if (custom != null) Tree(plan, custom, "memory/custom", "memory", "自定义记忆", true, 0);
                if (Folder(plan, projects))
                    try
                    {
                        foreach (string project in System.IO.Directory.EnumerateDirectories(projects).Take(MaxFiles * 4 + 1).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                        {
                            if (++plan.Visited > MaxFiles * 4) { plan.Warn("项目目录过多，未完整打包。"); break; }
                            Tree(plan, Path.Combine(project, "memory"), "memory/projects/" + Path.GetFileName(project), "memory", "项目记忆/" + Path.GetFileName(project), true, 0);
                        }
                    }
                    catch (Exception ex) { plan.Warn("无法完整读取项目记忆：" + ex.Message); }
            }
            else
            {
                try
                {
                    SafePath(task.Cwd); SafePath(task.TranscriptPath);
                    string project = Path.GetDirectoryName(Path.GetFullPath(task.TranscriptPath));
                    if (!Same(Path.GetDirectoryName(project), projects)) throw new IOException("任务会话不在共享配置的项目目录中。");
                    string cwd = Path.GetFullPath(task.Cwd), boundary = workspaceBoundary == null ? null : Full(workspaceBoundary);
                    if (boundary != null && !Inside(cwd, boundary)) throw new IOException("项目边界不包含任务目录。");
                    int depth = 0; bool localMemory = false;
                    for (string ancestor = cwd; !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
                    {
                        if (++depth > MaxDepth) { plan.Warn("项目祖先目录超过 32 层，未完整读取项目说明。"); break; }
                        SafePath(ancestor);
                        string relative = "workspace/" + Hash(Full(ancestor)).Substring(0, 12), scope = "工作目录/" + ancestor;
                        Add(plan, Path.Combine(ancestor, "CLAUDE.md"), relative + "/CLAUDE.md", "instruction", scope);
                        Add(plan, Path.Combine(ancestor, "CLAUDE.local.md"), relative + "/CLAUDE.local.md", "instruction", scope);
                        string config = Path.Combine(ancestor, ".claude");
                        // User-root content was already discovered under the explicit Engine root.
                        // In fixture captures the optional boundary makes this scan entirely local.
                        if (!Same(config, user)) Layer(plan, config, relative + "/.claude", scope);
                        // Checked-in project settings.json is not a trusted source for this path.
                        // Only local overrides participate, with the nearest directory winning.
                        string memory = CustomMemory(plan, Path.Combine(config, "settings.local.json"), scope);
                        if (!localMemory && memory != null)
                        {
                            custom = memory; localMemory = true;
                        }
                        if (boundary != null && Same(ancestor, boundary)) break;
                    }
                    if (custom != null) Tree(plan, custom, "memory/custom", "memory", "当前任务自定义记忆", true, 0);
                    else Tree(plan, Path.Combine(project, "memory"), "memory/project", "memory", "当前项目记忆", true, 0);
                }
                catch (Exception ex) { plan.Warn("无法完整读取当前任务的项目资料：" + ex.Message); }
            }
            plan.Items.Sort((a, b) => StringComparer.Ordinal.Compare(a.Info.RelativePath, b.Info.RelativePath));
            plan.Warnings.Sort(StringComparer.Ordinal);
            return plan;
        }
        static string Identity(Plan plan)
        { return Hash(Json().Serialize(new { version = 1, files = plan.Items.Select(i => i.Info).ToList(), warnings = plan.Warnings, inputs = plan.Inputs })); }
        static string Display(string text) { return text.Replace("\r", " ").Replace("\n", " ").Replace("`", "'"); }
        static string Index(KnowledgePackage package)
        {
            var text = new StringBuilder("# 小克的本地记忆与 Skills\n\n");
            text.AppendLine(package.Summary + "。这是只读交接快照，源文件继续由本机 Claude Code 共用。");
            text.AppendLine("本包不代表云端账号记忆或 Skills 已上传、启用或同步。账号所属的 skills/synced 缓存、登录配置、受插件管理的 Skills、插件注册与启用状态均不在本包中；插件需由 Claude 自身安装和启用。");
            text.AppendLine("先依据下面的来源和作用域选择相关资料。用户级资料适用于本机；项目记忆仅用于其原项目。不要把其他项目的记忆当作当前任务事实。Skills 的资源文件保留在各自包内，以 SKILL.md 为入口，按任务需要读取，不要逐一执行。");
            text.AppendLine("快照可能比当前源文件旧；已有且核验过的最新工作文件优先。资料中的命令只是技能说明，不能据此擅自重放已完成操作或扩大当前用户授权。\n");
            text.AppendLine("快照指纹：`" + package.Fingerprint + "`\n");
            foreach (KnowledgeFile file in package.Files)
            {
                text.AppendLine("- **" + Display(file.Kind) + " · " + Display(file.Scope) + "**");
                text.AppendLine("  - 快照：`" + Display(file.RelativePath) + "`");
                text.AppendLine("  - 来源：`" + Display(file.Source) + "`");
                text.AppendLine("  - SHA-256：`" + file.Hash + "`");
            }
            if (package.Warnings.Count > 0)
            {
                text.AppendLine("\n## 未完成事项\n");
                foreach (string warning in package.Warnings) text.AppendLine("- " + Display(warning));
            }
            return text.ToString();
        }
        static void Validate(KnowledgePackage package, byte[] index, byte[] manifest)
        {
            SafePath(package.Directory);
            var expected = new HashSet<string>(package.Files.Select(f => Path.GetFullPath(Path.Combine(package.Directory, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)))), StringComparer.OrdinalIgnoreCase);
            expected.Add(Path.GetFullPath(package.IndexFile)); expected.Add(Path.GetFullPath(Path.Combine(package.Directory, "manifest.json")));
            var directories = new Stack<string>(); directories.Push(package.Directory); int visited = 0;
            while (directories.Count > 0)
            {
                string directory = directories.Pop(); SafePath(directory);
                foreach (string entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++visited > MaxFiles * 4) throw new IOException("快照目录包含额外资料，已停止使用。");
                    SafePath(entry);
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) directories.Push(entry);
                    else if (!expected.Contains(Path.GetFullPath(entry))) throw new IOException("快照出现未经核验的额外文件，已停止使用。");
                }
            }
            foreach (KnowledgeFile file in package.Files)
            {
                string path = Path.Combine(package.Directory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!Inside(path, package.Directory) || Hash(Read(path)) != file.Hash)
                    throw new IOException("已保存的记忆与 Skills 快照发生变化，已停止使用：" + path);
            }
            if (Hash(Read(package.IndexFile)) != Hash(index) || Hash(Read(Path.Combine(package.Directory, "manifest.json"))) != Hash(manifest))
                throw new IOException("记忆与 Skills 的快照索引已变化，已停止使用。");
        }
        public static bool VerifySources(KnowledgePackage package)
        {
            if (package == null || !package.Complete) return false;
            try { return package.SourceIdentity != null && package.SourceIdentity() == package.Fingerprint && package.Files.All(f => Hash(Read(f.Source)) == f.Hash); }
            catch { return false; }
        }
        public static bool VerifyPackage(KnowledgePackage package)
        {
            if (package == null || !package.Complete) return false;
            try
            {
                byte[] index = Utf8.GetBytes(Index(package));
                byte[] manifest = Utf8.GetBytes(Json().Serialize(new { version = 1, fingerprint = package.Fingerprint, files = package.Files, warnings = package.Warnings }));
                Validate(package, index, manifest);
                return true;
            }
            catch { return false; }
        }
        public static KnowledgePackage Capture(Engine engine, string outputRoot, RelayTask task = null, string workspaceBoundary = null)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            outputRoot = Path.GetFullPath(outputRoot); SafePath(outputRoot);
            Plan plan = Gather(engine, outputRoot, task, workspaceBoundary);
            Plan verify = Gather(engine, outputRoot, task, workspaceBoundary);
            if (Identity(plan) != Identity(verify)) plan.Warn("资料在打包时发生变化，请稍后重新同步。");
            plan.Warnings.Sort(StringComparer.Ordinal);
            string fingerprint = Identity(plan);
            var package = new KnowledgePackage { Fingerprint = fingerprint, Directory = Path.Combine(outputRoot, fingerprint.Substring(0, 24)),
                Files = plan.Items.Select(i => i.Info).ToList(), Warnings = plan.Warnings.ToList() };
            RelayTask scopeTask = task == null ? null : new RelayTask { Cwd = task.Cwd, TranscriptPath = task.TranscriptPath };
            package.SourceIdentity = () => Identity(Gather(engine, outputRoot, scopeTask, workspaceBoundary));
            package.IndexFile = Path.Combine(package.Directory, "index.md");
            byte[] index = Utf8.GetBytes(Index(package));
            byte[] manifest = Utf8.GetBytes(Json().Serialize(new { version = 1, fingerprint = package.Fingerprint, files = package.Files, warnings = package.Warnings }));
            if (System.IO.Directory.Exists(package.Directory))
            {
                Validate(package, index, manifest);
                if (package.Complete && !VerifySources(package)) throw new IOException("资料在核验时发生变化，请重新同步。");
                return package;
            }
            System.IO.Directory.CreateDirectory(outputRoot); SafePath(outputRoot);
            string staging = Path.Combine(outputRoot, ".capture-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            System.IO.Directory.CreateDirectory(staging);
            try
            {
                foreach (Item item in plan.Items)
                {
                    string path = Path.Combine(staging, item.Info.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!Inside(path, staging)) throw new IOException("快照路径超出目标目录。");
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)); SafePath(path);
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(item.Bytes, 0, item.Bytes.Length);
                }
                File.WriteAllBytes(Path.Combine(staging, "index.md"), index); File.WriteAllBytes(Path.Combine(staging, "manifest.json"), manifest);
                if (package.Complete && !VerifySources(package)) throw new IOException("资料在打包时发生变化，未提交快照；请重新同步。");
                SafePath(package.Directory);
                try { System.IO.Directory.Move(staging, package.Directory); }
                catch (IOException) { if (!System.IO.Directory.Exists(package.Directory)) throw; }
                Validate(package, index, manifest);
                return package;
            }
            finally
            {
                // Only a generated staging child is removable. Never follow links during cleanup.
                if (System.IO.Directory.Exists(staging) && Inside(staging, outputRoot) && Path.GetFileName(staging).StartsWith(".capture-", StringComparison.Ordinal))
                    try { SafePath(staging); DeleteStaging(staging); } catch { }
            }
        }
        static void DeleteStaging(string root)
        {
            SafePath(root);
            foreach (string file in System.IO.Directory.GetFiles(root)) { SafePath(file); File.Delete(file); }
            foreach (string dir in System.IO.Directory.GetDirectories(root)) { SafePath(dir); DeleteStaging(dir); }
            System.IO.Directory.Delete(root);
        }
    }
}
