using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    static class OfficeTests
    {
        static int passed;

        static void Check(bool value, string description)
        {
            if (!value) throw new Exception("Office history: " + description);
            passed++;
        }

        static Engine Open(string root)
        {
            return new Engine(null, false, Path.Combine(root, "claude"), Path.Combine(root, "state"));
        }

        static void CheckLifecycle(string root)
        {
            Engine office = Open(root);
            Profile original = office.AddProfile("Morning");
            OfficeDesk desk = office.OfficeDesks().Single();
            Check(!desk.Archived && desk.ProfileId == original.Id && desk.Started == DateTime.Today && !desk.Ended.HasValue,
                  "new account owns a single active desk starting today");
            Check(desk.UsageDays(DateTime.Today) == 1 && desk.UsageDays(DateTime.Today.AddDays(4)) == 5,
                  "active desk counts both calendar endpoints");
            office.Rename(original, "Morning renamed");
            string stableKey = desk.Key;
            Check(office.OfficeDesks().Single().Key == stableKey && office.OfficeDesks().Single().Name == "Morning renamed",
                  "rename retains the same desk");
            File.WriteAllText(Path.Combine(original.Dir, "user-file.txt"), "isolated fixture");
            office.Forget(original);
            OfficeDesk archive = Open(root).OfficeDesks().Single();
            Check(archive.Archived && archive.Name == "Morning renamed" && archive.Ended == DateTime.Today,
                  "removal freezes the renamed desk and persists after restart");
            Check(archive.UsageDays(DateTime.Today.AddYears(1)) == 1 && string.IsNullOrEmpty(archive.ProfileId),
                  "archived desk stops counting and no longer points to an active account");
            Check(File.Exists(Path.Combine(original.Dir, "user-file.txt")) && Open(root).Profiles.Count == 0,
                  "removal retains existing Forget behavior without restoring a profile");
            office.AddProfile("Morning renamed");
            List<OfficeDesk> reopened = Open(root).OfficeDesks();
            Check(reopened.Count == 2 && reopened.Count(d => d.Archived) == 1 &&
                reopened.Select(d => d.Key).Distinct().Count() == 2, "reusing a name creates a distinct workstation");
        }

        static void CheckMigration(string root)
        {
            string state = Path.Combine(root, "state");
            string profiles = Path.Combine(state, "profiles");
            string activePath = Path.Combine(profiles, "p-aabbccdd");
            string archivePath = Path.Combine(profiles, "p-11223344");
            string noStartPath = Path.Combine(profiles, "p-0000ffff");
            Directory.CreateDirectory(activePath);
            var store = new { profiles = new[] { new { id = "p-aabbccdd", name = "Current", dir = activePath, realDefault = false } },
                seen = new Dictionary<string, object>(), identities = new Dictionary<string, object>(), lastActive = new Dictionary<string, object>() };
            File.WriteAllText(Path.Combine(state, "accounts.json"), new JavaScriptSerializer().Serialize(store));
            File.WriteAllLines(Path.Combine(state, "sync.log"), new[] {
                "2024-02-28 20:00:00  added account Past at " + archivePath,
                "2024-02-29 10:00:00  added account Current at " + activePath,
                "2024-03-01 23:00:00  removed from list: Past alias (folder kept: " + archivePath + ")",
                "2024-03-01 23:01:00  removed from list: private@example.test (folder kept: " + noStartPath + ")",
                "2024-03-01 23:02:00  removed from list: Current (folder kept: " + activePath + ")",
                "2024-03-01 23:03:00  removed from list: Another installation (folder kept: " + Path.Combine(root, "other", "p-55667788") + ")" });
            List<OfficeDesk> migrated = Open(root).OfficeDesks();
            Check(migrated.Count == 3 && migrated.Count(d => d.Archived) == 2, "legacy log migrates without archiving a live profile");
            Check(migrated.Single(d => !d.Archived).Started == new DateTime(2024, 2, 29),
                  "existing profile retains its own add date despite old removal message");
            OfficeDesk previous = migrated.Single(d => d.Name == "Past alias");
            Check(previous.Started == new DateTime(2024, 2, 28) && previous.Ended == new DateTime(2024, 3, 1) &&
                previous.UsageDays(DateTime.Today) == 3, "archived legacy desk uses evidence including leap day");
            OfficeDesk unknown = migrated.Single(d => d.Archived && d.Key != previous.Key);
            Check(!unknown.Started.HasValue && !unknown.UsageDays(DateTime.Today).HasValue && unknown.Ended.HasValue,
                  "unrecorded start date cannot invent a duration");
            Check(!unknown.Name.Contains("private@example.test"), "archived desk does not expose email in place of a nickname");
            Open(root).AddProfile("Newest");
            List<OfficeDesk> afterRestart = Open(root).OfficeDesks();
            Check(afterRestart.Count == 4 && afterRestart.Count(d => d.Archived) == 2 &&
                !Directory.Exists(archivePath) && !Directory.Exists(noStartPath), "subsequent persistence does not duplicate desks or recreate removed directories");
        }

        static void CheckStaleEngines(string root)
        {
            Engine first = Open(root);
            Profile leaving = first.AddProfile("Departed");
            Engine second = Open(root);
            second.Forget(second.Profiles.Single(p => p.Id == leaving.Id));
            first.AddProfile("Fresh");
            Engine after = Open(root);
            Check(after.Profiles.Count == 1 && after.Profiles.Single().Name == "Fresh",
                  "stale process cannot revive another process's forgotten profile");
            List<OfficeDesk> desks = after.OfficeDesks();
            Check(desks.Count == 2 && desks.Single(d => d.Name == "Departed").Archived && !desks.Single(d => d.Name == "Fresh").Archived,
                  "a later addition preserves remote lifecycle events");
        }

        static void CheckDefault(string root)
        {
            string current = Path.Combine(root, "profile"), claude = Path.Combine(root, "claude");
            Directory.CreateDirectory(current);
            Engine fresh = new Engine(current, false, claude, Path.Combine(root, "fresh-state"));
            OfficeDesk freshDesk = fresh.OfficeDesks().Single();
            Check(freshDesk.ProfileId == "default" && freshDesk.Started == DateTime.Today &&
                  freshDesk.UsageDays(DateTime.Today) == 1, "new default desk records its first managed day");

            string oldState = Path.Combine(root, "legacy-state");
            Directory.CreateDirectory(oldState);
            var store = new { profiles = new[] { new { id = "default", name = "Existing default", dir = current, realDefault = false } },
                identities = new Dictionary<string, object>(), seen = new Dictionary<string, object>(), lastActive = new Dictionary<string, object>() };
            File.WriteAllText(Path.Combine(oldState, "accounts.json"), new JavaScriptSerializer().Serialize(store));
            var legacy = new Engine(current, false, claude, oldState);
            OfficeDesk legacyDesk = legacy.OfficeDesks().Single();
            Check(legacyDesk.Name == "Existing default" && !legacyDesk.Started.HasValue && !legacyDesk.UsageDays(DateTime.Today).HasValue,
                  "previously managed default with no add evidence keeps an unknown date");
        }

        public static int Run()
        {
            passed = 0;
            string root = Path.Combine(Path.GetTempPath(), "cas-office-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                CheckLifecycle(Path.Combine(root, "new"));
                CheckMigration(Path.Combine(root, "legacy"));
                CheckStaleEngines(Path.Combine(root, "interleaved"));
                CheckDefault(Path.Combine(root, "default"));
                return passed;
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
