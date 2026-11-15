using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeAccountSync
{
    // Presentation preference only: never modifies accounts, cached identities or credentials.
    sealed class EmailPrivacy
    {
        public const string Mask = "邮箱已隐藏";
        static readonly Regex Email = new Regex(@"[\p{L}\p{N}\p{M}.!#$%&'*+/=?^_`{|}~\-]+@[\p{L}\p{N}\p{M}\-]+(?:\.[\p{L}\p{N}\p{M}\-]+)+", RegexOptions.CultureInvariant);
        readonly string settingFile;
        volatile bool hidden;
        public bool Hidden { get { return hidden; } }

        public EmailPrivacy(string stateDirectory)
        {
            settingFile = Path.Combine(stateDirectory, "hide-emails.txt");
            try
            {
                // First run and unreadable preferences keep addresses hidden. Reveal only by opt-in.
                hidden = !File.Exists(settingFile) || File.ReadAllText(settingFile).Trim() != "off";
            }
            catch { hidden = true; }
        }

        public static bool ContainsEmail(string text) { return !string.IsNullOrEmpty(text) && Email.IsMatch(text); }
        public string Text(string text) { return hidden && text != null ? Email.Replace(text, Mask) : text; }

        public void SetHidden(bool value)
        {
            // Hide immediately even if saving fails; the UI reports that persistence failed.
            hidden = value;
            Directory.CreateDirectory(Path.GetDirectoryName(settingFile));
            string temporary = settingFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, value ? "on" : "off", new UTF8Encoding(false));
                if (File.Exists(settingFile)) File.Replace(temporary, settingFile, null);
                else File.Move(temporary, settingFile);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
