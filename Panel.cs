// The account panel. All sync and process logic lives in ClaudeAccountSync.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace ClaudeAccountSync
{
    static class Theme
    {
        public static bool Dark;
        public static Color Bg, Surface, Ink, Muted, Line, Accent, AccentHover, AccentSoft, Hover, OnAccent;
        public static float Dpi = 96f;
        public static string SerifFamily, SansFamily;

        public static int S(int n) { return (int)Math.Round(n * Dpi / 96f); }
        public static Color Mix(Color a, Color b, float amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * amount), (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
        }
        public static Color Green { get { return Dark ? C(0x96BA9B) : C(0x507A5D); } }

        public static void Init(string force)
        {
            Dark = force == "dark";
            if (force == null)
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        Dark = k != null && Equals(k.GetValue("AppsUseLightTheme"), 0);
                }
                catch { }
            if (Dark)
            {
                Bg = C(0x262624); Surface = C(0x30302E); Ink = C(0xFAF9F5); Muted = C(0xA3A198); Line = C(0x45453F);
                Accent = C(0xD97757); AccentHover = C(0xE58A6C); AccentSoft = C(0x4A352C); Hover = C(0x3A3A37); OnAccent = C(0xFFFFFF);
            }
            else
            {
                // Muted keeps at least 4.5:1 against Bg, so 8.5pt secondary text stays readable.
                Bg = C(0xF8F6F0); Surface = C(0xFFFEFB); Ink = C(0x2D2A25); Muted = C(0x6B675E); Line = C(0xE5E0D5);
                Accent = C(0xC6613F); AccentHover = C(0xB25435); AccentSoft = C(0xF5E3DA); Hover = C(0xF0EEE6); OnAccent = C(0xFFFFFF);
            }
            using (var f = new InstalledFontCollection())
            {
                string[] names = f.Families.Select(x => x.Name).ToArray();
                SerifFamily = new[] { "Source Han Serif SC", "Noto Serif SC", "思源宋体", "Noto Serif CJK SC", "Georgia" }.FirstOrDefault(n => names.Contains(n)) ?? "Microsoft YaHei UI";
                SansFamily = names.Contains("Microsoft YaHei UI") ? "Microsoft YaHei UI" : SystemFonts.MessageBoxFont.Name;
            }
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Dpi = g.DpiX;
        }

        static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

        // Tints the native title bar to match the page (Windows 11; ignored elsewhere).
        public static void TintCaption(IntPtr h)
        {
            int dark = Dark ? 1 : 0, cap = Bg.R | Bg.G << 8 | Bg.B << 16, txt = Ink.R | Ink.G << 8 | Ink.B << 16;
            try
            {
                DwmSetWindowAttribute(h, 20, ref dark, 4);
                DwmSetWindowAttribute(h, 35, ref cap, 4);
                DwmSetWindowAttribute(h, 36, ref txt, 4);
            }
            catch (DllNotFoundException) { }
        }
    }

    enum PillKind { Primary, Ghost, Quiet }

    class Pill : Button
    {
        public PillKind Kind;
        // A disabled pill that reports a state ("使用中") reads as a status chip, not a dead button.
        public bool Chip;
        public const string Dots = "···";
        bool hover, pressed;
        float glow;
        readonly System.Windows.Forms.Timer tween = new System.Windows.Forms.Timer();

        public Pill(string text, PillKind kind)
        {
            Text = text; Kind = kind;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
            Height = Theme.S(34);
            Font = new Font(Theme.SansFamily, 9.5f);
            tween.Interval = 16;
            tween.Tick += (s, a) =>
            {
                Form form = FindForm();
                if (!Visible || form == null || !form.Visible || form.WindowState == FormWindowState.Minimized) { tween.Stop(); return; }
                float target = hover && Enabled ? 1 : 0;
                glow += (target - glow) * .28f;
                if (Math.Abs(target - glow) < .02f) { glow = target; tween.Stop(); }
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; tween.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; tween.Start(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = pressed ? Theme.S(1) : 0;
            var r = new RectangleF(.5f + inset, .5f + inset, Width - 1.5f - inset * 2, Height - 1.5f - inset * 2);
            Color fill = Color.Empty, border = Color.Empty, fg = Theme.Ink;
            if (!Enabled && Chip) { fg = Theme.Green; fill = Theme.Mix(Parent != null ? Parent.BackColor : Theme.Bg, Theme.Green, Theme.Dark ? .18f : .11f); }
            else if (!Enabled) { fg = Theme.Muted; if (Kind != PillKind.Quiet) border = Theme.Line; }
            else if (Kind == PillKind.Primary) { fill = Theme.Mix(Theme.Accent, Theme.AccentHover, pressed ? 1 : glow); fg = Theme.OnAccent; }
            else if (Kind == PillKind.Ghost) { border = Theme.Mix(Theme.Line, Theme.Accent, glow * .5f); fill = Theme.Mix(Theme.Surface, Theme.Hover, glow); }
            else { fg = Theme.Mix(Theme.Muted, Theme.Ink, glow); fill = Theme.Mix(Parent != null ? Parent.BackColor : Theme.Bg, Theme.Hover, glow); }
            using (GraphicsPath p = Theme.Round(r, Theme.S(8)))
            {
                if (fill != Color.Empty) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                if (border != Color.Empty) using (var pen = new Pen(border)) g.DrawPath(pen, p);
                if (Focused && ShowFocusCues) using (var pen = new Pen(Theme.Accent, 2f)) g.DrawPath(pen, p);
            }
            if (Text == Dots)
            {
                // Painted, so the overflow mark keeps its size and weight in every font.
                float size = Theme.S(3), step = Theme.S(6), cx = Width / 2f, cy = Height / 2f;
                using (var b = new SolidBrush(fg))
                    for (int i = -1; i <= 1; i++) g.FillEllipse(b, cx + i * step - size / 2f, cy - size / 2f, size, size);
                return;
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        protected override void Dispose(bool disposing) { if (disposing) tween.Dispose(); base.Dispose(disposing); }
    }

    // A keyboard-accessible checkbox painted as an eye, rather than a font-dependent glyph.
    class EmailEye : CheckBox
    {
        bool hover;
        public EmailEye()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(Theme.S(32), Theme.S(28)); AutoSize = false; Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.CheckButton;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(Parent == null ? Theme.Bg : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var box = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), Theme.S(7)))
            {
                if (hover || Checked) using (var b = new SolidBrush(Checked ? Theme.AccentSoft : Theme.Hover)) g.FillPath(b, box);
                if (Focused && ShowFocusCues) using (var p = new Pen(Theme.Accent, 1.5f)) g.DrawPath(p, box);
            }
            float cx = Width / 2f, cy = Height / 2f, rx = Theme.S(9), ry = Theme.S(5);
            using (var p = new Pen(Checked ? Theme.Accent : Theme.Muted, Math.Max(1.4f, Theme.Dpi / 64f)))
            using (var eye = new GraphicsPath())
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                eye.AddBezier(cx - rx, cy, cx - rx / 2, cy - ry * 1.5f, cx + rx / 2, cy - ry * 1.5f, cx + rx, cy);
                eye.AddBezier(cx + rx, cy, cx + rx / 2, cy + ry * 1.5f, cx - rx / 2, cy + ry * 1.5f, cx - rx, cy);
                g.DrawPath(p, eye);
                g.DrawEllipse(p, cx - Theme.S(2), cy - Theme.S(2), Theme.S(4), Theme.S(4));
                if (Checked) g.DrawLine(p, cx - rx, cy - ry - Theme.S(2), cx + rx, cy + ry + Theme.S(2));
            }
        }
    }

    class AddCard : Button
    {
        bool hover;

        public AddCard()
        {
            Text = "＋  添加账号";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
            Height = Theme.S(58);
            Font = new Font(Theme.SansFamily, 9.5f);
            AccessibleName = "添加账号";
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(12)))
            {
                if (hover) using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, p);
                using (var pen = new Pen(Focused && ShowFocusCues ? Theme.Accent : Theme.Line) { DashStyle = DashStyle.Dash })
                    g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, hover ? Theme.Ink : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    class Toggle : CheckBox
    {
        float position = -1;
        readonly System.Windows.Forms.Timer tween = new System.Windows.Forms.Timer();
        public Toggle(string text)
        {
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            AutoSize = false;
            Size = new Size(Theme.S(104), Theme.S(26));
            Cursor = Cursors.Hand;
            Font = new Font(Theme.SansFamily, 9f);
            tween.Interval = 16;
            tween.Tick += (s, a) =>
            {
                Form form = FindForm();
                if (!Visible || form == null || !form.Visible || form.WindowState == FormWindowState.Minimized) { tween.Stop(); return; }
                float target = Checked ? 1 : 0; position += (target - position) * .28f;
                if (Math.Abs(target - position) < .02f) { position = target; tween.Stop(); }
                Invalidate();
            };
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            if (!IsHandleCreated) position = Checked ? 1 : 0; else tween.Start();
            Invalidate(); base.OnCheckedChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? Theme.Bg : Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (position < 0) position = Checked ? 1 : 0;
            int w = Theme.S(32), h = Theme.S(18), y = (Height - h) / 2;
            using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, y + 0.5f, w - 1, h - 1), (h - 1) / 2f))
            using (var b = new SolidBrush(Theme.Mix(Theme.Line, Theme.Accent, position)))
                g.FillPath(b, p);
            int k = h - Theme.S(4);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, Theme.S(2) + (w - k - Theme.S(4)) * position, y + Theme.S(2), k, k);
            if (Focused && ShowFocusCues)
                using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, y + 0.5f, w - 1, h - 1), (h - 1) / 2f))
                using (var pen = new Pen(Theme.Ink))
                    g.DrawPath(pen, p);
            var tr = new Rectangle(w + Theme.S(8), 0, Width - w - Theme.S(8), Height);
            TextRenderer.DrawText(g, Text, Font, tr, Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        protected override void Dispose(bool disposing) { if (disposing) tween.Dispose(); base.Dispose(disposing); }
    }

    class OfficeSignal : Control
    {
        int frame;
        readonly Font labelFont = new Font(Theme.SansFamily, 8.5f);
        public OfficeSignal()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(Theme.S(126), Theme.S(25));
            AccessibleName = "本地资料自动检查";
        }
        public void AdvanceFrame(int value) { frame = value; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = Theme.Round(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), Theme.S(12)))
            {
                using (var b = new SolidBrush(Theme.Mix(Theme.Bg, Theme.Green, Theme.Dark ? .08f : .035f))) g.FillPath(b, shape);
                using (var p = new Pen(Theme.Mix(Theme.Line, Theme.Green, .18f))) g.DrawPath(p, shape);
            }
            float breath = (float)(Math.Sin(frame / 23f) + 1) / 2;
            float halo = Theme.S(7) + Theme.S(3) * breath;
            using (var b = new SolidBrush(Color.FromArgb(15 + (int)(14 * breath), Theme.Green)))
                g.FillEllipse(b, Theme.S(13) - halo / 2, Height / 2f - halo / 2, halo, halo);
            using (var b = new SolidBrush(Theme.Green)) g.FillEllipse(b, Theme.S(11), Height / 2f - Theme.S(2), Theme.S(4), Theme.S(4));
            TextRenderer.DrawText(g, "本地自动更新", labelFont, new Rectangle(Theme.S(24), 0, Width - Theme.S(29), Height), Theme.Green,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        protected override void Dispose(bool disposing) { if (disposing) labelFont.Dispose(); base.Dispose(disposing); }
    }

    class Row
    {
        public Profile Profile;
        public string Name, Sub, Badge, Primary;
        public UsageSnapshot Usage;
        public string UsageMessage, UsageSignature;
        public bool Running, PrimaryEnabled;
        public string Signature { get { return Profile.Id + "|" + Name + "|" + Sub + "|" + Badge + "|" + Primary + "|" + Running + "|" + PrimaryEnabled + "|" + UsageSignature; } }
        public Row ForDisplay(EmailPrivacy privacy)
        {
            var copy = (Row)MemberwiseClone();
            copy.Name = privacy.Text(Name); copy.Sub = privacy.Text(Sub);
            copy.Badge = privacy.Text(Badge); copy.Primary = privacy.Text(Primary);
            copy.UsageMessage = privacy.Text(UsageMessage);
            return copy;
        }
    }

    class Card : Control
    {
        public Row Row;
        public Action<Graphics, Card> PaintOffice;
        public PetInteraction Interaction;
        public bool CupHover;
        public bool Resident;
        public int AnimationFrame;
        public readonly Pill Go, More;
        readonly UsagePanel usage = new UsagePanel();
        readonly Font nameFont = new Font(Theme.SansFamily, 11.5f, FontStyle.Regular);
        readonly Font subFont = new Font(Theme.SansFamily, 9f);
        bool hover;
        float hoverBlend;

        public Card(Row row)
        {
            Row = row;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = Theme.S(140);
            Margin = new Padding(0, 0, 0, Theme.S(8));
            AccessibleName = row.Name;
            Text = row.Name; // not painted; exposes the card by name to UI Automation
            Go = new Pill(row.Primary, PillKind.Primary) { Enabled = row.PrimaryEnabled, Chip = !row.PrimaryEnabled, Width = Theme.S(row.Primary.Length > 2 ? 104 : 72), AccessibleName = row.Primary + " " + row.Name };
            More = new Pill(Pill.Dots, PillKind.Quiet) { Width = Theme.S(36), AccessibleName = "更多 " + row.Name };
            Controls.Add(Go); Controls.Add(More); Controls.Add(usage);
            usage.UpdateUsage(row.Usage, row.UsageMessage);
            MouseEnter += (s, a) => { hover = true; Invalidate(); };
            MouseLeave += (s, a) => { hover = false; Invalidate(); };
            Go.MouseEnter += (s, a) => { hover = true; Invalidate(); };
            Go.MouseLeave += (s, a) => { hover = false; Invalidate(); };
            More.MouseEnter += (s, a) => { hover = true; Invalidate(); };
            More.MouseLeave += (s, a) => { hover = false; Invalidate(); };
        }

        // Refreshes in place, so a click landing during a status change still reaches a live button.
        public void Update(Row row)
        {
            Row = row;
            Text = row.Name; AccessibleName = row.Name;
            Go.Text = row.Primary; Go.Enabled = row.PrimaryEnabled; Go.Chip = !row.PrimaryEnabled;
            Go.Width = Theme.S(row.Primary.Length > 2 ? 104 : 72);
            Go.AccessibleName = row.Primary + " " + row.Name;
            More.AccessibleName = "更多 " + row.Name;
            usage.UpdateUsage(row.Usage, row.UsageMessage);
            PerformLayout();
            Invalidate();
        }

        // A quota observation must not re-enable account buttons during a handoff.
        public void UpdateUsage(UsageSnapshot snapshot, string message)
        {
            Row.Usage = snapshot; Row.UsageMessage = message;
            Row.UsageSignature = UsagePanel.Description(snapshot, message);
            usage.UpdateUsage(snapshot, message);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            if (Go == null || More == null) return; // setting Height in the constructor lays out before they exist
            int pad = Theme.S(16);
            More.Location = new Point(Width - pad + Theme.S(6) - More.Width, (Theme.S(63) - More.Height) / 2);
            Go.Location = new Point(More.Left - Theme.S(4) - Go.Width, (Theme.S(63) - Go.Height) / 2);
            usage.SetBounds(pad, Theme.S(63), Math.Max(0, Width - pad * 2), Theme.S(72));
            base.OnLayout(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            hoverBlend += ((hover ? 1f : 0f) - hoverBlend) * .22f;
            var cardRect = new RectangleF(1, 1, Width - 3, Height - 3);
            // One soft line of depth under the card; the card itself stays flat.
            using (GraphicsPath shade = Theme.Round(new RectangleF(1, 2, Width - 3, Height - 3), Theme.S(12)))
            using (var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 46 : 13, 0, 0, 0))) g.FillPath(b, shade);
            using (GraphicsPath p = Theme.Round(cardRect, Theme.S(12)))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                if (Resident)
                {
                    GraphicsState save = g.Save(); g.SetClip(p);
                    // The brush is a pixel wider than the fill and mirrored, so no seam shows where it ends.
                    using (var glow = new LinearGradientBrush(new Rectangle(-1, 0, Math.Max(2, Width / 2 + 2), Height),
                        Theme.Mix(Theme.Surface, Theme.AccentSoft, .48f), Theme.Surface, LinearGradientMode.Horizontal))
                    {
                        glow.WrapMode = WrapMode.TileFlipX;
                        g.FillRectangle(glow, 0, 0, Width / 2, Height);
                    }
                    g.Restore(save);
                }
                float pulse = Resident ? .32f + .10f * (float)Math.Sin(AnimationFrame / 18f) : hoverBlend * .3f;
                using (var pen = new Pen(Theme.Mix(Theme.Line, Theme.Accent, pulse), Resident ? 1.25f : 1f)) g.DrawPath(pen, p);
            }
            using (var tile = Theme.Round(new RectangleF(Theme.S(11), Theme.S(7), Theme.S(83), Theme.S(54)), Theme.S(8)))
            using (var b = new SolidBrush(Resident ? Theme.Mix(Theme.Surface, Theme.AccentSoft, .72f) : Theme.Mix(Theme.Surface, Theme.Hover, .8f))) g.FillPath(b, tile);
            // The resident sprite is supplied by PaintOffice at the matching (19, 12) anchor.
            // Keep the original desk and pet drawing; only move their shared workstation.
            if (CupHover && Resident && (Interaction == null || !Interaction.IsTea))
                using (var glow = new SolidBrush(Theme.AccentSoft)) g.FillEllipse(glow, Theme.S(73), Theme.S(31), Theme.S(21), Theme.S(24));
            PetScene.Desk(g, Theme.S(10), Theme.S(14), Resident, AnimationFrame, Resident ? Interaction : null);
            int x = Theme.S(111), right = Go.Left - Theme.S(14), w = Math.Max(0, right - x);
            int nameH = nameFont.Height;
            int ny = Theme.S(10);
            int badgeWidth = string.IsNullOrWhiteSpace(Row.Badge) ? 0 : TextRenderer.MeasureText(Row.Badge, subFont).Width + Theme.S(20);
            if (w < badgeWidth + Theme.S(100)) badgeWidth = 0;
            int nameWidth = Math.Max(0, w - (badgeWidth > 0 ? badgeWidth + Theme.S(10) : 0));
            TextRenderer.DrawText(g, Row.Name, nameFont, new Rectangle(x, ny, nameWidth, nameH), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (badgeWidth > 0)
            {
                int actualName = Math.Min(nameWidth, TextRenderer.MeasureText(g, Row.Name, nameFont, new Size(nameWidth, nameH), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
                int bx = x + actualName + Theme.S(10), by = Theme.S(10);
                Color badgeInk = Row.Running ? Theme.Green : Theme.Muted;
                using (var badge = Theme.Round(new RectangleF(bx, by, badgeWidth, Theme.S(21)), Theme.S(10)))
                using (var b = new SolidBrush(Theme.Mix(Theme.Surface, badgeInk, Theme.Dark ? .16f : .09f))) g.FillPath(b, badge);
                using (var b = new SolidBrush(badgeInk)) g.FillEllipse(b, bx + Theme.S(7), by + Theme.S(9), Theme.S(3), Theme.S(3));
                TextRenderer.DrawText(g, Row.Badge, subFont, new Rectangle(bx + Theme.S(14), by + Theme.S(1), Math.Max(0, badgeWidth - Theme.S(18)), Theme.S(19)), badgeInk, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            }
            TextRenderer.DrawText(g, Row.Sub, subFont, new Rectangle(x, Theme.S(36), w, subFont.Height + 2), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (PaintOffice != null) PaintOffice(g, this);
        }

        protected override void Dispose(bool disposing)
        { if (disposing) { nameFont.Dispose(); subFont.Dispose(); } base.Dispose(disposing); }
    }

    class Colors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Theme.Surface; } }
    }

    class Dialog : Form
    {
        static string Present(IWin32Window owner, string text)
        { var panel = owner as Panel; return panel == null ? text : panel.DisplayText(text); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.TintCaption(Handle); }

        Dialog(string title, int height)
        {
            Text = title;
            ClientSize = new Size(Theme.S(420), height);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Ink;
            Font = new Font(Theme.SansFamily, 9.5f);
        }

        void Buttons(string okText, PillKind kind, bool focusCancel, Control extra)
        {
            int pad = Theme.S(20);
            var no = new Pill("取消", PillKind.Ghost) { Width = Theme.S(84), DialogResult = DialogResult.Cancel };
            var ok = new Pill(okText, kind) { Width = Theme.S(96), DialogResult = DialogResult.OK };
            ok.Location = new Point(ClientSize.Width - pad - ok.Width, ClientSize.Height - pad - ok.Height);
            no.Location = new Point(ok.Left - Theme.S(8) - no.Width, ok.Top);
            Controls.Add(no); Controls.Add(ok);
            CancelButton = no;
            if (focusCancel) ActiveControl = no;
            else { AcceptButton = ok; ActiveControl = extra; }
        }

        public static bool Confirm(IWin32Window owner, string title, string body, string okText)
        {
            title = Present(owner, title); body = Present(owner, body);
            using (var f = new Dialog(title, Theme.S(190)))
            {
                int pad = Theme.S(20);
                var head = new Label { Text = title, Font = new Font(Theme.SerifFamily, 14f), AutoSize = true, Location = new Point(pad, pad - Theme.S(2)), ForeColor = Theme.Ink };
                var txt = new Label { Text = body, ForeColor = Theme.Muted, Location = new Point(pad, Theme.S(54)), Size = new Size(f.ClientSize.Width - pad * 2, Theme.S(76)) };
                f.Controls.Add(head); f.Controls.Add(txt);
                f.Buttons(okText, PillKind.Primary, true, null);
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        public static string Ask(IWin32Window owner, string title, string prompt, string initial)
        {
            title = Present(owner, title); prompt = Present(owner, prompt); initial = Present(owner, initial);
            using (var f = new Dialog(title, Theme.S(212)))
            {
                int pad = Theme.S(20);
                var head = new Label { Text = title, Font = new Font(Theme.SerifFamily, 14f), AutoSize = true, Location = new Point(pad, pad - Theme.S(2)), ForeColor = Theme.Ink };
                var txt = new Label { Text = prompt, ForeColor = Theme.Muted, Location = new Point(pad, Theme.S(52)), Size = new Size(f.ClientSize.Width - pad * 2, Theme.S(48)) };
                var box = new TextBox { Text = initial, Location = new Point(pad, Theme.S(108)), Width = f.ClientSize.Width - pad * 2, BackColor = Theme.Surface, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle, AccessibleName = title };
                f.Controls.Add(head); f.Controls.Add(txt); f.Controls.Add(box);
                f.Buttons("确定", PillKind.Primary, false, box);
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }

        public static RelayTask PickTask(IWin32Window owner, IList<RelayTask> tasks)
        {
            using (var f = new Dialog("选择要带走的任务", Theme.S(320)))
            {
                f.ClientSize = new Size(Theme.S(580), Theme.S(320));
                int pad = Theme.S(20);
                var text = new Label { Text = "这个账号有多个任务。选择一个，小克会保存进度、暂停它，再到新账号续接。", ForeColor = Theme.Muted,
                    Location = new Point(pad, pad), Size = new Size(f.ClientSize.Width - pad * 2, Theme.S(48)) };
                var list = new ListBox { Location = new Point(pad, Theme.S(76)), Size = new Size(f.ClientSize.Width - pad * 2, Theme.S(168)),
                    BackColor = Theme.Surface, ForeColor = Theme.Ink, HorizontalScrollbar = true, AccessibleName = "要接力的任务" };
                foreach (var task in tasks) list.Items.Add(Present(owner, task.Title + "  ·  " + task.Cwd + "  [" + task.CliId.Substring(0, 8) + "]"));
                f.Controls.Add(text); f.Controls.Add(list); f.Buttons("开始接力", PillKind.Primary, false, list);
                var ok = f.Controls.OfType<Pill>().First(b => b.DialogResult == DialogResult.OK);
                ok.Enabled = false; list.SelectedIndexChanged += (s, a) => ok.Enabled = list.SelectedIndex >= 0;
                return f.ShowDialog(owner) == DialogResult.OK && list.SelectedIndex >= 0 ? tasks[list.SelectedIndex] : null;
            }
        }

        public static void ShowKnowledge(IWin32Window owner, KnowledgePackage pack)
        {
            using (var f = new Dialog("小克的记忆与 Skills", Theme.S(500)))
            {
                f.ClientSize = new Size(Theme.S(620), Theme.S(500));
                int pad = Theme.S(20);
                var head = new Label { Text = "记忆与 Skills 一起带走", AutoSize = true, Font = new Font(Theme.SerifFamily, 16f),
                    ForeColor = Theme.Ink, Location = new Point(pad, pad) };
                string body = pack.Summary + "\r\n\r\n本地自动更新：面板每 5 秒检查变化，替换“当前版本”索引；新增、修改、删除都会重新核验，不再只保存历史快照。\r\n\r\n" +
                    "Code 最新资料：通过会话开始/发送消息前的读取入口，给两个账号提供当前项目记忆和最新 Skills 目录。旧会话中已用过的同名 Skill，要在再次使用前重读最新正文。此过程不代发消息，不执行任务。\r\n\r\n" +
                    "云端 Claude / Cowork：记忆、Skills 和账号权限各自独立。本地打包不会上传到另一个云端账号。\r\n" +
                    "云端记忆：从原账号导出记忆文本，再到目标账号的 Your Claude → Start import 导入，完成后核对内容。\r\n" +
                    "云端 Skills：在目标账号的 Customize → Skills 上传原始 Skill ZIP 包。\r\n\r\n" +
                    "共享本地资料的变更继续保存在原目录；资料包是交接快照，不会反向覆盖原文件。";
                if (!pack.Complete) body += "\r\n\r\n需要处理（资料未完整保存时不会自动续跑）：\r\n• " + string.Join("\r\n• ", pack.Warnings.Take(30));
                var text = new TextBox { Text = Present(owner, body), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                    BackColor = Theme.Surface, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                    Location = new Point(pad, Theme.S(64)), Size = new Size(f.ClientSize.Width - pad * 2, Theme.S(308)), AccessibleName = "记忆与 Skills 同步范围" };
                var memoryHelp = new LinkLabel { Text = "云端记忆导入说明", AutoSize = true, LinkColor = Theme.Accent, Location = new Point(pad, Theme.S(388)) };
                memoryHelp.LinkClicked += (s, a) => Process.Start("https://support.claude.com/en/articles/12123587-import-and-export-your-memory-from-claude");
                var skillsHelp = new LinkLabel { Text = "云端 Skills 上传说明", AutoSize = true, LinkColor = Theme.Accent, Location = new Point(Theme.S(210), Theme.S(388)) };
                skillsHelp.LinkClicked += (s, a) => Process.Start("https://support.claude.com/en/articles/12512180-use-skills-in-claude");
                var folder = new Pill("打开最新索引", PillKind.Ghost) { Width = Theme.S(112), Location = new Point(pad, Theme.S(442)), Enabled = Directory.Exists(pack.Directory) };
                folder.Click += (s, a) => Process.Start("explorer.exe", App.Quote(Path.GetDirectoryName(pack.Directory)));
                var close = new Pill("关闭", PillKind.Primary) { Width = Theme.S(84), Location = new Point(f.ClientSize.Width - pad - Theme.S(84), Theme.S(442)), DialogResult = DialogResult.OK };
                f.Controls.AddRange(new Control[] { head, text, memoryHelp, skillsHelp, folder, close });
                f.AcceptButton = close; f.CancelButton = close;
                f.ShowDialog(owner);
            }
        }

        // The same meters as the account card, then the exact wording, in the app's own theme.
        // Presentation only: it shows the record it is given and never samples or refreshes.
        public static void ShowUsage(IWin32Window owner, string title, UsageSnapshot snapshot, string message, string help)
        {
            using (Dialog f = BuildUsage(Present(owner, title), snapshot, Present(owner, message),
                Present(owner, UsagePanel.Description(snapshot, message)), Present(owner, help)))
                f.ShowDialog(owner);
        }

        internal static Dialog BuildUsage(string title, UsageSnapshot snapshot, string message, string record, string help)
        {
            var f = new Dialog(title, Theme.S(420));
            int pad = Theme.S(20), inner = Theme.S(16), width = Theme.S(540) - pad * 2;
            const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;
            var small = new Font(Theme.SansFamily, 8.5f);
            f.Disposed += (s, a) => small.Dispose();
            var head = new Label { Text = title, Font = new Font(Theme.SerifFamily, 14f), AutoEllipsis = true, ForeColor = Theme.Ink,
                Location = new Point(pad, pad - Theme.S(2)), Size = new Size(width, Theme.S(32)) };
            var frame = new System.Windows.Forms.Panel { Location = new Point(pad, Theme.S(60)), Size = new Size(width, inner + Theme.S(26) + Theme.S(72) + Theme.S(10)), BackColor = Theme.Bg };
            frame.Paint += (s, a) =>
            {
                a.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath shape = Theme.Round(new RectangleF(.5f, .5f, frame.Width - 1.5f, frame.Height - 1.5f), Theme.S(12)))
                {
                    using (var b = new SolidBrush(Theme.Surface)) a.Graphics.FillPath(b, shape);
                    using (var pen = new Pen(Theme.Line)) a.Graphics.DrawPath(pen, shape);
                }
            };
            var caption = new Label { Text = "额度记录", ForeColor = Theme.Ink, BackColor = Theme.Surface, AutoSize = false,
                Location = new Point(inner, inner - Theme.S(4)), Size = new Size(width - inner * 2, Theme.S(24)) };
            var meters = new UsagePanel { Location = new Point(inner, inner + Theme.S(26)), Width = width - inner * 2, TabStop = false };
            meters.UpdateUsage(snapshot, message);
            frame.Controls.Add(caption); frame.Controls.Add(meters);
            int y = frame.Bottom + Theme.S(16);
            int recordHeight = TextRenderer.MeasureText(record, f.Font, new Size(width, 0), wrap).Height + Theme.S(4);
            var exact = new Label { Text = record, ForeColor = Theme.Ink, Location = new Point(pad, y), Size = new Size(width, recordHeight), AccessibleName = "额度记录原文" };
            y = exact.Bottom + Theme.S(12);
            int helpHeight = TextRenderer.MeasureText(help, small, new Size(width, 0), wrap).Height + Theme.S(4);
            var guide = new Label { Text = help, Font = small, ForeColor = Theme.Muted, Location = new Point(pad, y), Size = new Size(width, helpHeight) };
            y = guide.Bottom + Theme.S(18);
            var close = new Pill("关闭", PillKind.Primary) { Width = Theme.S(84), DialogResult = DialogResult.OK };
            f.ClientSize = new Size(Theme.S(540), y + close.Height + pad);
            close.Location = new Point(f.ClientSize.Width - pad - close.Width, y);
            f.Controls.AddRange(new Control[] { head, frame, exact, guide, close });
            f.AcceptButton = close; f.CancelButton = close; f.ActiveControl = close;
            return f;
        }
    }

    static class ProtocolRegistration
    {
        const string AppName = "ClaudeAccountSync";
        const string ProgId = "ClaudeAccountSync.Url";
        const string Capabilities = @"Software\ClaudeAccountSync\Capabilities";

        public static string Command(Engine engine, string scheme)
        {
            return App.Quote(Application.ExecutablePath) + " --state-dir " + App.Quote(engine.StateDir) +
                " --scheme " + App.Quote(scheme) + " --protocol-callback \"%1\"";
        }

        public static void Register(Engine engine, string scheme)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                k.SetValue("", "Claude Switch授权助手"); k.SetValue("URL Protocol", "");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId + @"\shell\open\command"))
                k.SetValue("", Command(engine, scheme));
            using (var k = Registry.CurrentUser.CreateSubKey(Capabilities))
            {
                k.SetValue("ApplicationName", "Claude Switch授权助手");
                k.SetValue("ApplicationDescription", "将 Claude 登录授权送到所选账号的独立窗口。");
                k.SetValue("ApplicationIcon", Application.ExecutablePath + ",0");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(Capabilities + @"\URLAssociations")) k.SetValue(scheme, ProgId);
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) k.SetValue(AppName, Capabilities);
            // This only makes the handler available. Windows Settings owns the user's default choice.
        }

        public static void OpenSettings(Engine engine, string scheme)
        {
            Register(engine, scheme);
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + AppName) { UseShellExecute = true });
        }
    }

    // A short-lived unpackaged callback broker avoids Store activation losing profile arguments.
    // Only the route (profile ID, scheme and deadline) is persisted; never the authorization URL.
    class CallbackRoute
    {
        readonly Engine engine;
        readonly string keyPath, rootPath, scheme, backupFile;
        string original, desired; // original "" = the value did not exist
        bool createdRoot;
        DateTime until;
        bool untilSignedIn;
        string stableAccount;
        int stableTicks;
        public bool RouteVerified { get; private set; }
        bool VerifyRoute() { string actual = ResolvedCommand(scheme); return string.Equals(actual, desired, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actual, ProtocolRegistration.Command(engine, scheme), StringComparison.OrdinalIgnoreCase); }
        public Profile Target { get; private set; }
        public bool Active { get { return Target != null; } }

        public CallbackRoute(Engine e, string scheme)
        {
            engine = e;
            this.scheme = scheme;
            rootPath = @"Software\Classes\" + scheme;
            keyPath = rootPath + @"\shell\open\command";
            backupFile = IOPath.Combine(e.StateDir, "callback-route.txt");
        }

        string Read() { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(keyPath)) return k == null ? null : k.GetValue("") as string; }

        [DllImport("shell32.dll")] static extern void SHChangeNotify(uint id, uint flags, IntPtr item1, IntPtr item2);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern uint AssocQueryString(uint flags, uint str, string assoc, string extra, System.Text.StringBuilder output, ref uint size);

        public static string ResolvedCommand(string scheme)
        {
            uint size = 32768;
            var output = new System.Text.StringBuilder((int)size);
            return AssocQueryString(0x1000, 1, scheme, "open", output, ref size) == 0 ? output.ToString() : null;
        }

        string TicketFile { get { return IOPath.Combine(engine.StateDir, "callback-target.json"); } }

        void SaveTicket(Profile target, DateTime deadline)
        {
            var data = new Dictionary<string, object> { { "profile", target.Id }, { "scheme", scheme }, { "expires", deadline.ToUniversalTime().Ticks.ToString() } };
            string tmp = TicketFile + ".tmp";
            File.WriteAllText(tmp, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(data));
            if (File.Exists(TicketFile)) File.Replace(tmp, TicketFile, null); else File.Move(tmp, TicketFile);
        }

        public static bool CanDispatch(Engine e, Profile p, string scheme)
        {
            try
            {
                var data = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(IOPath.Combine(e.StateDir, "callback-target.json")));
                long deadline;
                return Equals(data["profile"], p.Id) && Equals(data["scheme"], scheme) &&
                    long.TryParse(data["expires"] as string, out deadline) && deadline > DateTime.UtcNow.Ticks;
            }
            catch { return false; }
        }

        public static Profile DispatchTarget(Engine e, string scheme)
        {
            return e.Profiles.FirstOrDefault(p => CanDispatch(e, p, scheme)) ?? e.Profiles.FirstOrDefault(p => p.IsRealDefault);
        }

        // Creates whatever is missing: the app or Windows may have cleared this registration.
        void Write(string v)
        {
            using (RegistryKey r = Registry.CurrentUser.CreateSubKey(rootPath))
            {
                if (r.GetValue("URL Protocol") == null) r.SetValue("URL Protocol", "");
                if (r.GetValue("") == null) r.SetValue("", "URL:" + scheme);
            }
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(keyPath)) k.SetValue("", v);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        void Restore(string saved, bool removeRoot)
        {
            if (saved != "") { Write(saved); return; }
            if (removeRoot) { Registry.CurrentUser.DeleteSubKeyTree(rootPath, false); SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); return; }
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(keyPath, true)) if (k != null) k.DeleteValue("", false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        static bool IsRoute(string v) { return v != null && (v.IndexOf("--user-data-dir", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("--dispatch-callback", StringComparison.OrdinalIgnoreCase) >= 0); }

        void Remember(string v)
        {
            original = v;
            Directory.CreateDirectory(engine.StateDir);
            File.WriteAllText(backupFile, (createdRoot ? "created" : "existing") + "\n" + v, new System.Text.UTF8Encoding(false));
        }

        public void RecoverStale()
        {
            if (!File.Exists(backupFile)) return;
            string[] saved = File.ReadAllText(backupFile).Split(new[] { '\n' }, 2);
            if (saved.Length == 2 && IsRoute(Read())) Restore(saved[1], saved[0] == "created"); // otherwise the app already re-registered itself: newer, keep it
            File.Delete(backupFile);
            if (File.Exists(TicketFile)) File.Delete(TicketFile);
            engine.Log("callback route left by an earlier run was restored");
        }

        public bool Start(Profile target, string exe, bool waitForSignIn, int minutes)
        {
            if (exe == null) { Stop(); return false; }
            string cur = Read();
            if (original == null)
            {
                using (RegistryKey r = Registry.CurrentUser.OpenSubKey(rootPath)) createdRoot = r == null;
                Remember(cur == null ? "" : (IsRoute(cur) ? "\"" + exe + "\" \"%1\"" : cur));
            }
            Target = target; untilSignedIn = waitForSignIn; until = DateTime.Now.AddMinutes(minutes);
            stableAccount = null; stableTicks = 0;
            desired = App.Quote(Application.ExecutablePath) + " --dispatch-callback " + App.Quote(target.Id) +
                " --state-dir " + App.Quote(engine.StateDir) + " --scheme " + App.Quote(scheme) + " --callback \"%1\"";
            SaveTicket(target, until);
            Write(desired);
            RouteVerified = VerifyRoute();
            engine.Log("callback route on: " + engine.Display(target));
            return true;
        }

        // Returns "signed-in" or "expired" on the tick the route ends, otherwise null.
        public string Tick()
        {
            if (!Active) return null;
            bool wasVerified = RouteVerified;
            RouteVerified = VerifyRoute();
            string account = engine.ActiveAccount(Target);
            if (untilSignedIn && account != null)
            {
                stableTicks = account == stableAccount ? stableTicks + 1 : 1;
                stableAccount = account;
                if (stableTicks >= 3)
                {
                    bool duplicate = engine.IsDuplicate(Target);
                    Stop(); return duplicate ? "duplicate" : "signed-in";
                }
            }
            else { stableTicks = 0; stableAccount = null; }
            if (DateTime.Now > until) { Stop(); return "expired"; }
            string cur = Read();
            if (cur != desired)
            {
                Write(desired);
            }
            return !wasVerified && RouteVerified ? "route-ready" : null;
        }

        public void Stop()
        {
            if (original != null && string.Equals(Read(), desired, StringComparison.Ordinal)) Restore(original, createdRoot);
            if (File.Exists(backupFile)) File.Delete(backupFile);
            if (File.Exists(TicketFile)) File.Delete(TicketFile);
            if (Active) engine.Log("callback route off: " + engine.Display(Target));
            Target = null; original = null; desired = null; createdRoot = false;
        }
    }

    class Panel : Form
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ClaudeAccountSync";
        readonly Engine engine;
        readonly UsageMonitor usageMonitor;
        readonly TokenStatisticsService tokenStatistics;
        readonly TokenStatisticsView tokenView = new TokenStatisticsView();
        readonly System.Windows.Forms.Timer tokenTimer = new System.Windows.Forms.Timer();
        int tokenRefreshBusy;
        readonly EmailPrivacy emailPrivacy;
        readonly EmailEye emailEye = new EmailEye();
        readonly ToolTip privacyTip = new ToolTip();
        List<Row> latestRows = new List<Row>();
        string rawStatus, rawJourneyHeading, rawJourneyDetail;
        PetState rawJourneyState;
        readonly OfficeHost host = new OfficeHost();
        readonly PetScene pet = new PetScene();
        readonly PetInteraction petInteraction = new PetInteraction();
        readonly ToolTip petTip = new ToolTip { InitialDelay = 450, ReshowDelay = 200, AutoPopDelay = 2200 };
        readonly CompactJourney journey = new CompactJourney();
        readonly SideLounge lounge = new SideLounge();
        readonly System.Windows.Forms.Panel sidebar = new System.Windows.Forms.Panel();
        readonly Label brand, workspaceLabel;
        readonly NavButton accountsNav, tokenNav, relayNav, petNav;
        bool inLounge;
        string loungeDetail = "暂时离开 Claude，小克在这里等你。";
        PetState beforeRestState;
        string beforeRestHeading, beforeRestDetail;
        readonly IdleActivity activity = new IdleActivity(DateTime.UtcNow);
        volatile bool backgroundBusy = true;
        readonly System.Windows.Forms.Timer petTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer knowledgeTimer = new System.Windows.Forms.Timer();
        string lastKnowledgeMessage;
        volatile Dictionary<int, string> processProfiles = new Dictionary<int, string>();
        readonly Dictionary<string, string> refreshProcesses = new Dictionary<string, string>();
        bool switching;
        string petHomeFile;
        readonly AddCard addCard = new AddCard();
        readonly Label status = new Label();
        readonly Label head, sub, eyebrow, sectionHead, sectionHint;
        readonly OfficeSignal localSignal = new OfficeSignal();
        readonly Pill syncBtn, bakBtn;
        readonly NavButton logBtn, knowledgeBtn;
        readonly Toggle autostart, autoRelay;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly SemaphoreSlim work = new SemaphoreSlim(1, 1);
        readonly System.Windows.Forms.Timer routeTimer = new System.Windows.Forms.Timer();
        readonly CallbackRoute route;
        const int LoginMinutes = 20, ManualRouteMinutes = 15;
        public static string Scheme = "claude";
        HashSet<string> wasRunning = new HashSet<string>();
        Profile pendingRestart;
        string shownSignature;
        bool quitting;

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.TintCaption(Handle); }

        public Panel(Engine e, bool background)
        {
            engine = e;
            usageMonitor = new UsageMonitor(engine);
            tokenStatistics = new TokenStatisticsService(engine);
            usageMonitor.Updated += UsageUpdated;
            emailPrivacy = new EmailPrivacy(engine.StateDir);
            Text = "Claude Switch";
            Font = new Font(Theme.SansFamily, 9.5f);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg; ForeColor = Theme.Ink;
            ClientSize = new Size(Math.Min(Theme.S(1000), Screen.PrimaryScreen.WorkingArea.Width - Theme.S(40)), Math.Min(Theme.S(640), Screen.PrimaryScreen.WorkingArea.Height - Theme.S(60)));
            MinimumSize = new Size(Theme.S(900), Theme.S(600));
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Icon appIcon = null;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Icon = appIcon ?? SystemIcons.Application;

            sidebar.BackColor = Theme.Mix(Theme.Bg, Theme.Line, .30f);
            brand = new Label { Text = "小克办公室", Font = new Font(Theme.SerifFamily, 14f), AutoSize = false, ForeColor = Theme.Ink };
            workspaceLabel = new Label { Text = "CLAUDE", Font = new Font(Theme.SansFamily, 8.5f), AutoSize = true, ForeColor = Theme.Muted };
            accountsNav = new NavButton("账号工位", "accounts") { Selected = true };
            accountsNav.Click += (s, a) => { ShowTokenStatistics(false); host.Focus(); RefreshCards(); };
            tokenNav = new NavButton("Token 统计", "statistics");
            tokenNav.Click += (s, a) => { ShowTokenStatistics(true); RefreshTokenStatistics(); };
            tokenView.Visible = false;
            tokenView.RefreshRequested += (s, a) => RefreshTokenStatistics();
            relayNav = new NavButton("接力记录", "relay") { External = true };
            relayNav.Click += (s, a) => OpenRelayRecords();
            petNav = new NavButton("小克的日常", "pet");
            petNav.Click += (s, a) => ShowPetGallery();
            head = new Label { Text = "账号工作台", Font = new Font(Theme.SerifFamily, 20f), AutoSize = true, ForeColor = Theme.Ink };
            eyebrow = new Label { Text = "LOCAL  WORKSPACE", Font = new Font(Theme.SansFamily, 8f), AutoSize = true, ForeColor = Theme.Muted };
            sub = new Label { Text = "选好下一个工位，带着进度继续。", AutoSize = true, ForeColor = Theme.Muted };
            sectionHead = new Label { Text = "账号工位", Font = new Font(Theme.SansFamily, 10f), AutoSize = true, ForeColor = Theme.Ink };
            sectionHint = new Label { Text = "切换工位，进度同行", Font = new Font(Theme.SansFamily, 8.5f), AutoSize = true, ForeColor = Theme.Muted };
            emailEye.Checked = emailPrivacy.Hidden;
            UpdatePrivacyHint();
            emailEye.CheckedChanged += (s, a) => ToggleEmailPrivacy();
            petHomeFile = IOPath.Combine(engine.StateDir, "pet-home.txt");
            try { if (File.Exists(petHomeFile)) pet.HomeId = File.ReadAllText(petHomeFile).Trim(); } catch { }
            host.PaintTraveler = g => DrawTraveler(g, Point.Empty);
            host.MouseDown += (s, a) => InteractWithPet(host, a);
            host.MouseMove += (s, a) => HoverPet(host, a.Location);
            host.MouseLeave += (s, a) => host.Cursor = Cursors.Default;

            host.FlowDirection = FlowDirection.TopDown; host.WrapContents = false; host.AutoScroll = true; host.BackColor = Theme.Bg;
            host.Margin = Padding.Empty; host.Padding = Padding.Empty;
            addCard.Margin = Padding.Empty;
            addCard.Height = Theme.S(38);
            addCard.Click += (s, a) => DoAdd();
            if (Theme.Dark) host.HandleCreated += (s, a) => SetWindowTheme(host.Handle, "DarkMode_Explorer", null);

            syncBtn = new Pill("立即同步", PillKind.Ghost) { Width = Theme.S(92) };
            syncBtn.Click += (s, a) => DoSyncAll();
            logBtn = new NavButton("同步记录", "history") { External = true };
            logBtn.Click += (s, a) => OpenPath(engine.LogFile);
            bakBtn = new Pill("备份", PillKind.Quiet) { Width = Theme.S(50) };
            bakBtn.Click += (s, a) => { Directory.CreateDirectory(engine.BackupDir); OpenPath(engine.BackupDir); };
            knowledgeBtn = new NavButton("记忆与 Skills", "memory");
            knowledgeBtn.Click += (s, a) => DoKnowledge();
            autostart = new Toggle("开机自启") { Checked = AutostartEnabled() };
            autostart.CheckedChanged += (s, a) => SetAutostart(autostart.Checked);
            string relaySetting = IOPath.Combine(engine.StateDir, "auto-relay.txt");
            bool relayEnabled = true;
            try { if (File.Exists(relaySetting)) relayEnabled = File.ReadAllText(relaySetting).Trim() != "off"; } catch { }
            autoRelay = new Toggle("自动接力") { Checked = relayEnabled, Width = Theme.S(122), AccessibleName = "切换时自动接力" };
            autoRelay.CheckedChanged += (s, a) =>
            {
                try { Directory.CreateDirectory(engine.StateDir); File.WriteAllText(relaySetting, autoRelay.Checked ? "on" : "off"); }
                catch (Exception ex) { SetStatus("设置未保存：" + ex.Message); }
                if (!switching) RestPet(engine.Find(pet.HomeId), null);
            };
            status.ForeColor = Theme.Muted; status.AutoEllipsis = true;

            sidebar.Controls.AddRange(new Control[] { brand, workspaceLabel, accountsNav, tokenNav, knowledgeBtn, relayNav, logBtn, petNav, lounge, autostart });
            Controls.AddRange(new Control[] { sidebar, eyebrow, localSignal, head, sub, journey, sectionHead, emailEye, sectionHint, host, tokenView, autoRelay, bakBtn, syncBtn, status });
            Relayout();
            Resize += (s, a) => Relayout();

            tray.Icon = Icon; tray.Text = "Claude Switch"; tray.ContextMenuStrip = trayMenu; tray.Visible = true;
            tray.DoubleClick += (s, a) => ShowPanel();
            tray.BalloonTipClicked += (s, a) => { if (pendingRestart != null) { Profile p = pendingRestart; pendingRestart = null; DoRestart(p); } };
            StyleMenu(trayMenu);
            trayMenu.Opening += (s, a) => BuildTrayMenu();

            timer.Interval = 30000;
            timer.Tick += (s, a) => Tick();
            timer.Start();
            knowledgeTimer.Interval = 5000;
            knowledgeTimer.Tick += (s, a) => RefreshLiveKnowledge();
            knowledgeTimer.Start();
            route = new CallbackRoute(engine, Scheme);
            try { route.RecoverStale(); } catch (Exception ex) { engine.Log("route recovery: " + ex.Message); }
            routeTimer.Interval = 1000;
            routeTimer.Tick += (s, a) => RouteTick();
            routeTimer.Start();
            Application.ApplicationExit += (s, a) => { try { route.Stop(); } catch { } };
            Apply(Gather());
            if (!engine.Profiles.Any(p => p.Id == pet.HomeId)) pet.HomeId = engine.Profiles.Select(p => p.Id).FirstOrDefault();
            RestPet(engine.Find(pet.HomeId), null);
            petTimer.Interval = 40;
            petTimer.Tick += (s, a) => PetTick();
            petTimer.Start();
            Load += (s, a) => Tick();
            Load += (s, a) => RefreshLiveKnowledge();
            tokenTimer.Interval = 30000;
            tokenTimer.Tick += (s, a) => RefreshTokenStatistics();
            Load += (s, a) => { RefreshTokenStatistics(); tokenTimer.Start(); };
            if (background) { ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Load += (s, a) => { Hide(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; }; }
        }

        static void StyleMenu(ContextMenuStrip m)
        {
            m.Renderer = new ToolStripProfessionalRenderer(new Colors());
            m.BackColor = Theme.Surface; m.ForeColor = Theme.Ink; m.ShowImageMargin = false;
            m.Font = new Font(Theme.SansFamily, 9.5f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int y = ClientSize.Height - Theme.S(77), left = Theme.S(188);
            using (var pen = new Pen(Theme.Line))
            {
                e.Graphics.DrawLine(pen, Theme.S(164), 0, Theme.S(164), ClientSize.Height);
                e.Graphics.DrawLine(pen, left, y, ClientSize.Width - Theme.S(24), y);
            }
        }

        void Relayout()
        {
            int pad = Theme.S(24), side = Theme.S(164), left = side + pad, w = ClientSize.Width, h = ClientSize.Height;
            int mainWidth = Math.Max(Theme.S(400), w - left - pad), footerTop = h - Theme.S(66);
            sidebar.SetBounds(0, 0, side, h);
            brand.SetBounds(Theme.S(20), Theme.S(23), side - Theme.S(32), Theme.S(32));
            workspaceLabel.Location = new Point(Theme.S(20), Theme.S(72));
            NavButton[] navigation = { accountsNav, tokenNav, knowledgeBtn, relayNav, logBtn, petNav };
            for (int i = 0; i < navigation.Length; i++) navigation[i].SetBounds(Theme.S(12), Theme.S(96 + i * 42), side - Theme.S(24), Theme.S(38));
            int loungeTop = Math.Max(Theme.S(356), h - Theme.S(227));
            lounge.SetBounds(Theme.S(12), loungeTop, side - Theme.S(24), Math.Max(0, Math.Min(Theme.S(178), h - Theme.S(49) - loungeTop)));
            autostart.SetBounds(Theme.S(20), h - Theme.S(38), side - Theme.S(34), Theme.S(26));
            eyebrow.Visible = false;
            head.Location = new Point(left, Theme.S(20));
            sub.Location = new Point(left, head.Bottom + Theme.S(3));
            emailEye.SetBounds(w - pad - Theme.S(36), Theme.S(24), Theme.S(36), Theme.S(32));
            journey.SetBounds(left, Theme.S(91), mainWidth, Theme.S(72));
            sectionHead.Location = new Point(left, Theme.S(180));
            sectionHint.Location = new Point(w - pad - sectionHint.Width, Theme.S(182));
            host.SetBounds(left, Theme.S(210), mainWidth, Math.Max(0, footerTop - Theme.S(15) - Theme.S(210)));
            tokenView.SetBounds(left, Theme.S(91), mainWidth, Math.Max(0, footerTop - Theme.S(15) - Theme.S(91)));
            autoRelay.Location = new Point(left, footerTop + Theme.S(3));
            localSignal.Location = new Point(autoRelay.Right + Theme.S(16), footerTop + Theme.S(3));
            syncBtn.Location = new Point(w - pad - syncBtn.Width, footerTop);
            bakBtn.Location = new Point(syncBtn.Left - Theme.S(10) - bakBtn.Width, footerTop);
            status.SetBounds(left, h - Theme.S(25), mainWidth, Theme.S(20));
            FitCards();
            Invalidate();
        }

        void OpenRelayRecords()
        {
            string folder = IOPath.Combine(engine.StateDir, "relay");
            if (Directory.Exists(folder)) OpenPath(folder);
            else SetStatus("还没有接力记录；完成一次任务接力后会保存在这里。");
        }

        void ShowTokenStatistics(bool show)
        {
            accountsNav.Selected = !show; tokenNav.Selected = show;
            host.Visible = journey.Visible = sectionHead.Visible = sectionHint.Visible = !show;
            tokenView.Visible = show;
            head.Text = show ? "Token 统计" : "账号工作台";
            sub.Text = show ? "所有账号合并 · 本机 Claude Code 用量" : "选好下一个工位，带着进度继续。";
            if (show) tokenView.BringToFront();
            Relayout();
        }

        void RefreshTokenStatistics()
        {
            if (IsDisposed || Interlocked.CompareExchange(ref tokenRefreshBusy, 1, 0) != 0) return;
            tokenView.SetLoading();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    TokenStatisticsSnapshot snapshot = tokenStatistics.Refresh();
                    PublishTokenStatistics(() => tokenView.SetSnapshot(snapshot));
                }
                catch (IOException ex)
                {
                    string detail = ex.Message;
                    PublishTokenStatistics(() => tokenView.SetError(detail));
                }
                catch
                {
                    PublishTokenStatistics(() => tokenView.SetError("暂时无法读取 Token 记录，可稍后刷新。"));
                }
                finally { Interlocked.Exchange(ref tokenRefreshBusy, 0); }
            });
        }

        void PublishTokenStatistics(Action change)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)(() => { if (!IsDisposed) change(); }));
            }
            catch (InvalidOperationException) { } // The window may close during a background scan.
        }

        void ShowPetGallery()
        {
            foreach (Form open in OwnedForms) if (open is PetGallery) { open.Activate(); return; }
            new PetGallery().Show(this);
        }

        void FitCards()
        {
            // Decide the scrollbar from the content height, so cards never overflow sideways.
            int total = host.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
            int cw = host.Width - (total > host.Height ? SystemInformation.VerticalScrollBarWidth + Theme.S(2) : 0);
            foreach (Control c in host.Controls) c.Width = cw;
            host.HorizontalScroll.Visible = false;
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);

        void ShowPanel() { Show(); WindowState = FormWindowState.Normal; Activate(); RefreshCards(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void BuildTrayMenu()
        {
            trayMenu.Items.Clear();
            trayMenu.Items.Add(new ToolStripMenuItem("打开面板", null, (s, a) => ShowPanel()));
            trayMenu.Items.Add(new ToolStripSeparator());
            foreach (Profile p in engine.Profiles.ToList())
            {
                Profile q = p;
                string mail = engine.EmailOf(engine.ActiveAccount(p));
                string title = engine.Display(p);
                trayMenu.Items.Add(new ToolStripMenuItem(DisplayText("切换到 " + title + (mail != null && mail != title ? "（" + mail + "）" : "")), null, (s, a) => DoSwitch(q, false)));
            }
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(new ToolStripMenuItem("退出", null, (s, a) => { quitting = true; tray.Visible = false; Application.Exit(); }));
            foreach (ToolStripItem i in trayMenu.Items) i.ForeColor = Theme.Ink;
        }

        // User actions wait their turn; the periodic check simply skips when something is running.
        void Background(Action job, bool user)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (user) work.Wait();
                else if (!work.Wait(0)) return;
                try { job(); }
                catch (Exception ex)
                {
                    engine.Log("ERROR " + ex.Message);
                    UI(() => SetStatus("出错：" + ex.Message));
                }
                finally { work.Release(); }
                try { List<Row> rows = Gather(); UI(() => Apply(rows)); } catch (Exception ex) { engine.Log("refresh: " + ex.Message); }
            });
        }

        void UI(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

        public string DisplayText(string text) { return emailPrivacy.Text(text); }
        void SetStatus(string s) { rawStatus = s; status.Text = DisplayText(s); }
        void SetJourney(string heading, string detail, PetState state)
        {
            rawJourneyHeading = heading; rawJourneyDetail = detail; rawJourneyState = state;
            journey.Update(DisplayText(heading), DisplayText(detail), state);
            privacyTip.SetToolTip(journey, DisplayText(heading + "\n" + detail));
        }

        void UpdatePrivacyHint()
        {
            emailEye.AccessibleName = emailPrivacy.Hidden ? "显示所有账号邮箱" : "隐藏所有账号邮箱";
            emailEye.AccessibleDescription = emailPrivacy.Hidden ? "邮箱已隐藏，按空格或点击可显示。" : "邮箱可见，按空格或点击可隐藏。";
            privacyTip.SetToolTip(emailEye, emailEye.AccessibleName);
            sectionHint.Text = emailPrivacy.Hidden ? "邮箱已隐藏 · 进度同行" : "切换工位，进度同行";
        }

        void ToggleEmailPrivacy()
        {
            bool saved = true;
            try { emailPrivacy.SetHidden(emailEye.Checked); }
            catch { saved = false; }
            UpdatePrivacyHint();
            // Reapply raw data on the UI thread. Late background results take this same path.
            shownSignature = null;
            Apply(latestRows); BuildTrayMenu();
            status.Text = DisplayText(rawStatus);
            SetJourney(rawJourneyHeading, rawJourneyDetail, rawJourneyState);
            tray.BalloonTipTitle = DisplayText(tray.BalloonTipTitle);
            tray.BalloonTipText = DisplayText(tray.BalloonTipText);
            Relayout();
            if (!saved) SetStatus("邮箱显示设置未能保存；当前界面已更新，重启后可能恢复原设置。");
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        void SavePetHome()
        {
            try { Directory.CreateDirectory(engine.StateDir); File.WriteAllText(petHomeFile, pet.HomeId ?? ""); }
            catch (Exception ex) { engine.Log("pet preference: " + ex.Message); }
        }

        void RestPet(Profile p, string detail)
        {
            if (p == null) return;
            if (pet.HomeId != p.Id) petInteraction.ClearReaction();
            bool preserveRest = inLounge && !switching;
            bool signedIn = engine.ActiveAccount(p) != null;
            pet.Settle(p.Id, signedIn); SavePetHome();
            bool refresh = refreshProcesses.ContainsKey(p.Id);
            SetJourney(signedIn ? "小克正在 " + engine.Display(p) + " 办公" + (refresh ? " · 新进度待载入" : "") : "小克在等你登录 " + engine.Display(p),
                refresh ? "记录已保存；若 Code 列表未更新，请结束任务后重启这个账号窗口。" :
                detail ?? (signedIn ? (autoRelay.Checked ? "自动接力已开：保存进度、记忆与 Skills → 确认暂停 → 继续。" : "自动接力已关；只同步记录和本地资料，原任务继续运行。") : "在这个账号窗口完成一次登录，小克就能开始办公。"), pet.State);
            if (preserveRest)
            {
                beforeRestState = pet.State; beforeRestHeading = rawJourneyHeading; beforeRestDetail = rawJourneyDetail;
                pet.State = PetState.Resting;
                SetJourney("小克在休息区等你", loungeDetail, pet.State);
            }
            else inLounge = false;
            RepaintPet();
        }

        // Visual-only presence state. It never changes account identity, process lifetime or tasks.
        public void SetResting(bool resting, string detail)
        {
            if (switching) return;
            if (resting && inLounge && pet.State == PetState.Resting && (string.IsNullOrWhiteSpace(detail) || detail == loungeDetail)) return;
            if (!resting && !inLounge) return;
            petInteraction.ClearReaction();
            lounge.ClearInteraction();
            if (resting)
            {
                if (!inLounge || pet.State != PetState.Resting)
                {
                    beforeRestState = pet.State; beforeRestHeading = rawJourneyHeading; beforeRestDetail = rawJourneyDetail;
                }
                if (!string.IsNullOrWhiteSpace(detail)) loungeDetail = detail;
                inLounge = true; pet.State = PetState.Resting;
                SetJourney("小克在休息区等你", loungeDetail, pet.State);
            }
            else if (inLounge)
            {
                inLounge = false; pet.State = beforeRestState;
                SetJourney(beforeRestHeading ?? "小克回到工位了", beforeRestDetail ?? "继续和你一起办公。", pet.State);
            }
            RepaintPet();
        }

        string ProfileProcesses(string id)
        { return string.Join(",", processProfiles.Where(kv => kv.Value == id).Select(kv => kv.Key).OrderBy(pid => pid)); }

        void RepaintPet()
        {
            if (inLounge && pet.State != PetState.Resting) inLounge = false;
            journey.AdvanceFrame(pet.Frame);
            localSignal.AdvanceFrame(pet.Frame);
            lounge.Update(pet.State == PetState.Resting, pet.Frame, DisplayText(loungeDetail));
            foreach (Card c in host.Controls.OfType<Card>())
            {
                c.Resident = c.Row.Profile.Id == pet.HomeId && pet.State != PetState.Walking && pet.State != PetState.Resting;
                c.AnimationFrame = pet.Frame; c.Invalidate();
            }
            host.Invalidate();
        }

        bool TravelerPosition(out PointF position)
        {
            position = PointF.Empty;
            if (pet.State == PetState.Resting) return false;
            Card source = host.Controls.OfType<Card>().FirstOrDefault(c => c.Row.Profile.Id == pet.HomeId);
            Card target = host.Controls.OfType<Card>().FirstOrDefault(c => c.Row.Profile.Id == pet.TargetId);
            if (source == null) source = target;
            if (source == null) return false;
            float x = source.Left + Theme.S(19), y = source.Top + Theme.S(12);
            if (pet.State == PetState.Walking && target != null)
            {
                float t = Math.Min(1, pet.Travel), ease = t * t * (3 - 2 * t);
                y += (target.Top - source.Top) * ease;
                x += Theme.S(10) * (float)Math.Sin(t * Math.PI);
            }
            position = new PointF(x, y);
            return true;
        }

        void DrawTraveler(Graphics g, Point origin)
        {
            PointF position;
            if (TravelerPosition(out position))
                PetScene.Sprite(g, position.X - origin.X, position.Y - origin.Y, pet.State, pet.Frame, false, petInteraction);
        }

        PetReaction PetHit(Control surface, Point clientPoint)
        {
            if (switching || pet.State == PetState.Packing || pet.State == PetState.Syncing || pet.State == PetState.Walking) return PetReaction.None;
            PointF position;
            if (!TravelerPosition(out position)) return PetReaction.None;
            Point mouse = host.PointToClient(surface.PointToScreen(clientPoint));
            float dpi = Theme.Dpi / 96f;
            float x = (mouse.X - position.X) / dpi, y = (mouse.Y - position.Y) / dpi;
            if (x >= 1 && x <= 36 && y >= 5 && y <= 35) return y < 19 ? PetReaction.Pat : PetReaction.Wave;
            // Cup at card (79,38), with a little extra hit area around its visible pixels.
            if (!petInteraction.IsTea && x >= 51 && x <= 81 && y >= 15 && y <= 47) return PetReaction.Tea;
            return PetReaction.None;
        }

        void HoverPet(Control surface, Point point)
        {
            PetReaction target = PetHit(surface, point);
            surface.Cursor = target == PetReaction.None ? Cursors.Default : Cursors.Hand;
            Card card = surface as Card;
            if (card != null && card.CupHover != (target == PetReaction.Tea)) { card.CupHover = target == PetReaction.Tea; card.Invalidate(); }
            string hint = target == PetReaction.Pat ? "摸摸小克的头" : target == PetReaction.Wave ? "和小克打个招呼" : target == PetReaction.Tea ? "请小克喝茶" : "";
            if (petTip.GetToolTip(surface) != hint) petTip.SetToolTip(surface, hint);
        }

        void InteractWithPet(Control surface, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left) return;
            PetReaction target = PetHit(surface, args.Location);
            if (target == PetReaction.None || !petInteraction.TryReact(target)) return;
            petTip.Show(petInteraction.Message, surface, new Point(Math.Max(0, args.X), Math.Max(0, args.Y - Theme.S(28))), 1800);
            RepaintPet();
        }

        void PetTick()
        {
            pet.Frame++;
            if (!switching && pet.Frame % 10 == 0)
            {
                uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                string id;
                var map = processProfiles;
                if (map.TryGetValue((int)pid, out id) && id != pet.HomeId) RestPet(engine.Find(id), "小克跟着你来到这个账号。点切换时会自动打包同步。");
            }
            bool shouldRest = activity.Update(DateTime.UtcNow, IdleActivity.IsUsingClaude(processProfiles.Keys), backgroundBusy, switching);
            if (!switching && shouldRest != (pet.State == PetState.Resting))
                SetResting(shouldRest, "暂时没在使用 Claude，小克喝杯茶等你。任务状态不会因此改变。");
            // Keep tracking the active Claude window in the tray, but do not paint hidden animation.
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            if (pet.State == PetState.Walking) pet.Travel = Math.Min(1f, (float)(DateTime.UtcNow - pet.WalkStarted).TotalMilliseconds / 760f);
            PointF position;
            if (TravelerPosition(out position))
            {
                Point mouse = host.PointToClient(Cursor.Position);
                float dpi = Theme.Dpi / 96f;
                petInteraction.Advance(new PointF((mouse.X - position.X) / dpi, (mouse.Y - position.Y) / dpi));
            }
            else petInteraction.Advance(null);
            RepaintPet();
        }

        void SwitchPhase(string phase)
        {
            bool animate = false;
            Invoke((Action)(() =>
            {
                animate = Visible && WindowState != FormWindowState.Minimized;
                if (phase == "packing") { pet.State = PetState.Packing; SetJourney("小克正在收拾行李", "把已保存的会话记录装进行李箱…", pet.State); }
                else if (phase == "relay-saving") { pet.State = PetState.Packing; SetJourney("小克正在保存任务进度", "先保存完整会话，再核验原任务所在窗口。", pet.State); }
                else if (phase == "relay-pausing") { pet.State = PetState.Waiting; SetJourney("小克正在暂停原任务", "确认工具已结束、记录稳定后，才会去新账号。", pet.State); }
                else if (phase == "relay-resuming") { pet.State = PetState.Syncing; SetJourney("小克正在新账号续接", "新会话会读取交接文件，先核对已完成部分。", pet.State); }
                else if (phase == "knowledge") { pet.State = PetState.Packing; SetJourney("小克正在装好记忆与 Skills", "保存本地资料和项目说明，让新会话带着上下文继续。", pet.State); }
                else if (phase == "syncing") { pet.State = PetState.Syncing; SetJourney("小克正在同步进度", "正在把会话记录同步到目标账号；完成后才出发。", pet.State); }
                else if (phase == "opening") { pet.Walk(); SetJourney("小克出发啦", "正在前往新工位，到达后确认交接结果…", pet.State); }
                RepaintPet();
            }));
            if (animate && phase == "packing") Thread.Sleep(520);
            if (animate && phase == "opening") Thread.Sleep(800);
        }

        void SwitchEnabled(bool enabled)
        {
            addCard.Enabled = enabled; syncBtn.Enabled = enabled; autoRelay.Enabled = enabled; knowledgeBtn.Enabled = enabled;
            foreach (Card c in host.Controls.OfType<Card>()) { c.Go.Enabled = enabled && c.Row.PrimaryEnabled; c.More.Enabled = enabled; }
        }

        void RefreshCards() { Background(() => { }, true); }

        void UsageUpdated(Profile profile, UsageHistoryResult result)
        {
            UI(() =>
            {
                Row row = latestRows.FirstOrDefault(r => r.Profile.Id == profile.Id);
                if (row == null) return;
                if (result.Snapshot != null && !string.Equals(result.Snapshot.AccountId,
                    engine.ActiveAccount(row.Profile), StringComparison.OrdinalIgnoreCase)) return;
                row.Usage = result.Snapshot; row.UsageMessage = result.Message;
                row.UsageSignature = UsagePanel.Description(row.Usage, row.UsageMessage);
                foreach (Card card in host.Controls.OfType<Card>().Where(c => c.Row.Profile.Id == profile.Id))
                    card.UpdateUsage(row.Usage, DisplayText(row.UsageMessage));
            });
        }

        List<Row> Gather()
        {
            var running = new List<Instance>();
            try { running = App.Instances(); } catch (Exception ex) { engine.Log("process list: " + ex.Message); }
            var mapping = new Dictionary<int, string>();
            foreach (Instance instance in running)
            {
                Profile owner = engine.Profiles.FirstOrDefault(p => App.Belongs(instance, p));
                if (owner != null) mapping[instance.Pid] = owner.Id;
            }
            processProfiles = mapping;
            try { engine.LearnIdentities(); } catch (Exception ex) { engine.Log("identify: " + ex.Message); }
            int runningKnown = engine.Profiles.Count(p => running.Any(i => App.Belongs(i, p)));
            var rows = new List<Row>();
            foreach (Profile p in engine.Profiles.ToList())
            {
                string active; int count; long lastAt;
                engine.Summary(p, out active, out count, out lastAt);
                bool on = running.Any(i => App.Belongs(i, p));
                bool noDir = active != null && !Directory.Exists(IOPath.Combine(p.Registry, active));
                var r = new Row { Profile = p, Name = engine.Display(p), Running = on };
                UsageHistoryResult usage = usageMonitor.Read(p);
                r.Usage = usage.Snapshot; r.UsageMessage = usage.Message;
                r.UsageSignature = UsagePanel.Description(r.Usage, r.UsageMessage);
                r.Badge = active != null && engine.IsDuplicate(p) ? "和另一张卡是同一个账号" : (on ? "运行中" : (active == null ? "未登录" : (noDir ? "待新建会话" : null)));
                string mail = engine.EmailOf(active);
                string who = active == null ? null : (mail == null ? "邮箱未识别" : (mail == r.Name ? active.Substring(0, 8) : mail));
                if (active == null) r.Sub = "未确认登录 · 请在这个独立窗口登录要添加的账号";
                else if (noDir) r.Sub = who + " · 新建一个 Code 会话后才能同步";
                else r.Sub = who + " · " + count + " 个会话" + (lastAt == 0 ? "" : " · 最近 " + DateTimeOffset.FromUnixTimeMilliseconds(lastAt).LocalDateTime.ToString("MM-dd HH:mm"));
                bool duplicate = active != null && engine.IsDuplicate(p);
                if (duplicate) r.Sub = "登录了同一个账号 · 添加其他账号时，请在浏览器选择其他账号";
                bool onlyOne = on && runningKnown == 1 && active != null && !duplicate;
                r.Primary = onlyOne ? "使用中" : (active == null ? "打开并登录" : "切换");
                r.PrimaryEnabled = !onlyOne;
                rows.Add(r);
            }
            return rows;
        }

        void Apply(List<Row> rows)
        {
            // A general refresh may have been gathered before a newer sampler event.
            // Keep each limit's newest observation instead of briefly rolling the card back.
            foreach (Row row in rows)
            {
                Row previous = latestRows.FirstOrDefault(r => r.Profile.Id == row.Profile.Id);
                if (previous == null || previous.Usage == null) continue;
                if (row.Usage == null)
                {
                    if (!string.Equals(previous.Usage.AccountId, engine.ActiveAccount(row.Profile), StringComparison.OrdinalIgnoreCase)) continue;
                    row.Usage = previous.Usage; row.UsageMessage = previous.UsageMessage;
                }
                else
                {
                    UsageSnapshot merged = UsageMonitor.PreferNewest(row.Usage, previous.Usage);
                    if (merged.ObservedAt > row.Usage.ObservedAt) row.UsageMessage = previous.UsageMessage;
                    row.Usage = merged;
                }
                row.UsageSignature = UsagePanel.Description(row.Usage, row.UsageMessage);
            }
            latestRows = rows;
            accountsNav.CountText = rows.Count.ToString();
            rows = rows.Select(r => r.ForDisplay(emailPrivacy)).ToList();
            foreach (string id in refreshProcesses.Keys.ToList())
                if (refreshProcesses[id] != ProfileProcesses(id))
                {
                    refreshProcesses.Remove(id);
                    if (!switching && pet.HomeId == id) RestPet(engine.Find(id), null);
                }
            if (!switching && !rows.Any(r => r.Profile.Id == pet.HomeId))
            {
                Row next = rows.FirstOrDefault(r => r.Running) ?? rows.FirstOrDefault();
                if (next != null) RestPet(next.Profile, null);
            }
            string sig = string.Join("\n", rows.Select(r => r.Signature));
            if (sig == shownSignature) return;
            shownSignature = sig;
            List<Card> current = host.Controls.OfType<Card>().ToList();
            if (current.Select(c => c.Row.Profile.Id).SequenceEqual(rows.Select(r => r.Profile.Id)))
            {
                for (int i = 0; i < rows.Count; i++) current[i].Update(rows[i]);
                SwitchEnabled(!switching); RepaintPet();
                return;
            }
            host.SuspendLayout();
            var old = host.Controls.Cast<Control>().ToList();
            host.Controls.Clear();
            foreach (Control c in old) if (c != addCard) c.Dispose();
            foreach (Row r in rows)
            {
                var card = new Card(r);
                card.Interaction = petInteraction;
                card.MouseDown += (s, a) => InteractWithPet(card, a);
                card.MouseMove += (s, a) => HoverPet(card, a.Location);
                card.MouseLeave += (s, a) => { card.Cursor = Cursors.Default; card.CupHover = false; card.Invalidate(); };
                card.PaintOffice = (g, c) => DrawTraveler(g, new Point(c.Left, c.Top));
                Pill more = card.More;
                card.Go.Click += (s, a) => DoSwitch(card.Row.Profile, false);
                more.Click += (s, a) => ShowMore(card.Row.Profile, card.Row.Running, more);
                host.Controls.Add(card);
            }
            host.Controls.Add(addCard);
            FitCards();
            host.ResumeLayout();
            SwitchEnabled(!switching); RepaintPet();
        }

        void ShowMore(Profile p, bool on, Control anchor)
        {
            var m = new ContextMenuStrip();
            StyleMenu(m);
            var side = new ToolStripMenuItem("关闭其他窗口并切换", null, (s, a) => DoSwitch(p, true));
            // Grouped as: look, switch, manage, sign-in repair.
            m.Items.Add(new ToolStripMenuItem("额度详情 / 重新读取", null, (s, a) => DoUsage(p)));
            m.Items.Add(new ToolStripMenuItem("接力记录", null, (s, a) => { string folder = IOPath.Combine(engine.StateDir, "relay"); Directory.CreateDirectory(folder); OpenPath(folder); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("只切换（原任务继续运行）", null, (s, a) => DoSwitch(p, false, false)));
            m.Items.Add(side);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("重命名", null, (s, a) => DoRename(p)));
            var rm = new ToolStripMenuItem("从列表移除", null, (s, a) => DoRemove(p)) { Enabled = p.Id != "default" };
            m.Items.Add(rm);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("登录帮助 / 修复授权回调", null, (s, a) => DoManualRoute(p)));
            m.Items.Add(new ToolStripMenuItem("设置 Windows 授权回调（仅需一次）", null, (s, a) =>
            {
                try { ProtocolRegistration.OpenSettings(engine, Scheme); SetStatus("在 Windows 设置中，将 CLAUDE 链接选择为“Claude Switch授权助手”。"); }
                catch (Exception ex) { SetStatus("无法打开设置：" + ex.Message); }
            }));
            m.Items.Add(new ToolStripMenuItem("粘贴授权链接到这个账号", null, (s, a) => PasteCallback(p)));
            foreach (ToolStripItem i in m.Items) if (i.Enabled) i.ForeColor = Theme.Ink;
            m.Closed += (s, a) => BeginInvoke((Action)m.Dispose);
            m.Show(anchor, new Point(0, anchor.Height));
        }

        void RouteTick()
        {
            string ended;
            string name = route.Active ? engine.Display(route.Target) : null;
            try { ended = route.Tick(); }
            catch (Exception ex) { engine.Log("route: " + ex.Message); return; }
            if (ended == "signed-in")
            {
                SetStatus(name + " 的窗口已登录，回调已恢复。在里面新建一个 Code 会话，会话就会自动同步过去。");
                if (!switching) RestPet(engine.Find(pet.HomeId), "登录完成，小克准备好接收会话进度了。");
                RefreshCards();
            }
            else if (ended == "duplicate")
            {
                SetStatus("这个窗口登录了已有账号。请在新窗口退出后重登，并在浏览器选择“使用其他账号”。");
                RefreshCards();
            }
            else if (ended == "route-ready")
                SetStatus("Windows 授权回调已就绪。请在新窗口登录，并在浏览器选择其他账号。");
            else if (ended == "expired")
                SetStatus("回调转发已到时间并恢复。还要登录的话，再点一次“打开并登录”。");
        }

        void BeginLogin(Profile p, string exe)
        {
            try
            {
                if (route.Start(p, exe, true, LoginMinutes))
                    SetStatus(route.RouteVerified
                        ? "请在此窗口登录；浏览器中选择“使用其他账号”。完成后回到这里检查账号。"
                        : "请先点“··· → 设置 Windows 授权回调”；也可以粘贴授权链接完成登录。");
                else
                    SetStatus("已打开“" + engine.Display(p) + "”的窗口，但没能设置登录回调，详情见日志。");
            }
            catch (Exception ex)
            {
                engine.Log("route start: " + ex.Message);
                SetStatus("没能设置登录回调：" + ex.Message);
            }
        }

        void PasteCallback(Profile p)
        {
            if (!App.Instances().Any(i => App.Belongs(i, p)))
            {
                SetStatus("请先打开这个账号窗口，并在其中发起登录，再粘贴本次授权链接。");
                return;
            }
            string value = Dialog.Ask(this, "将授权送到 " + engine.Display(p),
                "在浏览器授权完成页复制“打开 Claude”的链接地址（claude:// 开头），粘贴到这里：", "");
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();
            if (!App.ValidCallback(value, Scheme)) { SetStatus("链接无效：请复制 claude:// 开头的完整链接，不是浏览器地址栏的网址。"); return; }
            string callback = value;
            Background(() =>
            {
                try
                {
                    App.ForwardCallback(p, App.FindExe(App.Instances()), callback, Scheme);
                    UI(() => SetStatus("授权已送到所选窗口，等待 Claude 确认登录。"));
                }
                catch { UI(() => SetStatus("授权链接未能送达，请重新发起登录。")); }
            }, true);
        }

        void DoManualRoute(Profile p)
        {
            Background(() =>
            {
                string exe = App.FindExe(App.Instances());
                UI(() =>
                {
                    try
                    {
                        SetStatus(route.Start(p, exe, engine.ActiveAccount(p) == null, ManualRouteMinutes)
                            ? "浏览器中选择“使用其他账号”；若授权回到旧窗口，使用“··· → 粘贴授权链接”。"
                            : "没能设置回调，详情见日志。");
                    }
                    catch (Exception ex) { engine.Log("route start: " + ex.Message); SetStatus("没能设置回调：" + ex.Message); }
                });
            }, true);
        }

        void DoSwitch(Profile p, bool closeOthers, bool allowRelay = true)
        {
            if (switching) return;
            bool needsLogin = engine.ActiveAccount(p) == null;
            if (needsLogin) closeOthers = false; // signing in never needs another window closed
            if (closeOthers)
            {
                List<Instance> others = App.Instances().Where(i => !App.Belongs(i, p) && engine.Profiles.Any(x => App.Belongs(i, x))).ToList();
                if (others.Count > 0 && !Dialog.Confirm(this, "切换到 " + engine.Display(p),"其他账号的 Claude 窗口会被强制关闭，正在运行的任务会中断。\n需要保留任务的话，取消后直接点卡片上的切换按钮。", "切换"))
                    return;
            }
            if (route.Active && route.Target != p && closeOthers) route.Stop();
            Profile source = engine.Find(pet.HomeId);
            bool relay = allowRelay && autoRelay.Checked && !closeOthers && !needsLogin && source != null && source.Id != p.Id && engine.ActiveAccount(source) != null;
            petInteraction.ClearReaction(); lounge.ClearInteraction();
            switching = true; SwitchEnabled(false); pet.Begin(p.Id);
            SetJourney("小克正在收拾行李", "把已保存的会话记录装进行李箱…", pet.State); RepaintPet();
            SetStatus("正在切换到 " + engine.Display(p) + "…");
            Background(() =>
            {
                try
                {
                string exe = App.FindExe(App.Instances());
                // Arm before opening Claude: a fast browser callback must never beat registration.
                if (needsLogin) Invoke((Action)(() => BeginLogin(p, exe)));
                string relaySkip = null;
                if (relay)
                {
                    List<RelayTask> candidates = RelayState.LiveCandidates(engine, source);
                    RelayTask task = candidates.Count == 1 ? candidates[0] : null;
                    if (candidates.Count > 1)
                    {
                        Invoke((Action)(() => task = Dialog.PickTask(this, candidates)));
                        if (task == null) { UI(() => { RestPet(source, "已取消接力，原任务没有改变。"); SetStatus("已取消切换。"); }); return; }
                    }
                    if (task != null)
                    {
                        UI(() => SetStatus("准备接力：" + task.Title));
                        var coordinator = new RelayCoordinator(engine, new RelayUi(engine));
                        RelayResult result = coordinator.Run(source, p, task, SwitchPhase);
                        engine.Log("relay " + result.Receipt.Id + ": " + result.Receipt.State);
                        UI(() =>
                        {
                            refreshProcesses.Remove(p.Id); RestPet(p, result.Message); SetStatus(result.Message);
                            if (!result.Confirmed) { pet.State = PetState.Waiting; SetJourney("小克已保存进度 · 等待核对", result.Message, pet.State); RepaintPet(); }
                        });
                        return;
                    }
                    relaySkip = "未发现可识别的运行任务，本次仅同步记录并切换。";
                }
                SyncResult r = App.Switch(engine, p, closeOthers, SwitchPhase);
                if (needsLogin)
                {
                    UI(() => RestPet(p, "先完成这个账号的登录，行李里的会话进度才有地方放。"));
                    return;
                }
                string msg = "已切换到 " + engine.Display(p);
                if (r != null && r.Waiting != null) msg += "。" + r.Waiting;
                else if (r != null) msg += "，同步了 " + r.Actions.Count + " 个会话";
                string detail = r != null && r.Waiting != null ? r.Waiting :
                    (r != null && r.Actions.Count > 0 ? "行李送达，已同步 " + r.Actions.Count + " 个会话。" : "进度已核对，本次没有可同步的新记录。");
                if (relaySkip != null) { detail = relaySkip; msg += "。" + relaySkip; }
                if (r != null && r.Knowledge != null)
                {
                    detail += " " + r.Knowledge.Summary;
                    if (!r.Knowledge.Complete) msg += "；本地资料未完整保存，请查看“记忆与 Skills”";
                }
                UI(() =>
                {
                    if (r != null && r.RequiresRefresh) refreshProcesses[p.Id] = ProfileProcesses(p.Id);
                    RestPet(p, detail); SetStatus(msg);
                    if (r != null && r.Waiting != null)
                    {
                        pet.State = PetState.Waiting;
                        SetJourney(r.RequiresRefresh ? "小克已到 · 部分进度待载入" : "小克已到，行李还需确认",
                            r.RequiresRefresh ? "部分记录已保存，需重启载入；" + detail : detail, pet.State); RepaintPet();
                    }
                });
                }
                catch (Exception ex)
                {
                    engine.Log("handoff failed: " + ex.Message);
                    UI(() =>
                    {
                        pet.State = PetState.Error; pet.TargetId = null;
                        SetJourney("小克暂时停下了", ex.Message, pet.State);
                        SetStatus("切换未完成：" + ex.Message); RepaintPet();
                    });
                }
                finally { UI(() => { switching = false; SwitchEnabled(true); }); }
            }, true);
        }

        void DoAdd()
        {
            string name = Dialog.Ask(this, "添加账号", "给另一个账号起个名字。原账号不用退出；稍后在浏览器选择其他账号：", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            Profile p = engine.AddProfile(name.Trim());
            SetStatus("正在打开 " + p.Name + "…");
            Background(() =>
            {
                string exe = App.FindExe(App.Instances());
                Invoke((Action)(() => BeginLogin(p, exe)));
                App.Launch(p, exe);
                App.WaitForProfile(p);
            }, true);
        }

        void DoRename(Profile p)
        {
            string initial = engine.Display(p);
            bool concealed = emailPrivacy.Hidden && EmailPrivacy.ContainsEmail(initial);
            string name = Dialog.Ask(this, "重命名", concealed ? "原名称含邮箱，已隐藏。请填写新的账号昵称：" : (engine.ActiveAccount(p) == null ? "新名称：" : "给这个账号起个名字："), concealed ? "" : initial);
            if (string.IsNullOrWhiteSpace(name)) return;
            engine.Rename(p, name.Trim());
            RefreshCards();
        }

        void DoRemove(Profile p)
        {
            if (p.Id == "default") return;
            if (!Dialog.Confirm(this, "从列表移除", "“" + engine.Display(p) + "”不再显示和同步。它的文件夹和登录状态仍保留在：\n" + p.Dir, "移除")) return;
            engine.Forget(p);
            RefreshCards();
        }

        void DoUsage(Profile p)
        {
            Background(() =>
            {
                usageMonitor.RefreshAsync().Wait();
                UsageHistoryResult result = usageMonitor.Read(p);
                UI(() => Dialog.ShowUsage(this, "额度详情 · " + engine.Display(p), result.Snapshot, result.Message,
                    "打开此账号的 Claude → 设置 → 用量，或 Code 右下角的用量详情，小克会自动读取已用、剩余和重置说明。" +
                    "\r\n普通 Code 页面读取底部按钮显示的五小时或每周额度；展开用量详情可读取两项。收起后，未显示的一项保留原读取时间。" +
                    "\r\n读取时间表示观察页面的时间，不是服务端更新时间；未打开的账号保留历史记录。不会暂停或启动任务。"));
            }, true);
        }

        void DoKnowledge()
        {
            SetStatus("正在核对本地记忆与 Skills…"); knowledgeBtn.Enabled = false;
            Background(() =>
            {
                try
                {
                    LiveKnowledgeResult live = LiveKnowledge.Refresh(engine);
                    if (!live.Ready) throw new IOException(live.Message);
                    KnowledgePackage pack = live.Package;
                    UI(() => { SetStatus(pack.Summary); Dialog.ShowKnowledge(this, pack); });
                }
                finally { UI(() => knowledgeBtn.Enabled = !switching); }
            }, true);
        }

        void DoSyncAll()
        {
            SetStatus("正在同步…");
            Background(() =>
            {
                LiveKnowledgeResult live = LiveKnowledge.Refresh(engine);
                if (!live.Ready) throw new IOException(live.Message);
                KnowledgePackage pack = live.Package;
                List<Instance> running = App.Instances();
                int total = 0;
                var waiting = new List<string>();
                foreach (Profile p in engine.Profiles.ToList())
                {
                    SyncResult r = engine.Sync(p, false);
                    total += r.Actions.Count;
                    if (r.Waiting != null) waiting.Add(engine.Display(p));
                    if (r.Actions.Count > 0 && running.Any(i => App.Belongs(i, p)))
                    {
                        Profile q = p;
                        int n = r.Actions.Count;
                        UI(() => OfferRestart(q, n));
                    }
                }
                string msg = "同步完成：写入 " + total + " 个会话；" + pack.Summary + (waiting.Count > 0 ? "；等待登录或新建会话：" + string.Join("、", waiting) : "");
                UI(() => SetStatus(msg + "（" + DateTime.Now.ToString("HH:mm") + "）"));
            }, true);
        }

        void OfferRestart(Profile p, int n)
        {
            refreshProcesses[p.Id] = ProfileProcesses(p.Id);
            if (!switching && pet.HomeId == p.Id) RestPet(p, null);
            pendingRestart = p;
            tray.ShowBalloonTip(10000, DisplayText("已同步 " + n + " 个会话到 " + engine.Display(p)), "它正在运行，重启后才能看到。点这里重启（会先确认）。", ToolTipIcon.Info);
        }

        void DoRestart(Profile p)
        {
            if (!Dialog.Confirm(this, "重启 " + engine.Display(p), "Claude 窗口会被强制关闭后重新打开，正在运行的会话和任务会中断，对话记录不会丢。", "重启")) return;
            Background(() =>
            {
                List<Instance> running = App.Instances();
                string exe = App.FindExe(running);
                foreach (Instance i in running.Where(i => App.Belongs(i, p))) App.Kill(i);
                Thread.Sleep(2000);
                engine.Sync(p, false);
                App.Launch(p, exe);
            }, true);
        }

        void RefreshLiveKnowledge()
        {
            usageMonitor.RequestRefresh();
            if (switching) return;
            Background(() =>
            {
                try { backgroundBusy = IdleActivity.HasBusyTask(engine); } catch { backgroundBusy = true; }
                LiveKnowledgeResult live = LiveKnowledge.Refresh(engine);
                UI(() =>
                {
                    if (!switching && lastKnowledgeMessage != live.Message)
                    {
                        lastKnowledgeMessage = live.Message;
                        SetStatus(live.Message);
                    }
                });
            }, false);
        }

        // Automatic sync: when an account changes inside a profile, and when a window closes
        // (its last sessions then reach the accounts not currently running).
        void Tick()
        {
            Background(() =>
            {
                List<Instance> running = App.Instances();
                var now = new HashSet<string>(engine.Profiles.Where(p => running.Any(i => App.Belongs(i, p))).Select(p => p.Id));
                bool closed = wasRunning.Any(id => !now.Contains(id));
                wasRunning = now;
                foreach (Profile p in engine.Profiles.ToList())
                {
                    bool isRunning = now.Contains(p.Id);
                    if (!engine.AccountChanged(p) && !(closed && !isRunning)) continue;
                    SyncResult r = engine.Sync(p, false);
                    if (r.Actions.Count > 0 && isRunning)
                    {
                        Profile q = p;
                        int n = r.Actions.Count;
                        UI(() => OfferRestart(q, n));
                    }
                }
            }, false);
        }

        static void OpenPath(string p) { if (File.Exists(p) || Directory.Exists(p)) Process.Start("explorer.exe", "\"" + p + "\""); }

        static bool AutostartEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        static void SetAutostart(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --background");
                else k.DeleteValue(RunName, false);
            }
        }

        // Captures the real on-screen window, including the tinted title bar.
        protected override void Dispose(bool disposing)
        {
            if (disposing) usageMonitor.Updated -= UsageUpdated;
            if (disposing) tokenTimer.Dispose();
            if (disposing) petTimer.Dispose();
            if (disposing) petTip.Dispose();
            if (disposing) privacyTip.Dispose();
            base.Dispose(disposing);
        }

        public void SaveShot(string path)
        {
            TopMost = true;
            Show();
            for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
            Rectangle b = Bounds;
            using (var bmp = new Bitmap(b.Width, b.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            quitting = true;
            tray.Visible = false;
            Hide();
        }
    }
}
