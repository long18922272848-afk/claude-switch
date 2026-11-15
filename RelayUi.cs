using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace ClaudeAccountSync
{
    // Uses only the same accessible controls exposed to a person using Claude. In particular,
    // there is no private IPC, clipboard, global keyboard input or coordinate-click fallback.
    sealed class RelayUi : IRelayDesktop
    {
        readonly Engine engine;
        readonly Func<bool> cancelled;
        readonly Dictionary<string, string> accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Binding> opened = new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
        static readonly string[] StopNames = { "Stop", "Stop response", "停止", "停止响应" };
        static readonly string[] SendNames = { "Send", "Send message", "发送", "发送消息" };

        sealed class Binding
        {
            public IntPtr Window;
            public int Pid;
            public string LocalId;
        }

        sealed class Node
        {
            public AutomationElement Element;
            public Node Parent;
            public ControlType Type;
            public string Kind, Name, Url;
            public bool Enabled, Heading, Navigation;
        }

        sealed class Selection
        {
            public Node Composer, Main, Document;
            public List<Node> Nodes;
        }

        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        public RelayUi(Engine engine) : this(engine, null) { }

        public RelayUi(Engine engine, Func<bool> cancelled)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            this.engine = engine;
            this.cancelled = cancelled;
            // Pin both accounts before the first action. task.AccountId names the SOURCE account;
            // it must never be used as the expected identity of the target profile.
            foreach (Profile profile in engine.Profiles)
            {
                string account = engine.ActiveAccount(profile);
                if (!string.IsNullOrEmpty(account)) accounts[profile.Id] = account;
            }
        }

        void Check(Profile profile)
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException("已取消自动接力。");
            string expected;
            if (profile == null || string.IsNullOrEmpty(profile.Id) || !accounts.TryGetValue(profile.Id, out expected) ||
                !string.Equals(engine.ActiveAccount(profile), expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("账号登录状态已改变，已停止自动接力；请重新核验两个账号。");
        }

        static void CheckTask(RelayTask task)
        {
            if (task == null || task.LocalId == null || !Regex.IsMatch(task.LocalId, @"\Alocal_[A-Za-z0-9-]{1,64}\z"))
                throw new IOException("任务标识无效，未操作 Claude 窗口。");
        }

        Binding FindWindow(Profile profile)
        {
            var matches = new List<Binding>();
            foreach (Instance instance in App.Instances().Where(i => App.Belongs(i, profile)))
            {
                try
                {
                    using (Process process = Process.GetProcessById(instance.Pid))
                    {
                        process.Refresh();
                        IntPtr hwnd = process.MainWindowHandle;
                        uint owner;
                        if (hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out owner) != 0 && owner == (uint)instance.Pid)
                            matches.Add(new Binding { Window = hwnd, Pid = instance.Pid });
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (matches.Count > 1) throw new IOException("同一账号存在多个 Claude 主窗口，无法唯一确认任务窗口。");
            return matches.Count == 1 ? matches[0] : null;
        }

        void VerifyOwner(Profile profile, Binding binding)
        {
            Check(profile);
            uint owner;
            if (binding == null || GetWindowThreadProcessId(binding.Window, out owner) == 0 || owner != (uint)binding.Pid)
                throw new IOException("Claude 窗口已关闭或改变，已停止自动接力。");
        }

        void Focus(Profile profile, Binding binding)
        {
            VerifyOwner(profile, binding);
            ShowWindowAsync(binding.Window, IsIconic(binding.Window) ? 9 : 5);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                VerifyOwner(profile, binding);
                SetForegroundWindow(binding.Window);
                if (GetForegroundWindow() == binding.Window) return;
                Thread.Sleep(80);
            }
            throw new IOException("Windows 未允许聚焦所选账号窗口，自动接力已停下；请打开该窗口后重试。");
        }

        Binding EnsureWindow(Profile profile, string exe)
        {
            Check(profile);
            Binding binding = FindWindow(profile);
            if (binding == null)
            {
                App.Launch(profile, exe);
                App.WaitForProfile(profile);
                var wait = Stopwatch.StartNew();
                while (binding == null && wait.ElapsedMilliseconds < 10000)
                {
                    Check(profile);
                    binding = FindWindow(profile);
                    if (binding == null) Thread.Sleep(200);
                }
            }
            if (binding == null) throw new IOException("未找到所选账号的 Claude 主窗口。");
            Focus(profile, binding);
            return binding;
        }

        public void Open(Profile profile, RelayTask task)
        {
            CheckTask(task);
            Check(profile);
            string exe = App.FindExe(App.Instances());
            Binding binding = EnsureWindow(profile, exe);
            binding.LocalId = task.LocalId;
            App.ForwardCallback(profile, exe, "claude://code/continue?session=" + Uri.EscapeDataString(task.LocalId) + "&source=desktop_action", "claude");
            WaitForSelection(profile, binding, task, 10000);
            opened[profile.Id] = binding;
        }

        Binding RequireOpened(Profile profile, RelayTask task)
        {
            CheckTask(task);
            Check(profile);
            Binding binding;
            if (!opened.TryGetValue(profile.Id, out binding) || !string.Equals(binding.LocalId, task.LocalId, StringComparison.Ordinal))
                throw new IOException("尚未核验指定任务窗口，请重新打开任务再接力。");
            Binding actual = FindWindow(profile);
            if (actual == null || actual.Window != binding.Window || actual.Pid != binding.Pid)
                throw new IOException("任务窗口已经改变，已停止自动接力。");
            Focus(profile, binding);
            return binding;
        }

        public void Interrupt(Profile profile, RelayTask task)
        {
            Binding binding = RequireOpened(profile, task);
            Selection selected = WaitForSelection(profile, binding, task, 2500);
            if (ComposerValue(selected.Composer).Length != 0)
                throw new IOException("任务输入框已有未发送内容，已保留内容并停止接力。");
            List<Node> stops = Buttons(selected, StopNames);
            if (stops.Count == 0)
            {
                // Absence is not evidence of a successful stop. The coordinator must independently
                // establish runtime quiescence before saving or starting the target task.
                return;
            }
            if (stops.Count != 1) throw new IOException("找到多个停止按钮，无法确认原任务，未执行停止。");
            VerifyOwner(profile, binding);
            Invoke(stops[0], "停止");
        }

        public void Start(Profile profile, string cwd, string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) throw new ArgumentException("接力提示词不能为空。", "prompt");
            if (prompt.Length > 14336) throw new IOException("接力指令过长，未打开目标任务。");
            if (string.IsNullOrEmpty(cwd) || !Path.IsPathRooted(cwd) || !Directory.Exists(cwd))
                throw new IOException("任务工作目录不存在，未打开目标任务。");
            string link = "claude://code/new?q=" + Uri.EscapeDataString(prompt) + "&folder=" +
                Uri.EscapeDataString(Path.GetFullPath(cwd)) + "&source=desktop_action";
            if (!App.ValidCallback(link, "claude")) throw new IOException("接力链接过长或无效，未打开目标任务。");
            Check(profile);
            string exe = App.FindExe(App.Instances());
            Binding binding = EnsureWindow(profile, exe);
            VerifyOwner(profile, binding);
            Selection before = ComposerSelection(Capture(binding), false);
            if (before != null && ComposerValue(before.Composer).Length != 0)
                throw new IOException("目标账号输入框已有未发送内容，已保留内容，未打开接力任务。");
            // The normal new-task route receives the continuation and workspace. Dispatch exactly
            // once: opening/prefilling a route is NOT proof it started. The coordinator must observe
            // its unique marker in a target-account transcript before reporting success or retrying.
            App.ForwardCallback(profile, exe, link, "claude");
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 6000)
            {
                VerifyOwner(profile, binding);
                Node send = null;
                try
                {
                    Selection selected = ComposerSelection(Capture(binding), false);
                    if (selected != null && IsNewTaskUrl(selected.Document.Url) &&
                        string.Equals(ComposerValue(selected.Composer), prompt, StringComparison.Ordinal) &&
                        Buttons(selected, StopNames).Count == 0)
                    {
                        List<Node> sends = Buttons(selected, SendNames);
                        if (sends.Count == 1)
                        {
                            // Some releases prefill, while others may submit the deep-link prompt.
                            // Send only our exact, still-present draft, once. Empty or changed text
                            // is never submitted; the coordinator independently verifies acceptance.
                            VerifyOwner(profile, binding);
                            if (!string.Equals(ComposerValue(selected.Composer), prompt, StringComparison.Ordinal)) return;
                            send = sends[0];
                        }
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (IOException) { }
                if (send != null)
                {
                    // Deliberately outside the polling catches: an ambiguous Invoke failure must
                    // never turn into a second click in this loop.
                    VerifyOwner(profile, binding);
                    Invoke(send, "发送");
                    return;
                }
                Thread.Sleep(200);
            }
            // No UI acknowledgement is a pending outcome, not a successful start. Never re-dispatch.
        }

        Selection WaitForSelection(Profile profile, Binding binding, RelayTask task, int timeoutMs)
        {
            var wait = Stopwatch.StartNew();
            string last = "尚未显示指定任务";
            var sameTitle = string.IsNullOrWhiteSpace(task.Title) ? new List<RelayTask>() :
                RelayState.Discover(engine, profile).Where(t => string.Equals(t.Title, task.Title, StringComparison.Ordinal)).ToList();
            bool uniqueTitle = sameTitle.Count == 1 && string.Equals(sameTitle[0].LocalId, task.LocalId, StringComparison.Ordinal);
            do
            {
                VerifyOwner(profile, binding);
                try
                {
                    Selection selected = Select(Capture(binding), task, uniqueTitle);
                    VerifyOwner(profile, binding);
                    return selected;
                }
                catch (ElementNotAvailableException) { last = "Claude 正在更新任务页面"; }
                catch (InvalidOperationException) { last = "Claude 暂未提供可用的辅助功能控件"; }
                catch (IOException e) { last = e.Message; }
                if (wait.ElapsedMilliseconds < timeoutMs) Thread.Sleep(200);
            } while (wait.ElapsedMilliseconds < timeoutMs);
            throw new IOException("无法安全操作指定任务：" + last + "。自动接力已停下，未使用键盘或鼠标猜测操作。");
        }

        static string Property(AutomationElement element, AutomationProperty property)
        {
            object value = element.GetCurrentPropertyValue(property, true);
            return value == null || value == AutomationElement.NotSupported ? "" : value as string ?? "";
        }

        static bool IsHeading(Node node)
        {
            return node.Heading || node.Kind == "标题" || string.Equals(node.Kind, "heading", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsMain(Node node)
        {
            return node.Kind == "主要" || string.Equals(node.Kind, "main", StringComparison.OrdinalIgnoreCase);
        }

        static bool Below(Node node, Node ancestor)
        {
            for (Node p = node; p != null; p = p.Parent) if (object.ReferenceEquals(p, ancestor)) return true;
            return false;
        }

        static bool InNavigation(Node node)
        {
            for (Node p = node; p != null; p = p.Parent)
                if (p.Type == ControlType.Button || p.Type == ControlType.Menu || p.Type == ControlType.MenuItem ||
                    p.Type == ControlType.Tree || p.Navigation) return true;
            return false;
        }

        static List<Node> Capture(Binding binding)
        {
            var root = AutomationElement.FromHandle(binding.Window);
            if (root == null || !root.Current.IsEnabled) throw new IOException("Claude 主窗口不可用或存在对话框");
            // Exact provider-side name filtering identifies navigation containers without fetching
            // names/text from arbitrary chat message groups. Framework UIA does not expose ARIA role.
            var navigationNames = new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "Sidebar"),
                new PropertyCondition(AutomationElement.NameProperty, "侧边栏"),
                new PropertyCondition(AutomationElement.NameProperty, "导航"),
                new PropertyCondition(AutomationElement.NameProperty, "Navigation"));
            AutomationElementCollection navigation = root.FindAll(TreeScope.Descendants, navigationNames);
            var nodes = new List<Node>();
            var queue = new Queue<Node>();
            queue.Enqueue(new Node { Element = root });
            var watch = Stopwatch.StartNew();
            TreeWalker walker = TreeWalker.ControlViewWalker;
            while (queue.Count > 0)
            {
                if (nodes.Count >= 1800 || watch.ElapsedMilliseconds > 2200)
                    throw new IOException("任务辅助功能树过大或响应过慢，无法完整核验");
                Node node = queue.Dequeue();
                AutomationElement.AutomationElementInformation current = node.Element.Current;
                if (current.IsOffscreen) continue;
                node.Type = current.ControlType;
                node.Enabled = current.IsEnabled;
                node.Kind = current.LocalizedControlType;
                object heading = node.Element.GetCurrentPropertyValue(AutomationElement.HeadingLevelProperty, true);
                // HeadingLevel is an internal enum in Framework UIA, but the public property's
                // documented values are HeadingLevel_None=80050 and Level1..Level9=80051..80059.
                if (heading is int || (heading != null && heading.GetType().IsEnum))
                {
                    int level = Convert.ToInt32(heading);
                    node.Heading = level >= 80051 && level <= 80059;
                }
                node.Navigation = node.Kind == "导航" || node.Kind == "补充" ||
                    string.Equals(node.Kind, "navigation", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(node.Kind, "complementary", StringComparison.OrdinalIgnoreCase);
                foreach (AutomationElement nav in navigation)
                    if (Automation.Compare(node.Element, nav)) { node.Navigation = true; break; }
                // Do not retrieve text from transcript paragraphs or messages.
                if (node.Type == ControlType.Button || node.Type == ControlType.Edit || IsHeading(node)) node.Name = current.Name;
                if (node.Type == ControlType.Document)
                {
                    node.Url = Property(node.Element, ValuePattern.ValueProperty);
                }
                nodes.Add(node);
                for (AutomationElement child = walker.GetFirstChild(node.Element); child != null; child = walker.GetNextSibling(child))
                {
                    if (queue.Count + nodes.Count >= 1800 || watch.ElapsedMilliseconds > 2200)
                        throw new IOException("任务辅助功能树过大或响应过慢，无法完整核验");
                    queue.Enqueue(new Node { Element = child, Parent = node });
                }
            }
            return nodes;
        }

        internal static bool UrlIdentifiesSession(string url, string localId)
        {
            return !string.IsNullOrEmpty(localId) && string.Equals(UrlSession(url), localId, StringComparison.Ordinal);
        }

        static string UrlSession(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) ||
                parsed.Scheme != Uri.UriSchemeHttps || !string.Equals(parsed.Host, "claude.ai", StringComparison.OrdinalIgnoreCase)) return null;
            string[] segments = parsed.AbsolutePath.Trim('/').Split('/');
            return segments.Length == 2 && segments[0] == "epitaxy" ? Uri.UnescapeDataString(segments[1]) : null;
        }

        static bool IsNewTaskUrl(string url)
        {
            Uri parsed;
            return Uri.TryCreate(url, UriKind.Absolute, out parsed) && parsed.Scheme == Uri.UriSchemeHttps &&
                string.Equals(parsed.Host, "claude.ai", StringComparison.OrdinalIgnoreCase) && parsed.AbsolutePath.TrimEnd('/') == "/epitaxy";
        }

        internal static bool CanUseTitleFallback(string url)
        {
            // A title on a Chat or other page cannot identify a local Code session, even when it
            // happens to match. Missing provider URL or the known Code shell are the only fallbacks.
            return string.IsNullOrWhiteSpace(url) || IsNewTaskUrl(url);
        }

        static Selection Select(List<Node> nodes, RelayTask task, bool uniqueTitle)
        {
            Selection selected = ComposerSelection(nodes, true);
            Node document = selected.Document;
            bool urlMatches = UrlIdentifiesSession(document.Url, task.LocalId);
            if (!urlMatches)
            {
                if (!CanUseTitleFallback(document.Url)) throw new IOException("页面地址不是所选 Code 任务，未操作当前窗口");
                if (!uniqueTitle) throw new IOException("任务标题为空或与其他任务重复，无法依据标题确认任务");
                // Claude's accessible page header can be a sibling of the main landmark. Accept it
                // only as one exact heading in this document, never a sidebar button/title.
                var headings = nodes.Where(n => IsHeading(n) && n.Type != ControlType.Button && Below(n, document) &&
                    !InNavigation(n) && !string.IsNullOrEmpty(task.Title) && string.Equals(n.Name, task.Title, StringComparison.Ordinal)).ToList();
                if (headings.Count != 1) throw new IOException("页面地址或会话标题未能唯一确认所选任务");
            }
            return selected;
        }

        static Selection ComposerSelection(List<Node> nodes, bool required)
        {
            var composers = nodes.Where(n => n.Type == ControlType.Edit && n.Enabled &&
                (n.Name == "提示词" || string.Equals(n.Name, "Prompt", StringComparison.OrdinalIgnoreCase)) && !InNavigation(n)).ToList();
            if (!required && composers.Count == 0) return null;
            if (composers.Count != 1) throw new IOException("未找到唯一的任务提示词输入框");
            Node composer = composers[0], main = null, document = null;
            for (Node p = composer.Parent; p != null; p = p.Parent)
            {
                if (main == null && IsMain(p)) main = p;
                if (document == null && p.Type == ControlType.Document) document = p;
            }
            if (document == null || main == null) throw new IOException("无法确认任务输入框位于会话主窗格");
            return new Selection { Composer = composer, Main = main, Document = document, Nodes = nodes };
        }

        static List<Node> Buttons(Selection selected, string[] names)
        {
            return selected.Nodes.Where(n => n.Type == ControlType.Button && n.Enabled && Below(n, selected.Main) &&
                names.Any(name => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        static ValuePattern EditableValue(Node composer)
        {
            object pattern;
            if (!composer.Element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) || ((ValuePattern)pattern).Current.IsReadOnly)
                throw new IOException("当前 Claude 未提供可写的提示词辅助功能控件；请手动接力。");
            return (ValuePattern)pattern;
        }

        static string ComposerValue(Node composer) { return EditableValue(composer).Current.Value ?? ""; }

        static void Invoke(Node button, string action)
        {
            object pattern;
            if (!button.Element.Current.IsEnabled || button.Element.Current.IsOffscreen ||
                !button.Element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                throw new IOException("Claude 的" + action + "按钮未提供可用的辅助功能操作，未执行。");
            ((InvokePattern)pattern).Invoke();
        }
    }
}
