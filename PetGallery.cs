using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    // An independent animation viewer: it owns no Engine, profile or task state.
    sealed class PetGallery : Form
    {
        readonly PetGalleryCanvas canvas = new PetGalleryCanvas();
        readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer();
        readonly Label title, subtitle;
        public PetGallery()
        {
            Text = "小克的日常"; BackColor = Theme.Bg; ForeColor = Theme.Ink;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            ClientSize = new Size(Theme.S(788), Theme.S(398));
            MinimumSize = Size; MaximumSize = Size; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; KeyPreview = true;
            title = new Label { Text = "小克的日常", AutoSize = true, ForeColor = Theme.Ink, Font = new Font(Theme.SerifFamily, 19f), Location = new Point(Theme.S(24), Theme.S(18)) };
            subtitle = new Label { Text = "眼睛跟着鼠标看。点头摸摸、点身体招手，点工位的茶杯请它喝茶。", AutoSize = true, ForeColor = Theme.Muted, Font = new Font(Theme.SansFamily, 9f), Location = new Point(Theme.S(24), Theme.S(57)) };
            canvas.SetBounds(Theme.S(18), Theme.S(88), ClientSize.Width - Theme.S(36), Theme.S(290));
            Controls.AddRange(new Control[] { title, subtitle, canvas });
            animation.Interval = 40;
            animation.Tick += (s, e) => { if (Visible && WindowState != FormWindowState.Minimized) canvas.Advance(); };
            Shown += (s, e) => animation.Start();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; } };
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.TintCaption(Handle); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { animation.Stop(); animation.Dispose(); title.Font.Dispose(); subtitle.Font.Dispose(); }
            base.Dispose(disposing);
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
