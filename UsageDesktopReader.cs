using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace ClaudeAccountSync
{
    // Passive public accessibility reads only. No focus, navigation, invocation, input, auth or IPC.
    static class UsageDesktopReader
    {
        internal sealed class Node
        {
            public AutomationElement Element;
            public Node Parent;
            public ControlType Type;
            public string Kind, Name, Url;
            public bool Heading, Meter;
            public double? Percent;
            public int Depth;
        }
        sealed class Window
        {
            public IntPtr Handle;
            public int Pid;
        }
        static readonly Regex NativeButton = new Regex(@"\A用量[：:]\s*5\s*小时限额的\s*(\d{1,3}(?:\.\d+)?)%\s*(.{1,70}重置)\z", RegexOptions.CultureInvariant);
        static readonly Regex WeeklyNativeButton = new Regex(@"\A用量[：:]\s*每周\s*·\s*所有模型[：:]\s*(\d{1,3}(?:\.\d+)?)%\s*(.{1,70}重置)\z", RegexOptions.CultureInvariant);
        static readonly string[] SessionLabels = { "当前会话", "5 小时限额", "Current session", "5-hour limit" };
        static readonly string[] WeeklyLabels = { "本周", "每周 · 所有模型", "This week", "Weekly · all models" };
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);

        public static UsageHistoryResult Read(Engine engine, Profile profile)
        {
            try
            {
                string account = engine.ActiveAccount(profile);
                if (account == null) return Missing("账号未登录，无法读取用量页面");
                Window window = FindWindow(profile);
                if (window == null) return Missing("打开此账号的 Claude 用量页，可读取额度及重置说明");
                List<Node> nodes = Capture(window.Handle);
                var documents = nodes.Where(n => n.Type == ControlType.Document && (SettingsUrl(n.Url) || CodeUrl(n.Url))).ToList();
                if (documents.Count != 1) return Missing("未找到可核验的 Claude 用量页面");
                Node document = documents[0];
                Node section = null;
                if (SettingsUrl(document.Url))
                {
                    if (IsEmbeddedSettings(document.Url)) section = FindNamedSection(document, nodes, "设置", "Settings");
                    else section = document;
                }
                else
                {
                    section = FindNamedSection(document, nodes, "用量", "Usage");
                }
                if (section != null) PopulateSection(section, nodes);
                UsageHistoryResult result = Parse(nodes, profile.Id, account, DateTimeOffset.UtcNow, section);
                // The HWND, owning main process, profile and account must all still match after UIA.
                Window after = FindWindow(profile);
                if (after == null || after.Handle != window.Handle || after.Pid != window.Pid ||
                    !string.Equals(account, engine.ActiveAccount(profile), StringComparison.OrdinalIgnoreCase))
                    return Missing("Claude 窗口或账号正在变更，未更新额度");
                return result;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException) &&
                    !(ex is InvalidOperationException) && !(ex is ElementNotAvailableException) &&
                    !(ex is COMException) && !(ex is System.ComponentModel.Win32Exception) &&
                    !(ex is FormatException)) throw;
                return Missing("Claude 用量页面暂时无法读取；已有记录保留");
            }
        }

        static Window FindWindow(Profile profile)
        {
            var windows = new List<Window>();
            foreach (Instance instance in App.Instances().Where(i => App.Belongs(i, profile)))
                using (Process process = Process.GetProcessById(instance.Pid))
                {
                    process.Refresh(); IntPtr hwnd = process.MainWindowHandle; uint owner;
                    if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(hwnd) &&
                        GetWindowThreadProcessId(hwnd, out owner) != 0 && owner == (uint)instance.Pid)
                        windows.Add(new Window { Handle = hwnd, Pid = instance.Pid });
                }
            return windows.Count == 1 ? windows[0] : null;
        }

        static string Value(AutomationElement element)
        {
            object value = element.GetCurrentPropertyValue(ValuePattern.ValueProperty, true);
            if (value is string) return (string)value;
            return null;
        }

        static List<Node> Capture(IntPtr handle)
        {
            var nodes = new List<Node>();
            var pending = new Stack<Node>();
            pending.Push(new Node { Element = AutomationElement.FromHandle(handle) });
            TreeWalker walker = TreeWalker.ControlViewWalker;
            var watch = Stopwatch.StartNew();
            while (pending.Count != 0)
            {
                if (nodes.Count + pending.Count > 4000 || watch.ElapsedMilliseconds > 2500)
                    throw new IOException("Accessibility view too large");
                Node node = pending.Pop();
                if (node.Depth > 50) throw new IOException("Accessibility view too deep");
                var info = node.Element.Current;
                if (info.IsOffscreen) continue;
                node.Type = info.ControlType; node.Kind = info.LocalizedControlType;
                object heading = node.Element.GetCurrentPropertyValue(AutomationElement.HeadingLevelProperty, true);
                if (heading is int || (heading != null && heading.GetType().IsEnum))
                { int level = Convert.ToInt32(heading); node.Heading = level >= 80051 && level <= 80059; }
                node.Heading |= node.Kind == "标题" || string.Equals(node.Kind, "heading", StringComparison.OrdinalIgnoreCase);
                // Do not fetch chat paragraphs, messages, group text or drafts during discovery.
                if (node.Type == ControlType.Button || node.Type == ControlType.Edit || node.Type == ControlType.Hyperlink || node.Heading)
                    node.Name = info.Name;
                if (node.Type == ControlType.Document || node.Type == ControlType.Hyperlink) node.Url = Value(node.Element);
                nodes.Add(node);
                var children = new List<Node>();
                for (AutomationElement child = walker.GetFirstChild(node.Element); child != null; child = walker.GetNextSibling(child))
                {
                    if (nodes.Count + pending.Count + children.Count > 4000 || watch.ElapsedMilliseconds > 2500)
                        throw new IOException("Accessibility view too large");
                    children.Add(new Node { Element = child, Parent = node, Depth = node.Depth + 1 });
                }
                for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
            }
            return nodes;
        }

        static Node FindNamedSection(Node document, List<Node> nodes, params string[] labels)
        {
            var condition = new OrCondition(labels.Select(s => (Condition)new PropertyCondition(AutomationElement.NameProperty, s)).ToArray());
            var matches = document.Element.FindAll(TreeScope.Descendants, condition);
            var candidates = new List<Node>();
            foreach (AutomationElement element in matches)
            {
                Node node = nodes.FirstOrDefault(n => Automation.Compare(n.Element, element));
                if (node == null || node.Type == ControlType.Text || node.Type == ControlType.Button || node.Heading) continue;
                node.Name = node.Element.Current.Name;
                // A popup must actually contain its heading and meter descendants; named text is insufficient.
                if (nodes.Any(n => n != node && Below(n, node) && n.Heading &&
                    (n.Name == "用量" || n.Name == "Usage" || n.Name == "您的用量" || n.Name == "Your usage"))) candidates.Add(node);
            }
            // Nested aliases of the same section are permitted only if there is a unique outer container.
            candidates = candidates.Where(n => !candidates.Any(p => p != n && Below(n, p))).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        static void PopulateSection(Node section, List<Node> nodes)
        {
            foreach (Node node in nodes.Where(n => Below(n, section)))
            {
                if (node.Type == ControlType.Text || node.Type == ControlType.ProgressBar ||
                    node.Kind == "计量条" || node.Kind == "meter" || node.Kind == "进度条")
                    node.Name = node.Element.Current.Name;
                bool recognized = IsLabel(node.Name, SessionLabels) || IsLabel(node.Name, WeeklyLabels);
                if (!recognized || node.Type == ControlType.Text || node.Heading) continue;
                object pattern;
                if (node.Element.TryGetCurrentPattern(RangeValuePattern.Pattern, out pattern))
                {
                    var range = ((RangeValuePattern)pattern).Current;
                    if (range.Minimum == 0 && range.Maximum == 100 && ValidPercent(range.Value))
                    { node.Meter = true; node.Percent = range.Value; }
                }
                if (!node.Meter && (node.Type == ControlType.ProgressBar || node.Kind == "计量条" || node.Kind == "meter"))
                {
                    double percent;
                    if (double.TryParse((Value(node.Element) ?? "").Trim().TrimEnd('%'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out percent) && ValidPercent(percent))
                    { node.Meter = true; node.Percent = percent; }
                }
            }
        }

        internal static UsageHistoryResult Parse(List<Node> nodes, string profileId, string account, DateTimeOffset observedAt, Node section)
        {
            var documents = nodes.Where(n => n.Type == ControlType.Document && (SettingsUrl(n.Url) || CodeUrl(n.Url))).ToList();
            if (documents.Count != 1) return Missing("页面身份未核验，未读取额度");
            Node document = documents[0];
            bool settings = SettingsUrl(document.Url);
            Node main = nodes.FirstOrDefault(n => Below(n, document) && (n.Kind == "主要" || n.Kind == "main"));
            var buttons = nodes.Where(n => n.Type == ControlType.Button && main != null && Below(n, main) &&
                (NativeButton.IsMatch(n.Name ?? "") || WeeklyNativeButton.IsMatch(n.Name ?? ""))).ToList();
            bool codeControls = CodeUrl(document.Url) && main != null &&
                nodes.Any(n => Below(n, main) && n.Type == ControlType.Edit && (n.Name == "提示词" || n.Name == "Prompt")) && buttons.Count == 1;
            if (!settings && !codeControls) return Missing("未找到 Claude 原生额度控件");
            if (section != null && Below(section, document))
            {
                bool heading = nodes.Any(n => Below(n, section) && n.Heading &&
                    (settings ? n.Name == "您的用量" || n.Name == "Your usage" : n.Name == "用量" || n.Name == "Usage"));
                bool link = nodes.Any(n => Below(n, section) && n.Type == ControlType.Hyperlink && DirectSettingsUrl(n.Url));
                bool embeddedDialog = !IsEmbeddedSettings(document.Url) || section.Name == "设置" || section.Name == "Settings";
                if (heading && embeddedDialog && (settings || link))
                {
                    List<Node> data = nodes.Where(n => Below(n, section)).ToList();
                    UsageWindow five = ReadWindow(data, SessionLabels, "five_hour", "5 小时");
                    UsageWindow week = ReadWindow(data, WeeklyLabels, "seven_day", "周额度");
                    if (five != null && week != null)
                    {
                        return new UsageHistoryResult { Snapshot = new UsageSnapshot { ProfileId = profileId, AccountId = account,
                            Source = UsageSource.DesktopUi, ObservedAt = observedAt, Windows = new List<UsageWindow> { five, week } },
                            Message = "读取自此账号当前显示的 Claude 用量页；重置说明按页面原文显示" };
                    }
                }
            }
            // Claude may display either limit in its native footer. Its explicit label decides
            // which window is observed; the other limit must never be inferred from this value.
            if (!settings && codeControls)
            {
                Match match = NativeButton.Match(buttons[0].Name);
                bool weekly = !match.Success;
                if (weekly) match = WeeklyNativeButton.Match(buttons[0].Name);
                double used;
                if (double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out used) && ValidPercent(used))
                    return new UsageHistoryResult { Snapshot = new UsageSnapshot { ProfileId = profileId, AccountId = account,
                        Source = UsageSource.DesktopUi, ObservedAt = observedAt, Windows = new List<UsageWindow> {
                            new UsageWindow { Key = weekly ? "seven_day" : "five_hour", Label = weekly ? "周额度" : "5 小时", UsedPercent = used, ResetText = match.Groups[2].Value.Trim() } } },
                        Message = weekly ? "读取自 Claude Code 周额度按钮；打开用量弹窗可读取 5 小时额度" :
                            "读取自 Claude Code 用量按钮；打开用量弹窗可读取周额度" };
            }
            return Missing("当前页面未提供可完整核验的额度控件");
        }

        static UsageWindow ReadWindow(List<Node> nodes, string[] labels, string key, string label)
        {
            var meters = nodes.Where(n => n.Meter && n.Percent.HasValue && IsLabel(n.Name, labels)).ToList();
            if (meters.Count != 1 || !ValidPercent(meters[0].Percent.Value)) return null;
            Node meter = meters[0]; int end = nodes.IndexOf(meter), start = end - 1;
            while (start >= 0 && !nodes[start].Meter) start--;
            // An unused session has no reset clock yet. Accept only the exact observed
            // explanation, and only alongside the five-hour meter's explicit zero value.
            var resets = nodes.Skip(start + 1).Take(end - start - 1).Where(n => n.Type == ControlType.Text &&
                (ResetPhrase(n.Name) || (key == "five_hour" && meter.Percent.Value == 0 &&
                    n.Name == "从您发送第一条消息时开始计算"))).Select(n => n.Name.Trim()).Distinct().ToList();
            if (resets.Count != 1) return null;
            return new UsageWindow { Key = key, Label = label, UsedPercent = meter.Percent, ResetText = resets[0] };
        }

        static bool ResetPhrase(string text)
        {
            return text != null && text.Length <= 100 && !text.Any(char.IsControl) && Regex.IsMatch(text, @"\d") &&
                (text.Trim().EndsWith("重置", StringComparison.Ordinal) || text.Trim().StartsWith("Resets ", StringComparison.OrdinalIgnoreCase));
        }
        static bool IsLabel(string name, IEnumerable<string> labels)
        { return name != null && labels.Any(s => string.Equals(s, name.Trim(), StringComparison.OrdinalIgnoreCase)); }
        static bool ValidPercent(double n) { return !double.IsNaN(n) && !double.IsInfinity(n) && n >= 0 && n <= 100; }
        static bool Below(Node node, Node ancestor)
        { for (Node p = node; p != null; p = p.Parent) if (object.ReferenceEquals(p, ancestor)) return true; return false; }
        internal static bool DirectSettingsUrl(string url)
        { Uri u; return ClaudeUrl(url, out u) && u.AbsolutePath.TrimEnd('/') == "/settings/usage" && string.IsNullOrEmpty(u.Fragment); }
        internal static bool IsEmbeddedSettings(string url)
        { Uri u; return ClaudeUrl(url, out u) && (u.AbsolutePath.TrimEnd('/') == "/epitaxy" || u.AbsolutePath.StartsWith("/epitaxy/", StringComparison.Ordinal)) && u.Fragment == "#settings/usage"; }
        internal static bool SettingsUrl(string url) { return DirectSettingsUrl(url) || IsEmbeddedSettings(url); }
        internal static bool CodeUrl(string url)
        { Uri u; return ClaudeUrl(url, out u) && (u.AbsolutePath.TrimEnd('/') == "/epitaxy" || u.AbsolutePath.StartsWith("/epitaxy/", StringComparison.Ordinal)) && string.IsNullOrEmpty(u.Fragment); }
        static bool ClaudeUrl(string url, out Uri uri)
        { return Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https" && string.Equals(uri.Host, "claude.ai", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo); }
        static UsageHistoryResult Missing(string message) { return new UsageHistoryResult { Message = message }; }
    }
}
