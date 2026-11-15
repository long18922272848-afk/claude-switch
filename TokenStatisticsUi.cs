using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    // Statistics use one combined, deduplicated local ledger for every account.
    sealed class TokenStatisticsView : UserControl
    {
        readonly Label heading = new Label();
        readonly ComboBox range = new TokenRangeComboBox();
        readonly Pill refresh = new Pill("刷新", PillKind.Ghost);
        readonly System.Windows.Forms.Panel body = new System.Windows.Forms.Panel();
        readonly TokenStatisticsCanvas canvas = new TokenStatisticsCanvas();
        TokenStatisticsSnapshot snapshot;
        bool loading;
        string error;
        public event EventHandler RefreshRequested;

        public TokenStatisticsView()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg; ForeColor = Theme.Ink;
            Font = new Font(Theme.SansFamily, 9f);
            AccessibleName = "合并 Token 统计";
            heading.Text = "概览 · 全部账号合并"; heading.AutoSize = false;
            heading.Font = new Font(Theme.SansFamily, 10f); heading.ForeColor = Theme.Ink;
            range.DropDownStyle = ComboBoxStyle.DropDownList;
            range.BackColor = Theme.Surface; range.ForeColor = Theme.Ink; range.FlatStyle = FlatStyle.Flat;
            range.DrawMode = DrawMode.OwnerDrawFixed; range.ItemHeight = Theme.S(22);
            range.DrawItem += (s, e) =>
            {
                int index = e.Index >= 0 ? e.Index : range.SelectedIndex;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Theme.Hover : Theme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (index >= 0 && index < range.Items.Count)
                    TextRenderer.DrawText(e.Graphics, range.Items[index].ToString(), range.Font,
                        new Rectangle(e.Bounds.X + Theme.S(7), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Theme.S(10)), e.Bounds.Height), Theme.Ink,
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
            };
            range.AccessibleName = "统计时间范围";
            range.Items.AddRange(new object[] { "近 30 天", "近 6 个月", "全部时间" });
            range.SelectedIndex = 1;
            range.SelectedIndexChanged += (s, e) => ApplySnapshot();
            refresh.AccessibleName = "重新读取本地 Token 记录";
            refresh.Click += (s, e) => { var handler = RefreshRequested; if (handler != null) handler(this, EventArgs.Empty); };
            body.AutoScroll = true; body.BackColor = Theme.Bg;
            body.Controls.Add(canvas);
            Controls.AddRange(new Control[] { heading, range, refresh, body });
            Resize += (s, e) => Arrange();
            body.ClientSizeChanged += (s, e) => ArrangeCanvas();
            Arrange(); ApplySnapshot();
        }

        public void SetLoading()
        { loading = true; error = null; refresh.Enabled = false; refresh.Text = "读取中"; ApplySnapshot(); }

        public void SetError(string message)
        { loading = false; error = message; refresh.Enabled = true; refresh.Text = "刷新"; ApplySnapshot(); }

        public void SetSnapshot(TokenStatisticsSnapshot value)
        { snapshot = value; loading = false; error = null; refresh.Enabled = true; refresh.Text = "刷新"; ApplySnapshot(); }

        void Arrange()
        {
            int gap = Theme.S(10), inset = Theme.S(18), buttonWidth = Theme.S(70), rangeWidth = Theme.S(128);
            int contentWidth = Width - SystemInformation.VerticalScrollBarWidth - Theme.S(2);
            refresh.SetBounds(Math.Max(0, contentWidth - inset - buttonWidth), 0, buttonWidth, Theme.S(30));
            range.SetBounds(Math.Max(0, refresh.Left - gap - rangeWidth), Theme.S(3), rangeWidth, Theme.S(26));
            heading.SetBounds(inset, 0, Math.Max(0, range.Left - gap - inset), Theme.S(30));
            body.SetBounds(0, Theme.S(42), Width, Math.Max(0, Height - Theme.S(42)));
            ArrangeCanvas();
        }

        void ArrangeCanvas()
        {
            // Always reserve the scrollbar gutter: resizing cannot oscillate at its threshold.
            int width = Math.Max(Theme.S(320), body.Width - SystemInformation.VerticalScrollBarWidth - Theme.S(2));
            canvas.Width = width;
            body.AutoScrollMinSize = new Size(0, canvas.Height);
        }

        void ApplySnapshot()
        {
            DateTime end = DateTime.Today;
            DateTime start = range.SelectedIndex == 0 ? end.AddDays(-29) :
                range.SelectedIndex == 2 ? (snapshot == null || !snapshot.FirstDay.HasValue ? end : snapshot.FirstDay.Value.Date) : end.AddMonths(-6).AddDays(1);
            if (start > end) start = end;
            TokenStatisticsSnapshot filtered = snapshot == null ? null : snapshot.ForRange(start, end);
            string state = error != null ? (snapshot == null ? "读取失败：" : "刷新失败，保留上次记录：") + error :
                loading && snapshot == null ? "正在读取本地 Code 用量记录…" : null;
            heading.Text = snapshot == null ? "概览 · 全部账号合并" : "概览 · " + snapshot.ProfileCount + " 个账号合并";
            canvas.SetData(filtered, start, end, state);
            ArrangeCanvas();
            AccessibleDescription = canvas.AccessibleDescription;
        }

        protected override void Dispose(bool disposing)
        { if (disposing) { heading.Font.Dispose(); Font.Dispose(); } base.Dispose(disposing); }
    }

    // The native collapsed field ignores the palette and WM_PRINT owner-draw on some themes.
    // Keep ComboBox navigation/accessibility, but paint this field for both screen and capture.
    sealed class TokenRangeComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F && IsHandleCreated)
                using (var g = Graphics.FromHwnd(Handle)) DrawField(g);
            else if ((m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero)
                using (var g = Graphics.FromHdc(m.WParam)) DrawField(g);
        }

        void DrawField(Graphics g)
        {
            Rectangle r = ClientRectangle;
            if (r.Width < 2 || r.Height < 2) return;
            using (var brush = new SolidBrush(Theme.Surface)) g.FillRectangle(brush, r);
            using (var pen = new Pen(Focused ? Theme.Accent : Theme.Line)) g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
            int arrow = Theme.S(24);
            TextRenderer.DrawText(g, SelectedItem == null ? string.Empty : SelectedItem.ToString(), Font,
                new Rectangle(Theme.S(8), 0, Math.Max(0, r.Width - arrow - Theme.S(10)), r.Height), Enabled ? Theme.Ink : Theme.Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            float cx = r.Width - arrow / 2f, cy = r.Height / 2f;
            using (var pen = new Pen(Theme.Muted, Math.Max(1, Theme.Dpi / 96f)))
                g.DrawLines(pen, new[] { new PointF(cx - Theme.S(3), cy - Theme.S(1)), new PointF(cx, cy + Theme.S(2)), new PointF(cx + Theme.S(3), cy - Theme.S(1)) });
        }
    }

    sealed class TokenStatisticsCanvas : Control
    {
        readonly System.Windows.Forms.Panel heatmapHost = new System.Windows.Forms.Panel();
        readonly TokenHeatmap heatmap = new TokenHeatmap();
        readonly Font normal = new Font(Theme.SansFamily, 9f);
        readonly Font small = new Font(Theme.SansFamily, 8.5f);
        readonly Font amount = new Font(Theme.SansFamily, 18f);
        readonly Font medium = new Font(Theme.SansFamily, 11f);
        readonly ToolTip tip = new ToolTip { InitialDelay = 350, AutoPopDelay = 15000 };
        TokenStatisticsSnapshot snapshot;
        DateTime start, end;
        string message;
        int summaryTop, tableTop, noteTop;
        int hoverRow = -1;
        const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

        public TokenStatisticsCanvas()
        {
            BackColor = Theme.Surface;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            heatmapHost.BackColor = Theme.Surface; heatmapHost.AutoScroll = true;
            heatmapHost.Controls.Add(heatmap); Controls.Add(heatmapHost);
            Resize += (s, e) => Arrange();
            MouseMove += ShowNumberTip;
            MouseLeave += (s, e) => { tip.SetToolTip(this, null); SetHoverRow(-1); };
            AccessibleName = "合并用量概览与 Token 明细";
        }

        public void SetData(TokenStatisticsSnapshot value, DateTime from, DateTime through, string state)
        {
            snapshot = value; start = from; end = through; message = state;
            hoverRow = -1;
            heatmap.SetData(value == null ? null : value.Days, start, end);
            AccessibleDescription = value == null ? (state ?? "尚未读取 Token 记录") :
                "总计 " + Exact(value.Totals.Total) + " Token，输入 " + Exact(value.Totals.Input) + "，输出 " + Exact(value.Totals.Output) +
                "，缓存读取 " + Exact(value.Totals.CacheRead) + "，缓存写入 " + Exact(value.Totals.CacheWrite) + "；" + value.SessionCount + " 个会话，" + value.ActiveDays + " 个活跃日。";
            Arrange(); Invalidate();
        }

        void Arrange()
        {
            int inset = Theme.S(18), inner = Math.Max(Theme.S(260), Width - inset * 2);
            heatmap.FitWidth(inner);
            int chartHeight = heatmap.Height + (heatmap.Width > inner ? SystemInformation.HorizontalScrollBarHeight : 0) + Theme.S(2);
            heatmapHost.SetBounds(inset, Theme.S(30), inner, chartHeight);
            heatmapHost.AutoScrollMinSize = heatmap.Size;
            summaryTop = heatmapHost.Bottom + Theme.S(28);
            tableTop = summaryTop + Theme.S(84);
            noteTop = tableTop + Theme.S(152);
            Height = noteTop + Theme.S(94);
        }

        internal static string Compact(long value)
        {
            if (value >= 1000000000L) return (value / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (value >= 1000000L) return (value / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000L) return (value / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal static string Exact(long value) { return value.ToString("N0", CultureInfo.InvariantCulture); }
        internal static string ReadingComparison(TokenStatisticsSnapshot value)
        {
            if (value == null) return null;
            // Display estimate only: the chosen English edition is approximately 22,000 tokens.
            // Cache traffic is deliberately excluded from the amount of text being compared.
            double books = value.Totals.Input / 22000d + value.Totals.Output / 22000d;
            if (books > 0 && books < .01) return "输入与输出不到《小王子》的 0.01 倍（不含缓存）";
            string figure = books == 0 ? "0" : books < 1 ? books.ToString("0.00", CultureInfo.InvariantCulture) : books.ToString("0.#", CultureInfo.InvariantCulture);
            return "输入与输出 ≈《小王子》的 " + figure + " 倍（不含缓存）";
        }
        internal static string ReadingComparisonExplanation(TokenStatisticsSnapshot value)
        {
            if (value == null) return "尚未读取 Token 用量，暂不计算作品对照。";
            return ReadingComparison(value) + "\r\n以英文版《小王子》每本约 22,000 Token 作为展示估算标尺。\r\n" +
                "仅用所选范围的输入与输出；不计缓存读取或缓存写入。\r\n版本、语言与分词方式都会影响结果，不是精确值或官方换算。\r\n" +
                "英文版词数参考（16,534 词）：https://www.arbookfind.com/bookdetailprint.aspx?l=EN&q=123";
        }
        string Value(long value) { return snapshot == null ? "—" : Compact(value); }
        void TextLine(Graphics g, string text, Font font, Rectangle bounds, Color color, bool right)
        { TextRenderer.DrawText(g, text ?? string.Empty, font, bounds, color, Line | (right ? TextFormatFlags.Right : 0)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; g.Clear(Theme.Surface);
            int x = Theme.S(18), w = Width - x * 2, h = Theme.S(22);
            TextLine(g, "使用活跃度", normal, new Rectangle(x, Theme.S(5), w / 2, h), Theme.Ink, false);
            TextLine(g, start.ToString("yyyy.MM.dd") + " — " + end.ToString("yyyy.MM.dd"), small,
                new Rectangle(x + w / 2, Theme.S(5), w / 2, h), Theme.Muted, true);
            int legendY = heatmapHost.Bottom + Theme.S(7);
            string warning = snapshot == null || string.IsNullOrWhiteSpace(snapshot.Warning) ? null : snapshot.Warning;
            string note = message ?? warning ?? ReadingComparison(snapshot) ?? "尚未读取本地记录";
            bool issue = message != null ? !message.StartsWith("正在", StringComparison.Ordinal) : warning != null;
            TextLine(g, note, small, new Rectangle(x, legendY, Math.Max(0, w - Theme.S(154)), h), issue ? Theme.Accent : Theme.Muted, false);
            int lx = Width - x - Theme.S(140);
            TextLine(g, "少", small, new Rectangle(lx, legendY, Theme.S(22), h), Theme.Muted, false);
            for (int i = 0; i < 5; i++)
                using (var brush = new SolidBrush(TokenHeatmap.LevelColor(i)))
                    g.FillRectangle(brush, lx + Theme.S(25 + i * 16), legendY + Theme.S(5), Theme.S(12), Theme.S(12));
            TextLine(g, "多", small, new Rectangle(lx + Theme.S(108), legendY, Theme.S(26), h), Theme.Muted, true);

            TokenTotals totals = snapshot == null ? new TokenTotals() : snapshot.Totals;
            int third = w / 3;
            string[] captions = { "Token 总数", "有用量的会话", "活跃天数" };
            string[] figures = { Value(totals.Total), snapshot == null ? "—" : snapshot.SessionCount.ToString("N0"),
                snapshot == null ? "—" : snapshot.ActiveDays + " / " + ((end - start).Days + 1) };
            for (int i = 0; i < 3; i++)
            {
                if (i > 0)
                    using (var pen = new Pen(Theme.Line))
                        g.DrawLine(pen, x + third * i - Theme.S(12), summaryTop + Theme.S(4),
                            x + third * i - Theme.S(12), summaryTop + Theme.S(46));
                TextLine(g, captions[i], small, new Rectangle(x + third * i, summaryTop, third - Theme.S(12), Theme.S(18)), Theme.Muted, false);
                TextLine(g, figures[i], amount, new Rectangle(x + third * i, summaryTop + Theme.S(18), third - Theme.S(12), Theme.S(32)), Theme.Ink, false);
            }
            int half = w / 2;
            string model = snapshot == null || string.IsNullOrWhiteSpace(snapshot.MostUsedModel) ? "—" : snapshot.MostUsedModel;
            model = ModelLabel(model);
            DrawPair(g, "最常用模型", model, x, summaryTop + Theme.S(56), half - Theme.S(15));
            DrawPair(g, "最活跃的一天", snapshot == null || !snapshot.BusiestDay.HasValue ? "—" : snapshot.BusiestDay.Value.ToString("M 月 d 日"),
                x + half + Theme.S(10), summaryTop + Theme.S(56), half - Theme.S(10));

            TextLine(g, "Token 明细", medium, new Rectangle(x, tableTop, w / 2, Theme.S(24)), Theme.Ink, false);
            TextLine(g, "跨账号副本已去重", small, new Rectangle(x + w / 2, tableTop, w / 2, Theme.S(24)), Theme.Muted, true);
            string[] labels = { "输入", "输出", "缓存读取", "缓存写入", "总计（含缓存）" };
            long[] values = { totals.Input, totals.Output, totals.CacheRead, totals.CacheWrite, totals.Total };
            for (int i = 0; i < 5; i++)
            {
                int y = tableTop + Theme.S(27 + i * 24);
                if (i == hoverRow)
                    using (var brush = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Ink, Theme.Dark ? .06f : .035f)))
                    using (var shape = Theme.Round(new Rectangle(x - Theme.S(6), y + 1, w + Theme.S(12), Theme.S(23)), Theme.S(4)))
                        g.FillPath(brush, shape);
                using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, x, y, Width - x, y);
                TextLine(g, labels[i], normal, new Rectangle(x, y + Theme.S(1), w / 2, Theme.S(23)), i == 4 ? Theme.Ink : Theme.Muted, false);
                TextLine(g, Value(values[i]), normal, new Rectangle(x + w / 2, y + Theme.S(1), w / 2, Theme.S(23)), Theme.Ink, true);
            }
            string coverage = "仅统计本机可读取的 Claude Code 记录，按本机时区归日。灰格表示未记录到用量。\r\n总计 = 输入 + 输出 + 缓存读取 + 缓存写入；不等同于订阅额度或账单。";
            TextRenderer.DrawText(g, coverage, small, new Rectangle(x, noteTop + Theme.S(5), w, Theme.S(43)), Theme.Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            string status = message;
            if (string.IsNullOrEmpty(status) && snapshot != null)
                status = "更新于 " + snapshot.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss") + " · 已读取 " + snapshot.SourceFileCount + " 个日志文件" +
                    (string.IsNullOrWhiteSpace(snapshot.Warning) ? "" : " · " + snapshot.Warning);
            TextRenderer.DrawText(g, status ?? string.Empty, small, new Rectangle(x, noteTop + Theme.S(53), w, Theme.S(36)),
                !string.IsNullOrWhiteSpace(snapshot == null ? null : snapshot.Warning) || message != null ? Theme.Accent : Theme.Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        void DrawPair(Graphics g, string label, string value, int x, int y, int width)
        {
            int labelWidth = Theme.S(100);
            TextLine(g, label, small, new Rectangle(x, y, Math.Min(labelWidth, width), Theme.S(23)), Theme.Muted, false);
            TextLine(g, value, normal, new Rectangle(x + labelWidth, y, Math.Max(0, width - labelWidth), Theme.S(23)), Theme.Ink, true);
        }

        static string ModelLabel(string model)
        {
            if (model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) model = model.Substring(7);
            var match = System.Text.RegularExpressions.Regex.Match(model, @"^(opus|sonnet|haiku)-(\d+)-(\d+)(?:-\d{8})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return !match.Success ? model : char.ToUpperInvariant(match.Groups[1].Value[0]) + match.Groups[1].Value.Substring(1) + " " + match.Groups[2].Value + "." + match.Groups[3].Value;
        }

        void SetHoverRow(int row)
        {
            if (row == hoverRow) return;
            hoverRow = row;
            Invalidate(new Rectangle(0, tableTop + Theme.S(27), Width, Theme.S(120)));
        }

        void ShowNumberTip(object sender, MouseEventArgs e)
        {
            if (snapshot == null) { tip.SetToolTip(this, message); return; }
            int row = (e.Y - tableTop - Theme.S(27)) / Theme.S(24);
            SetHoverRow(e.Y >= tableTop + Theme.S(27) && e.Y < tableTop + Theme.S(147) && e.X >= Theme.S(12) && e.X < Width - Theme.S(12) ? row : -1);
            string text = null;
            if (e.Y >= heatmapHost.Bottom && e.Y < summaryTop)
                text = message ?? (string.IsNullOrWhiteSpace(snapshot.Warning) ? ReadingComparisonExplanation(snapshot) : snapshot.Warning);
            else if (e.Y >= tableTop + Theme.S(27) && row >= 0 && row < 5)
            {
                long[] values = { snapshot.Totals.Input, snapshot.Totals.Output, snapshot.Totals.CacheRead, snapshot.Totals.CacheWrite, snapshot.Totals.Total };
                text = Exact(values[row]) + " Token";
            }
            else if (e.Y >= summaryTop && e.Y < summaryTop + Theme.S(52)) text = AccessibleDescription + "\r\n会话仅计算有有效 Token 用量记录的会话。";
            else if (e.Y >= noteTop) text = message ?? snapshot.Warning;
            else if (e.Y >= summaryTop + Theme.S(52) && e.Y < tableTop) text = snapshot.MostUsedModel + "\r\n按所选范围 Token 总量（含缓存）排序。";
            if (tip.GetToolTip(this) != text) tip.SetToolTip(this, text);
        }

        protected override void Dispose(bool disposing)
        { if (disposing) { normal.Dispose(); small.Dispose(); amount.Dispose(); medium.Dispose(); tip.Dispose(); } base.Dispose(disposing); }
    }

}
