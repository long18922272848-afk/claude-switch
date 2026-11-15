// ClaudeAccountSync: one-click switching between Claude desktop accounts, with Code sessions carried over.
//
// Each account lives in its own Electron --user-data-dir ("profile") and is signed in once, inside the
// app itself. Switching means starting another profile. Passwords and token caches are never stored here.
// Config reads are limited to account UUID and the app's persisted sign-in state. OAuth callbacks are
// forwarded in memory to the selected profile, never logged or written to disk.
// It writes local_*.json pointers into a target profile's active account, and its own files under
// %LOCALAPPDATA%\ClaudeAccountSync. Transcripts and source registries are never modified.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace ClaudeAccountSync
{
    class Profile
    {
        public string Id, Name, Dir;
        public bool IsRealDefault; // the app's own default dir: started with no --user-data-dir
        public string Registry { get { return IOPath.Combine(Dir, "claude-code-sessions"); } }
    }

    class Identity
    {
        public string Email, Nick;
    }

    class Entry
    {
        public string Path, Name, AccountId, Cli, Title, Snapshot;
        public Dictionary<string, object> Data;
        public DateTime Mtime;
        public long Activity;
        public bool Archived;
    }

    class SyncAction
    {
        public bool IsUpdate;
        public Entry Source, Existing;
        public string Dest;
    }

    class SyncResult
    {
        public Profile Target;
        public string Active, Leaf, Waiting, Backup;
        public List<SyncAction> Actions = new List<SyncAction>();
        public int SkipArchived, SkipDeleted, SkipNoTranscript, SkipInvalid, SkipConcurrent;
        // Claude can keep session registry entries in memory. A running target may need its Code
        // view reopened/reloaded to show the committed metadata; running jobs are never transferred.
        public bool RequiresRefresh;
        public KnowledgePackage Knowledge;
    }

    class Instance
    {
        public int Pid;
        public string Dir; // null = default dir
        public string Exe;
    }

    class Engine
    {
        const int KeepBackups = 10;
        readonly JavaScriptSerializer json;
        readonly object gate = new object();
        public readonly string ClaudeDir, StateDir, StoreFile, LogFile, BackupDir, ProfilesDir;
        public List<Profile> Profiles = new List<Profile>();
        Dictionary<string, List<string>> seen = new Dictionary<string, List<string>>();
        Dictionary<string, string> lastActive = new Dictionary<string, string>();
        // Keyed by account UUID, so a card always names the account really signed in, whatever the profile is called.
        public Dictionary<string, Identity> Identities = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);
        // Bounded and delimited: cached bytes run on after the address, and no match beats a wrong one.
        static readonly Regex EmailRx = new Regex(@"(?<![A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,24}(?![A-Za-z0-9\-.])");

        public Engine(string defaultDir, bool realDefault, string claudeDir, string stateDir)
        {
            json = new JavaScriptSerializer();
            json.MaxJsonLength = int.MaxValue;
            json.RecursionLimit = 1000;
            ClaudeDir = claudeDir;
            StateDir = stateDir;
            StoreFile = IOPath.Combine(stateDir, "accounts.json");
            LogFile = IOPath.Combine(stateDir, "sync.log");
            BackupDir = IOPath.Combine(stateDir, "backups");
            ProfilesDir = IOPath.Combine(stateDir, "profiles");
            Load();
            if (defaultDir != null && !Profiles.Any(p => p.Id == "default"))
                Profiles.Insert(0, new Profile { Id = "default", Name = "默认账号", Dir = defaultDir, IsRealDefault = realDefault });
            else if (defaultDir != null)
            {
                Profile d = Profiles.First(p => p.Id == "default");
                d.Dir = defaultDir; d.IsRealDefault = realDefault;
            }
        }

        public static string DiscoverDefaultDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var c = new List<string>();
            string d = IOPath.Combine(local, "Claude-Data");
            if (Directory.Exists(d)) c.AddRange(Directory.GetDirectories(d, "claude_*"));
            string pk = IOPath.Combine(local, "Packages");
            if (Directory.Exists(pk))
                foreach (string p in Directory.GetDirectories(pk, "Claude_*"))
                    c.Add(IOPath.Combine(p, @"LocalCache\Roaming\Claude"));
            c.Add(IOPath.Combine(roaming, "Claude"));
            return c.Where(x => Directory.Exists(IOPath.Combine(x, "claude-code-sessions")) && File.Exists(IOPath.Combine(x, "config.json")))
                    .OrderByDescending(x => File.GetLastWriteTimeUtc(IOPath.Combine(x, "config.json")))
                    .FirstOrDefault();
        }

        void Load()
        {
            if (!File.Exists(StoreFile)) return;
            try
            {
                var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(StoreFile, Encoding.UTF8));
                foreach (object o in (IEnumerable)d["profiles"])
                {
                    var p = (Dictionary<string, object>)o;
                    Profiles.Add(new Profile { Id = (string)p["id"], Name = (string)p["name"], Dir = (string)p["dir"],
                        IsRealDefault = p.ContainsKey("realDefault") && Equals(p["realDefault"], true) });
                }
                foreach (var kv in (Dictionary<string, object>)d["seen"])
                    seen[kv.Key] = ((IEnumerable)kv.Value).Cast<object>().Select(x => x.ToString()).ToList();
                object ids;
                if (d.TryGetValue("identities", out ids))
                    foreach (var kv in (Dictionary<string, object>)ids)
                    {
                        var x = (Dictionary<string, object>)kv.Value;
                        object em, ni;
                        x.TryGetValue("email", out em); x.TryGetValue("nick", out ni);
                        Identities[kv.Key] = new Identity { Email = em as string, Nick = ni as string };
                    }
                foreach (var kv in (Dictionary<string, object>)d["lastActive"])
                    lastActive[kv.Key] = kv.Value as string;
            }
            catch (Exception ex) { Log("accounts.json unreadable: " + ex.Message); }
        }

        void Save()
        {
            Directory.CreateDirectory(StateDir);
            var d = new Dictionary<string, object>();
            d["profiles"] = Profiles.Select(p => new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "dir", p.Dir }, { "realDefault", p.IsRealDefault } }).ToList();
            d["seen"] = seen;
            d["lastActive"] = lastActive;
            lock (Identities)
                d["identities"] = Identities.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object> { { "email", kv.Value.Email }, { "nick", kv.Value.Nick } });
            string tmp = StoreFile + ".tmp";
            File.WriteAllText(tmp, json.Serialize(d), new UTF8Encoding(false));
            if (File.Exists(StoreFile)) File.Replace(tmp, StoreFile, null); else File.Move(tmp, StoreFile);
        }

        public void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(StateDir);
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        public Profile AddProfile(string name)
        {
            lock (gate)
            {
                string id = "p-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var p = new Profile { Id = id, Name = name, Dir = IOPath.Combine(ProfilesDir, id) };
                Directory.CreateDirectory(p.Dir);
                Profiles.Add(p);
                Save();
                Log("added account " + name + " at " + p.Dir);
                return p;
            }
        }

        // A name given to a signed-in card belongs to that account, and follows it to any window.
        public void Rename(Profile p, string name)
        {
            lock (gate)
            {
                p.Name = name;
                string a = ActiveAccount(p);
                if (a != null)
                {
                    lock (Identities)
                    {
                        Identity i;
                        if (!Identities.TryGetValue(a, out i)) Identities[a] = i = new Identity();
                        i.Nick = name;
                    }
                }
                Save();
            }
        }

        // Only forgets the entry. The profile's folder and its login stay on disk.
        public void Forget(Profile p) { lock (gate) { Profiles.Remove(p); Save(); Log("removed from list: " + p.Name + " (folder kept: " + p.Dir + ")"); } }

        public string EmailOf(string account)
        {
            Identity i;
            lock (Identities)
                return account != null && Identities.TryGetValue(account, out i) && !string.IsNullOrEmpty(i.Email) ? i.Email : null;
        }

        // What a card is called: the real account when signed in; the chosen name only before sign-in.
        public string Display(Profile p)
        {
            string a = ActiveAccount(p);
            if (a == null) return p.Name;
            Identity i;
            lock (Identities)
                if (Identities.TryGetValue(a, out i))
                {
                    if (!string.IsNullOrEmpty(i.Nick)) return i.Nick;
                    if (!string.IsNullOrEmpty(i.Email)) return i.Email;
                }
            return "账号 " + a.Substring(0, 8);
        }

        public bool IsDuplicate(Profile p)
        {
            string a = ActiveAccount(p);
            return a != null && Profiles.Any(o => o != p && string.Equals(ActiveAccount(o), a, StringComparison.OrdinalIgnoreCase));
        }

        // Learns account-to-email pairs from two local places, extracting only those two fields:
        // the CLI's oauthAccount record, and the app's cached account object next to the active UUID.
        public void LearnIdentities()
        {
            lock (gate)
            {
                bool changed = LearnFromCliConfig();
                foreach (Profile p in Profiles)
                {
                    string a = ActiveAccount(p);
                    if (a == null || EmailOf(a) != null) continue;
                    string mail = EmailFromAppCache(p, a);
                    if (mail == null) continue;
                    changed |= SetEmail(a, mail);
                }
                if (changed) Save();
            }
        }

        bool SetEmail(string account, string mail)
        {
            lock (Identities)
            {
                Identity i;
                if (!Identities.TryGetValue(account, out i)) Identities[account] = i = new Identity();
                if (string.Equals(i.Email, mail, StringComparison.OrdinalIgnoreCase)) return false;
                i.Email = mail;
            }
            Log("identified " + account.Substring(0, 8) + " = " + mail);
            return true;
        }

        bool LearnFromCliConfig()
        {
            foreach (string f in new[] { ClaudeDir.TrimEnd('\\') + ".json", IOPath.Combine(ClaudeDir, ".claude.json") })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    string t = ReadShared(f);
                    int at = t.IndexOf("\"oauthAccount\"", StringComparison.Ordinal);
                    if (at < 0) continue;
                    string part = t.Substring(at, Math.Min(2000, t.Length - at));
                    Match u = Regex.Match(part, "\"accountUuid\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"");
                    Match m = Regex.Match(part, "\"emailAddress\"\\s*:\\s*\"([^\"]+)\"");
                    if (u.Success && m.Success) return SetEmail(u.Groups[1].Value, m.Groups[1].Value);
                }
                catch (IOException) { }
            }
            return false;
        }

        // Accepts an address only when it is the single one close to this account's UUID.
        string EmailFromAppCache(Profile p, string account)
        {
            string root = IOPath.Combine(p.Dir, @"IndexedDB\https_claude.ai_0.indexeddb.blob");
            if (!Directory.Exists(root)) return null;
            Encoding latin = Encoding.GetEncoding(28591); // one char per byte; the cached strings are plain ASCII
            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(400))
            {
                try
                {
                    if (new FileInfo(f).Length > 5 * 1024 * 1024) continue;
                    string s;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, latin, false))
                        s = sr.ReadToEnd();
                    for (int i = s.IndexOf(account, StringComparison.OrdinalIgnoreCase); i >= 0; i = s.IndexOf(account, i + 36, StringComparison.OrdinalIgnoreCase))
                    {
                        int from = Math.Max(0, i - 1200);
                        var found = EmailRx.Matches(s.Substring(from, Math.Min(2400, s.Length - from))).Cast<Match>()
                            .Select(x => x.Value.ToLowerInvariant()).Distinct().ToList();
                        if (found.Count == 1) return found[0];
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        public Profile Find(string q)
        {
            return Profiles.FirstOrDefault(p => p.Id == q || p.Name == q);
        }

        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false), true))
                return sr.ReadToEnd();
        }

        // Regex on purpose: this file also holds token caches, which must never be deserialized here.
        public string ActiveAccount(Profile p)
        {
            string cfg = IOPath.Combine(p.Dir, "config.json");
            if (!File.Exists(cfg)) return null;
            try
            {
                string text = ReadShared(cfg);
                // lastKnownAccountUuid survives logout. Current Claude persists this boolean whenever
                // its verified account state changes; an old UUID alone is not a successful login.
                if (!Regex.IsMatch(text, "\"windowSizeWasSignedIn\"\\s*:\\s*true\\b")) return null;
                Match m = Regex.Match(text, "\"lastKnownAccountUuid\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (IOException) { return null; }
        }

        Entry LoadEntry(string path, string account)
        {
            var e = new Entry { Path = path, Name = IOPath.GetFileName(path), AccountId = account, Mtime = File.GetLastWriteTimeUtc(path) };
            try
            {
                e.Snapshot = ReadShared(path);
                e.Data = json.Deserialize<Dictionary<string, object>>(e.Snapshot);
                object v;
                if (e.Data.TryGetValue("lastActivityAt", out v) && v != null) e.Activity = Convert.ToInt64(v);
                if (e.Data.TryGetValue("isArchived", out v) && v is bool) e.Archived = (bool)v;
                if (e.Data.TryGetValue("cliSessionId", out v)) e.Cli = v as string;
                if (e.Data.TryGetValue("title", out v)) e.Title = v as string;
            }
            catch { e.Data = null; }
            return e;
        }

        List<Entry> LoadAccount(string dir, string account)
        {
            var list = new List<Entry>();
            foreach (string leaf in Directory.GetDirectories(dir))
                foreach (string f in Directory.GetFiles(leaf, "local_*.json"))
                    list.Add(LoadEntry(f, account));
            return list;
        }

        public void Summary(Profile p, out string active, out int sessions, out long lastAt)
        {
            active = ActiveAccount(p); sessions = 0; lastAt = 0;
            if (active == null) return;
            string dir = IOPath.Combine(p.Registry, active);
            if (!Directory.Exists(dir)) return;
            foreach (Entry e in LoadAccount(dir, active))
            {
                if (e.Data == null || e.Archived) continue;
                sessions++;
                if (e.Activity > lastAt) lastAt = e.Activity;
            }
        }

        HashSet<string> Transcripts()
        {
            string root = IOPath.Combine(ClaudeDir, "projects");
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("transcript dir not found: " + root);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in Directory.GetDirectories(root))
                foreach (string f in Directory.GetFiles(p, "*.jsonl"))
                    ids.Add(IOPath.GetFileNameWithoutExtension(f));
            return ids;
        }

        string Comparable(Dictionary<string, object> d)
        {
            var c = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in d) if (kv.Key != "bridgeSessionIds") c[kv.Key] = kv.Value;
            return json.Serialize(c);
        }

        static string PickLeaf(string dir)
        {
            string best = null; DateTime bestAt = DateTime.MinValue;
            foreach (string leaf in Directory.GetDirectories(dir))
            {
                DateTime at = DateTime.MinValue;
                foreach (string f in Directory.GetFiles(leaf, "local_*.json"))
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > at) at = t;
                }
                if (best == null || at > bestAt) { best = leaf; bestAt = at; }
            }
            return best;
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                string dest = IOPath.Combine(to, IOPath.GetFileName(f));
                using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var o = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
                    s.CopyTo(o);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(f));
            }
            foreach (string d in Directory.GetDirectories(from))
                CopyDir(d, IOPath.Combine(to, IOPath.GetFileName(d)));
        }

        string MakeBackup(Profile p)
        {
            string root = IOPath.Combine(BackupDir, p.Id);
            string dest = IOPath.Combine(root, "registry-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            CopyDir(p.Registry, dest);
            foreach (var d in new DirectoryInfo(root).GetDirectories("registry-*").OrderByDescending(x => x.Name).Skip(KeepBackups))
                d.Delete(true);
            return dest;
        }

        public bool AccountChanged(Profile p)
        {
            string a = ActiveAccount(p), last;
            if (a == null) return false;
            lock (gate)
                return !lastActive.TryGetValue(p.Id, out last) || !string.Equals(last, a, StringComparison.OrdinalIgnoreCase);
        }

        public SyncResult Sync(Profile target, bool dryRun, Action<string> progress = null)
        {
            lock (gate) return SyncLocked(target, dryRun, progress);
        }

        SyncResult SyncLocked(Profile target, bool dryRun, Action<string> progress)
        {
            var r = new SyncResult { Target = target };
            string active = r.Active = ActiveAccount(target);
            if (active == null) { r.Waiting = "尚未登录：在这个账号的 Claude 窗口里登录一次"; return r; }
            string activeDir = IOPath.Combine(target.Registry, active);
            string leaf = Directory.Exists(activeDir) ? PickLeaf(activeDir) : null;
            if (leaf == null) { r.Waiting = "还没有会话目录：在这个账号里新建一个 Code 会话后才能同步"; return r; }
            r.Leaf = leaf;

            var mine = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry e in LoadAccount(activeDir, active)) mine[e.Name] = e;

            var winners = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (Profile p in Profiles)
            {
                if (!Directory.Exists(p.Registry)) continue;
                foreach (string acc in Directory.GetDirectories(p.Registry))
                {
                    if (string.Equals(acc.TrimEnd('\\'), activeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (Entry e in LoadAccount(acc, IOPath.GetFileName(acc)))
                    {
                        if (e.Data == null) { r.SkipInvalid++; continue; }
                        Entry best;
                        if (!winners.TryGetValue(e.Name, out best) || e.Activity > best.Activity || (e.Activity == best.Activity && e.Mtime > best.Mtime))
                            winners[e.Name] = e;
                    }
                }
            }

            HashSet<string> transcripts = Transcripts();
            string key = target.Id + "|" + active;
            List<string> seenList;
            var seenSet = new HashSet<string>(seen.TryGetValue(key, out seenList) ? seenList : new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (Entry src in winners.Values)
            {
                Entry have;
                if (mine.TryGetValue(src.Name, out have))
                {
                    if (have.Data == null) { r.SkipInvalid++; continue; }
                    // Only a strictly later edit that loses no activity may replace the target's copy.
                    if (src.Mtime > have.Mtime && src.Activity >= have.Activity && Comparable(src.Data) != Comparable(have.Data))
                        r.Actions.Add(new SyncAction { IsUpdate = true, Source = src, Existing = have, Dest = have.Path });
                    continue;
                }
                if (src.Archived) { r.SkipArchived++; continue; }
                if (seenSet.Contains(src.Name)) { r.SkipDeleted++; continue; } // deleted here on purpose: do not resurrect
                if (src.Cli == null || !transcripts.Contains(src.Cli)) { r.SkipNoTranscript++; continue; }
                r.Actions.Add(new SyncAction { Source = src, Dest = IOPath.Combine(leaf, src.Name) });
            }

            if (dryRun) return r;

            if (progress != null) progress("syncing");

            if (r.Actions.Count > 0)
            {
                r.Backup = MakeBackup(target);
                foreach (SyncAction a in r.Actions.ToList())
                {
                    if (!WriteEntry(a, active))
                    {
                        r.Actions.Remove(a); r.SkipConcurrent++;
                        Log("[" + Display(target) + "] kept concurrently changed session " + a.Source.Name);
                        continue;
                    }
                    Log("[" + Display(target) + "] " + (a.IsUpdate ? "updated " : "copied  ") + a.Source.Name + "  " + a.Source.Title);
                }
                Log("[" + Display(target) + "] backup: " + r.Backup);
            }
            // Keep the history of imported IDs: dropping a deleted ID here would resurrect it on
            // the next switch after the first skip, which is especially visible with rapid handoffs.
            seen[key] = seenSet.Union(LoadAccount(activeDir, active).Select(e => e.Name), StringComparer.OrdinalIgnoreCase).ToList();
            lastActive[target.Id] = active;
            Save();
            if (r.SkipConcurrent > 0) r.Waiting = "有 " + r.SkipConcurrent + " 个会话正在变化，已保留目标进度，下次切换时会重试";
            return r;
        }

        bool DestinationUnchanged(SyncAction a)
        {
            if (!a.IsUpdate) return !File.Exists(a.Dest);
            if (!File.Exists(a.Dest)) return false;
            try { return File.GetLastWriteTimeUtc(a.Dest) == a.Existing.Mtime && ReadShared(a.Dest) == a.Existing.Snapshot; }
            catch (IOException) { return false; }
        }

        bool WriteEntry(SyncAction a, string active)
        {
            var data = new Dictionary<string, object>(a.Source.Data);
            // bridgeSessionIds point at server sessions of the owning account.
            object bridge = null;
            if (a.IsUpdate && a.Existing.Data.TryGetValue("bridgeSessionIds", out bridge) && bridge != null) data["bridgeSessionIds"] = bridge;
            else if (!a.IsUpdate && string.Equals(a.Source.AccountId, active, StringComparison.OrdinalIgnoreCase) && a.Source.Data.ContainsKey("bridgeSessionIds")) { }
            else data["bridgeSessionIds"] = new object[0];
            string tmp = a.Dest + ".sync-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, json.Serialize(data), new UTF8Encoding(false));
                // Keep the source's mtime so the same entry is not copied back and forth.
                File.SetLastWriteTimeUtc(tmp, a.Source.Mtime);
                // Recheck immediately before replacing. A new destination uses create-only Move so
                // another writer winning that race can never be overwritten by this synchronizer.
                if (!DestinationUnchanged(a)) return false;
                try { if (a.IsUpdate) File.Replace(tmp, a.Dest, null); else File.Move(tmp, a.Dest); }
                catch (IOException) { if (!DestinationUnchanged(a)) return false; throw; }
                Entry check = LoadEntry(a.Dest, active);
                if (check.Data == null || Comparable(check.Data) != Comparable(a.Source.Data))
                    throw new InvalidDataException("verification failed for " + a.Dest);
                return true;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }

    static class App
    {
        static string Norm(string p) { return IOPath.GetFullPath(p).TrimEnd('\\'); }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int command);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        public static List<Instance> Instances()
        {
            var list = new List<Instance>();
            using (var s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='Claude.exe'"))
            using (var all = s.Get())
                foreach (ManagementObject m in all)
                    using (m)
                    {
                        string cmd = m["CommandLine"] as string, exe = m["ExecutablePath"] as string;
                        if (cmd == null || exe == null || cmd.Contains("--type=")) continue;
                        if (exe.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0 && exe.IndexOf(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Match d = Regex.Match(cmd, "(?:^|\\s)--user-data-dir(?:=|\\s+)(?:\"([^\"]+)\"|(\\S+))", RegexOptions.IgnoreCase);
                        string dir = !d.Success ? null : (d.Groups[1].Success ? d.Groups[1].Value : d.Groups[2].Value);
                        list.Add(new Instance { Pid = Convert.ToInt32(m["ProcessId"]), Dir = dir, Exe = exe });
                    }
            return list;
        }

        public static bool Belongs(Instance i, Profile p)
        {
            if (i.Dir == null) return p.IsRealDefault;
            return string.Equals(Norm(i.Dir), Norm(p.Dir), StringComparison.OrdinalIgnoreCase);
        }

        public static void Kill(Instance i)
        {
            var psi = new ProcessStartInfo("taskkill", "/T /F /PID " + i.Pid) { CreateNoWindow = true, UseShellExecute = false };
            using (Process p = Process.Start(psi))
                if (!p.WaitForExit(10000) || (p.ExitCode != 0 && Instances().Any(x => x.Pid == i.Pid)))
                    throw new IOException("Claude 尚未退出，已停止切换，请关闭对应窗口后重试。");
        }

        public static string FindExe(List<Instance> running)
        {
            if (running.Count > 0) return running[0].Exe;
            try
            {
                var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command \"(Get-AppxPackage -Name Claude).InstallLocation\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                using (Process p = Process.Start(psi))
                {
                    string dir = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(15000);
                    string exe = IOPath.Combine(dir, @"app\Claude.exe");
                    if (dir.Length > 0 && File.Exists(exe)) return exe;
                }
            }
            catch { }
            string local = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"AnthropicClaude\claude.exe");
            return File.Exists(local) ? local : null;
        }

        public static void Launch(Profile p, string exe)
        {
            string leaf = IOPath.GetFileName(p.Dir.TrimEnd('\\'));
            if (p.IsRealDefault && leaf.StartsWith("claude_", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start("explorer.exe", @"shell:AppsFolder\Claude_" + leaf.Substring("claude_".Length) + "!Claude");
                return;
            }
            if (exe == null) throw new FileNotFoundException("找不到 Claude 程序");
            Directory.CreateDirectory(p.Dir);
            string args = p.IsRealDefault ? "" : "--user-data-dir=\"" + p.Dir + "\"";
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
        }

        public static string Quote(string value)
        {
            // CommandLineToArgvW quoting, including trailing backslashes. Never invoke cmd/powershell.
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }

        public static bool ValidCallback(string value, string scheme)
        {
            Uri uri;
            return value != null && value.Length <= 16384 &&
                !value.Any(c => char.IsControl(c) || c == '"' || c == '\\' || char.IsWhiteSpace(c)) &&
                Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase) &&
                value.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase);
        }

        public static void ForwardCallback(Profile p, string exe, string value, string scheme)
        {
            if (!ValidCallback(value, scheme)) throw new ArgumentException("请粘贴完整的 " + scheme + ":// 授权链接。");
            if (exe == null || !File.Exists(exe)) throw new FileNotFoundException("找不到 Claude 程序");
            string args = (p.IsRealDefault ? "" : "--user-data-dir=" + Quote(p.Dir) + " ") + "-- " + Quote(value);
            // An unpackaged broker starts the actual executable. Shell activation of a Windows Store
            // executable can route to the package's default instance instead of the chosen profile.
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
        }

        public static void WaitForProfile(Profile target)
        {
            for (int n = 0; n < 40; n++)
            {
                if (Instances().Any(i => Belongs(i, target))) return;
                Thread.Sleep(250);
            }
            throw new IOException("没有检测到所选账号的独立窗口，未确认切换成功。请重试或查看日志。");
        }

        static IntPtr FindProfileWindow(Profile target, out int pid)
        {
            pid = 0;
            // Refresh the command-line/profile mapping after sync; do not choose a window by its
            // shared Claude title or package identity, both of which can name a different account.
            foreach (Instance instance in Instances().Where(i => Belongs(i, target)))
            {
                try
                {
                    using (Process process = Process.GetProcessById(instance.Pid))
                    {
                        process.Refresh();
                        IntPtr hwnd = process.MainWindowHandle;
                        uint owner;
                        if (hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out owner) != 0 && owner == (uint)instance.Pid)
                        { pid = instance.Pid; return hwnd; }
                    }
                }
                catch (ArgumentException) { } // Process exited between the snapshots.
                catch (InvalidOperationException) { }
            }
            return IntPtr.Zero;
        }

        static bool FocusProfileWindow(IntPtr hwnd, int pid)
        {
            ShowWindowAsync(hwnd, IsIconic(hwnd) ? 9 : 5); // restore minimized; otherwise preserve size
            for (int attempt = 0; attempt < 8; attempt++)
            {
                SetForegroundWindow(hwnd);
                Thread.Sleep(80);
                uint foregroundPid;
                GetWindowThreadProcessId(GetForegroundWindow(), out foregroundPid);
                if (foregroundPid == (uint)pid) return true;
            }
            return false; // Windows can reject foreground activation; never label that a success.
        }

        static void ActivateProfile(Profile target, string exe)
        {
            int pid;
            IntPtr hwnd = FindProfileWindow(target, out pid);
            if (hwnd == IntPtr.Zero)
            {
                // A tray-only or not-yet-started instance still needs its normal launch path.
                Launch(target, exe);
                WaitForProfile(target);
                for (int attempt = 0; attempt < 40 && hwnd == IntPtr.Zero; attempt++)
                {
                    Thread.Sleep(250);
                    hwnd = FindProfileWindow(target, out pid);
                }
            }
            if (hwnd == IntPtr.Zero)
                throw new IOException("目标账号进程已打开，但没有找到它的窗口，未确认切换成功。请从任务栏打开该账号窗口后重试。");
            if (!FocusProfileWindow(hwnd, pid))
                throw new IOException("会话记录已核对，但 Windows 未允许将目标账号窗口切到前台。请手动点击该账号窗口后重试。");
        }

        // Closes the other accounts' windows (force: running sessions stop), syncs, then starts the target.
        public static SyncResult PrepareSwitch(Engine e, Profile target, bool targetRunning, Action<string> progress = null)
        {
            SyncResult result = e.Sync(target, false, progress);
            if (progress != null) progress("knowledge");
            LiveKnowledgeResult live = LiveKnowledge.Refresh(e);
            if (!live.Ready) throw new IOException(live.Message);
            result.Knowledge = live.Package;
            result.RequiresRefresh = targetRunning && result.Actions.Count > 0;
            return result;
        }

        public static SyncResult Switch(Engine e, Profile target, bool closeOthers, Action<string> progress = null)
        {
            if (progress != null) progress("packing");
            List<Instance> running = Instances();
            string exe = FindExe(running);
            bool targetRunning = running.Any(i => Belongs(i, target));
            if (closeOthers)
                foreach (Instance i in running.Where(i => !Belongs(i, target) && e.Profiles.Any(p => Belongs(i, p))))
                {
                    Kill(i);
                    e.Log("closed instance pid " + i.Pid);
                }
            if (closeOthers && running.Count > 0) Thread.Sleep(2000); // let exit-time writes land before syncing
            SyncResult r = PrepareSwitch(e, target, targetRunning, progress);
            if (progress != null) progress("opening");
            ActivateProfile(target, exe);
            e.Log("switched to " + e.Display(target) + (closeOthers ? "" : " (side by side)"));
            return r;
        }
    }

    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        static string Arg(string[] a, string n) { int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

        public static bool ValidCallbackArguments(string[] args)
        {
            if (args.Length % 2 != 0) return false;
            var allowed = new HashSet<string>(new[] { "--dispatch-callback", "--state-dir", "--scheme", "--callback", "--protocol-callback" });
            var supplied = new HashSet<string>();
            for (int i = 0; i < args.Length; i += 2)
                if (!allowed.Contains(args[i]) || !supplied.Add(args[i])) return false;
            bool permanent = supplied.Contains("--protocol-callback");
            return supplied.Contains("--state-dir") && supplied.Contains("--scheme") &&
                (permanent ? supplied.Count == 3 : supplied.Count == 4 && supplied.Contains("--dispatch-callback") && supplied.Contains("--callback"));
        }

        [STAThread]
        static int Main(string[] args)
        {
            // Protocol URLs are untrusted. Reject extra flags introduced by a malformed shell URL
            // before parsing any directory override or taking any other action.
            if ((args.Contains("--dispatch-callback") || args.Contains("--protocol-callback")) && !ValidCallbackArguments(args)) return 2;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string envCfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            string defArg = Arg(args, "--default-dir");
            string defDir = defArg ?? Engine.DiscoverDefaultDir();
            string claudeDir = Arg(args, "--claude-dir") ?? (string.IsNullOrEmpty(envCfg) ? Path.Combine(home, ".claude") : envCfg);
            string stateDir = Arg(args, "--state-dir") ?? Path.Combine(local, "ClaudeAccountSync");
            string shot = Arg(args, "--shot");

            string add = Arg(args, "--add"), sw = Arg(args, "--switch"), open = Arg(args, "--open");
            bool cli = args.Contains("--list") || args.Contains("--sync") || args.Contains("--knowledge") || args.Contains("--usage") || args.Contains("--token-stats") || add != null || sw != null || open != null;
            var engine = new Engine(defDir, defArg == null, claudeDir, stateDir);

            // Native Code hook: no UI, no account switch and no task submission. Only fresh,
            // scoped context is returned to the Code process that invoked this executable.
            if (args.Contains("--knowledge-context"))
            {
                var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                try
                {
                    using (var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
                        output.Write(CodeKnowledgeHook.Handle(engine, input.ReadToEnd()));
                }
                catch { output.Write("{\"systemMessage\":\"小克未能读取最新本地资料；不要把旧上下文当成已同步。\"}"); }
                return 0;
            }
            if (args.Contains("--enable-live-knowledge"))
            {
                var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                try { output.WriteLine(CodeKnowledgeHook.Install(engine, Application.ExecutablePath)); return 0; }
                catch (Exception ex) { output.WriteLine("ERROR: " + ex.Message); return 1; }
            }

            // Protocol activation must be handled before the single-panel mutex. Do not print the URL
            // or exception messages here: either can contain the short-lived authorization code.
            string callbackProfile = Arg(args, "--dispatch-callback");
            string protocolCallback = Arg(args, "--protocol-callback");
            if (callbackProfile != null || protocolCallback != null)
            {
                try
                {
                    string scheme = Arg(args, "--scheme") ?? "claude";
                    Profile p = callbackProfile != null ? engine.Find(callbackProfile) :
                        CallbackRoute.DispatchTarget(engine, scheme);
                    if (p == null || (callbackProfile != null && !CallbackRoute.CanDispatch(engine, p, scheme))) return 2;
                    App.ForwardCallback(p, App.FindExe(App.Instances()), protocolCallback ?? Arg(args, "--callback"), scheme);
                    engine.Log("callback forwarded to profile " + p.Id);
                    return 0;
                }
                catch { engine.Log("callback forwarding failed (URL omitted)"); return 1; }
            }
            if (args.Contains("--configure-protocol"))
            {
                ProtocolRegistration.OpenSettings(engine, Arg(args, "--scheme") ?? "claude");
                return 0;
            }

            if (cli)
            {
                var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                try
                {
                    if (args.Contains("--token-stats"))
                    {
                        TokenStatisticsSnapshot snapshot = new TokenStatisticsService(engine).Refresh();
                        o.WriteLine(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(snapshot));
                    }
                    if (args.Contains("--usage"))
                    {
                        var privacy = new EmailPrivacy(engine.StateDir);
                        foreach (Profile profile in engine.Profiles.ToList())
                        {
                            UsageHistoryResult result = new UsageMonitor(engine).Read(profile);
                            o.WriteLine(privacy.Text(engine.Display(profile)));
                            o.WriteLine(UsageDisplay.MeterLine(result.Snapshot, "five_hour", DateTimeOffset.UtcNow));
                            o.WriteLine(UsageDisplay.MeterLine(result.Snapshot, "seven_day", DateTimeOffset.UtcNow));
                            o.WriteLine(UsageDisplay.Freshness(result.Snapshot, DateTimeOffset.UtcNow));
                            o.WriteLine(result.Message);
                        }
                    }
                    if (args.Contains("--knowledge"))
                    {
                        LiveKnowledgeResult live = LiveKnowledge.Refresh(engine);
                        if (!live.Ready) { o.WriteLine(live.Message); return 1; }
                        KnowledgePackage pack = live.Package;
                        o.WriteLine(pack.Summary); o.WriteLine("index=" + pack.IndexFile);
                        foreach (string warning in pack.Warnings) o.WriteLine("warning=" + warning);
                        if (!pack.Complete) return 1;
                    }
                    if (add != null) { Profile p = engine.AddProfile(add); o.WriteLine("added " + p.Name + " " + p.Dir); }
                    if (args.Contains("--list")) engine.LearnIdentities();
                    if (args.Contains("--list"))
                        foreach (Profile p in engine.Profiles)
                        {
                            string a; int n; long t;
                            engine.Summary(p, out a, out n, out t);
                            o.WriteLine("{0}\t{1}\tactive={2}\tsessions={3}\tdir={4}\tshown={5}\temail={6}\tdup={7}", p.Id, p.Name, a ?? "-", n, p.Dir, engine.Display(p), engine.EmailOf(a) ?? "-", engine.IsDuplicate(p));
                        }
                    if (args.Contains("--sync"))
                    {
                        bool dry = args.Contains("--dry-run");
                        foreach (Profile p in engine.Profiles)
                        {
                            SyncResult r = engine.Sync(p, dry);
                            o.WriteLine("[{0}] active={1}{2}", p.Name, r.Active ?? "-", r.Waiting != null ? " waiting: " + r.Waiting : "");
                            foreach (SyncAction x in r.Actions) o.WriteLine((x.IsUpdate ? "update  " : "copy    ") + x.Source.Name + "  " + x.Source.Title);
                            o.WriteLine("actions={0} skipArchived={1} skipDeleted={2} skipNoTranscript={3} skipInvalid={4}{5}", r.Actions.Count, r.SkipArchived, r.SkipDeleted, r.SkipNoTranscript, r.SkipInvalid, dry ? " (dry run)" : "");
                        }
                    }
                    if (sw != null || open != null)
                    {
                        Profile p = engine.Find(sw ?? open);
                        if (p == null) { o.WriteLine("ERROR: no such account"); return 2; }
                        if (engine.ActiveAccount(p) == null)
                        {
                            o.WriteLine("ERROR: account is not signed in; open the panel and use Open and sign in so callback routing stays active.");
                            return 2;
                        }
                        App.Switch(engine, p, sw != null);
                        o.WriteLine("launched " + p.Name);
                    }
                    return 0;
                }
                catch (Exception ex) { o.WriteLine("ERROR: " + ex.Message); return 1; }
            }

            if (defDir == null) { MessageBox.Show("找不到 Claude 桌面端的数据目录。", "Claude Switch"); return 2; }
            SetProcessDPIAware();
            Theme.Init(Arg(args, "--theme"));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Panel.Scheme = Arg(args, "--scheme") ?? "claude"; // tests pass a throwaway scheme
            if (shot != null)
            {
                using (var f = new Panel(engine, false)) { f.SaveShot(shot); }
                return 0;
            }
            bool first;
            using (var mutex = new Mutex(true, @"Local\ClaudeAccountSync-" + (stateDir.GetHashCode() & 0xffff), out first))
            {
                if (!first) return 0;
                Application.Run(new Panel(engine, args.Contains("--background")));
            }
            return 0;
        }
    }
}
