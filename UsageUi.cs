using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    // A record display, not an availability promise: elapsed resets never refill these bars.
    sealed class UsagePanel : Control
    {
        UsageSnapshot snapshot;
        string message;
        readonly Font labelFont = new Font(Theme.SansFamily, 8.5f);
        readonly Font amountFont = new Font(Theme.SansFamily, 14f);
        readonly Font stateFont = new Font(Theme.SansFamily, 10f);
        readonly ToolTip tip = new ToolTip();

        public UsagePanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(72); BackColor = Theme.Surface; AccessibleName = "账号额度记录";
        }

        public void UpdateUsage(UsageSnapshot value, string explanation)
        {
            snapshot = value; message = explanation;
            AccessibleDescription = Description(snapshot, message);
            tip.SetToolTip(this, AccessibleDescription); Invalidate();
        }

        public static string Description(UsageSnapshot value, string explanation)
        {
            var now = DateTimeOffset.UtcNow;
            string text = UsageDisplay.MeterLine(value, "five_hour", now) + "\r\n" + UsageDisplay.MeterLine(value, "seven_day", now);
            if (value != null)
                foreach (var window in value.Windows.Where(w => w.Key != "five_hour" && w.Key != "seven_day"))
                    text += "\r\n" + UsageDisplay.MeterLine(value, window.Key, now);
            return text + "\r\n" + UsageDisplay.Freshness(value, now) + "\r\n" + explanation;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Surface); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, 0, Width, 0);
            int gap = Theme.S(24), column = Math.Max(0, (Width - gap) / 2);
            var now = DateTimeOffset.UtcNow;
            DrawWindow(g, "five_hour", 0, column, now);
            DrawWindow(g, "seven_day", column + gap, column, now);
            string footer = snapshot == null ? (message ?? "尚未读取额度") : UsageDisplay.Freshness(snapshot, now);
            // A filled dot only when every shown limit is a current observation; otherwise a hollow ring.
            bool live = snapshot != null && snapshot.Windows.Count > 0 && snapshot.Windows.All(w => UsageDisplay.CurrentUsedPercent(snapshot, w, now).HasValue);
            int dot = Theme.S(6), footerTop = Theme.S(54), footerHeight = Theme.S(17);
            float dotTop = footerTop + (footerHeight - dot) / 2f;
            if (live) using (var b = new SolidBrush(Theme.Green)) g.FillEllipse(b, .5f, dotTop, dot, dot);
            else using (var pen = new Pen(Theme.Muted, Math.Max(1f, Theme.Dpi / 96f))) g.DrawEllipse(pen, 1f, dotTop + .5f, dot - 1, dot - 1);
            int textLeft = dot + Theme.S(7);
            TextRenderer.DrawText(g, footer, labelFont, new Rectangle(textLeft, footerTop, Math.Max(0, Width - textLeft), footerHeight), Theme.Muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        }

        void DrawWindow(Graphics g, string key, int x, int width, DateTimeOffset now)
        {
            var window = snapshot == null ? null : snapshot.Find(key);
            bool expired = window != null && window.ResetsAt.HasValue && window.ResetsAt.Value <= now;
            bool historical = UsageDisplay.WindowSource(snapshot, window) == UsageSource.DesktopHistory;
            bool known = window != null && window.UsedPercent.HasValue;
            double? used = UsageDisplay.CurrentUsedPercent(snapshot, window, now);
            // The figure stands alone in the large size; its "已用" caption and the state words stay small.
            string figure = used.HasValue ? used.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : expired ? "等待刷新" : !known ? "未知" : "上次已用 " + window.UsedPercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            string caption = used.HasValue ? "已用" : string.Empty;
            Color ink = used.HasValue ? (used.Value >= 90 ? Theme.Accent : Theme.Green) : Theme.Muted;
            string name = key == "five_hour" ? "5 小时" : "每周";
            string note;
            if (used.HasValue) note = " · 剩余 " + (100 - used.Value).ToString("0.#", CultureInfo.InvariantCulture) + "%";
            else if (expired) note = " · 已过期";
            else if (known) note = historical ? " · 历史记录" : " · 待刷新";
            else { name += "额度"; note = string.Empty; }
            const TextFormatFlags line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            Font valueFont = used.HasValue ? amountFont : stateFont;
            int figureWidth = Math.Min(width, TextRenderer.MeasureText(g, figure, valueFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(2));
            int captionWidth = caption.Length == 0 ? 0 : TextRenderer.MeasureText(g, caption, labelFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + Theme.S(5);
            if (figureWidth + captionWidth > width) captionWidth = 0;
            int valueWidth = figureWidth + captionWidth, labelWidth = Math.Max(0, width - valueWidth - Theme.S(10));
            int nameWidth = Math.Min(labelWidth, TextRenderer.MeasureText(g, name, labelFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
            TextRenderer.DrawText(g, name, labelFont, new Rectangle(x, Theme.S(6), nameWidth, Theme.S(20)), Theme.Ink, line | TextFormatFlags.EndEllipsis);
            if (note.Length > 0 && labelWidth > nameWidth)
                TextRenderer.DrawText(g, note, labelFont, new Rectangle(x + nameWidth, Theme.S(6), labelWidth - nameWidth, Theme.S(20)), Theme.Muted, line | TextFormatFlags.EndEllipsis);
            if (captionWidth > 0)
                TextRenderer.DrawText(g, caption, labelFont, new Rectangle(x + width - valueWidth, Theme.S(7), captionWidth, Theme.S(20)), Theme.Muted, line);
            TextRenderer.DrawText(g, figure, valueFont, new Rectangle(x + width - figureWidth, Theme.S(1), figureWidth, Theme.S(26)), ink, line | TextFormatFlags.Right);
            int y = Theme.S(27), barHeight = Theme.S(6);
            if (width > barHeight)
                using (var track = Theme.Round(new RectangleF(x, y, width, barHeight), barHeight / 2f))
                using (var b = new SolidBrush(Theme.Line)) g.FillPath(b, track);
            // Match Claude: the bar and primary number both describe consumed quota.
            // Historical, stale and expired observations never become a current meter.
            if (used.HasValue && used.Value > 0 && width > barHeight)
            {
                // A sliver still reads as a rounded cap; zero paints nothing.
                float filled = Math.Max(barHeight, (float)(width * Math.Min(100, used.Value) / 100));
                using (var fill = Theme.Round(new RectangleF(x, y, filled, barHeight), barHeight / 2f))
                using (var b = new SolidBrush(ink)) g.FillPath(b, fill);
            }
            bool reached = used.HasValue && used.Value >= 100;
            string reset = UsageDisplay.ResetDescription(window, now);
            if (reached) reset = "已用尽 · " + reset;
            TextRenderer.DrawText(g, reset, labelFont, new Rectangle(x, Theme.S(36), width, Theme.S(18)), reached ? Theme.Accent : Theme.Muted, line | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        { if (disposing) { labelFont.Dispose(); amountFont.Dispose(); stateFont.Dispose(); tip.Dispose(); } base.Dispose(disposing); }
    }
}
