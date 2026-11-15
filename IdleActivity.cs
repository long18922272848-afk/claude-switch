using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    // Presence affects the mascot only. No task or process is ever changed here.
    class IdleActivity
    {
        public const int RestAfterSeconds = 20, UserIdleSeconds = 60;
        DateTime lastActive;
        public IdleActivity(DateTime now) { lastActive = now; }
        public bool Update(DateTime now, bool usingClaude, bool backgroundBusy, bool switching)
        {
            if (now < lastActive || usingClaude || backgroundBusy || switching) lastActive = now;
            return !usingClaude && !backgroundBusy && !switching && (now - lastActive).TotalSeconds >= RestAfterSeconds;
        }

        [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size, Time; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput input);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        public static bool IsUsingClaude(IEnumerable<int> pids)
        {
            uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if (!pids.Contains((int)pid)) return false;
            var input = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            if (!GetLastInputInfo(ref input)) return true;
            uint idle = unchecked((uint)Environment.TickCount - input.Time);
            return idle < UserIdleSeconds * 1000;
        }

        internal static bool BusyStatus(string status)
        {
            // Unknown or waiting-for-input status stays at the desk, not a false idle signal.
            return status != "idle";
        }
        public static bool HasBusyTask(Engine engine)
        {
            try
            {
                string root = Path.Combine(engine.ClaudeDir, "sessions");
                if (!Directory.Exists(root)) return false;
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return true;
                int count = 0;
                var json = new JavaScriptSerializer { MaxJsonLength = 262144 };
                foreach (string file in Directory.EnumerateFiles(root, "*.json"))
                {
                    if (++count > 2048) return true;
                    int pid;
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0) continue;
                    try
                    {
                        using (var process = Process.GetProcessById(pid))
                            if (!process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch (ArgumentException) { continue; }
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return true;
                    Dictionary<string, object> record;
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (stream.Length > 262144) return true;
                        using (var reader = new StreamReader(stream)) record = json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    }
                    if (record == null) return true;
                    object value;
                    string status = record.TryGetValue("status", out value) ? value as string : null;
                    if (BusyStatus(status)) return true;
                }
                return false;
            }
            catch { return true; }
        }
    }

    static class IdleActivityTests
    {
        public static int Run()
        {
            int count = 0;
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception("Presence fixture: " + name); count++; };
            DateTime now = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
            var activity = new IdleActivity(now);
            check(!activity.Update(now.AddSeconds(19), false, false, false), "absence has a grace period");
            check(activity.Update(now.AddSeconds(20), false, false, false), "idle enters lounge after grace period");
            check(!activity.Update(now.AddSeconds(21), true, false, false), "using Claude immediately wakes pet");
            check(!activity.Update(now.AddSeconds(40), false, false, false), "returning resets countdown");
            check(!activity.Update(now.AddSeconds(80), false, true, false), "busy background work keeps pet at desk");
            check(!activity.Update(now.AddSeconds(100), false, false, true), "handoff never enters lounge");
            check(!activity.Update(now.AddSeconds(110), false, false, false), "handoff ending has fresh grace period");
            check(activity.Update(now.AddSeconds(120), false, false, false), "rest resumes after handoff grace");
            check(!activity.Update(now.AddSeconds(90), false, false, false), "clock rollback does not force rest");
            check(!IdleActivity.BusyStatus("idle") && IdleActivity.BusyStatus("busy") && IdleActivity.BusyStatus("shell"), "recognized running states keep desk");
            check(IdleActivity.BusyStatus("waiting") && IdleActivity.BusyStatus(null) && IdleActivity.BusyStatus("future"), "unknown status cannot claim idle");
            return count;
        }
    }
}
