using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    // Isolated rendering harness. No Show, Application.Run, DoEvents, clicks, sync, or live state.
    sealed class NativeReviewPanel : Panel
    {
        public NativeReviewPanel(Engine engine) : base(engine, false) { }
        protected override void OnLoad(EventArgs e) { throw new InvalidOperationException("Review must never invoke Form.Load."); }
        protected override void SetVisibleCore(bool value)
        {
            if (value) throw new InvalidOperationException("Review must never show a window.");
            base.SetVisibleCore(false);
        }
    }

    static class NativeReview
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly List<string> report = new List<string>();
        static int failures;
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        static T Field<T>(object value, string name)
        { return (T)typeof(Panel).GetField(name, Private).GetValue(value); }
        static T Member<T>(object value, string name)
        {
            var field = value.GetType().GetField(name, Private | BindingFlags.Public);
            if (field != null) return (T)field.GetValue(value);
            return (T)value.GetType().GetProperty(name, Private | BindingFlags.Public).GetValue(value, null);
        }
        static void Invoke(Panel panel, string name, params object[] arguments)
        { typeof(Panel).GetMethod(name, Private).Invoke(panel, arguments); }
        static void Check(bool condition, string text)
        { report.Add((condition ? "PASS " : "FAIL ") + text); if (!condition) failures++; }

        static Engine Fixture(string root, int count)
        {
            string state = Path.Combine(root, "state"), claude = Path.Combine(root, "claude");
            Directory.CreateDirectory(state); Directory.CreateDirectory(claude);
            File.WriteAllText(Path.Combine(state, "hide-emails.txt"), "on");
            var engine = new Engine(null, false, claude, state);
            for (int i = 0; i < count; i++)
            {
                string id = "review-" + i, account = Guid.NewGuid().ToString(), name = i == 0 ? "账号 A" : i == 1 ? "账号 B" : "示例工位 " + (i + 1);
                string dir = Path.Combine(root, id);
                Directory.CreateDirectory(Path.Combine(dir, "claude-code-sessions", account));
                File.WriteAllText(Path.Combine(dir, "config.json"), "{\"windowSizeWasSignedIn\":true,\"lastKnownAccountUuid\":\"" + account + "\"}");
                engine.Profiles.Add(new Profile { Id = id, Name = name, Dir = dir, IsRealDefault = false });
                engine.Identities[account] = new Identity { Nick = name, Email = "review" + i + "@example.test" };
            }
            return engine;
        }

        static List<Row> Rows(Engine engine)
        {
            var rows = new List<Row>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < engine.Profiles.Count; i++)
            {
                Profile p = engine.Profiles[i];
                var usage = new UsageSnapshot { ProfileId = p.Id, AccountId = engine.ActiveAccount(p), Source = UsageSource.DesktopUi, ObservedAt = now };
                usage.Windows.Add(new UsageWindow { Key = "five_hour", UsedPercent = i == 1 ? 100 : 61, ResetText = i == 1 ? "00:30 重置" : "02:20 重置" });
                usage.Windows.Add(new UsageWindow { Key = "seven_day", UsedPercent = i == 1 ? 22 : 49, ResetText = i == 1 ? "星期日 10:00 重置" : "星期五 22:00 重置" });
                // Accounts beyond the first two cover the non-current states: history, stale, never read.
                if (i == 2) usage.Source = UsageSource.DesktopHistory;
                if (i == 3) usage.ObservedAt = now.AddMinutes(-12);
                bool unread = i == 4;
                if (unread) usage = null;
                rows.Add(new Row { Profile = p, Name = p.Name, Sub = unread ? "未确认登录 · 请在这个独立窗口登录要添加的账号" : "review" + i + "@example.test · 12 个会话 · 最近 11-10 12:06",
                    Badge = i == 0 ? "运行中" : unread ? "未登录" : null,
                    Primary = i == 0 ? "使用中" : unread ? "打开并登录" : "切换", PrimaryEnabled = i != 0, Running = i == 0, Usage = usage,
                    UsageMessage = unread ? "尚未读取额度" : "隔离界面检查示例数据", UsageSignature = UsagePanel.Description(usage, "隔离界面检查") });
            }
            return rows;
        }

        // Dates and counts are synthetic. This harness never refreshes the service or scans real logs.
        static TokenStatisticsSnapshot TokenFixture(bool empty)
        {
            DateTime today = DateTime.Today;
            var value = new TokenStatisticsSnapshot { UpdatedAt = DateTimeOffset.Now, ProfileCount = 3,
                    SourceFileCount = empty ? 0 : 21, MostUsedModel = empty ? null : "claude-opus-4-6", LongestSession = empty ? TimeSpan.Zero : TimeSpan.FromHours(2) };
            if (empty) return value;
            for (int offset = 0; offset <= 430; offset++)
            {
                if (offset % 9 != 0 && offset > 20) continue;
                long factor = 1 + offset % 7;
                var day = new TokenDay { Date = today.AddDays(-offset), Input = factor * 1250, Output = factor * 24000,
                    CacheRead = factor * 2400000, CacheWrite = factor * 51000, SessionCount = (int)factor };
                value.Days.Add(day);
                value.Totals.Input += day.Input; value.Totals.Output += day.Output;
                value.Totals.CacheRead += day.CacheRead; value.Totals.CacheWrite += day.CacheWrite;
            }
            value.Days = value.Days.OrderBy(d => d.Date).ToList();
            value.ActiveDays = value.Days.Count; value.SessionCount = 24; value.ResponseCount = 512;
            value.FirstDay = value.Days.First().Date; value.LastDay = today;
            value.BusiestDay = value.Days.OrderByDescending(d => d.Total).First().Date;
            return value;
        }

        static void RenderForm(Form form, string path)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.MinimumSize = Size.Empty; form.MaximumSize = Size.Empty;
            LayoutRecursively(form);
            using (var bitmap = RenderHiddenTree(form, new HashSet<Control>())) bitmap.Save(path, ImageFormat.Png);
            Check(!form.Visible, Path.GetFileName(path) + " never shown");
        }

        // The quota detail dialog, built from the same fixture rows and never shown.
        static void RenderUsageDialog(string output, string fixtureRoot, string theme)
        {
            Theme.Init(theme); Theme.Dpi = 96;
            Engine engine = Fixture(Path.Combine(fixtureRoot, "usage-dialog-" + theme), 2);
            Row row = Rows(engine)[1];
            const string help = "打开此账号的 Claude → 设置 → 用量，或 Code 右下角的用量详情，小克会自动读取已用、剩余和重置说明。" +
                "\r\n普通 Code 页面读取底部按钮显示的五小时或每周额度；展开用量详情可读取两项。收起后，未显示的一项保留原读取时间。" +
                "\r\n读取时间表示观察页面的时间，不是服务端更新时间；未打开的账号保留历史记录。不会暂停或启动任务。";
            using (Dialog dialog = Dialog.BuildUsage("额度详情 · " + row.Name, row.Usage, row.UsageMessage, UsagePanel.Description(row.Usage, row.UsageMessage), help))
            {
                Size wanted = dialog.ClientSize;
                RenderForm(dialog, Path.Combine(output, "native-usage-dialog-" + theme + ".png"));
                Check(dialog.ClientSize == wanted, "usage dialog " + theme + " keeps its computed size");
                foreach (Control c in dialog.Controls)
                    Check(dialog.ClientRectangle.Contains(c.Bounds), "usage dialog " + theme + " inside client: " + c.GetType().Name);
                var ordered = dialog.Controls.Cast<Control>().OrderBy(c => c.Top).ToList();
                for (int i = 1; i < ordered.Count; i++)
                    Check(ordered[i].Top >= ordered[i - 1].Bottom, "usage dialog " + theme + " stacks without overlap: " + ordered[i].GetType().Name);
            }
            Console.WriteLine("native-usage-dialog-" + theme + " rendered");
        }

        static List<Control> Descendants(Control root)
        {
            var result = new List<Control> { root };
            foreach (Control child in root.Controls) result.AddRange(Descendants(child));
            return result;
        }

        // Simulate point fonts as well as Theme.S geometry without changing Windows settings.
        static void ScaleReviewFonts(Control root, float ratio)
        {
            if (Math.Abs(ratio - 1) < .001f) return;
            var controls = Descendants(root);
            var controlFonts = controls.Select(c => new KeyValuePair<Control, Font>(c, c.Font)).ToArray();
            foreach (var pair in controlFonts)
                pair.Key.Font = new Font(pair.Value.FontFamily, pair.Value.SizeInPoints * ratio, pair.Value.Style);
            foreach (Control control in controls)
                for (Type type = control.GetType(); type != null && type.Namespace == "ClaudeAccountSync"; type = type.BaseType)
                    foreach (FieldInfo field in type.GetFields(Private | BindingFlags.DeclaredOnly))
                        if (field.FieldType == typeof(Font))
                        {
                            Font font = (Font)field.GetValue(control);
                            if (font != null) field.SetValue(control, new Font(font.FontFamily, font.SizeInPoints * ratio, font.Style));
                        }
        }

        static void StopTimersAndTray(Panel panel)
        {
            foreach (FieldInfo field in typeof(Panel).GetFields(Private))
            {
                var timer = field.GetValue(panel) as System.Windows.Forms.Timer;
                if (timer != null) timer.Stop();
            }
            Field<NotifyIcon>(panel, "tray").Visible = false;
        }

        static void LayoutRecursively(Control control)
        {
            control.CreateControl(); control.PerformLayout();
            foreach (Control child in control.Controls) LayoutRecursively(child);
        }

        static Bitmap RenderHiddenTree(Control control, HashSet<Control> hidden)
        {
            var bitmap = new Bitmap(Math.Max(1, control.ClientSize.Width), Math.Max(1, control.ClientSize.Height));
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            // WM_PRINT omits children of a never-shown form. Render each native control's
            // own DrawToBitmap result and composite in z-order, clipped by its parent.
            using (Graphics g = Graphics.FromImage(bitmap))
                foreach (Control child in control.Controls.Cast<Control>().Reverse())
                {
                    if (hidden.Contains(child) || child.Width <= 0 || child.Height <= 0) continue;
                    if (!control.ClientRectangle.IntersectsWith(child.Bounds)) continue;
                    using (Bitmap rendered = RenderHiddenTree(child, hidden)) g.DrawImageUnscaled(rendered, child.Location);
                }
            return bitmap;
        }

        static void CheckLayout(Panel panel, string name, bool many)
        {
            var host = Field<OfficeHost>(panel, "host");
            var add = Field<AddCard>(panel, "addCard");
            var cards = host.Controls.OfType<Card>().ToList();
            int content = host.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
            report.Add(name + " client=" + panel.ClientSize + " host=" + host.Bounds + " content=" + content + " display=" + host.DisplayRectangle + " vscroll=" + host.VerticalScroll.Visible);
            Check(many ? content > host.ClientSize.Height : content <= host.ClientSize.Height, name + (many ? " multiple accounts require host scrolling" : " two accounts and Add fit host without scrolling"));
            Check(host.AutoScroll, name + " account host keeps AutoScroll enabled");
            Check(!host.HorizontalScroll.Visible, name + " no horizontal account scrollbar");
            if (!many) Check(add.Bottom <= host.ClientSize.Height, name + " Add remains inside account viewport");

            var top = panel.Controls.Cast<Control>().Where(c => !ReferenceEquals(c, Field<Label>(panel, "eyebrow")) &&
                !ReferenceEquals(c, Field<TokenStatisticsView>(panel, "tokenView"))).ToList();
            foreach (Control c in top)
                Check(panel.ClientRectangle.Contains(c.Bounds), name + " inside client: " + c.GetType().Name + " " + c.Text);
            for (int i = 0; i < top.Count; i++)
                for (int j = i + 1; j < top.Count; j++)
                    Check(!top[i].Bounds.IntersectsWith(top[j].Bounds), name + " separated: " + top[i].GetType().Name + " [" + top[i].Text + "] " + top[i].Bounds + "/" + top[j].GetType().Name + " [" + top[j].Text + "] " + top[j].Bounds);
            var sidebar = Field<System.Windows.Forms.Panel>(panel, "sidebar");
            var sideControls = sidebar.Controls.Cast<Control>().ToList();
            foreach (Control child in sideControls) Check(sidebar.ClientRectangle.Contains(child.Bounds), name + " sidebar bounds: " + child.Text);
            for (int i = 0; i < sideControls.Count; i++)
                for (int j = i + 1; j < sideControls.Count; j++)
                    Check(!sideControls[i].Bounds.IntersectsWith(sideControls[j].Bounds), name + " sidebar controls separate");

            foreach (Card card in cards)
            {
                report.Add(name + " card " + card.Row.Name + " " + card.Bounds);
                Check(card.Right <= host.ClientSize.Width, name + " card fits host width");
                Check(card.ClientRectangle.Contains(card.Go.Bounds) && card.ClientRectangle.Contains(card.More.Bounds), name + " card actions inside bounds");
                Check(!card.Go.Bounds.IntersectsWith(card.More.Bounds), name + " card actions separate");
                var usage = card.Controls.OfType<UsagePanel>().Single();
                Check(card.ClientRectangle.Contains(usage.Bounds), name + " usage inside card");
                Check(usage.Top >= card.Go.Bottom && usage.Top >= card.More.Bottom, name + " usage separated from actions");
                using (Graphics g = usage.CreateGraphics())
                {
                    var labelFont = (Font)typeof(UsagePanel).GetField("labelFont", Private).GetValue(usage);
                    var amountFont = (Font)typeof(UsagePanel).GetField("amountFont", Private).GetValue(usage);
                    int column = (usage.Width - Theme.S(24)) / 2;
                    if (card.Row.Usage == null) continue;
                    foreach (var window in card.Row.Usage.Windows)
                    {
                        // Mirrors UsagePanel.DrawWindow: "5 小时 · 剩余 39%" on the left, "已用" + "61%" on the right.
                        double remaining = 100 - window.UsedPercent.Value;
                        string label = (window.Key == "five_hour" ? "5 小时" : "每周") + " · 剩余 " + remaining.ToString("0.#") + "%";
                        string amount = window.UsedPercent.Value.ToString("0.#") + "%";
                        int labelWidth = TextRenderer.MeasureText(g, label, labelFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                        int amountWidth = TextRenderer.MeasureText(g, amount, amountFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(2);
                        int captionWidth = TextRenderer.MeasureText(g, "已用", labelFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(5);
                        Check(labelWidth + captionWidth + amountWidth + Theme.S(10) <= column, name + " quota label/value fit: " + label + " / 已用 " + amount);
                    }
                    string source = UsageDisplay.Freshness(card.Row.Usage, DateTimeOffset.UtcNow);
                    int sourceWidth = TextRenderer.MeasureText(g, source, labelFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(13);
                    Check(sourceWidth <= usage.Width && Theme.S(54) >= Theme.S(36) + Theme.S(18), name + " source timestamp fits separate line");
                    Check(labelFont.GetHeight(g) <= Theme.S(17) && amountFont.GetHeight(g) <= Theme.S(26), name + " quota/source fonts fit vertical rectangles");
                }
            }
        }

        static void Render(string output, string fixtureRoot, string theme, int width, int dpi, int accounts, int height = 640)
        {
            Theme.Init(theme); float nativeDpi = Theme.Dpi; Theme.Dpi = dpi;
            report.Add("Native desktop DPI=" + nativeDpi + "; target review DPI=" + dpi);
            // A taller client shows every account at once, for reviewing the non-current quota states.
            bool scrolls = accounts > 2 && height == 640;
            string name = "native-" + theme + "-" + width + "x" + height + "-" + dpi + "dpi" + (scrolls ? "-many" : accounts > 2 ? "-states" : "");
            Engine engine = Fixture(Path.Combine(fixtureRoot, name), accounts);
            Panel.Scheme = "claude-native-review-" + Guid.NewGuid().ToString("N");
            using (var panel = new NativeReviewPanel(engine))
            {
                StopTimersAndTray(panel);
                panel.FormBorderStyle = FormBorderStyle.None; // Export exact client content, no synthetic caption.
                panel.MinimumSize = Size.Empty;
                panel.ClientSize = new Size(Theme.S(width), Theme.S(height));
                var rows = Rows(engine); Invoke(panel, "Apply", rows);
                PetScene pet = Field<PetScene>(panel, "pet"); pet.HomeId = engine.Profiles[0].Id; pet.State = PetState.Working; pet.Frame = 24;
                Invoke(panel, "SetJourney", "小克正在 账号 A 办公", "自动接力已开：保存进度、记忆与 Skills → 确认暂停 → 继续。", PetState.Working);
                Invoke(panel, "SetStatus", "隔离界面检查 · 示例账号与额度 · 未操作真实任务");
                ScaleReviewFonts(panel, dpi / nativeDpi);
                Invoke(panel, "Relayout"); LayoutRecursively(panel); Invoke(panel, "FitCards"); LayoutRecursively(panel); Invoke(panel, "RepaintPet");
                using (var bitmap = RenderHiddenTree(panel, new HashSet<Control> { Field<Label>(panel, "eyebrow"), Field<TokenStatisticsView>(panel, "tokenView") }))
                {
                    bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
                }
                Check(!panel.Visible, name + " never shown");
                CheckLayout(panel, name, scrolls);
                StopTimersAndTray(panel);
            }
            Console.WriteLine(name + " rendered");
        }

        static void RenderTokens(string output, string fixtureRoot, string theme, int width, int dpi, bool empty, int height = 640)
        {
            Theme.Init(theme); float nativeDpi = Theme.Dpi; Theme.Dpi = dpi;
            string name = "native-tokens-" + theme + "-" + width + "x" + height + "-" + dpi + "dpi" + (empty ? "-empty" : "");
            Engine engine = Fixture(Path.Combine(fixtureRoot, name), 3);
            Panel.Scheme = "claude-native-token-review-" + Guid.NewGuid().ToString("N");
            using (var panel = new NativeReviewPanel(engine))
            {
                StopTimersAndTray(panel);
                panel.FormBorderStyle = FormBorderStyle.None;
                panel.MinimumSize = Size.Empty;
                panel.ClientSize = new Size(Theme.S(width), Theme.S(height));
                Invoke(panel, "Apply", Rows(engine));
                var view = Field<TokenStatisticsView>(panel, "tokenView");
                view.SetSnapshot(TokenFixture(empty));
                Invoke(panel, "ShowTokenStatistics", true);
                Invoke(panel, "SetStatus", "隔离界面检查 · 三个虚构账号合并 · 未读取真实会话");
                ScaleReviewFonts(panel, dpi / nativeDpi);
                Invoke(panel, "Relayout"); LayoutRecursively(panel);
                var hidden = new HashSet<Control> { Field<Label>(panel, "eyebrow"), Field<OfficeHost>(panel, "host"),
                    Field<CompactJourney>(panel, "journey"), Field<Label>(panel, "sectionHead"), Field<Label>(panel, "sectionHint") };
                using (var bitmap = RenderHiddenTree(panel, hidden)) bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
                if (!empty && width == 1000)
                    RenderTokenHover(panel, view, hidden, Path.Combine(output, name + "-hover.png"));
                Check(!panel.Visible, name + " never shown");
                Check(Field<NavButton>(panel, "tokenNav").Selected && !Field<NavButton>(panel, "accountsNav").Selected, name + " statistics navigation selected");
                var top = panel.Controls.Cast<Control>().Where(c => !hidden.Contains(c)).ToList();
                foreach (Control c in top) Check(panel.ClientRectangle.Contains(c.Bounds), name + " inside client: " + c.GetType().Name);
                for (int i = 0; i < top.Count; i++)
                    for (int j = i + 1; j < top.Count; j++)
                        Check(!top[i].Bounds.IntersectsWith(top[j].Bounds), name + " separate top controls: " + top[i].GetType().Name + "/" + top[j].GetType().Name);
                foreach (Control c in view.Controls) Check(view.ClientRectangle.Contains(c.Bounds), name + " view child inside viewport: " + c.GetType().Name);
                CheckTokenContents(view, name, empty);
                if (!empty && width == 1000 && dpi == 96)
                {
                    var range = Member<ComboBox>(view, "range");
                    range.SelectedIndex = range.Items.Count - 1; LayoutRecursively(view);
                    using (var bitmap = RenderHiddenTree(panel, hidden)) bitmap.Save(Path.Combine(output, name + "-all-time.png"), ImageFormat.Png);
                    var body = Member<System.Windows.Forms.Panel>(view, "body");
                    var canvas = Member<Control>(view, "canvas");
                    // Hidden WinForms controls reset scroll position during layout. Export the whole
                    // content separately and verify its configured scrolling extent instead of
                    // pretending this is a live mouse-wheel test.
                    using (var bitmap = RenderHiddenTree(canvas, new HashSet<Control>())) bitmap.Save(Path.Combine(output, name + "-details.png"), ImageFormat.Png);
                    Check(body.AutoScrollMinSize.Height >= canvas.Height && body.AutoScrollMinSize.Height > body.ClientSize.Height,
                        name + " vertical scroll extent includes source notes below the viewport");
                }
                Invoke(panel, "ShowTokenStatistics", false);
                Check(!Field<NavButton>(panel, "tokenNav").Selected && Field<NavButton>(panel, "accountsNav").Selected, name + " returns to accounts");
                StopTimersAndTray(panel);
            }
            Console.WriteLine(name + " rendered");
        }

        static void CheckTokenContents(TokenStatisticsView view, string name, bool empty)
        {
            var range = Member<ComboBox>(view, "range");
            var body = Member<System.Windows.Forms.Panel>(view, "body");
            var canvas = Member<Control>(view, "canvas");
            Check(body.AutoScroll, name + " statistics body scrolls vertically");
            Check(!body.HorizontalScroll.Visible, name + " statistics body has no horizontal overflow");
            Check(canvas.Width <= body.ClientSize.Width, name + " canvas fits body width");
            Check(range.DropDownStyle == ComboBoxStyle.DropDownList, name + " range accepts known options only");
            Check(range.Items.Count >= 3, name + " multiple date ranges available");
            var heatmapHost = Member<System.Windows.Forms.Panel>(canvas, "heatmapHost");
            var heatmap = Member<Control>(canvas, "heatmap");
            Check(heatmapHost.AutoScroll, name + " long heatmap is horizontally scrollable");
            Check(canvas.ClientRectangle.Contains(heatmapHost.Bounds), name + " heatmap viewport stays inside canvas");
            int initial = range.SelectedIndex;
            string shortComparison = null;
            for (int index = 0; index < range.Items.Count; index++)
            {
                range.SelectedIndex = index; LayoutRecursively(view);
                DateTime first = Member<DateTime>(heatmap, "FirstDate"), last = Member<DateTime>(heatmap, "LastDate");
                int dayCount = Member<int>(heatmap, "DayCellCount");
                string label = name + " [" + range.Items[index] + "]";
                Check(last.Date == DateTime.Today, label + " ends at local today");
                Check(dayCount == (last.Date - first.Date).Days + 1, label + " one cell for every included day");
                Check(first.Date <= last.Date && first.TimeOfDay == TimeSpan.Zero, label + " date range is ordered at midnight");
                if (index == 0) Check(dayCount == 30 && first.Date == DateTime.Today.AddDays(-29), label + " exact inclusive 30-day range");
                if (index == range.Items.Count - 1 && !empty) Check(first == TokenFixture(false).FirstDay.Value, label + " all-time retains older year");
                TokenStatisticsSnapshot shown = Member<TokenStatisticsSnapshot>(canvas, "snapshot");
                TokenStatisticsSnapshot expected = TokenFixture(empty).ForRange(first, last);
                Check(shown != null && shown.Totals.Total == expected.Totals.Total && shown.ActiveDays == expected.ActiveDays,
                    label + " displayed totals and active days match selected period");
                Check(shown != null && shown.Days.All(d => d.Date >= first && d.Date <= last), label + " daily values stay within selected dates");
                string comparison = TokenStatisticsCanvas.ReadingComparison(shown);
                if (index == 0) shortComparison = comparison;
                if (index == range.Items.Count - 1 && !empty)
                    Check(comparison != shortComparison, label + " book comparison changes with the selected period");
                var cellBounds = heatmap.GetType().GetMethod("GetCellBounds", Private | BindingFlags.Public);
                Check(cellBounds != null, label + " cells expose deterministic geometry");
                if (cellBounds != null)
                {
                    var seen = new HashSet<Rectangle>();
                    bool cellsInside = true, cellsDistinct = true;
                    for (DateTime day = first.Date; day <= last.Date; day = day.AddDays(1))
                    {
                        Rectangle bounds = (Rectangle)cellBounds.Invoke(heatmap, new object[] { day });
                        cellsInside &= bounds.Width > 0 && bounds.Height > 0 && heatmap.ClientRectangle.Contains(bounds);
                        cellsDistinct &= seen.Add(bounds);
                    }
                    Check(cellsInside, label + " every date cell lies inside heatmap");
                    Check(cellsDistinct, label + " every date maps to a distinct cell");
                    Check(((Rectangle)cellBounds.Invoke(heatmap, new object[] { first.AddDays(-1) })).IsEmpty &&
                        ((Rectangle)cellBounds.Invoke(heatmap, new object[] { last.AddDays(1) })).IsEmpty, label + " outside range has no cell");
                }
                Check(!body.HorizontalScroll.Visible, label + " body does not scroll sideways");
                Check(!heatmapHost.VerticalScroll.Visible, label + " heatmap does not scroll vertically");
                if (index == range.Items.Count - 1 && !empty) Check(heatmap.Width > heatmapHost.ClientSize.Width, label + " long history stays scrollable instead of shrinking cells");
            }
            range.SelectedIndex = initial; LayoutRecursively(view);
            var previous = Member<TokenStatisticsSnapshot>(canvas, "snapshot");
            long previousTotal = previous.Totals.Total;
            view.SetLoading();
            Check(!Member<Pill>(view, "refresh").Enabled, name + " loading disables duplicate refresh button");
            Check(Member<TokenStatisticsSnapshot>(canvas, "snapshot").Totals.Total == previousTotal, name + " loading retains previous totals");
            Check(Member<Pill>(view, "refresh").Text == "读取中", name + " refresh button identifies background loading");
            view.SetError("隔离测试读取异常");
            Check(Member<Pill>(view, "refresh").Enabled, name + " refresh can be retried after an error");
            Check(Member<TokenStatisticsSnapshot>(canvas, "snapshot").Totals.Total == previousTotal, name + " error retains previous totals");
            Check(Member<string>(canvas, "message").Contains("刷新失败"), name + " retained data is identified after an error");
            view.SetSnapshot(TokenFixture(empty)); LayoutRecursively(view);
        }

        static void CheckReadingComparison()
        {
            Check(TokenStatisticsCanvas.ReadingComparison(null) == null, "book comparison does not invent zero before loading");
            var value = new TokenStatisticsSnapshot();
            Check(TokenStatisticsCanvas.ReadingComparison(value).Contains("0 倍"), "book comparison reports zero for a loaded empty period");
            value.Totals.Input = 44000; value.Totals.Output = 22000;
            string plain = TokenStatisticsCanvas.ReadingComparison(value);
            Check(plain.Contains("3 倍"), "book comparison uses 22000 tokens as one Little Prince reference");
            value.Totals.CacheRead = 999999999; value.Totals.CacheWrite = 999999999;
            Check(TokenStatisticsCanvas.ReadingComparison(value) == plain, "book comparison excludes both cache counters");
            value.Totals.Input = 1; value.Totals.Output = 0;
            string tiny = TokenStatisticsCanvas.ReadingComparison(value);
            Check(tiny.Contains("不到") && tiny.Contains("0.01 倍"), "book comparison does not round positive tiny use to zero");
        }

        static void HeatmapEvent(TokenHeatmap map, string name, object args)
        { typeof(TokenHeatmap).GetMethod(name, Private).Invoke(map, new[] { args }); }

        static Point Center(Rectangle bounds)
        { return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2); }

        static void RenderTokenHover(Panel panel, TokenStatisticsView view, HashSet<Control> hidden, string path)
        {
            var canvas = Member<Control>(view, "canvas");
            var heatmap = Member<TokenHeatmap>(canvas, "heatmap");
            DateTime day = DateTime.Today.AddDays(-2);
            heatmap.SetHoverDate(day);
            Rectangle cell = heatmap.GetCellBounds(day);
            Point offset = Point.Empty;
            for (Control c = heatmap; c != panel; c = c.Parent) offset.Offset(c.Location);
            cell.Offset(offset);
            using (var bubble = TokenHeatmapPopup.RenderBubble(heatmap.TooltipTitle, heatmap.TooltipValue, Theme.Dpi))
            using (var bitmap = RenderHiddenTree(panel, hidden))
            {
                Rectangle popup = TokenHeatmapPopup.CalculatePopupBounds(cell, bubble.Size, panel.ClientRectangle);
                using (Graphics g = Graphics.FromImage(bitmap))
                    g.DrawImage(bubble, new Rectangle(popup.Location, bubble.Size), 0, 0, bubble.Width, bubble.Height, GraphicsUnit.Pixel);
                bitmap.Save(path, ImageFormat.Png);
                Check(panel.ClientRectangle.Contains(popup), Path.GetFileName(path) + " popup and shadow fit panel");
                Check(!popup.IntersectsWith(cell), Path.GetFileName(path) + " popup leaves hovered cell visible");
                Check(!panel.Visible, Path.GetFileName(path) + " composed without showing a window");
            }
            heatmap.ClearHover();
        }

        static void CheckHeatmapInteraction(string output, string theme, int dpi)
        {
            Theme.Init(theme); float nativeDpi = Theme.Dpi; Theme.Dpi = dpi;
            string label = "heatmap interaction " + theme + " " + dpi + "dpi";
            DateTime first = new DateTime(2024, 1, 1), last = new DateTime(2024, 1, 31), day = new DateTime(2024, 1, 15);
            var values = new List<TokenDay> { new TokenDay { Date = day, Input = 1000, Output = 40000, CacheRead = 3100000, SessionCount = 2 },
                new TokenDay { Date = day.AddDays(1), Input = 1, Output = 2, CacheRead = 100000000, SessionCount = 1 },
                new TokenDay { Date = day.AddDays(2), SessionCount = 1 } };
            using (var host = new System.Windows.Forms.Panel { AutoScroll = true, Size = new Size(Theme.S(480), Theme.S(180)) })
            using (var map = new TokenHeatmap())
            {
                host.Controls.Add(map); map.SetData(values, first, last); map.FitWidth(Theme.S(480));
                ScaleReviewFonts(map, dpi / nativeDpi); LayoutRecursively(host);
                Check(TokenHeatmap.FormatTokens(3141000) == "314.1万 个 Token", label + " Chinese ten-thousand unit uses synthetic data");
                Check(TokenHeatmap.FormatTokens(123000000) == "1.2亿 个 Token", label + " Chinese hundred-million unit");
                Check(TokenHeatmap.FormatTokens(9999).Contains("9,999") || TokenHeatmap.FormatTokens(9999).Contains("9999"), label + " small amounts retain exact digits");
                Rectangle cell = map.GetCellBounds(day);
                Check(map.DateAt(Center(cell)) == day, label + " cell center resolves exact date");
                Check(map.DateAt(new Point(cell.Right, cell.Top + cell.Height / 2)) == null, label + " cell gap has no date");
                Check(map.DateAt(new Point(0, 0)) == null, label + " month and weekday area has no date");
                Check(map.DateAt(new Point(map.Width + Theme.S(10), cell.Top)) == null, label + " outside grid has no date");
                HeatmapEvent(map, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, Center(cell).X, Center(cell).Y, 0));
                Check(map.HoverDate == day && map.TooltipTitle.Contains("1月15日"), label + " mouse hover updates date");
                Check(map.TooltipValue == "314.1万 个 Token", label + " hover reports all four token categories");
                Check(!map.Focused, label + " hover does not steal keyboard focus");
                Rectangle next = map.GetCellBounds(day.AddDays(1));
                HeatmapEvent(map, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, Center(next).X, Center(next).Y, 0));
                Check(map.HoverDate == day.AddDays(1) && map.TooltipValue.Contains("亿"), label + " crossing cells updates content immediately");
                HeatmapEvent(map, "OnMouseLeave", EventArgs.Empty);
                Check(!map.HoverDate.HasValue, label + " leaving grid clears hover");
                map.SetHoverDate(day.AddDays(2));
                Check(map.TooltipValue == "0 个 Token", label + " explicit zero differs from missing records");
                map.SetHoverDate(day.AddDays(3));
                Check(map.TooltipValue.Contains("未记录"), label + " missing day is identified");
                map.SetData(null, first, last); map.SetHoverDate(day);
                Check(map.TooltipValue.Contains("尚未读取"), label + " unloaded source never becomes zero");
                map.SetData(values, first, last); map.SetHoverDate(day);
                map.SetData(values, first.AddDays(1), last);
                Check(!map.HoverDate.HasValue, label + " changing range clears obsolete hover");
                map.SetHoverDate(day);
                typeof(ScrollableControl).GetMethod("OnScroll", Private).Invoke(host,
                    new object[] { new ScrollEventArgs(ScrollEventType.ThumbPosition, 10, ScrollOrientation.HorizontalScroll) });
                Check(!map.HoverDate.HasValue, label + " scroll dismisses popup anchor");
                map.SetHoverDate(day);
                HeatmapEvent(map, "OnKeyDown", new KeyEventArgs(Keys.End));
                Check(map.HoverDate == last, label + " keyboard End identifies last day");
                HeatmapEvent(map, "OnKeyDown", new KeyEventArgs(Keys.Home));
                Check(map.HoverDate == first.AddDays(1), label + " keyboard Home identifies first included day");
                HeatmapEvent(map, "OnKeyDown", new KeyEventArgs(Keys.Left));
                Check(map.HoverDate == first.AddDays(1), label + " keyboard cannot move before range");
                HeatmapEvent(map, "OnLostFocus", EventArgs.Empty);
                Check(!map.HoverDate.HasValue, label + " losing focus clears hover");
                map.SetHoverDate(day); map.Visible = false;
                Check(!map.HoverDate.HasValue, label + " hiding page clears hover");
                map.Visible = true; map.SetData(values, first, last); map.SetHoverDate(day);
                using (var bubble = TokenHeatmapPopup.RenderBubble(map.TooltipTitle, map.TooltipValue, Theme.Dpi))
                {
                    using (var baseBubble = TokenHeatmapPopup.RenderBubble(map.TooltipTitle, map.TooltipValue, 96))
                        Check(Math.Abs(bubble.Width - baseBubble.Width * dpi / 96f) <= 2 && Math.Abs(bubble.Height - baseBubble.Height * dpi / 96f) <= 2,
                            label + " popup bitmap scales in pixels with DPI");
                    Rectangle available = new Rectangle(Theme.S(20), Theme.S(20), Theme.S(480), Theme.S(220));
                    foreach (Point corner in new[] { new Point(available.Left, available.Top), new Point(available.Right - cell.Width, available.Top),
                        new Point(available.Left, available.Bottom - cell.Height), new Point(available.Right - cell.Width, available.Bottom - cell.Height) })
                    {
                        var anchor = new Rectangle(corner, cell.Size);
                        Rectangle positioned = TokenHeatmapPopup.CalculatePopupBounds(anchor, bubble.Size, available);
                        Check(available.Contains(positioned), label + " edge placement stays in available viewport " + corner);
                        Check(!positioned.IntersectsWith(anchor), label + " edge placement avoids anchor " + corner);
                    }
                    var negativeScreen = new Rectangle(-Theme.S(1400), -Theme.S(120), Theme.S(900), Theme.S(640));
                    var negativeCell = new Rectangle(negativeScreen.Left + Theme.S(2), negativeScreen.Top + Theme.S(2), cell.Width, cell.Height);
                    Rectangle negativePopup = TokenHeatmapPopup.CalculatePopupBounds(negativeCell, bubble.Size, negativeScreen);
                    Check(negativeScreen.Contains(negativePopup) && !negativePopup.IntersectsWith(negativeCell),
                        label + " secondary monitor with negative coordinates keeps popup visible");
                    using (var preview = new Bitmap(Theme.S(520), Theme.S(250)))
                    using (var drawing = Graphics.FromImage(preview))
                    using (var chart = RenderHiddenTree(map, new HashSet<Control>()))
                    {
                        drawing.Clear(Theme.Surface); drawing.DrawImageUnscaled(chart, Theme.S(20), Theme.S(30));
                        cell = map.GetCellBounds(day); cell.Offset(Theme.S(20), Theme.S(30));
                        Rectangle positioned = TokenHeatmapPopup.CalculatePopupBounds(cell, bubble.Size, new Rectangle(Point.Empty, preview.Size));
                        drawing.DrawImage(bubble, new Rectangle(positioned.Location, bubble.Size), 0, 0, bubble.Width, bubble.Height, GraphicsUnit.Pixel);
                        preview.Save(Path.Combine(output, "native-token-hover-" + theme + "-" + dpi + "dpi.png"), ImageFormat.Png);
                    }
                }
                map.ClearHover();
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                string output = Path.GetFullPath(args.Length == 0 ? AppDomain.CurrentDomain.BaseDirectory : args[0]);
                Directory.CreateDirectory(output);
                string fixtureRoot = Path.Combine(output, "native-fixture-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixtureRoot);
                report.Add("Hidden in-process DrawToBitmap; no Show, message pump, clicks, task or sync calls.");
                report.Add("Hover events target isolated controls; popup images use production RenderBubble and geometry without opening a popup window.");
                report.Add("Every engine/config/Claude path is under: " + fixtureRoot);
                report.Add("125% and 150% cases simulate Theme.S geometry and point fonts; system DPI is unchanged.");
                CheckReadingComparison();
                foreach (string theme in new[] { "light", "dark" })
                {
                    CheckHeatmapInteraction(output, theme, 96);
                    CheckHeatmapInteraction(output, theme, 144);
                    Render(output, fixtureRoot, theme, 1000, 96, 2);
                    Render(output, fixtureRoot, theme, 900, 96, 2);
                    Render(output, fixtureRoot, theme, 1000, 120, 2);
                    RenderTokens(output, fixtureRoot, theme, 1000, 96, false);
                    RenderTokens(output, fixtureRoot, theme, 900, 96, false, 561);
                    RenderTokens(output, fixtureRoot, theme, 1000, 144, false);
                    RenderTokens(output, fixtureRoot, theme, 1000, 96, true);
                }
                Render(output, fixtureRoot, "light", 1000, 96, 5);
                Render(output, fixtureRoot, "light", 1000, 96, 5, 1080);
                Render(output, fixtureRoot, "dark", 1000, 96, 5, 1080);
                RenderUsageDialog(output, fixtureRoot, "light");
                RenderUsageDialog(output, fixtureRoot, "dark");
                Theme.Init("light"); Theme.Dpi = 96;
                using (var gallery = new PetGallery())
                {
                    gallery.FormBorderStyle = FormBorderStyle.None;
                    gallery.MinimumSize = Size.Empty; gallery.MaximumSize = Size.Empty;
                    gallery.ClientSize = new Size(788, 398); LayoutRecursively(gallery);
                    var officeOnly = new HashSet<Control>(gallery.Controls.Cast<Control>().Where(c => c is OfficeMap || c is OfficeDetails));
                    using (var bitmap = RenderHiddenTree(gallery, officeOnly))
                    { bitmap.Save(Path.Combine(output, "native-pet-gallery.png"), ImageFormat.Png); }
                }
                report.Add("Failures=" + failures);
                File.WriteAllLines(Path.Combine(output, "native-review-report.txt"), report, new UTF8Encoding(false));
                Console.WriteLine("Layout findings=" + failures + "; report=" + Path.Combine(output, "native-review-report.txt"));
                return failures == 0 ? 0 : 2;
            }
            catch (Exception ex)
            { Console.Error.WriteLine(ex.ToString()); return 1; }
        }
    }
}
