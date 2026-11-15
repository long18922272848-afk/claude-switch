using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    // These controls only present state supplied by the panel. They never start work,
    // change accounts, or decide whether Claude is idle.
    class SideLounge : Control
    {
        bool resting;
        int frame, previousInteractionFrame = -1;
        string detail = "忙完这一段，来这里喝杯茶。";
        readonly Font headingFont = new Font(Theme.SansFamily, 9.5f, FontStyle.Bold);
        readonly Font detailFont = new Font(Theme.SansFamily, 8.5f);
        readonly PetInteraction interaction = new PetInteraction();
        readonly ToolTip interactionTip = new ToolTip();
        PetReaction hoverReaction;
        string RestHeading { get { return frame / 240 % 3 == 1 ? "小克在看书" : frame / 240 % 3 == 2 ? "小克打盹中" : "小克喝茶中"; } }

        public SideLounge()
        {
            Size = new Size(Theme.S(140), Theme.S(178));
            TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AccessibleName = "休息间。小克正在工位办公。";
        }

        public void Update(bool isResting, int animationFrame, string description)
        {
            if (resting && !isResting) ClearInteraction();
            resting = isResting;
            frame = Math.Max(0, animationFrame);
            int elapsed = previousInteractionFrame < 0 || frame < previousInteractionFrame ? 40 : (int)Math.Min(int.MaxValue, (long)(frame - previousInteractionFrame) * 40);
            previousInteractionFrame = frame;
            if (resting && IsHandleCreated)
            {
                Point mouse = PointToClient(System.Windows.Forms.Cursor.Position);
                interaction.Advance(SpritePoint(mouse), elapsed);
                UpdateHover(HitTest(mouse));
            }
            else interaction.Advance(null, elapsed);
            detail = resting
                ? (frame / 240 % 3 == 1 ? "翻两页书，等你回来。" : frame / 240 % 3 == 2 ? "眯一小会，养足精神。" : "抿口热茶，发会儿呆。")
                : "忙完这一段，来这里喝杯茶。";
            AccessibleName = (resting ? InteractionHeading + "。" : "休息间。小克正在工位办公。") + DisplayDetail;
            AccessibleDescription = description + " 点头摸摸、点身体挥手、点茶杯喝茶。键盘：回车摸头，空格挥手，T 喝茶。";
            Invalidate();
        }

        string InteractionHeading { get { return interaction.Reaction == PetReaction.Pat ? "小克开心了" : interaction.Reaction == PetReaction.Wave ? "小克在和你招手" : interaction.IsTea ? "小克喝茶中" : RestHeading; } }
        string DisplayDetail { get { return resting && interaction.Reaction != PetReaction.None ? interaction.Message : detail; } }

        public void ClearInteraction()
        { interaction.ClearReaction(); UpdateHover(PetReaction.None); Invalidate(); }

        Rectangle RoomArea()
        { int inset = Theme.S(6); return new Rectangle(inset, Theme.S(32), Math.Max(0, Width - inset * 2), Math.Min(Theme.S(82), Math.Max(0, Height - Theme.S(97)))); }

        float RoomScale(Rectangle area)
        { return Math.Min(1f, Math.Min(area.Width / (float)Math.Max(1, Theme.S(132)), area.Height / (float)Math.Max(1, Theme.S(82)))); }

        PointF RoomPoint(Point client)
        {
            Rectangle area = RoomArea(); float fit = RoomScale(area), dpi = Theme.Dpi / 96f;
            if (fit <= 0 || dpi <= 0) return new PointF(float.MinValue, float.MinValue);
            float x = area.Left + (area.Width - Theme.S(132) * fit) / 2f;
            return new PointF((client.X - x) / (fit * dpi), (client.Y - area.Top) / (fit * dpi));
        }

        PointF SpritePoint(Point client)
        {
            PointF p = RoomPoint(client); float dpi = Theme.Dpi / 96f;
            return new PointF(p.X - Theme.S(41) / dpi, p.Y - Theme.S(22) / dpi);
        }

        internal PetReaction HitTest(Point client)
        {
            if (!resting || !ClientRectangle.Contains(client)) return PetReaction.None;
            PointF local = SpritePoint(client);
            if (new RectangleF(3, 5, 31, 14).Contains(local)) return PetReaction.Pat;
            if (new RectangleF(0, 19, 35, 16).Contains(local)) return PetReaction.Wave;
            return !interaction.IsTea && new RectangleF(90, 27, 44, 44).Contains(RoomPoint(client)) ? PetReaction.Tea : PetReaction.None;
        }

        void UpdateHover(PetReaction reaction)
        {
            Cursor = reaction == PetReaction.None ? Cursors.Default : Cursors.Hand;
            if (hoverReaction == reaction) return;
            hoverReaction = reaction;
            interactionTip.SetToolTip(this, reaction == PetReaction.Pat ? "摸摸小克的头" : reaction == PetReaction.Wave ? "和小克打个招呼" : reaction == PetReaction.Tea ? "请小克喝茶" : "");
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e) { UpdateHover(HitTest(e.Location)); base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { UpdateHover(PetReaction.None); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                PetReaction hit = HitTest(e.Location);
                if (hit != PetReaction.None && interaction.TryReact(hit)) { Focus(); UpdateHover(PetReaction.None); Invalidate(); }
            }
            base.OnMouseDown(e);
        }
        protected override bool IsInputKey(Keys keyData)
        { Keys key = keyData & Keys.KeyCode; return key == Keys.Enter || key == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (resting)
            {
                PetReaction reaction = e.KeyCode == Keys.Enter ? PetReaction.Pat : e.KeyCode == Keys.Space ? PetReaction.Wave : e.KeyCode == Keys.T ? PetReaction.Tea : PetReaction.None;
                if (reaction != PetReaction.None) { interaction.TryReact(reaction); e.Handled = true; e.SuppressKeyPress = true; Invalidate(); }
            }
            base.OnKeyDown(e);
        }

        static void Pixel(Graphics g, Color color, float x, float y, float width, float height)
        {
            using (var brush = new SolidBrush(color)) g.FillRectangle(brush, x, y, width, height);
        }

        void DrawRoom(Graphics g, Rectangle area)
        {
            float scale = RoomScale(area);
            if (scale <= 0) return;
            var room = g.Save();
            g.TranslateTransform(area.Left + (area.Width - Theme.S(132) * scale) / 2f, area.Top);
            g.ScaleTransform(scale, scale);
            var pixels = g.Save();
            g.ScaleTransform(Theme.Dpi / 96f, Theme.Dpi / 96f);
            g.SmoothingMode = SmoothingMode.None;
            Color leaf = Theme.Green;
            Color sofa = Theme.Mix(leaf, Theme.Surface, Theme.Dark ? .36f : .62f);
            Color edge = Theme.Mix(leaf, Theme.Muted, .35f);
            using (var shadow = new SolidBrush(Color.FromArgb(Theme.Dark ? 23 : 10, Theme.Ink)))
                g.FillEllipse(shadow, 8, 64, 116, 8);
            Pixel(g, edge, 28, 59, 4, 9);
            Pixel(g, edge, 76, 59, 4, 9);
            Pixel(g, sofa, 24, 33, 60, 29);
            Pixel(g, Theme.Mix(sofa, Theme.Surface, .24f), 28, 36, 52, 14);
            Pixel(g, edge, 20, 46, 8, 16);
            Pixel(g, edge, 80, 46, 8, 16);
            Pixel(g, Theme.Mix(sofa, Theme.Surface, .14f), 29, 52, 24, 8);
            Pixel(g, Theme.Mix(sofa, Theme.Surface, .14f), 55, 52, 24, 8);
            // The plant, table and cup remain in the room while the resident is working.
            Pixel(g, leaf, 8, 37, 2, 20);
            Pixel(g, leaf, 3, 35, 6, 4);
            Pixel(g, leaf, 10, 28, 6, 7);
            Pixel(g, Theme.AccentSoft, 3, 55, 13, 10);
            Pixel(g, Theme.Line, 4, 64, 11, 2);
            Pixel(g, Theme.Line, 98, 53, 28, 3);
            Pixel(g, Theme.Line, 100, 56, 3, 12);
            Pixel(g, Theme.Line, 122, 56, 3, 12);
            if (!resting || !interaction.IsTea)
            {
                if (hoverReaction == PetReaction.Tea) Pixel(g, Theme.Mix(Theme.AccentSoft, Theme.Surface, .25f), 103, 42, 19, 14);
                Pixel(g, Theme.AccentSoft, 106, 45, 10, 8);
                Pixel(g, Theme.Accent, 108, 47, 6, 4);
                Pixel(g, Theme.Muted, 116, 46, 3, 5);
                Pixel(g, Theme.Surface, 116, 47, 2, 3);
            }
            if (resting && !interaction.IsTea)
            {
                int rise = frame / 9 % 8;
                Pixel(g, Color.FromArgb(72 - rise * 7, Theme.Muted), 110 + frame / 25 % 2, 40 - rise, 1, 4);
            }
            g.Restore(pixels);
            // Sprite already applies DPI scaling. Keep only the room's fit scale here.
            if (resting) PetScene.Sprite(g, Theme.S(41), Theme.S(22), PetState.Resting, frame, false, interaction);
            g.Restore(room);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? Theme.Bg : Parent.BackColor);
            if (Width <= Theme.S(12) || Height <= Theme.S(12)) return;
            int inset = Theme.S(6), textWidth = Math.Max(0, Width - inset * 2);
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, inset, 1, Width - inset, 1);
            TextRenderer.DrawText(g, "休息间", detailFont,
                new Rectangle(inset, Theme.S(11), textWidth, Theme.S(20)), Theme.Muted,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            DrawRoom(g, RoomArea());
            int textTop = Math.Min(Theme.S(112), Math.Max(Theme.S(34), Height - Theme.S(66)));
            TextRenderer.DrawText(g, resting ? InteractionHeading : "给思路留点空闲", headingFont,
                new Rectangle(inset, textTop, textWidth, Theme.S(22)), Theme.Ink,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, DisplayDetail, detailFont,
                new Rectangle(inset, textTop + Theme.S(26), textWidth, Math.Max(0, Height - textTop - Theme.S(28))),
                Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(inset, Theme.S(30), textWidth, Theme.S(81)), Theme.Muted, BackColor);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { headingFont.Dispose(); detailFont.Dispose(); interactionTip.Dispose(); }
            base.Dispose(disposing);
        }
    }

    class CompactJourney : Control
    {
        public string Heading = "小克准备上班了";
        public string Detail = "选择一个账号，小克会带着进度一起过去。";
        public PetState State = PetState.Working;
        readonly Font titleFont = new Font(Theme.SansFamily, 10.5f, FontStyle.Bold);
        readonly Font smallFont = new Font(Theme.SansFamily, 8.5f);
        int frame;
        float marker = 3;

        public CompactJourney()
        {
            Height = Theme.S(72);
            TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleName = "小克的工作状态";
        }

        public void Update(string heading, string detail, PetState state)
        {
            Heading = heading ?? string.Empty;
            Detail = detail ?? string.Empty;
            State = state;
            AccessibleName = Heading + "。" + Detail;
            Invalidate();
        }

        int Selected
        {
            get
            {
                return State == PetState.Packing ? 0 : State == PetState.Syncing ? 1 :
                    State == PetState.Walking ? 2 : State == PetState.Working ? 3 : -1;
            }
        }

        public void AdvanceFrame(int value)
        {
            frame = Math.Max(0, value);
            int selected = Selected;
            if (selected >= 0) marker += (selected - marker) * .24f;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? Theme.Bg : Parent.BackColor);
            if (Width < Theme.S(32) || Height < Theme.S(32)) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f);
            using (var shape = Theme.Round(bounds, Math.Min(Theme.S(10), Height / 3f)))
            {
                using (var brush = new SolidBrush(Theme.Mix(Theme.Surface, Theme.AccentSoft, .24f)))
                    g.FillPath(brush, shape);
                using (var pen = new Pen(Theme.Line)) g.DrawPath(pen, shape);
            }
            bool labels = Width >= Theme.S(620);
            bool dots = Width >= Theme.S(340);
            int stagesWidth = labels ? Theme.S(248) : dots ? Theme.S(104) : 0;
            int left = Theme.S(16), right = Theme.S(16);
            int textWidth = Math.Max(0, Width - left - right - stagesWidth - (stagesWidth > 0 ? Theme.S(18) : 0));
            TextRenderer.DrawText(g, Heading, titleFont,
                new Rectangle(left, Theme.S(13), textWidth, Theme.S(24)), Theme.Ink,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Detail, smallFont,
                new Rectangle(left, Theme.S(41), textWidth, Theme.S(19)), Theme.Muted,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (stagesWidth == 0) return;
            int sx = Width - right - stagesWidth;
            int selected = Selected;
            if (selected < 0)
            {
                string status = State == PetState.Resting ? "休息中" : State == PetState.Waiting ? "等待确认" : "需留意";
                Color ink = State == PetState.Error ? Theme.Accent : Theme.Muted;
                TextRenderer.DrawText(g, status, smallFont,
                    new Rectangle(sx, Theme.S(23), stagesWidth, Theme.S(26)), ink,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                return;
            }
            int cell = stagesWidth / 4;
            int cy = labels ? Theme.S(25) : Height / 2;
            int dot = Theme.S(6);
            string[] names = { "保存", "同步", "搬家", "继续" };
            float mx = sx + cell * marker + cell / 2f;
            using (var pen = new Pen(Theme.Line, Math.Max(1f, Theme.Dpi / 96f)))
                g.DrawLine(pen, sx + cell / 2, cy, sx + cell * 3 + cell / 2, cy);
            // The stretch already covered is tinted, so the row reads as progress rather than four dots.
            if (mx > sx + cell / 2f)
                using (var pen = new Pen(Theme.Mix(Theme.Accent, Theme.Line, .45f), Math.Max(1.5f, Theme.Dpi / 64f)))
                    g.DrawLine(pen, sx + cell / 2f, cy, mx, cy);
            for (int i = 0; i < 4; i++)
            {
                int cx = sx + cell * i + cell / 2;
                using (var brush = new SolidBrush(i < selected ? Theme.Mix(Theme.Accent, Theme.Line, .45f) : Theme.Line))
                    g.FillEllipse(brush, cx - dot / 2f, cy - dot / 2f, dot, dot);
                if (labels)
                    TextRenderer.DrawText(g, names[i], smallFont,
                        new Rectangle(sx + cell * i, Theme.S(40), cell, Theme.S(20)),
                        i == selected ? Theme.Accent : Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            bool moving = State == PetState.Packing || State == PetState.Syncing || State == PetState.Walking;
            float alpha = moving ? (float)(.5 + .5 * Math.Sin(frame * .07)) : 0;
            using (var halo = new SolidBrush(Color.FromArgb(25 + (int)(alpha * 20), Theme.Accent)))
                g.FillEllipse(halo, mx - Theme.S(7), cy - Theme.S(7), Theme.S(14), Theme.S(14));
            using (var brush = new SolidBrush(Theme.Accent))
                g.FillEllipse(brush, mx - dot / 2f, cy - dot / 2f, dot, dot);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); smallFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    class NavButton : Button
    {
        bool selected, hover, pressed;
        string iconKey = "accounts", countText = string.Empty;
        readonly Font countFont = new Font(Theme.SansFamily, 8.5f);
        readonly Font navFont = new Font(Theme.SansFamily, 9.5f);

        public bool Selected
        {
            get { return selected; }
            set { if (selected == value) return; selected = value; AccessibleDescription = value ? "当前页面" : string.Empty; Invalidate(); }
        }
        public string IconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; Invalidate(); } }
        public string Glyph { get { return IconKey; } set { IconKey = value; } }
        public string CountText { get { return countText; } set { countText = value ?? string.Empty; Invalidate(); } }
        // Marks an entry that opens a folder or file outside the panel instead of a view inside it.
        public bool External { get { return external; } set { external = value; if (value) AccessibleDescription = "在资源管理器中打开"; Invalidate(); } }
        bool external;

        public NavButton() : this(string.Empty, "accounts") { }
        public NavButton(string text, string icon) : this(text, icon, false) { }
        public NavButton(string text, string icon, bool isSelected)
        {
            Text = text;
            iconKey = icon ?? string.Empty;
            selected = isSelected;
            Size = new Size(Theme.S(140), Theme.S(38));
            Font = navFont;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            TextAlign = ContentAlignment.MiddleLeft;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleDescription = selected ? "当前页面" : string.Empty;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { pressed = false; Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = false; Invalidate(); } base.OnKeyUp(e); }

        void DrawIcon(Graphics g, Rectangle r, Color color)
        {
            if (string.IsNullOrEmpty(iconKey)) return;
            var state = g.Save();
            g.TranslateTransform(r.X, r.Y);
            g.ScaleTransform(r.Width / 16f, r.Height / 16f);
            using (var pen = new Pen(color, 1.2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                string key = iconKey.ToLowerInvariant();
                if (key == "memory" || key == "book" || key == "skills" || key == "knowledge")
                {
                    g.DrawRectangle(pen, 1.5f, 2, 13, 12);
                    g.DrawLine(pen, 8, 2, 8, 14);
                    g.DrawLine(pen, 3.5f, 5, 6, 5);
                    g.DrawLine(pen, 10, 5, 12.5f, 5);
                }
                else if (key == "relay" || key == "route" || key == "handoff")
                {
                    g.DrawEllipse(pen, 1, 1, 4, 4);
                    g.DrawEllipse(pen, 11, 11, 4, 4);
                    g.DrawLine(pen, 5, 3, 10, 3);
                    g.DrawArc(pen, 8, 3, 5, 5, 270, 180);
                    g.DrawLine(pen, 10, 8, 5, 8);
                    g.DrawArc(pen, 2, 8, 5, 5, 90, 180);
                    g.DrawLine(pen, 5, 13, 11, 13);
                }
                else if (key == "sync" || key == "refresh" || key == "history")
                {
                    g.DrawArc(pen, 2, 2, 12, 12, 30, 145);
                    g.DrawArc(pen, 2, 2, 12, 12, 210, 145);
                    g.DrawLines(pen, new[] { new PointF(1, 6), new PointF(2, 9), new PointF(5, 7) });
                    g.DrawLines(pen, new[] { new PointF(11, 9), new PointF(14, 7), new PointF(15, 10) });
                }
                else if (key == "statistics")
                {
                    g.DrawLine(pen, 1, 14, 15, 14);
                    g.DrawRectangle(pen, 2, 8, 2.5f, 6);
                    g.DrawRectangle(pen, 6.75f, 5, 2.5f, 9);
                    g.DrawRectangle(pen, 11.5f, 1, 2.5f, 13);
                }
                else if (key == "pet")
                {
                    g.SmoothingMode = SmoothingMode.None;
                    using (var brush = new SolidBrush(color))
                    {
                        g.FillRectangle(brush, 3, 4, 10, 8);
                        g.FillRectangle(brush, 1, 8, 3, 3);
                        g.FillRectangle(brush, 12, 8, 3, 3);
                        g.FillRectangle(brush, 4, 12, 2, 3);
                        g.FillRectangle(brush, 10, 12, 2, 3);
                    }
                    using (var brush = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor))
                    { g.FillRectangle(brush, 5, 6, 1, 2); g.FillRectangle(brush, 10, 6, 1, 2); }
                }
                else if (key == "settings" || key == "sliders")
                {
                    g.DrawLine(pen, 1, 4, 15, 4);
                    g.DrawLine(pen, 1, 12, 15, 12);
                    using (var brush = new SolidBrush(selected ? Theme.Hover : Parent == null ? Theme.Bg : Parent.BackColor))
                    { g.FillRectangle(brush, 4, 2, 3, 4); g.FillRectangle(brush, 9, 10, 3, 4); }
                    g.DrawRectangle(pen, 4, 2, 3, 4);
                    g.DrawRectangle(pen, 9, 10, 3, 4);
                }
                else
                {
                    g.DrawRectangle(pen, 1, 1, 5, 5);
                    g.DrawRectangle(pen, 10, 1, 5, 5);
                    g.DrawRectangle(pen, 1, 10, 5, 5);
                    g.DrawRectangle(pen, 10, 10, 5, 5);
                }
            }
            g.Restore(state);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color background = Parent == null ? Theme.Bg : Parent.BackColor;
            g.Clear(background);
            if (Width <= Theme.S(8) || Height <= Theme.S(8)) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Derived from the sidebar's own colour: Theme.Hover is nearly identical to it in the light theme.
            Color selectedFill = Theme.Mix(background, Theme.Ink, Theme.Dark ? .09f : .06f);
            Color fill = selected ? selectedFill : hover && Enabled ? Theme.Mix(background, selectedFill, .6f) : background;
            if (pressed && Enabled) fill = Theme.Mix(fill, Theme.AccentSoft, .5f);
            Color ink = Enabled ? Theme.Ink : Theme.Muted;
            using (var shape = Theme.Round(new RectangleF(1, 1, Width - 2, Height - 2), Theme.S(7)))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, shape);
                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(Theme.Accent, Math.Max(1.2f, Theme.Dpi / 80f))) g.DrawPath(pen, shape);
            }
            int iconSize = Theme.S(16), inset = Theme.S(11);
            if (selected)
            {
                // The current view carries a short accent bar on its leading edge.
                float barHeight = Theme.S(16), barWidth = Theme.S(3);
                using (var bar = Theme.Round(new RectangleF(Theme.S(2), (Height - barHeight) / 2f, barWidth, barHeight), barWidth / 2f))
                using (var brush = new SolidBrush(Enabled ? Theme.Accent : Theme.Muted)) g.FillPath(brush, bar);
            }
            DrawIcon(g, new Rectangle(inset, (Height - iconSize) / 2, iconSize, iconSize),
                selected && Enabled ? Theme.Accent : Theme.Muted);
            int left = string.IsNullOrEmpty(iconKey) ? inset : Theme.S(37);
            int countWidth = string.IsNullOrEmpty(countText) ? 0 : Math.Min(Theme.S(40), Math.Max(Theme.S(20),
                TextRenderer.MeasureText(g, countText, countFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(12)));
            int markWidth = external && countWidth == 0 ? Theme.S(14) : 0;
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(left, 0, Math.Max(0, Width - left - inset - countWidth - markWidth), Height), ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (countWidth > 0)
            {
                int badgeHeight = Theme.S(18);
                var badge = new Rectangle(Width - inset - countWidth + Theme.S(2), (Height - badgeHeight) / 2, countWidth, badgeHeight);
                using (var shape = Theme.Round(badge, badgeHeight / 2f))
                using (var brush = new SolidBrush(Theme.Mix(fill, Theme.Muted, Theme.Dark ? .24f : .14f))) g.FillPath(brush, shape);
                TextRenderer.DrawText(g, countText, countFont, badge, Enabled ? Theme.Ink : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            if (markWidth > 0)
            {
                // A small "opens outside" arrow: faint at rest, clearer under the pointer or focus.
                float size = Theme.S(7), ax = Width - inset - size + Theme.S(1), ay = (Height - size) / 2f;
                Color mark = Theme.Mix(fill, Theme.Muted, hover || Focused ? 1f : .55f);
                using (var pen = new Pen(mark, Math.Max(1.2f, Theme.Dpi / 80f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLine(pen, ax, ay + size, ax + size, ay);
                    g.DrawLines(pen, new[] { new PointF(ax + size * .3f, ay), new PointF(ax + size, ay), new PointF(ax + size, ay + size * .7f) });
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { countFont.Dispose(); navFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
