using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace ClaudeAccountSync
{
    static class UsageTests
    {
        static int passed;
        static void Check(bool ok, string label) { if (!ok) throw new Exception("Usage fixture failed: " + label); passed++; }
        static bool Rejects(Action action) { try { action(); return false; } catch (FormatException) { return true; } catch (ArgumentException) { return true; } }
        static void CheckConsumedMeterRendering()
        {
            // Exercise the real paint path with synthetic observations, never a live account.
            // Sampling the 80% point catches an accidental inversion to a remaining bar.
            Theme.Init("light"); Theme.Dpi = 96;
            DateTimeOffset time = DateTimeOffset.UtcNow;
            var sample = UsageParser.Parse("{\"five_hour\":{\"utilization\":87},\"seven_day\":{\"utilization\":84}}", "paint-fixture", "11111111-1111-4111-8111-111111111111", UsageSource.DesktopUi, time);
            using (var card = new Card(new Row { Profile = new Profile { Id = "paint-fixture" }, Name = "review@example.test", Sub = "隔离绘图账号", Primary = "切换", PrimaryEnabled = true }) { Width = 432 })
            {
                card.Go.Enabled = false; card.More.Enabled = false;
                card.UpdateUsage(sample, "隔离额度更新检查");
                var usage = card.Controls.OfType<UsagePanel>().Single();
                using (var updated = new Bitmap(usage.Width, usage.Height))
                {
                    usage.DrawToBitmap(updated, new Rectangle(Point.Empty, updated.Size));
                    Check(!card.Go.Enabled && !card.More.Enabled &&
                        usage.AccessibleDescription.Contains("已用 87% · 剩余 13%") && usage.AccessibleDescription.Contains("已用 84% · 剩余 16%") &&
                        updated.GetPixel(150, 30).ToArgb() == Theme.Green.ToArgb() && updated.GetPixel(362, 30).ToArgb() == Theme.Green.ToArgb(),
                        "quota-only card refresh paints87/84 without re-enabling handoff-disabled account actions");
                }
            }
            using (var panel = new UsagePanel { Width = 400 })
            using (var image = new Bitmap(400, panel.Height))
            {
                Action paint = () => { panel.UpdateUsage(sample, "隔离额度绘图检查"); panel.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); };
                paint();
                Check(image.GetPixel(150, 30).ToArgb() == Theme.Green.ToArgb() && image.GetPixel(362, 30).ToArgb() == Theme.Green.ToArgb(), "87/84 consumed bars remain filled beyond eighty percent");
                Check(image.GetPixel(180, 30).ToArgb() == Theme.Line.ToArgb() && image.GetPixel(392, 30).ToArgb() == Theme.Line.ToArgb(), "87/84 consumed bars leave the right remainder unfilled");
                Check(panel.AccessibleDescription.Contains("已用 87% · 剩余 13%") && panel.AccessibleDescription.Contains("已用 84% · 剩余 16%"), "accessible quota text identifies used and remaining separately");
                sample.FiveHour.UsedPercent = 100; sample.Weekly.UsedPercent = 0; paint();
                Check(image.GetPixel(180, 30).ToArgb() == Theme.Accent.ToArgb() && image.GetPixel(220, 30).ToArgb() == Theme.Line.ToArgb(), "used100 fills the track and used0 keeps it empty");
                sample.FiveHour.UsedPercent = 1; paint();
                Check(image.GetPixel(3, 30).ToArgb() == Theme.Green.ToArgb() && image.GetPixel(12, 30).ToArgb() == Theme.Line.ToArgb(), "a one percent sliver stays visible as a cap without overstating use");
                sample.Source = UsageSource.DesktopHistory; paint();
                Check(image.GetPixel(10, 30).ToArgb() == Theme.Line.ToArgb() && image.GetPixel(220, 30).ToArgb() == Theme.Line.ToArgb(), "history never paints a current consumed meter");
                sample.Source = UsageSource.DesktopUi; sample.ObservedAt = time.AddMinutes(-3); paint();
                Check(image.GetPixel(10, 30).ToArgb() == Theme.Line.ToArgb(), "stale observations never paint a current consumed meter");
                sample.ObservedAt = time; sample.FiveHour.ResetsAt = time.AddSeconds(-1); paint();
                Check(image.GetPixel(10, 30).ToArgb() == Theme.Line.ToArgb(), "passed reset never paints a current consumed meter");
                sample.FiveHour.ResetsAt = null; sample.FiveHour.UsedPercent = null; paint();
                Check(image.GetPixel(10, 30).ToArgb() == Theme.Line.ToArgb() && panel.AccessibleDescription.Contains("未知"), "unknown usage stays empty and explicitly unknown");
            }
            // The detail dialog is built hidden from a record it is handed; it never samples.
            var detail = UsageParser.Parse("{\"five_hour\":{\"utilization\":87},\"seven_day\":{\"utilization\":84}}", "paint-fixture", "11111111-1111-4111-8111-111111111111", UsageSource.DesktopUi, DateTimeOffset.UtcNow);
            using (Dialog dialog = Dialog.BuildUsage("额度详情 · 隔离账号", detail, "隔离额度详情检查", UsagePanel.Description(detail, "隔离额度详情检查"), "隔离说明"))
            {
                var meters = dialog.Controls.OfType<System.Windows.Forms.Panel>().SelectMany(frame => frame.Controls.OfType<UsagePanel>()).Single();
                Check(!dialog.Visible && meters.AccessibleDescription.Contains("已用 87% · 剩余 13%") &&
                    dialog.Controls.OfType<System.Windows.Forms.Label>().Any(label => label.Text.Contains("已用 84% · 剩余 16%")) &&
                    dialog.AcceptButton is Pill && dialog.Controls.Cast<System.Windows.Forms.Control>().All(c => dialog.ClientRectangle.Contains(c.Bounds)),
                    "quota detail dialog repeats the card's record inside its own bounds without showing a window");
            }
            using (var chip = new Pill("使用中", PillKind.Primary) { Enabled = false, Chip = true, Width = 104 })
            using (var plain = new Pill("切换", PillKind.Primary) { Enabled = false, Width = 104 })
            using (var chipImage = new Bitmap(chip.Width, chip.Height))
            using (var plainImage = new Bitmap(plain.Width, plain.Height))
            {
                chip.DrawToBitmap(chipImage, new Rectangle(Point.Empty, chipImage.Size));
                plain.DrawToBitmap(plainImage, new Rectangle(Point.Empty, plainImage.Size));
                Check(chipImage.GetPixel(8, chip.Height / 2).ToArgb() != Theme.Bg.ToArgb() && plainImage.GetPixel(8, plain.Height / 2).ToArgb() == Theme.Bg.ToArgb(),
                    "an in-use account shows a tinted status chip while a locked switch button stays plain");
            }
        }
        public static int Run(string temporaryRoot)
        {
            passed = 0;
            Directory.CreateDirectory(temporaryRoot);
            DateTimeOffset now = DateTimeOffset.Parse("2026-11-08T14:00:00Z", CultureInfo.InvariantCulture);
            const string account = "11111111-1111-4111-8111-111111111111", otherAccount = "22222222-2222-4222-8222-222222222222";
            Func<string, UsageSnapshot> parse = s => UsageParser.Parse(s, "default", account, UsageSource.ClaudeApi, now);
            var original = parse("{\"five_hour\":{\"utilization\":0,\"resets_at\":\"2026-11-09T01:00:00+09:00\"},\"seven_day\":{\"utilization\":42.5,\"resets_at\":\"2026-11-10T12:00:00Z\"},\"seven_day_opus\":{\"utilization\":99},\"access_token\":\"fixture-token-never-stored\"}");
            Check(original.FiveHour.UsedPercent == 0 && original.Weekly.UsedPercent == 42.5, "zero and fractional use remain actual values");
            Check(original.FiveHour.ResetsAt == now.AddHours(2), "ISO offset reset normalized to UTC");
            Check(original.Find("seven_day_opus").Label == "Opus 周额度" && original.Windows.Count == 3, "model limits retained but unrelated fields excluded");
            var missing = parse("{\"five_hour\":null,\"seven_day\":{\"resets_at\":null}}");
            Check(!missing.FiveHour.UsedPercent.HasValue && !missing.Weekly.UsedPercent.HasValue, "absent limits never mean zero usage");
            Check(UsageDisplay.MeterLine(missing, "five_hour", now).Contains("未知"), "unknown limit displayed as unknown");
            var statusline = UsageParser.Parse("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":65,\"resets_at\":1794150000},\"seven_day\":null},\"session_id\":\"private-fixture\"}", "default", account, UsageSource.CodeStatusline, now);
            Check(statusline.FiveHour.UsedPercent == 65 && statusline.FiveHour.ResetsAt.HasValue, "public statusline wrapper and epoch seconds read");
            Check(parse("{\"five_hour\":{\"utilization\":\"40\"}}").FiveHour.UsedPercent == null, "numeric strings rejected instead of guessed");
            Check(parse("{\"five_hour\":{\"utilization\":true}}").FiveHour.UsedPercent == null, "boolean not interpreted as percentage");
            Check(parse("{\"five_hour\":{\"utilization\":-1},\"seven_day\":{\"utilization\":101}}").Windows.All(w => !w.UsedPercent.HasValue), "invalid bounds remain unknown without clamping");
            Check(parse("{\"five_hour\":{\"utilization\":20,\"used_percentage\":30}}").FiveHour.UsedPercent == null, "conflicting schemas do not pick arbitrary usage");
            Check(parse("{\"five_hour\":{\"resets_at\":\"2026-11-09T12:00:00\"}}").FiveHour.ResetsAt == null, "timezone-free reset rejected");
            Check(parse("{\"five_hour\":{\"resets_at\":1794150000000}}").FiveHour.ResetsAt == null, "milliseconds not mistaken for epoch seconds");
            Check(Rejects(() => parse("{bad json}")) && Rejects(() => parse("{\"error\":\"unauthorized\"}")), "malformed and error responses rejected");
            Check(Rejects(() => parse("{\"rate_limits\":null}")) && Rejects(() => parse("[]")), "missing public usage object rejected");
            Check(Rejects(() => parse(new string('x', UsageParser.MaximumJsonLength + 1))), "oversized input rejected");
            Check(UsageDisplay.IsFresh(original, now.AddSeconds(120)) && !UsageDisplay.IsFresh(original, now.AddSeconds(121)), "freshness has bounded duration");
            Check(!UsageDisplay.IsFresh(original, now.AddMinutes(-1)), "clock rollback does not mark future sample fresh");
            Check(UsageDisplay.MeterLine(original, "five_hour", now.AddMinutes(3)).Contains("上次已用 0%"), "stale zero marked historical rather than currently full");
            Check(UsageDisplay.MeterLine(original, "five_hour", now.AddHours(2)).Contains("等待刷新") && !UsageDisplay.MeterLine(original, "five_hour", now.AddHours(2)).Contains("0%"), "passed reset never predicts renewed quota");
            Check(!UsageDisplay.IsCurrent(original, original.FiveHour, now.AddHours(2)) && !UsageDisplay.IsCurrent(missing, missing.FiveHour, now), "stale and unknown unsuitable for current-usage decisions");
            Check(UsageDisplay.MeterLine(statusline, "seven_day_sonnet", now).Contains("未提供"), "omitted model limit explicitly unavailable");
            Check(UsageDisplay.MeterLine(statusline, "seven_day", now).Contains("重置未知"), "absent reset does not get fabricated");

            var native = UsageParser.Parse("{\"five_hour\":{\"utilization\":100,\"reset_text\":\"周一00:30重置\"},\"seven_day\":{\"utilization\":22,\"reset_text\":\"星期日10:00重置\"}}", "default", account, UsageSource.DesktopUi, now);
            Check(native.FiveHour.ResetText == "周一00:30重置" && native.Weekly.ResetText == "星期日10:00重置" && !native.FiveHour.ResetsAt.HasValue && !native.Weekly.ResetsAt.HasValue, "native weekday phrases retained without inventing calendar dates");
            Check(UsageDisplay.MeterLine(native, "five_hour", now).Contains("周一00:30重置") && UsageDisplay.ResetDescription(native.Weekly, now) == "星期日10:00重置", "display uses actual visible reset phrase");
            Check(UsageDisplay.RemainingPercent(native, native.FiveHour, now) == 0 && UsageDisplay.RemainingPercent(native, native.Weekly, now) == 78 && UsageDisplay.IsLimitReached(native, native.FiveHour, now), "fresh native usage100 and22 imply remaining0 and78 only");
            Check(UsageDisplay.RemainingPercent(native, native.Weekly, now.AddMinutes(3)) == null && !UsageDisplay.IsLimitReached(native, native.FiveHour, now.AddMinutes(3)), "expired sample not asserted as current available or blocked");
            var consumed = UsageParser.Parse("{\"five_hour\":{\"utilization\":87},\"seven_day\":{\"utilization\":84}}", "default", account, UsageSource.DesktopUi, now);
            Check(UsageDisplay.CurrentUsedPercent(consumed, consumed.FiveHour, now) == 87 && UsageDisplay.CurrentUsedPercent(consumed, consumed.Weekly, now) == 84, "main consumed readings match Claude87 and84 rather than13 and16");
            Check(UsageDisplay.RemainingPercent(consumed, consumed.FiveHour, now) == 13 && UsageDisplay.RemainingPercent(consumed, consumed.Weekly, now) == 16, "remaining readings remain supplementary complements");
            Check(UsageDisplay.CurrentUsedPercent(consumed, consumed.FiveHour, now.AddMinutes(3)) == null, "stale consumed reading not represented as current");
            consumed.Weekly.Source = UsageSource.DesktopHistory;
            Check(UsageDisplay.CurrentUsedPercent(consumed, consumed.Weekly, now) == null && UsageDisplay.MeterLine(consumed, "seven_day", now).Contains("上次已用 84%"), "freshly read history still labelled as previous consumed reading");
            consumed.FiveHour.ResetsAt = now;
            Check(UsageDisplay.CurrentUsedPercent(consumed, consumed.FiveHour, now) == null, "reset-passed consumed reading unavailable until refreshed");
            var english = parse("{\"five_hour\":{\"utilization\":15,\"reset_text\":\"Resets Monday at 12:30 AM\"}}");
            Check(english.FiveHour.ResetText == "Resets Monday at 12:30 AM" && !english.FiveHour.ResetsAt.HasValue, "English reset phrase remains source text");
            Check(parse("{\"five_hour\":{\"reset_text\":\"Resets\\nprivate details\"}}").FiveHour.ResetText == null && parse("{\"five_hour\":{\"reset_text\":\"user@example.test reset\"}}").FiveHour.ResetText == null, "reset fallback rejects unrelated multiline or email content");
            string nativeCache = Path.Combine(temporaryRoot, "native"); UsageStore.Save(nativeCache, native);
            var nativeRead = UsageStore.Read(nativeCache, "default", account);
            Check(nativeRead.FiveHour.ResetText == native.FiveHour.ResetText && nativeRead.Weekly.ResetText == native.Weekly.ResetText, "raw reset phrases survive own cache without date conversion");
            native.Weekly.ObservedAt = now.AddMinutes(-10); native.Weekly.Source = UsageSource.DesktopHistory;
            Check(UsageDisplay.IsCurrent(native, native.FiveHour, now) && !UsageDisplay.IsCurrent(native, native.Weekly, now), "live five-hour update cannot refresh independently old weekly observation");
            Check(UsageDisplay.MeterLine(native, "seven_day", now).Contains("上次已用") && UsageDisplay.Freshness(native, now).Contains("待刷新"), "mixed observations identify stale weekly and overall refresh need");
            UsageStore.Save(nativeCache, native); nativeRead = UsageStore.Read(nativeCache, "default", account);
            Check(nativeRead.Weekly.ObservedAt == now.AddMinutes(-10) && nativeRead.Weekly.Source == UsageSource.DesktopHistory && UsageDisplay.WindowObservedAt(nativeRead, nativeRead.FiveHour) == now, "per-window time and provenance survive cache while omitted time falls back");
            native.Weekly.ObservedAt = now;
            Check(UsageDisplay.RemainingPercent(native, native.Weekly, now) == null, "even recent local history does not claim live remaining quota");
            Check(Rejects(() => parse("{\"five_hour\":{\"utilization\":10,\"observed_at\":\"2026-11-08T15:00:00Z\"}}")), "future window observation cannot silently refresh current evidence");

            UsageStore.Save(temporaryRoot, original);
            UsageSnapshot loaded = UsageStore.Read(temporaryRoot, "default", account);
            Check(loaded != null && loaded.FiveHour.UsedPercent == 0 && loaded.Weekly.ResetsAt == original.Weekly.ResetsAt && loaded.ObservedAt == now, "cache round trip keeps source observation and reset times");
            string cache = Directory.GetFiles(Path.Combine(temporaryRoot, "usage"), "*.json").Single();
            Check(!File.ReadAllText(cache).Contains("fixture-token") && !File.ReadAllText(cache).Contains("access_token"), "raw response secrets never enter cache");
            Check(UsageStore.Read(temporaryRoot, "default", otherAccount) == null && UsageStore.Read(temporaryRoot, "another-profile", account) == null, "cache bound to both current account and profile");
            var older = UsageParser.Parse("{\"five_hour\":{\"utilization\":90}}", "default", account, UsageSource.DesktopHistory, now.AddDays(-1));
            UsageStore.Save(temporaryRoot, older);
            Check(UsageStore.Read(temporaryRoot, "default", account).FiveHour.UsedPercent == 0, "older history cannot replace newer observation");
            string valid = File.ReadAllText(cache);
            File.WriteAllText(cache, valid.Replace(account, otherAccount));
            Check(UsageStore.Read(temporaryRoot, "default", account) == null, "tampered internal binding rejected even at correct path");
            File.WriteAllText(cache, "broken");
            Check(UsageStore.Read(temporaryRoot, "default", account) == null, "broken cache produces unavailable result");
            File.WriteAllText(cache, valid);
            var engine = new Engine(Path.Combine(temporaryRoot, "profile"), false, Path.Combine(temporaryRoot, "claude"), temporaryRoot);
            Profile profile = engine.Profiles[0]; Directory.CreateDirectory(profile.Dir);
            File.WriteAllText(Path.Combine(profile.Dir, "config.json"), "{\"windowSizeWasSignedIn\":true,\"lastKnownAccountUuid\":\"" + account + "\"}");
            Check(UsageStore.Read(engine, profile) != null, "engine read uses confirmed current sign-in identity");
            File.WriteAllText(Path.Combine(profile.Dir, "config.json"), "{\"windowSizeWasSignedIn\":true,\"lastKnownAccountUuid\":\"" + otherAccount + "\"}");
            Check(UsageStore.Read(engine, profile) == null, "re-login immediately hides prior account usage");
            bool rejected = false; try { UsageStore.Save(engine, profile, original); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(cache) == valid, "identity changed during collection cannot publish cached result");
            CheckConsumedMeterRendering();
            return passed;
        }
    }
}
