using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    enum PetState { Working, Packing, Syncing, Walking, Waiting, Error, Resting }

    // One resident, drawn in host coordinates by both the host and its cards. This lets the
    // sprite cross card boundaries without a top-level overlay or intercepting mouse clicks.
    class PetScene
    {
        public string HomeId, TargetId;
        public PetState State = PetState.Working;
        public int Frame;
        public float Travel;
        public DateTime WalkStarted;
        public void Begin(string id) { TargetId = id; State = PetState.Packing; Travel = 0; }
        public void Walk() { State = PetState.Walking; Travel = 0; WalkStarted = DateTime.UtcNow; }
        public void Settle(string id, bool loggedIn)
        { HomeId = id; TargetId = null; State = loggedIn ? PetState.Working : PetState.Waiting; Travel = 0; }

        public static void Desk(Graphics g, int x, int y, bool occupied, int frame)
        { Desk(g, x, y, occupied, frame, null); }

        public static void Desk(Graphics g, int x, int y, bool occupied, int frame, PetInteraction interaction)
        {
            var save = g.Save();
            g.TranslateTransform(x, y); g.ScaleTransform(Theme.Dpi / 96f, Theme.Dpi / 96f);
            // A small pool of light belongs to the workstation, not to a floating overlay.
            if (occupied)
            {
                float pulse = (float)(0.5 + 0.5 * Math.Sin(frame * 0.018));
                using (var glow = new SolidBrush(Color.FromArgb(18 + (int)(pulse * 6), Theme.Accent)))
                    g.FillEllipse(glow, 15, 4, 62, 34);
            }
            using (var floor = new SolidBrush(Color.FromArgb(Theme.Dark ? 28 : 9, Theme.Ink))) g.FillEllipse(floor, 3, 39, 72, 8);
            Pixel(g, Theme.Line, 6, 31, 63, 3); Pixel(g, Theme.Line, 10, 34, 3, 9); Pixel(g, Theme.Line, 62, 34, 3, 9);
            Pixel(g, Theme.Dark ? Theme.Muted : Color.FromArgb(218, 195, 169), 6, 31, 63, 2);
            Pixel(g, Theme.Muted, 43, 11, 24, 17); Pixel(g, Theme.Surface, 45, 13, 20, 12);
            Pixel(g, occupied ? Theme.AccentSoft : Theme.Hover, 46, 14, 18, 10);
            int workBeat = frame % 180;
            int textWidth = occupied ? 7 + Math.Min(6, workBeat / 18) : 9;
            Pixel(g, occupied ? Theme.Accent : Theme.Line, 48, 17, textWidth, 2);
            Pixel(g, Theme.Line, 48, 21, occupied ? 7 + Math.Min(5, workBeat / 24) : 13, 1);
            if (occupied && workBeat < 124 && frame / 18 % 2 == 0)
                Pixel(g, Theme.Muted, 49 + textWidth, 17, 1, 2);
            Pixel(g, Theme.Muted, 53, 28, 4, 3);
            Pixel(g, Theme.Line, 36, 30, 15, 1);
            // Coffee and a tiny plant keep empty desks recognisable too.
            bool cupOnDesk = interaction == null || !interaction.IsTea;
            if (cupOnDesk)
            {
                Pixel(g, Theme.AccentSoft, 69, 24, 6, 7); Pixel(g, Theme.Accent, 70, 25, 4, 5);
                Pixel(g, Theme.Muted, 75, 25, 2, 4); Pixel(g, Theme.Surface, 75, 26, 1, 2);
            }
            Pixel(g, Theme.Dark ? Theme.Muted : Color.FromArgb(143, 162, 133), 3, 21, 2, 8);
            Pixel(g, Theme.Dark ? Theme.Muted : Color.FromArgb(143, 162, 133), 0, 20, 4, 3);
            Pixel(g, Theme.Dark ? Theme.Muted : Color.FromArgb(143, 162, 133), 4, 17, 4, 4);
            Pixel(g, Theme.AccentSoft, 0, 28, 8, 4);
            if (occupied && cupOnDesk)
            {
                Steam(g, 71, 21, frame, 58);
            }
            g.Restore(save);
        }

        static void Pixel(Graphics g, Color c, float x, float y, float w, float h)
        { using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, w, h); }

        static void Steam(Graphics g, int x, int y, int frame, int opacity)
        {
            // Fade each wisp in and out before the next one rises; no hard frame-to-frame flicker.
            int beat = frame % 96;
            int alpha = (int)(opacity * Math.Sin(beat * Math.PI / 96));
            int rise = beat / 16;
            Pixel(g, Color.FromArgb(alpha, Theme.Muted), x, y - rise, 1, 2);
            Pixel(g, Color.FromArgb(alpha / 2, Theme.Muted), x + 2, y - 2 - rise / 2, 1, 2);
        }

        static void Check(Graphics g, Color color, int x, int y)
        {
            Pixel(g, color, x, y + 2, 2, 2);
            Pixel(g, color, x + 2, y + 4, 2, 2);
            Pixel(g, color, x + 4, y + 2, 2, 2);
            Pixel(g, color, x + 6, y, 2, 2);
        }

        static void Suitcase(Graphics g, PetState state, int frame, int bob, Color body, Color shade, Color light, Color eye)
        {
            bool walking = state == PetState.Walking;
            bool packing = state == PetState.Packing;
            int beat = frame % 160;
            bool open = packing && beat < 122;
            var save = g.Save();
            // Keep the wheels on the floor while the walking body rises one pixel.
            if (walking) g.TranslateTransform(0, bob);
            if (!open)
            {
                Pixel(g, Theme.Muted, 39, 16, 9, 2);
                Pixel(g, Theme.Muted, 39, 18, 2, 5); Pixel(g, Theme.Muted, 46, 18, 2, 5);
            }
            else
            {
                Pixel(g, eye, 36, 21, 16, 4);
                if (beat >= 24 && beat < 96)
                {
                    int drop = Math.Min(18, (beat - 24) / 4);
                    Pixel(g, Theme.Muted, 40, 4 + drop, 9, 11);
                    Pixel(g, Theme.Surface, 41, 5 + drop, 7, 9);
                    Pixel(g, Theme.Accent, 42, 7 + drop, 4, 1);
                    Pixel(g, Theme.Line, 42, 10 + drop, 4, 1);
                }
            }
            // The front hides the paper as it settles inside the case.
            Pixel(g, shade, 35, 23, 18, 9); Pixel(g, body, 37, 24, 14, 7);
            Pixel(g, light, 42, 24, 3, 7); Pixel(g, shade, 42, 26, 3, 2);
            int lidY = !open ? 21 : beat < 20 ? 20 - beat / 4 : beat < 92 ? 15 : 15 + (beat - 92) / 5;
            Pixel(g, shade, 35, lidY, 18, 3); Pixel(g, light, 37, lidY, 14, 1);
            if (open)
            {
                Pixel(g, shade, 35, lidY + 3, 2, 23 - lidY - 3);
                Pixel(g, shade, 51, lidY + 3, 2, 23 - lidY - 3);
            }
            Pixel(g, eye, 37, 32, 3, 3); Pixel(g, eye, 48, 32, 3, 3);
            if (walking)
            {
                int spoke = frame / 7 % 2;
                Pixel(g, Theme.Muted, 38, 32 + spoke, 1, 1);
                Pixel(g, Theme.Muted, 49, 33 - spoke, 1, 1);
            }
            if (packing && beat >= 132)
                Check(g, Theme.Green, 52, 16);
            if (state == PetState.Syncing)
            {
                int verify = frame % 180;
                for (int i = 0; i < 3; i++)
                    Pixel(g, verify >= 24 + i * 25 ? Theme.Green : Theme.Line, 38 + i * 5, 11, 3, 2);
                if (verify >= 90) Check(g, Theme.Green, 40, 2);
                if (verify >= 110 && verify < 166)
                {
                    int alpha = (int)(125 * Math.Sin((verify - 110) * Math.PI / 56));
                    Color glint = Color.FromArgb(alpha, Theme.Accent);
                    Pixel(g, glint, 56, 17, 1, 5); Pixel(g, glint, 54, 19, 5, 1);
                }
            }
            g.Restore(save);
        }

        public static void Sprite(Graphics g, float x, float y, PetState state, int frame, bool faceLeft)
        { Sprite(g, x, y, state, frame, faceLeft, null); }

        public static void Sprite(Graphics g, float x, float y, PetState state, int frame, bool faceLeft, PetInteraction interaction)
        {
            PetReaction reaction = interaction == null ? PetReaction.None : interaction.Reaction;
            int reactionFrame = interaction == null ? 0 : interaction.ReactionFrame;
            // A gesture changes only this draw call, never the caller's work/rest state.
            if (reaction == PetReaction.Tea) state = PetState.Resting;
            else if (reaction == PetReaction.Pat || reaction == PetReaction.Wave) state = PetState.Waiting;
            var save = g.Save();
            g.TranslateTransform(x, y); g.ScaleTransform(Theme.Dpi / 96f, Theme.Dpi / 96f);
            bool walk = state == PetState.Walking;
            bool bag = walk || state == PetState.Packing || state == PetState.Syncing;
            int step = frame / 7 % 2;
            int workBeat = frame % 180;
            bool thinking = state == PetState.Working && workBeat >= 124 && workBeat < 166;
            int restBeat = frame % 240, restActivity = frame / 240 % 3;
            if (reaction == PetReaction.Tea) { restActivity = 0; restBeat = 90 + (int)(reactionFrame * 1.1f); }
            int bob = walk ? step : state == PetState.Resting && restActivity == 2 && restBeat >= 80 && restBeat < 160 ? 1 : 0;
            using (var shadow = new SolidBrush(Color.FromArgb(24, Theme.Ink))) g.FillEllipse(shadow, 2, 31, bag ? 53 : 34, 5);
            if (faceLeft) { g.TranslateTransform(34, 0); g.ScaleTransform(-1, 1); }
            g.TranslateTransform(0, -bob);
            if (reaction == PetReaction.Pat && reactionFrame < 18)
                g.TranslateTransform(0, -(float)Math.Sin(reactionFrame / 18.0 * Math.PI) * 2.3f);
            var body = Color.FromArgb(218, 127, 91);
            var shade = Color.FromArgb(183, 93, 63);
            var light = Color.FromArgb(238, 161, 123);
            var eye = Color.FromArgb(47, 39, 34);
            Pixel(g, body, 7, 8, 23, 19); Pixel(g, body, 3, 14, 5, 11);
            Pixel(g, light, 8, 8, 20, 3); Pixel(g, shade, 3, 23, 5, 3);
            Pixel(g, body, 0, 21, 5, 5); Pixel(g, light, 0, 21, 3, 2);
            bool sleeping = state == PetState.Resting && restActivity == 2;
            bool sipping = state == PetState.Resting && restActivity == 0 && restBeat >= 120 && restBeat < 154;
            bool blink = frame % 160 >= 155 || sleeping || sipping;
            int gaze = thinking ? -1 : (bag || (state == PetState.Waiting && frame % 220 < 130)) ? 1 : 0;
            if (state == PetState.Error) gaze = frame / 70 % 2 == 0 ? -1 : 0;
            float gazeX = interaction == null ? gaze : blink ? 0 : interaction.LookX * (faceLeft ? -1 : 1);
            float gazeY = interaction == null || blink ? 0 : interaction.LookY;
            if (reaction == PetReaction.Pat && reactionFrame < 35)
            {
                Pixel(g, eye, 12, 16, 1, 1); Pixel(g, eye, 13, 15, 3, 1); Pixel(g, eye, 16, 16, 1, 1);
                Pixel(g, eye, 23, 16, 1, 1); Pixel(g, eye, 24, 15, 3, 1); Pixel(g, eye, 27, 16, 1, 1);
                Pixel(g, shade, 9, 21, 3, 1); Pixel(g, shade, 26, 21, 3, 1);
            }
            else
            {
                Pixel(g, eye, 13 + gazeX, blink ? 17 : 15 + gazeY, 3, blink ? 1 : 4);
                Pixel(g, eye, 24 + gazeX, blink ? 17 : 15 + gazeY, 3, blink ? 1 : 4);
            }
            Pixel(g, light, 17, 23, 3, 2);
            Pixel(g, body, 8 + (walk ? step * 2 : 0), 27, 4, 5);
            Pixel(g, body, 18 - (walk ? step * 2 : 0), 27, 3, 5);
            Pixel(g, body, 27 + (walk ? step : 0), 26, 3, 6);
            Pixel(g, shade, 6 + (walk ? step * 2 : 0), 31, 6, 2);
            Pixel(g, shade, 25 + (walk ? step : 0), 31, 5, 2);
            if (state == PetState.Working)
            {
                if (thinking)
                {
                    Pixel(g, body, 28, 20, 5, 3); Pixel(g, light, 30, 17, 3, 4);
                    Pixel(g, Theme.Muted, 25, 4, 2, 2);
                    if (workBeat >= 140) Pixel(g, Theme.Muted, 29, 1, 3, 2);
                }
                else
                {
                    int tap = workBeat < 110 ? step : 0;
                    Pixel(g, body, 29, 24 - tap, 5, 3); Pixel(g, shade, 32, 26 - tap, 2, 1);
                    Pixel(g, light, 25, 25 + tap, 4, 2);
                }
            }
            if (state == PetState.Resting)
            {
                if (restActivity == 1)
                {
                    Pixel(g, Theme.Muted, 14, 23, 20, 10);
                    Pixel(g, Theme.Surface, 15, 23, 8, 8); Pixel(g, Theme.Surface, 25, 23, 8, 8);
                    Pixel(g, Theme.AccentSoft, 24, 24, 1, 8);
                    Pixel(g, Theme.Line, 17, 25, 4, 1); Pixel(g, Theme.Line, 27, 25, 4, 1);
                    Pixel(g, Theme.Line, 17, 28, 5, 1); Pixel(g, Theme.Line, 27, 28, 4, 1);
                    if (restBeat >= 158 && restBeat < 194)
                    {
                        int pageX = 31 - (restBeat - 158) / 3;
                        Pixel(g, Theme.AccentSoft, pageX, 22, 2, 9);
                        Pixel(g, Theme.Surface, pageX + 2, 23, 1, 7);
                    }
                    Pixel(g, light, 13, 27, 3, 3); Pixel(g, light, 31, 27, 3, 3);
                }
                else if (restActivity == 0)
                {
                    int lift = restBeat < 104 || restBeat >= 178 ? 0 : restBeat < 120 ? (restBeat - 104) / 4 : restBeat < 158 ? 4 : (177 - restBeat) / 5;
                    Pixel(g, body, 25, 25 - lift, 8, 3);
                    Pixel(g, Theme.Surface, 27, 21 - lift, 6, 7);
                    Pixel(g, Theme.Muted, 28, 21 - lift, 4, 1);
                    Pixel(g, Theme.Surface, 33, 22 - lift, 1, 4);
                    Pixel(g, light, 26, 25 - lift, 2, 2);
                    Steam(g, 29, 18 - lift, frame, sipping ? 28 : 60);
                }
                else
                {
                    Pixel(g, light, 21, 25, 9, 2); Pixel(g, shade, 21, 27, 6, 1);
                    int breath = frame % 120;
                    Color nap = Color.FromArgb(45 + (int)(45 * Math.Sin(breath * Math.PI / 120)), Theme.Muted);
                    Pixel(g, nap, 28, 2, 5, 1); Pixel(g, nap, 31, 3, 1, 1);
                    Pixel(g, nap, 29, 4, 1, 1); Pixel(g, nap, 28, 5, 5, 1);
                }
            }
            if (bag)
            {
                Suitcase(g, state, frame, bob, body, shade, light, eye);
                int reach = state == PetState.Packing && frame % 160 >= 24 && frame % 160 < 96 ? 3 : 0;
                Pixel(g, body, 29, 22 - reach, 8, 3);
                Pixel(g, light, 35, 21 - reach, 3, 3);
                if (walk) Pixel(g, body, 37, 18, 3, 5);
            }
            if (state == PetState.Waiting)
            {
                int waitBeat = frame % 220;
                if (reaction == PetReaction.Wave) waitBeat = 132 + reactionFrame % 36;
                else if (reaction == PetReaction.Pat) waitBeat = 0;
                if (waitBeat >= 132 && waitBeat < 180)
                {
                    int wave = (waitBeat - 132) / 12 % 2;
                    Pixel(g, body, 29, 18, 3, 6); Pixel(g, light, 31, 13 + wave * 2, 3, 6);
                }
                else Pixel(g, body, 29, 24, 4, 3);
            }
            if (state == PetState.Error)
            {
                // A puzzled question mark and a slow temple tap, rather than an alarm flash.
                Pixel(g, shade, 17, 0, 5, 1); Pixel(g, shade, 21, 1, 2, 2);
                Pixel(g, shade, 19, 3, 3, 1); Pixel(g, shade, 19, 5, 2, 1);
                Pixel(g, shade, 12, 12, 4, 1); Pixel(g, shade, 25, 13, 3, 1);
                Pixel(g, eye, 20, 23, 2, 1);
                if (frame % 180 >= 48 && frame % 180 < 104)
                {
                    Pixel(g, body, 4, 16, 4, 7); Pixel(g, light, 5, 13, 3, 4);
                }
            }
            if (reaction == PetReaction.Pat)
                for (int i = 0; i < 2; i++)
                {
                    float age = (reactionFrame - i * 6) / 44f;
                    if (age <= 0 || age >= 1) continue;
                    Color heart = Color.FromArgb((int)(Math.Sin(age * Math.PI) * 190), Theme.Accent);
                    float hx = 11 + i * 15, hy = 5 - age * 7;
                    Pixel(g, heart, hx + 1, hy, 2, 1); Pixel(g, heart, hx + 4, hy, 2, 1);
                    Pixel(g, heart, hx, hy + 1, 7, 2); Pixel(g, heart, hx + 1, hy + 3, 5, 1);
                    Pixel(g, heart, hx + 2, hy + 4, 3, 1); Pixel(g, heart, hx + 3, hy + 5, 1, 1);
                }
            g.Restore(save);
        }
    }

    class OfficeHost : FlowLayoutPanel
    {
        public Action<Graphics> PaintTraveler;
        public OfficeHost() { DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); if (PaintTraveler != null) PaintTraveler(e.Graphics); }
    }

    // Presentation only. Foreground/activity decisions remain in the panel; this view neither
    // changes accounts nor pauses work, and it draws one pet only while the lounge is occupied.
    class LoungeBar : Control
    {
        bool resting;
        int frame;
        string detail = "小克正在工位忙碌。";
        readonly Font headingFont = new Font(Theme.SansFamily, 10.5f, FontStyle.Bold);
        readonly Font detailFont = new Font(Theme.SansFamily, 8.5f);
        readonly Font tagFont = new Font(Theme.SansFamily, 7.5f, FontStyle.Bold);
        public LoungeBar()
        {
            Height = Theme.S(86);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleName = "休息区。小克正在工位忙碌。";
        }
        public void Update(bool isResting, int animationFrame, string description)
        {
            resting = isResting; frame = animationFrame;
            detail = !resting ? "小克正在工位忙碌。忙完了，来这里喝口茶。" :
                (string.IsNullOrWhiteSpace(description) ? "喝口茶、翻翻书，等你回来继续办公。" : description);
            AccessibleName = "休息区。" + (resting ? "小克正在休息。" : "小克正在工位忙碌。") + detail;
            Invalidate();
        }
        static void Rect(Graphics g, Color color, int x, int y, int w, int h)
        { using (var brush = new SolidBrush(color)) g.FillRectangle(brush, x, y, w, h); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < Theme.S(230)) return;
            Color leaf = Theme.Dark ? Color.FromArgb(123, 153, 123) : Color.FromArgb(110, 143, 103);
            Color fill = Theme.Mix(Theme.Surface, leaf, Theme.Dark ? 0.09f : 0.055f);
            var area = new RectangleF(1, 1, Width - 2, Height - 3);
            using (var shape = Theme.Round(area, Theme.S(14)))
            {
                using (var brush = new LinearGradientBrush(area, fill, Theme.Surface, 0f)) g.FillPath(brush, shape);
                using (var border = new Pen(Theme.Mix(Theme.Line, leaf, resting ? 0.30f : 0.08f))) g.DrawPath(border, shape);
            }
            var save = g.Save(); g.TranslateTransform(Theme.S(14), Theme.S(7)); g.ScaleTransform(Theme.Dpi / 96f, Theme.Dpi / 96f);
            using (var shadow = new SolidBrush(Color.FromArgb(Theme.Dark ? 24 : 10, Theme.Ink))) g.FillEllipse(shadow, 12, 57, 119, 12);
            Color sofa = Theme.Mix(leaf, Theme.Surface, Theme.Dark ? 0.33f : 0.48f), edge = Theme.Mix(leaf, Theme.Muted, 0.25f);
            Rect(g, edge, 25, 54, 4, 10); Rect(g, edge, 76, 54, 4, 10);
            Rect(g, sofa, 22, 29, 62, 30); Rect(g, Theme.Mix(sofa, Theme.Surface, 0.28f), 26, 31, 54, 16);
            Rect(g, edge, 18, 42, 8, 17); Rect(g, edge, 80, 42, 8, 17);
            Rect(g, Theme.Mix(sofa, Theme.Surface, 0.18f), 27, 49, 25, 8); Rect(g, Theme.Mix(sofa, Theme.Surface, 0.18f), 54, 49, 25, 8);
            // The little side table and plant remain when the lounge is empty.
            Rect(g, Theme.Line, 98, 49, 28, 3); Rect(g, Theme.Line, 101, 52, 3, 12); Rect(g, Theme.Line, 121, 52, 3, 12);
            Rect(g, Theme.AccentSoft, 107, 42, 9, 7); Rect(g, Theme.Accent, 108, 43, 7, 5);
            Rect(g, Theme.Muted, 116, 43, 2, 4);
            Rect(g, leaf, 4, 31, 2, 22); Rect(g, leaf, 0, 29, 6, 4); Rect(g, leaf, 6, 23, 6, 6);
            Rect(g, Theme.AccentSoft, 0, 51, 12, 11); Rect(g, Theme.Line, 1, 61, 10, 2);
            if (resting)
            {
                int steam = frame / 8 % 9;
                Rect(g, Color.FromArgb(75 - steam * 6, Theme.Muted), 109 + frame / 24 % 2, 36 - steam, 1, 4);
                Rect(g, Color.FromArgb(45, Theme.Muted), 113, 35 - steam / 2, 1, 3);
            }
            g.Restore(save);
            if (resting) PetScene.Sprite(g, Theme.S(50), Theme.S(20), PetState.Resting, frame, false);
            string title = resting ? "小克的休息时间" : "休息区";
            int tx = Theme.S(161), right = Theme.S(18);
            TextRenderer.DrawText(g, title, headingFont, new Rectangle(tx, Theme.S(17), Math.Max(0, Width - tx - Theme.S(116)), Theme.S(24)), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, detail, detailFont, new Rectangle(tx, Theme.S(46), Math.Max(0, Width - tx - right), Theme.S(20)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            string badge = resting ? "TEA BREAK" : "LOUNGE";
            TextRenderer.DrawText(g, badge, tagFont, new Rectangle(Width - Theme.S(104), Theme.S(20), Theme.S(86), Theme.S(18)), resting ? leaf : Theme.Muted, TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { headingFont.Dispose(); detailFont.Dispose(); tagFont.Dispose(); } base.Dispose(disposing); }
    }

    class JourneyBar : Control
    {
        public string Heading = "小克准备上班了", Detail = "选择一个账号，小克会带着进度一起过去。";
        public PetState State;
        readonly Font titleFont = new Font(Theme.SansFamily, 11f, FontStyle.Bold);
        readonly Font smallFont = new Font(Theme.SansFamily, 8.5f);
        readonly Font numberFont = new Font(Theme.SansFamily, 7.5f, FontStyle.Bold);
        int frame;
        float marker = 3;
        public JourneyBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(106); AccessibleName = "小克的工作状态";
        }
        public void Update(string heading, string detail, PetState state)
        { Heading = heading; Detail = detail; State = state; AccessibleName = heading + "。" + detail; Invalidate(); }
        int Selected
        { get { return State == PetState.Packing ? 0 : State == PetState.Syncing ? 1 : State == PetState.Walking ? 2 : State == PetState.Working ? 3 : -1; } }
        public void AdvanceFrame(int value)
        {
            frame = value;
            int selected = Selected;
            if (selected >= 0) marker += (selected - marker) * 0.24f;
            Invalidate();
        }
        static Color Mix(Color a, Color b, float amount)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < Theme.S(40) || Height < Theme.S(40)) return;
            var bounds = new RectangleF(1, 1, Width - 2, Height - 3);
            Color left = Mix(Theme.AccentSoft, Theme.Surface, 0.12f), right = Mix(Theme.AccentSoft, Theme.Surface, 0.75f);
            using (var shape = Theme.Round(bounds, Theme.S(16)))
            {
                using (var fill = new LinearGradientBrush(bounds, left, right, 5f)) g.FillPath(fill, shape);
                var save = g.Save(); g.SetClip(shape);
                using (var halo = new SolidBrush(Color.FromArgb(Theme.Dark ? 9 : 28, Theme.Accent)))
                    g.FillEllipse(halo, Width - Theme.S(132), -Theme.S(72), Theme.S(180), Theme.S(180));
                using (var fine = new Pen(Color.FromArgb(Theme.Dark ? 18 : 38, Theme.Accent), Theme.S(1)))
                    g.DrawEllipse(fine, Width - Theme.S(153), -Theme.S(83), Theme.S(210), Theme.S(210));
                g.Restore(save);
                using (var outline = new Pen(Mix(Theme.Line, Theme.Accent, 0.14f))) g.DrawPath(outline, shape);
            }
            TextRenderer.DrawText(g, Heading, titleFont, new Rectangle(Theme.S(18), Theme.S(14), Math.Max(0, Width - Theme.S(144)), Theme.S(23)), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Detail, smallFont, new Rectangle(Theme.S(18), Theme.S(42), Width - Theme.S(36), Theme.S(19)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            string stateText = State == PetState.Working ? "办公中" : State == PetState.Packing ? "准备中" : State == PetState.Syncing ? "同步中" : State == PetState.Walking ? "搬家中" : State == PetState.Resting ? "休息中" : State == PetState.Waiting ? "待确认" : "需留意";
            Color stateColor = State == PetState.Working ? (Theme.Dark ? Color.FromArgb(151, 186, 153) : Color.FromArgb(82, 119, 88)) : Theme.Accent;
            RectangleF badge = new RectangleF(Width - Theme.S(106), Theme.S(14), Theme.S(88), Theme.S(24));
            using (var capsule = Theme.Round(badge, Theme.S(12)))
            using (var fill = new SolidBrush(Color.FromArgb(Theme.Dark ? 35 : 160, Theme.Surface))) g.FillPath(fill, capsule);
            float pulse = (float)(0.5 + 0.5 * Math.Sin(frame * 0.055));
            float dotX = badge.X + Theme.S(13), dotY = badge.Y + Theme.S(12);
            using (var glow = new SolidBrush(Color.FromArgb(16 + (int)(pulse * 28), stateColor)))
                g.FillEllipse(glow, dotX - Theme.S(6), dotY - Theme.S(6), Theme.S(12), Theme.S(12));
            using (var dot = new SolidBrush(stateColor)) g.FillEllipse(dot, dotX - Theme.S(3), dotY - Theme.S(3), Theme.S(6), Theme.S(6));
            TextRenderer.DrawText(g, stateText, smallFont, new Rectangle((int)badge.X + Theme.S(25), (int)badge.Y + Theme.S(3), Theme.S(57), Theme.S(19)), stateColor, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            string[] stages = { "收拾行李", "同步进度", "前往新号", "继续办公" };
            int selected = Selected, cell = (Width - Theme.S(36)) / 4;
            int chipWidth = Math.Min(Theme.S(112), cell - Theme.S(5)), chipY = Theme.S(70), chipHeight = Theme.S(25);
            if (selected >= 0)
            {
                using (var chip = Theme.Round(new RectangleF(Theme.S(13) + marker * cell, chipY, chipWidth, chipHeight), Theme.S(10)))
                using (var fill = new SolidBrush(Color.FromArgb(Theme.Dark ? 36 : 170, Theme.Surface))) g.FillPath(fill, chip);
            }
            for (int i = 0; i < stages.Length; i++)
            {
                int x = Theme.S(21) + i * cell, cy = chipY + chipHeight / 2;
                Color ink = i == selected ? Theme.Accent : Theme.Muted;
                if (i < stages.Length - 1)
                    using (var pen = new Pen(Color.FromArgb(Theme.Dark ? 90 : 160, Theme.Line), Theme.S(1)))
                        g.DrawLine(pen, x + Theme.S(91), cy, x + cell - Theme.S(11), cy);
                using (var circle = new SolidBrush(i == selected ? Theme.Accent : Mix(Theme.Line, Theme.Surface, 0.18f)))
                    g.FillEllipse(circle, x, cy - Theme.S(7), Theme.S(14), Theme.S(14));
                TextRenderer.DrawText(g, (i + 1).ToString(), numberFont, new Rectangle(x, cy - Theme.S(8), Theme.S(14), Theme.S(16)), i == selected ? Theme.OnAccent : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, stages[i], smallFont, new Rectangle(x + Theme.S(20), cy - Theme.S(10), Math.Max(0, chipWidth - Theme.S(27)), Theme.S(20)), ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) { titleFont.Dispose(); smallFont.Dispose(); numberFont.Dispose(); } base.Dispose(disposing); }
    }
}
