using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    // Both pages are views. Selecting a desk only selects its local history record.
    sealed class PetGallery : Form
    {
        readonly PetGalleryCanvas canvas = new PetGalleryCanvas();
        readonly OfficeMap map = new OfficeMap { Name = "officeMap" };
        readonly OfficeDetails details = new OfficeDetails();
        readonly Pill dailyTab = new Pill("日常", PillKind.Primary) { Name = "dailyTab" };
        readonly Pill officeTab = new Pill("办公室", PillKind.Ghost) { Name = "officeTab" };
        readonly Func<List<OfficeDesk>> readDesks;
        readonly Func<string> residentId;
        readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer();
        readonly Label title, subtitle;
        float officeScale;
        bool office;
        bool reading;
        long nextRefresh;
        public PetGallery() : this(() => new List<OfficeDesk>(), () => null) { }
        public PetGallery(Func<List<OfficeDesk>> readDesks, Func<string> residentId)
        {
            this.readDesks = readDesks;
            this.residentId = residentId;
            Text = "小克的日常"; BackColor = Theme.Bg; ForeColor = Theme.Ink;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; KeyPreview = true;
            title = new Label { Text = "小克的日常", ForeColor = Theme.Ink, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Theme.SerifFamily, 19f) };
            subtitle = new Label { Text = "眼睛跟着鼠标看。点头摸摸、点身体招手，点工位的茶杯请它喝茶。", AutoSize = true, ForeColor = Theme.Muted, Font = new Font(Theme.SansFamily, 9f), Location = new Point(Theme.S(24), Theme.S(57)) };
            Controls.AddRange(new Control[] { title, subtitle, canvas, map, details, dailyTab, officeTab });
            dailyTab.Click += (s, e) => ShowOffice(false);
            officeTab.Click += (s, e) => ShowOffice(true);
            map.SelectionChanged += (s, e) => details.SelectDesk(map.SelectedDesk);
            ShowOffice(false);
            animation.Interval = 40;
            animation.Tick += (s, e) => {
                if (!Visible || WindowState == FormWindowState.Minimized) return;
                if (!office) canvas.Advance();
                else if (DateTime.UtcNow.Ticks >= nextRefresh) RefreshOffice();
            };
            Shown += (s, e) => animation.Start();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; } };
        }
        internal void ShowOffice(bool value)
        {
            office = value;
            SuspendLayout();
            Rectangle area = Screen.FromControl(Owner ?? this).WorkingArea;
            if (value)
            {
                Size frame = SizeFromClientSize(Size.Empty);
                var available = new Size(area.Width - frame.Width - Theme.S(16), area.Height - frame.Height - Theme.S(16));
                ClientSize = OfficeClientSize(available, Theme.Dpi);
                officeScale = Math.Min(ClientSize.Width / 788f, ClientSize.Height / 536f);
                int margin = U(18), top = U(58), side = ClientSize.Height - top - margin;
                map.SetBounds(margin, top, side, side);
                int right = map.Right + U(16);
                details.SetBounds(right, top, ClientSize.Width - margin - right, side);
                dailyTab.SetBounds(ClientSize.Width - U(178), U(9), U(69), U(32));
                officeTab.SetBounds(ClientSize.Width - margin - U(83), U(9), U(83), U(32));
                title.SetBounds(U(72), U(5), U(245), U(40));
                SetPixelFont(title, Theme.SerifFamily, 25 * officeScale, FontStyle.Bold);
                SetPixelFont(dailyTab, Theme.SansFamily, 14 * officeScale, FontStyle.Regular);
                SetPixelFont(officeTab, Theme.SansFamily, 14 * officeScale, FontStyle.Regular);
            }
            else
            {
                ClientSize = new Size(Math.Min(Theme.S(788), area.Width - Theme.S(20)),
                    Math.Min(Theme.S(398), area.Height - Theme.S(54)));
                title.SetBounds(Theme.S(24), Theme.S(14), Theme.S(280), Theme.S(40));
                SetPixelFont(title, Theme.SerifFamily, 19f * Theme.Dpi / 72f, FontStyle.Regular);
                SetPixelFont(dailyTab, Theme.SansFamily, 9.5f * Theme.Dpi / 72f, FontStyle.Regular);
                SetPixelFont(officeTab, Theme.SansFamily, 9.5f * Theme.Dpi / 72f, FontStyle.Regular);
                dailyTab.SetBounds(ClientSize.Width - Theme.S(176), Theme.S(19), Theme.S(66), Theme.S(32));
                officeTab.SetBounds(ClientSize.Width - Theme.S(102), Theme.S(19), Theme.S(84), Theme.S(32));
                canvas.SetBounds(Theme.S(18), Theme.S(88), ClientSize.Width - Theme.S(36), ClientSize.Height - Theme.S(108));
            }
            dailyTab.Kind = value ? PillKind.Ghost : PillKind.Primary;
            officeTab.Kind = value ? PillKind.Primary : PillKind.Ghost;
            dailyTab.Invalidate(); officeTab.Invalidate();
            subtitle.Visible = canvas.Visible = !value;
            map.Visible = details.Visible = value;
            ResumeLayout(true);
            Invalidate();
            if (Visible) {
                Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                                     Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
            }
            if (value) { RefreshOffice(); map.Focus(); }
            else canvas.Focus();
        }
        internal static Size OfficeClientSize(Size availableClient, float dpi)
        {
            float scale = Math.Min(dpi / 96f, Math.Min(availableClient.Width / 788f, availableClient.Height / 536f));
            return new Size(Math.Max(1, (int)Math.Round(788 * scale)), Math.Max(1, (int)Math.Round(536 * scale)));
        }
        int U(int n) { return (int)Math.Round(n * officeScale); }
        internal static void SetPixelFont(Control control, string family, float pixels, FontStyle style)
        {
            Font previous = control.Font;
            if (previous.Unit == GraphicsUnit.Pixel && Math.Abs(previous.Size - pixels) < .01f && previous.Style == style) return;
            control.Font = new Font(family, Math.Max(1f, pixels), style, GraphicsUnit.Pixel);
            previous.Dispose();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!office) return;
            var g = e.Graphics;
            using (var pen = new Pen(Theme.Line, Math.Max(1f, officeScale)))
            {
                g.DrawLine(pen, U(18), U(49), ClientSize.Width - U(18), U(49));
                g.DrawLine(pen, U(60), U(12), U(60), U(38));
            }
            var saved = g.Save();
            g.TranslateTransform(U(18), U(4));
            float factor = officeScale * 96f / Theme.Dpi;
            g.ScaleTransform(factor, factor);
            PetScene.Sprite(g, 0, 0, PetState.Waiting, 16, false);
            g.Restore(saved);
        }
        async void RefreshOffice()
        {
            if (reading) return;
            reading = true;
            try
            {
                var desks = await Task.Run(readDesks);
                if (IsDisposed || Disposing || !office) return;
                map.SetDesks(desks, residentId());
                details.SetCounts(desks.Count(d => !d.Archived), desks.Count(d => d.Archived));
                details.SelectDesk(map.SelectedDesk);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("Office history: {0}", ex.GetType().Name);
                if (!IsDisposed && !Disposing && office) details.ShowReadError();
            }
            finally { reading = false; nextRefresh = DateTime.UtcNow.AddSeconds(2).Ticks; }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.TintCaption(Handle); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { animation.Stop(); animation.Dispose(); title.Font.Dispose(); subtitle.Font.Dispose(); dailyTab.Font.Dispose(); officeTab.Font.Dispose(); }
            base.Dispose(disposing);
        }
    }

    sealed class OfficeDetails : System.Windows.Forms.Panel
    {
        readonly Label heading, name, periodLabel, period, daysLabel, counts;
        readonly OfficeValueLabel status = new OfficeValueLabel();
        readonly OfficeValueLabel days = new OfficeValueLabel();
        readonly ToolTip tip = new ToolTip();
        readonly Color paper = Theme.Dark ? Color.FromArgb(50, 45, 39) : Color.FromArgb(250, 244, 232);
        readonly Color cover = Theme.Dark ? Color.FromArgb(65, 52, 41) : Color.FromArgb(234, 214, 181);
        readonly Color inset = Theme.Dark ? Color.FromArgb(62, 54, 45) : Color.FromArgb(241, 229, 207);
        readonly Color rule = Theme.Dark ? Color.FromArgb(97, 81, 65) : Color.FromArgb(207, 186, 153);
        readonly Color ink = Theme.Dark ? Color.FromArgb(247, 232, 207) : Color.FromArgb(66, 46, 32);
        readonly Color pencil = Theme.Dark ? Color.FromArgb(206, 187, 163) : Color.FromArgb(104, 82, 59);
        public OfficeDetails()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            heading = AddLabel("工位档案", "officeHeading", false, pencil, cover);
            name = AddLabel("", "officeName", true, ink, cover);
            AddLabel("", "officeStatus", false, pencil, cover, status);
            status.TextAlign = ContentAlignment.MiddleCenter;
            periodLabel = AddLabel("使用起止日期", "officePeriodLabel", false, pencil, paper);
            period = AddLabel("", "officePeriod", false, ink, paper);
            daysLabel = AddLabel("累计使用天数", "officeDaysLabel", false, pencil, inset);
            AddLabel("", "officeDays", true, ink, inset, days);
            counts = AddLabel("", "officeCounts", false, pencil, paper);
            tip.SetToolTip(days, "从添加到封存（在用工位算至今天），首尾日期各计入。");
            tip.SetToolTip(daysLabel, tip.GetToolTip(days));
        }
        Label AddLabel(string text, string id, bool serif, Color color, Color background, Label label = null)
        {
            label = label ?? new Label();
            label.Name = id; label.Text = text; label.ForeColor = color; label.BackColor = background;
            label.AutoEllipsis = true; label.UseMnemonic = false; label.TextAlign = ContentAlignment.MiddleLeft;
            label.Font = new Font(serif ? Theme.SerifFamily : Theme.SansFamily, 10f);
            Controls.Add(label);
            return label;
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (counts == null) return;
            int pad = V(20), w = Math.Max(1, Width - 2 * pad), badge = V(76);
            Place(heading, V(46), 18, w - V(26), 24, 14, false, FontStyle.Bold);
            Place(name, pad, 51, Math.Max(1, w - badge - V(8)), 43, 27, true, FontStyle.Bold);
            Place(status, Width - pad - badge, 59, badge, 26, 13, false);
            Place(periodLabel, pad, 130, w, 26, 15, false, FontStyle.Bold);
            Place(period, V(42), 164, w - V(22), 68, 20, false);
            Place(daysLabel, V(36), 268, w - V(32), 25, 14, false);
            Place(days, V(38), 292, w - V(36), 63, 40, true);
            days.UnitFont = daysLabel.Font;
            Place(counts, V(49), 402, w - V(29), 38, 14, false);
            Invalidate();
        }
        int V(int units) { return (int)Math.Round(Height * units / 460f); }
        void Place(Label field, int x, int y, int width, int height, int pixels, bool serif, FontStyle style = FontStyle.Regular)
        {
            field.SetBounds(x, V(y), width, V(height));
            PetGallery.SetPixelFont(field, serif ? Theme.SerifFamily : Theme.SansFamily, pixels * Math.Min(Width / 276f, Height / 460f), style);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 20 || Height < 20) return;
            var g = e.Graphics;
            using (var shape = Theme.Round(new RectangleF(.5f, .5f, Width - 1f, Height - 1f), Math.Max(1, V(8))))
            using (var fill = new SolidBrush(paper))
            using (var coverFill = new SolidBrush(cover))
            using (var blockFill = new SolidBrush(inset))
            using (var pen = new Pen(rule, Math.Max(1, V(1))))
            using (var stamp = new Pen(pencil, Math.Max(1.2f, Height / 300f)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Theme.Bg);
                g.FillPath(fill, shape);
                var clipped = g.Save();
                g.SetClip(shape);
                g.FillRectangle(coverFill, 0, 0, Width, V(108));
                g.Restore(clipped);
                g.DrawPath(pen, shape);
                g.DrawLine(pen, 1, V(108), Width - 2, V(108));
                // File-folder stamp in the header, sized with the same grid as the text.
                g.DrawPolygon(stamp, new[] { new Point(V(21),V(24)), new Point(V(28),V(24)), new Point(V(31),V(27)),
                    new Point(V(39),V(27)), new Point(V(39),V(36)), new Point(V(21),V(36)) });
                g.DrawLine(stamp, V(22), V(29), V(38), V(29));
                // Mark the two endpoints next to their actual text baselines.
                int middle = period.Top + period.Height / 2;
                int offset = period.Font.Height / 2, radius = Math.Max(2, V(3));
                g.DrawLine(pen, V(25), middle - offset, V(25), middle + offset);
                foreach (int y in new[] { middle - offset, middle + offset })
                {
                    g.FillEllipse(fill, V(25) - radius, y - radius, radius * 2, radius * 2);
                    g.DrawEllipse(stamp, V(25) - radius, y - radius, radius * 2, radius * 2);
                }
                using (var metric = Theme.Round(new RectangleF(V(19), V(254), Width - V(38), V(116)), Math.Max(1, V(7))))
                    g.FillPath(blockFill, metric);
                g.DrawLine(pen, V(20), V(390), Width - V(20), V(390));
                g.DrawRectangle(stamp, V(22), V(415), V(16), V(9));
                g.DrawLine(stamp, V(24), V(424), V(24), V(429));
                g.DrawLine(stamp, V(36), V(424), V(36), V(429));
            }
        }
        public void SetCounts(int active, int archived)
        {
            counts.Text = string.Format("在用 {0}     ·     封存 {1}", active, archived);
        }
        static string FormatDate(DateTime? date)
        {
            return date.HasValue ? date.Value.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) : "未记录";
        }
        public void SelectDesk(OfficeDesk desk)
        {
            string displayName = desk == null ? "暂无工位" : desk.Name;
            if (name.Text != displayName) { name.Text = displayName; tip.SetToolTip(name, displayName); }
            status.Text = desk == null ? "" : (desk.Archived ? "● 已封存" : "● 在用");
            bool active = desk != null && !desk.Archived;
            status.ForeColor = active ? (Theme.Dark ? Color.FromArgb(204, 224, 175) : Color.FromArgb(63, 86, 51)) : pencil;
            Color badgeColor = desk == null ? cover : active ? (Theme.Dark ? Color.FromArgb(64, 75, 54) : Color.FromArgb(216, 224, 191)) : inset;
            if (status.Fill != badgeColor) { status.Fill = badgeColor; status.Invalidate(); }
            period.Text = desk == null ? "—" : FormatDate(desk.Started) + Environment.NewLine +
                (desk.Archived ? FormatDate(desk.Ended) : "至今");
            int? total = desk == null ? null : desk.UsageDays(DateTime.Today);
            string amount = total.HasValue ? total.Value.ToString(CultureInfo.InvariantCulture) : "—";
            if (days.Text != amount) {
                days.Text = amount;
                days.AccessibleName = total.HasValue ? "累计使用 " + amount + " 天" : "累计天数未记录";
            }
        }
        public void ShowReadError() { counts.Text = "工位记录暂时无法读取"; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                tip.Dispose();
                foreach (Control label in Controls) label.Font.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    sealed class OfficeValueLabel : Label
    {
        public Color Fill;
        public Font UnitFont;
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1) return;
            if (UnitFont != null)
            {
                var g = e.Graphics;
                using (var format = StringFormat.GenericTypographic)
                using (var brush = new SolidBrush(ForeColor))
                {
                    float y = (Height - Font.GetHeight(g)) / 2f;
                    g.DrawString(Text, Font, brush, new PointF(0, y), format);
                    if (Text.Length > 0 && Text != "—")
                    {
                        float advance = g.MeasureString(Text, Font, PointF.Empty, format).Width;
                        float baseline = y + Font.Size * Font.FontFamily.GetCellAscent(Font.Style) / Font.FontFamily.GetEmHeight(Font.Style);
                        float unitAscent = UnitFont.Size * UnitFont.FontFamily.GetCellAscent(UnitFont.Style) / UnitFont.FontFamily.GetEmHeight(UnitFont.Style);
                        g.DrawString("天", UnitFont, brush, new PointF(advance + UnitFont.Size * .35f, baseline - unitAscent), format);
                    }
                }
                return;
            }
            if (!Fill.IsEmpty)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(new RectangleF(0, 0, Width, Height), Height / 2f))
                using (var brush = new SolidBrush(Fill)) e.Graphics.FillPath(brush, path);
            }
            var align = TextAlign == ContentAlignment.MiddleCenter ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Right;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, align | TextFormatFlags.NoPadding |
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    sealed class PetGalleryCanvas : Control
    {
        readonly PetState[] states = { PetState.Working, PetState.Packing, PetState.Syncing, PetState.Walking, PetState.Waiting, PetState.Error, PetState.Resting };
        readonly string[] names = { "认真办公", "收好行李", "核对资料", "换个工位", "等你回来", "遇到小问题", "喝茶与休憩" };
        readonly string[] details = { "打字，也留一点思考时间", "打开箱子，装进好思路", "逐一检查，妥当再出发", "拖着小箱子，继续向前", "望一望，再和你挥挥手", "挠挠头，等你一起解决", "抿口茶、翻翻书、眯一会" };
        readonly Font label = new Font(Theme.SansFamily, 10f);
        readonly Font note = new Font(Theme.SansFamily, 8.5f);
        readonly PetInteraction[] interactions = new PetInteraction[7];
        readonly ToolTip interactionTip = new ToolTip();
        int frame;
        int selected, hoverIndex = -1;
        PetReaction hoverReaction;
        public PetGalleryCanvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            TabStop = true;
            for (int i = 0; i < interactions.Length; i++) interactions[i] = new PetInteraction();
            AccessibleName = "七种小克动作预览";
            AccessibleDescription = string.Join("；", names) + "。方向键选择小克，回车摸头，空格挥手，第一格可按 T 请喝茶。";
        }
        public void Advance()
        {
            frame++;
            Point? mouse = IsHandleCreated ? (Point?)PointToClient(System.Windows.Forms.Cursor.Position) : null;
            for (int i = 0; i < interactions.Length; i++) interactions[i].Advance(mouse.HasValue ? (PointF?)SpritePoint(i, mouse.Value) : null);
            if (mouse.HasValue) UpdateHover(mouse.Value);
            Invalidate();
        }

        Rectangle Cell(int index)
        {
            int gap = Theme.S(10), cw = Math.Max(0, (Width - gap * 3) / 4), ch = Math.Max(0, (Height - gap) / 2);
            return new Rectangle(index % 4 * (cw + gap), index / 4 * (ch + gap), cw, ch);
        }
        float Drift(int index) { return states[index] == PetState.Walking ? Theme.S(10) * (float)Math.Sin(frame / 38.0) : 0; }
        PointF SpriteOrigin(int index)
        {
            Rectangle cell = Cell(index);
            return new PointF(cell.Left + Theme.S(24) + 1.55f * (Theme.S(10) + Drift(index)), cell.Top + Theme.S(43));
        }
        PointF SpritePoint(int index, Point client)
        {
            PointF origin = SpriteOrigin(index); float ratio = 1.55f * Theme.Dpi / 96f;
            return new PointF((client.X - origin.X) / ratio, (client.Y - origin.Y) / ratio);
        }
        internal RectangleF CupBounds()
        {
            Rectangle cell = Cell(0); float ratio = 1.55f * Theme.Dpi / 96f;
            float cx = cell.Left + Theme.S(24) + 73 * ratio;
            float cy = cell.Top + Theme.S(43) + 1.55f * Theme.S(2) + 27 * ratio;
            return new RectangleF(cx - Theme.S(22), cy - Theme.S(22), Theme.S(44), Theme.S(44));
        }
        internal PetReaction HitTest(Point client, out int index)
        {
            index = -1;
            if (!ClientRectangle.Contains(client)) return PetReaction.None;
            for (int i = 0; i < states.Length; i++)
            {
                if (!Cell(i).Contains(client)) continue;
                PointF local = SpritePoint(i, client);
                if (new RectangleF(3, 5, 31, 14).Contains(local)) { index = i; return PetReaction.Pat; }
                if (new RectangleF(0, 19, 35, 16).Contains(local)) { index = i; return PetReaction.Wave; }
                if (i == 0 && !interactions[0].IsTea && CupBounds().Contains(client)) { index = i; return PetReaction.Tea; }
                return PetReaction.None;
            }
            return PetReaction.None;
        }
        void UpdateHover(Point client)
        {
            int index; PetReaction hit = HitTest(client, out index);
            Cursor = hit == PetReaction.None ? Cursors.Default : Cursors.Hand;
            if (hoverIndex == index && hoverReaction == hit) return;
            hoverIndex = index; hoverReaction = hit;
            interactionTip.SetToolTip(this, hit == PetReaction.Pat ? "摸摸小克的头" : hit == PetReaction.Wave ? "和小克打个招呼" : hit == PetReaction.Tea ? "请小克喝茶" : "");
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e) { UpdateHover(e.Location); base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { UpdateHover(new Point(-1, -1)); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int index; PetReaction hit = HitTest(e.Location, out index);
                if (index >= 0 && hit != PetReaction.None && interactions[index].TryReact(hit))
                { selected = index; Focus(); AccessibleDescription = interactions[index].Message; Invalidate(); }
            }
            base.OnMouseDown(e);
        }
        protected override bool IsInputKey(Keys keyData)
        { Keys key = keyData & Keys.KeyCode; return key == Keys.Enter || key == Keys.Space || key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            { selected = Math.Max(0, Math.Min(states.Length - 1, selected + (e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : e.KeyCode == Keys.Up ? -4 : 4))); e.Handled = true; e.SuppressKeyPress = true; Invalidate(); }
            else
            {
                PetReaction hit = e.KeyCode == Keys.Enter ? PetReaction.Pat : e.KeyCode == Keys.Space ? PetReaction.Wave : e.KeyCode == Keys.T && selected == 0 ? PetReaction.Tea : PetReaction.None;
                if (hit != PetReaction.None) { interactions[selected].TryReact(hit); AccessibleDescription = interactions[selected].Message; e.Handled = true; e.SuppressKeyPress = true; Invalidate(); }
            }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < states.Length; i++)
            {
                Rectangle cell = Cell(i); int x = cell.Left, y = cell.Top, cw = cell.Width, ch = cell.Height;
                if (cw < Theme.S(24) || ch < Theme.S(28)) continue;
                using (var shape = Theme.Round(new RectangleF(x + 1, y + 1, cw - 2, ch - 2), Theme.S(12)))
                {
                    using (var fill = new SolidBrush(Theme.Surface)) g.FillPath(fill, shape);
                    using (var line = new Pen(Theme.Line)) g.DrawPath(line, shape);
                }
                TextRenderer.DrawText(g, names[i], label, new Rectangle(x + Theme.S(12), y + Theme.S(10), cw - Theme.S(24), Theme.S(23)), Theme.Ink, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                var saved = g.Save();
                g.TranslateTransform(x + Theme.S(24), y + Theme.S(43)); g.ScaleTransform(1.55f, 1.55f);
                if (states[i] == PetState.Working)
                {
                    if (hoverIndex == i && hoverReaction == PetReaction.Tea && !interactions[i].IsTea)
                        using (var glow = new SolidBrush(Theme.AccentSoft)) g.FillRectangle(glow, Theme.S(67), Theme.S(24), Theme.S(12), Theme.S(11));
                    PetScene.Desk(g, 0, Theme.S(2), true, frame, interactions[i]);
                }
                PetScene.Sprite(g, Theme.S(10) + Drift(i), 0, states[i], frame + i * 23, false, interactions[i]);
                g.Restore(saved);
                TextRenderer.DrawText(g, interactions[i].Reaction == PetReaction.None ? details[i] : interactions[i].Message, note, new Rectangle(x + Theme.S(12), y + ch - Theme.S(28), cw - Theme.S(24), Theme.S(19)), Theme.Muted,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                if (Focused && ShowFocusCues && selected == i) ControlPaint.DrawFocusRectangle(g, new Rectangle(x + Theme.S(5), y + Theme.S(5), cw - Theme.S(10), ch - Theme.S(10)), Theme.Muted, Theme.Surface);
            }
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { label.Dispose(); note.Dispose(); interactionTip.Dispose(); } base.Dispose(disposing); }
    }
}
