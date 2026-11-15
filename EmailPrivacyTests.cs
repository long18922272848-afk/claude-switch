using System;
using System.IO;

namespace ClaudeAccountSync
{
    static class EmailPrivacyTests
    {
        static int passed;
        static void Check(bool ok, string name)
        { if (!ok) throw new Exception("EmailPrivacy fixture failed: " + name); passed++; }

        public static int Run(string temporaryRoot)
        {
            passed = 0;
            string root = Path.Combine(temporaryRoot, "privacy-" + Guid.NewGuid().ToString("N"));
            var privacy = new EmailPrivacy(root);
            const string address = "First.Last+work@sample-domain.example.test";
            Check(privacy.Hidden && privacy.Text(address) == EmailPrivacy.Mask, "first run hides account addresses before any preference is stored");
            privacy.SetHidden(true);
            Check(privacy.Hidden && privacy.Text(address) == EmailPrivacy.Mask, "hiding removes complete address including domain");
            Check(privacy.Text("切换到 " + address + "（other@example.test）") == "切换到 " + EmailPrivacy.Mask + "（" + EmailPrivacy.Mask + "）", "multiple addresses in status, tray or nickname are hidden");
            Check(privacy.Text("昵称 账号 A · 24 个会话") == "昵称 账号 A · 24 个会话", "ordinary nicknames and session information remain visible");
            Check(privacy.Text("Cloud@work") == "Cloud@work", "nickname at-sign is not mistaken for an email address");
            Check(privacy.Text("用户@例子.invalid") == EmailPrivacy.Mask, "international addresses are hidden");
            Check(privacy.Text(null) == null && privacy.Text("") == "", "empty presentation fields are safe");
            Check(EmailPrivacy.ContainsEmail(address) && !EmailPrivacy.ContainsEmail("账号 12345678"), "rename can avoid pre-filling hidden addresses");
            Check(new EmailPrivacy(root).Hidden, "hidden preference survives restart");
            privacy.SetHidden(false);
            Check(!new EmailPrivacy(root).Hidden && privacy.Text(address) == address, "showing restores original value and persists");
            File.WriteAllText(Path.Combine(root, "hide-emails.txt"), "damaged");
            Check(new EmailPrivacy(root).Hidden, "damaged existing preference fails closed");

            // Raw background refresh data remains unchanged and can be presented again.
            var original = new Row { Profile = new Profile { Id = "fixture", Name = address }, Name = address,
                Sub = address + " · 24 个会话", Badge = "运行中", Primary = "切换", Running = true, PrimaryEnabled = true };
            privacy.SetHidden(true);
            Row display = original.ForDisplay(privacy);
            Check(display.Name == EmailPrivacy.Mask && !display.Sub.Contains(address) && original.Name == address && original.Sub.Contains(address), "rendering does not mutate raw refresh rows or profile identity");
            Check(display.Profile == original.Profile && display.Running && display.PrimaryEnabled, "presentation copy preserves account routing and switch behavior");
            privacy.SetHidden(false);
            Check(original.ForDisplay(privacy).Name == address, "same raw snapshot reveals correctly after toggle");
            privacy.SetHidden(true);
            Check(original.ForDisplay(privacy).Name == EmailPrivacy.Mask, "late background snapshot honors current privacy preference");

            string blocked = Path.Combine(root, "not-directory"); File.WriteAllText(blocked, "fixture");
            var failing = new EmailPrivacy(blocked); bool failed = false;
            try { failing.SetHidden(true); } catch (IOException) { failed = true; }
            Check(failed && failing.Hidden, "save failure still hides immediately without changing identity");
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "preference writes leave no temporary files");
            return passed;
        }
    }
}
