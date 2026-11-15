using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;

namespace ClaudeAccountSync
{
    static class UsageDesktopReaderTests
    {
        static int passed;
        static readonly DateTimeOffset At = new DateTimeOffset(2026, 11, 8, 23, 57, 0, TimeSpan.FromHours(9));
        sealed class Fixture
        {
            public List<UsageDesktopReader.Node> Nodes = new List<UsageDesktopReader.Node>();
            public UsageDesktopReader.Node Doc, Main, Button, Section, Five, Week;
            public Fixture(bool settings)
            {
                Doc = Add(null, ControlType.Document, null);
                Doc.Url = "https://claude.ai/epitaxy?coldLaunch=1" + (settings ? "#settings/usage" : "");
                Main = Add(Doc, ControlType.Group, null); Main.Kind = "主要";
                Add(Main, ControlType.Edit, "提示词");
                Button = Add(Main, ControlType.Button, "用量：5 小时限额的 100%周一00:30 重置");
                Section = Add(Main, ControlType.Pane, settings ? "设置" : "用量");
                var heading = Add(Section, ControlType.Text, settings ? "您的用量" : "用量"); heading.Heading = true;
                if (!settings) Add(Section, ControlType.Hyperlink, "查看全部：设置中的用量").Url = "https://claude.ai/settings/usage";
                Add(Section, ControlType.Text, settings ? "当前会话" : "5 小时限额");
                Add(Section, ControlType.Text, "周一00:30 重置");
                Five = Add(Section, ControlType.ProgressBar, settings ? "当前会话" : "5 小时限额"); Five.Meter = true; Five.Percent = 100;
                Add(Section, ControlType.Text, "已使用100%");
                Add(Section, ControlType.Text, settings ? "本周" : "每周 · 所有模型");
                Add(Section, ControlType.Text, "星期日10:00 重置");
                Week = Add(Section, ControlType.ProgressBar, settings ? "本周" : "每周 · 所有模型"); Week.Meter = true; Week.Percent = 22;
                Add(Section, ControlType.Text, "已使用22%");
                Add(Section, ControlType.Text, "Claude Code：100%");
            }
            public UsageDesktopReader.Node Add(UsageDesktopReader.Node parent, ControlType type, string name)
            {
                var node = new UsageDesktopReader.Node { Parent = parent, Type = type, Name = name };
                Nodes.Add(node); return node;
            }
            public UsageHistoryResult Read() { return UsageDesktopReader.Parse(Nodes, "test-profile", "test-account", At, Section); }
        }
        static void Check(bool condition, string label)
        { if (!condition) throw new Exception("Usage desktop fixture failed: " + label); passed++; }
        public static int Run(string temporaryRoot)
        {
            passed = 0;
            var settings = new Fixture(true);
            UsageSnapshot value = settings.Read().Snapshot;
            Check(value != null && value.FiveHour.UsedPercent == 100 && value.Weekly.UsedPercent == 22, "actual settings dialog named meters");
            Check(value.Source == UsageSource.DesktopUi && value.ProfileId == "test-profile" && value.AccountId == "test-account" && value.ObservedAt == At, "verified observation identity retained");
            Check(value.FiveHour.ResetText == "周一00:30 重置" && value.Weekly.ResetText == "星期日10:00 重置" && !value.Weekly.ResetsAt.HasValue, "exact reset phrases without guessed date");
            Check(value.Weekly.UsedPercent != 100, "product breakdown percentage is not weekly quota");
            var popup = new Fixture(false);
            value = popup.Read().Snapshot;
            Check(value != null && value.FiveHour.UsedPercent == 100 && value.Weekly.UsedPercent == 22, "native Code popup recognized");
            value = UsageDesktopReader.Parse(popup.Nodes, "p", "a", At, null).Snapshot;
            Check(value != null && value.FiveHour.UsedPercent == 100 && value.Weekly == null, "native footer alone never invents weekly quota");
            var current = new Fixture(false);
            current.Button.Name = "用量：5 小时限额的 87%18:40 重置";
            value = UsageDesktopReader.Parse(current.Nodes, "p", "a", At, null).Snapshot;
            Check(value != null && value.FiveHour.UsedPercent == 87 && value.FiveHour.ResetText == "18:40 重置" && value.Weekly == null,
                "observed compact footer preserves 87 percent used and time-only reset");
            current.Five.Percent = 87; current.Week.Percent = 84;
            current.Nodes.First(n => n.Name == "周一00:30 重置").Name = "18:40 重置";
            current.Nodes.First(n => n.Name == "星期日10:00 重置").Name = "周五22:00 重置";
            current.Nodes.First(n => n.Name == "已使用100%").Name = "已使用87%";
            current.Nodes.First(n => n.Name == "已使用22%").Name = "已使用84%";
            value = current.Read().Snapshot;
            Check(value != null && value.FiveHour.UsedPercent == 87 && value.Weekly.UsedPercent == 84 &&
                value.FiveHour.ResetText == "18:40 重置" && value.Weekly.ResetText == "周五22:00 重置",
                "observed popup keeps distinct 87 and 84 used values with their own resets");
            current.Five.Percent = 88;
            current.Nodes.First(n => n.Name == "已使用87%").Name = "已使用88%";
            UsageSnapshot updated = current.Read().Snapshot;
            Check(updated.FiveHour.UsedPercent == 88 && updated.Weekly.UsedPercent == 84 && value.FiveHour.UsedPercent == 87,
                "next observation reads current meters without mutating or reusing the previous snapshot");
            var weeklyFooter = new Fixture(false);
            weeklyFooter.Button.Name = "用量：每周 · 所有模型：43%周日10:00 重置";
            value = UsageDesktopReader.Parse(weeklyFooter.Nodes, "p", "a", At, null).Snapshot;
            Check(value != null && value.FiveHour == null && value.Weekly.UsedPercent == 43 && value.Weekly.ResetText == "周日10:00 重置",
                "observed weekly footer supplies only weekly all-models usage");
            weeklyFooter.Week.Percent = 43;
            weeklyFooter.Nodes.First(n => n.Name == "已使用22%").Name = "已使用43%";
            value = weeklyFooter.Read().Snapshot;
            Check(value != null && value.FiveHour.UsedPercent == 100 && value.Weekly.UsedPercent == 43,
                "weekly native footer also verifies the complete Code usage popup");
            weeklyFooter.Button.Name = "用量：每周 · Sonnet：43%周日10:00 重置";
            Check(weeklyFooter.Read().Snapshot == null, "model-specific weekly footer cannot stand for all-models usage");
            weeklyFooter.Button.Name = "用量：每周 · 所有模型：43%周日10:00 重置";
            weeklyFooter.Add(weeklyFooter.Main, ControlType.Button, "用量：5 小时限额的 87%18:40 重置");
            Check(weeklyFooter.Read().Snapshot == null, "two competing native footer controls remain ambiguous");
            var notStarted = new Fixture(false);
            notStarted.Button.Name = "用量：每周 · 所有模型：43%周日10:00 重置";
            notStarted.Five.Percent = 0; notStarted.Week.Percent = 43;
            var startNote = notStarted.Nodes.First(n => n.Name == "周一00:30 重置");
            startNote.Name = "从您发送第一条消息时开始计算";
            notStarted.Nodes.First(n => n.Name == "已使用100%").Name = "已使用0%";
            notStarted.Nodes.First(n => n.Name == "已使用22%").Name = "已使用43%";
            notStarted.Nodes.First(n => n.Name == "星期日10:00 重置").Name = "周日10:00 重置";
            value = notStarted.Read().Snapshot;
            Check(value != null && value.FiveHour.UsedPercent == 0 && value.Weekly.UsedPercent == 43,
                "unused five-hour session and weekly quota both read from native popup");
            Check(value.FiveHour.ResetText == "从您发送第一条消息时开始计算" && !value.FiveHour.ResetsAt.HasValue &&
                UsageDisplay.ResetDescription(value.FiveHour, At) == "从您发送第一条消息时开始计算",
                "not-started explanation retained without inventing a reset date");
            UsageSnapshot restored = UsageParser.Parse("{\"five_hour\":{\"utilization\":0,\"reset_text\":\"从您发送第一条消息时开始计算\"}}", "p", "a", UsageSource.DesktopUi, At);
            Check(restored.FiveHour.ResetText == value.FiveHour.ResetText && !restored.FiveHour.ResetsAt.HasValue,
                "not-started explanation survives the cache parser");
            notStarted.Five.Percent = 1;
            value = notStarted.Read().Snapshot;
            Check(value != null && value.FiveHour == null && value.Weekly.UsedPercent == 43,
                "nonzero session cannot use not-started explanation; verified weekly footer remains available");
            notStarted.Five.Percent = 0;
            startNote.Name = "其他未经核验的开始说明";
            Check(notStarted.Read().Snapshot.FiveHour == null && UsageParser.ResetPhrase(startNote.Name) == null,
                "arbitrary start explanation cannot replace a reset");
            popup.Doc.Url = "https://claude.ai/chat/example";
            Check(popup.Read().Snapshot == null, "chat content cannot imitate native quota UI");
            popup.Doc.Url = "https://other.example/epitaxy";
            Check(popup.Read().Snapshot == null, "other domain refused");
            popup.Doc.Url = "https://claude.ai.evil.example/epitaxy";
            Check(popup.Read().Snapshot == null, "lookalike origin refused");
            popup.Doc.Url = "https://claude.ai/epitaxy";
            popup.Button.Type = ControlType.Text;
            Check(popup.Read().Snapshot == null, "plain text footer imitation refused");
            popup.Button.Type = ControlType.Button;
            popup.Button.Parent = popup.Doc;
            Check(popup.Read().Snapshot == null, "usage control outside main refused");
            popup.Button.Parent = popup.Main;
            popup.Nodes.RemoveAll(n => n.Type == ControlType.Edit);
            Check(popup.Read().Snapshot == null, "Code composer identity required");

            settings.Section.Name = "other dialog";
            Check(settings.Read().Snapshot == null, "embedded settings requires settings dialog");
            settings.Section.Name = "设置";
            settings.Week.Name = "Unrelated meter";
            Check(settings.Read().Snapshot == null, "unrelated meter cannot supply weekly quota");
            settings.Week.Name = "本周";
            var clone = settings.Add(settings.Section, ControlType.ProgressBar, "本周"); clone.Meter = true; clone.Percent = 99;
            Check(settings.Read().Snapshot == null, "duplicate named meters refused");
            settings.Nodes.Remove(clone);
            settings.Week.Percent = 101;
            Check(settings.Read().Snapshot == null, "invalid range refused");
            settings.Week.Percent = 22;
            settings.Nodes.RemoveAll(n => n.Name == "星期日10:00 重置");
            Check(settings.Read().Snapshot == null, "missing weekly reset does not reuse session reset");
            var direct = new Fixture(true); direct.Doc.Url = "https://claude.ai/settings/usage";
            Check(direct.Read().Snapshot != null, "exact standalone settings URL accepted");
            Check(!UsageDesktopReader.SettingsUrl("https://claude.ai/chat/example#settings/usage") && !UsageDesktopReader.SettingsUrl("https://claude.ai/epitaxy#settings/usage-extra"), "fragment settings match is exact and Code scoped");
            Check(!UsageDesktopReader.CodeUrl("http://claude.ai/epitaxy") && !UsageDesktopReader.CodeUrl("https://claude.ai:444/epitaxy"), "unsafe origin or port refused");
            return passed;
        }
    }
}
