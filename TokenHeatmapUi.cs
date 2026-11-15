using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    sealed class TokenHeatmap : Control
    {
        readonly Font small = new Font(Theme.SansFamily, 8.5f);
        readonly Timer animation = new Timer { Interval = 15 };
        readonly Dictionary<DateTime, TokenDay> days = new Dictionary<DateTime, TokenDay>();
        readonly List<Control> watched = new List<Control>();
        TokenHeatmapPopup popup;
        DateTime weekStart, selected;
        DateTime? hoverDate;
        bool loaded, rewiring, disposed;
        int pitch = Theme.S(16), weeks = 1, labelWidth = Theme.S(36), top = Theme.S(20), origin;
        long peak, animationStarted;
        float hoverAmount;
        internal DateTime FirstDate { get; private set; }
        internal DateTime LastDate { get; private set; }
        internal int DayCellCount { get { return (LastDate - FirstDate).Days + 1; } }
        internal DateTime? HoverDate { get { return hoverDate; } }
        internal Rectangle GridContentBounds { get { return new Rectangle(origin, 0, labelWidth + weeks * pitch, Height); } }
        internal string TooltipTitle { get { return hoverDate.HasValue ? DateTitle(hoverDate.Value) : null; } }
        internal string TooltipValue
        {
            get
            {
                if (!hoverDate.HasValue) return null;
                if (!loaded) return "尚未读取";
                TokenDay day;
                return days.TryGetValue(hoverDate.Value, out day) ? FormatTokens(day.Total) : "未记录到用量";
            }
        }

        public TokenHeatmap()
        {
            BackColor = Theme.Surface; TabStop = true; AccessibleName = "每日 Token 热图";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            FirstDate = LastDate = selected = DateTime.Today; weekStart = FirstDate.AddDays(-MondayIndex(FirstDate));
            animation.Tick += (s, e) => AdvanceAnimation();
        }

        static int MondayIndex(DateTime date) { return ((int)date.DayOfWeek + 6) % 7; }
        string DateTitle(DateTime date)
        { return date.ToString(FirstDate.Year == LastDate.Year ? "M月d日" : "yyyy年M月d日", CultureInfo.InvariantCulture); }

        public void SetData(IList<TokenDay> values, DateTime from, DateTime through)
        {
            bool sameRange = FirstDate == from.Date && LastDate == through.Date;
            if (!sameRange) ClearHover();
            loaded = values != null; days.Clear();
            if (values != null) foreach (var day in values) days[day.Date.Date] = day;
            FirstDate = from.Date; LastDate = through.Date;
            weekStart = FirstDate.AddDays(-MondayIndex(FirstDate));
            weeks = (LastDate - weekStart).Days / 7 + 1;
            peak = days.Count == 0 ? 0 : days.Values.Max(d => d.Total);
            if (selected < FirstDate || selected > LastDate) selected = LastDate;
            UpdateDescription();
            if (hoverDate.HasValue) UpdatePopup();
            Invalidate();
        }

        public void FitWidth(int width)
        {
            int nextPitch = Math.Max(Theme.S(12), Math.Min(Theme.S(16), (width - labelWidth - Theme.S(2)) / Math.Max(1, weeks)));
            int content = labelWidth + weeks * nextPitch + Theme.S(2);
            int nextOrigin = Math.Max(0, (width - content) / 2);
            var nextSize = new Size(Math.Max(width, content), top + nextPitch * 7 + Theme.S(2));
            if (pitch != nextPitch || origin != nextOrigin || Size != nextSize) ClearHover();
            pitch = nextPitch; origin = nextOrigin; Size = nextSize;
        }

        internal Rectangle GetCellBounds(DateTime date)
        {
            date = date.Date;
            if (date < FirstDate || date > LastDate) return Rectangle.Empty;
            int n = (date - weekStart).Days;
            return new Rectangle(origin + labelWidth + n / 7 * pitch, top + n % 7 * pitch, pitch - Theme.S(2), pitch - Theme.S(2));
        }

        internal static string FormatTokens(long value)
        {
            if (value >= 100000000L) return (value / 100000000d).ToString("0.#", CultureInfo.InvariantCulture) + "亿 个 Token";
            if (value >= 10000L)
            {
                double wan = Math.Round(value / 10000d, 1, MidpointRounding.AwayFromZero);
                if (wan >= 10000) return "1亿 个 Token";
                return wan.ToString("0.#", CultureInfo.InvariantCulture) + "万 个 Token";
            }
            return value.ToString("N0", CultureInfo.InvariantCulture) + " 个 Token";
        }

        internal string DayDescription(DateTime date)
        {
            if (!loaded) return date.ToString("yyyy-MM-dd") + " · 尚未读取";
            TokenDay day;
            if (!days.TryGetValue(date.Date, out day)) return date.ToString("yyyy-MM-dd dddd") + "\r\n未记录到 Token 用量（不代表云端没有使用）";
            return date.ToString("yyyy-MM-dd dddd") + "\r\n总计 " + TokenStatisticsCanvas.Exact(day.Total) + " Token · " + day.SessionCount + " 个会话\r\n输入 " + TokenStatisticsCanvas.Exact(day.Input) +
                " · 输出 " + TokenStatisticsCanvas.Exact(day.Output) + "\r\n缓存读取 " + TokenStatisticsCanvas.Exact(day.CacheRead) + " · 缓存写入 " + TokenStatisticsCanvas.Exact(day.CacheWrite);
        }

        internal static Color LevelColor(int level)
        {
            int[] colors = Theme.Dark ? new[] { 0x45453F, 0x26476D, 0x326CA2, 0x438DCE, 0x79B8FA } : new[] { 0xE8E6E1, 0xC8E1FA, 0x82B8F5, 0x398AE4, 0x1558B0 };
            int rgb = colors[Math.Max(0, Math.Min(4, level))];
            return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics; g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            string[] weekday = { "周一", "周三", "周五", "周日" };
            for (int i = 0; i < 4; i++)
                TextRenderer.DrawText(g, weekday[i], small, new Rectangle(origin, top + i * 2 * pitch - Theme.S(1), labelWidth - Theme.S(4), pitch), Theme.Muted, flags);
            int previousRight = -1;
            for (DateTime d = FirstDate; d <= LastDate; d = d.AddDays(1))
            {
                Rectangle r = GetCellBounds(d);
                if (d == FirstDate || d.Day == 1)
                {
                    string label = d.Month == 1 || d == FirstDate ? d.ToString("yyyy/M") : d.Month + "月";
                    int width = TextRenderer.MeasureText(g, label, small, Size.Empty, TextFormatFlags.NoPadding).Width;
                    int left = Math.Min(r.X, Width - width);
                    if (left > previousRight + Theme.S(5))
                    { TextRenderer.DrawText(g, label, small, new Rectangle(left, 0, width + 1, top - Theme.S(2)), Theme.Muted, flags); previousRight = left + width; }
                }
                if (!e.ClipRectangle.IntersectsWith(r)) continue;
                TokenDay day; long total = days.TryGetValue(d, out day) ? day.Total : 0;
                int level = total <= 0 || peak <= 0 ? 0 : Math.Min(4, Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)total / peak) * 4)));
                using (var shape = Theme.Round(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), Math.Max(1, Theme.S(3))))
                {
                    if (!loaded) { using (var pen = new Pen(Theme.Line)) g.DrawPath(pen, shape); }
                    else
                    {
                        Color color = LevelColor(level);
                        if (hoverDate == d) color = Theme.Mix(color, Theme.Dark ? Color.White : Color.FromArgb(25, 94, 172), .15f * hoverAmount);
                        using (var brush = new SolidBrush(color)) g.FillPath(brush, shape);
                    }
                    if (hoverDate == d || (Focused && selected == d))
                    {
                        int alpha = hoverDate == d ? (int)(100 + 155 * hoverAmount) : 180;
                        using (var pen = new Pen(Color.FromArgb(alpha, Theme.Dark ? Color.FromArgb(180, 215, 255) : Color.FromArgb(27, 93, 166)), Math.Max(1, Theme.Dpi / 80f)))
                            g.DrawPath(pen, shape);
                    }
                }
            }
        }

        internal DateTime? DateAt(Point point)
        {
            int left = origin + labelWidth;
            if (point.X < left || point.Y < top || point.Y >= top + pitch * 7) return null;
            int col = (point.X - left) / pitch, row = (point.Y - top) / pitch;
            if (col >= weeks) return null;
            DateTime date = weekStart.AddDays(col * 7 + row);
            return date >= FirstDate && date <= LastDate && GetCellBounds(date).Contains(point) ? date : (DateTime?)null;
        }

        internal void SetHoverDate(DateTime? date, bool keyboard = false)
        {
            if (date.HasValue) date = date.Value.Date;
            if (date.HasValue && (date < FirstDate || date > LastDate)) date = null;
            if (!date.HasValue) { ClearHover(); return; }
            bool changed = hoverDate != date;
            var old = hoverDate; hoverDate = date;
            if (keyboard) selected = date.Value;
            if (changed)
            {
                hoverAmount = .25f; animationStarted = Stopwatch.GetTimestamp();
                if (Visible && IsHandleCreated) animation.Start();
                if (old.HasValue) Invalidate(GetCellBounds(old.Value));
                Invalidate(GetCellBounds(date.Value));
            }
            Cursor = Cursors.Hand;
            AccessibleDescription = DayDescription(date.Value);
            UpdatePopup();
        }

        internal void ClearHover()
        {
            var old = hoverDate; hoverDate = null; hoverAmount = 0;
            animation.Stop();
            if (popup != null && !popup.IsDisposed) popup.Hide();
            Cursor = Cursors.Default;
            if (old.HasValue) Invalidate(GetCellBounds(old.Value));
            UpdateDescription();
        }

        void UpdateDescription()
        {
            AccessibleDescription = hoverDate.HasValue ? DayDescription(hoverDate.Value) :
                "每列一周，从周一至周日；" + FirstDate.ToString("yyyy-MM-dd") + " 至 " + LastDate.ToString("yyyy-MM-dd") +
                "。悬停显示日期与用量；方向键逐日查看，Home 与 End 跳到首末日。";
        }

        void AdvanceAnimation()
        {
            if (!hoverDate.HasValue || !Visible || disposed) { animation.Stop(); return; }
            float t = (float)((Stopwatch.GetTimestamp() - animationStarted) / (double)Stopwatch.Frequency / .10);
            hoverAmount = Math.Min(1, .25f + .75f * (1 - (float)Math.Pow(1 - Math.Min(1, t), 3)));
            Invalidate(GetCellBounds(hoverDate.Value));
            if (popup != null && popup.Visible) popup.SetFade(.75f + .25f * hoverAmount);
            if (t >= 1) animation.Stop();
        }

        void UpdatePopup()
        {
            if (!hoverDate.HasValue || !Visible || !IsHandleCreated || disposed) return;
            Form owner = FindForm();
            // A never-shown fixture can exercise hover state without opening any native window.
            if (owner == null || !owner.Visible || owner.WindowState == FormWindowState.Minimized || Form.ActiveForm != owner) return;
            Rectangle cell = RectangleToScreen(GetCellBounds(hoverDate.Value));
            Rectangle visibleCell = cell;
            for (Control c = Parent; c != null; c = c.Parent)
                visibleCell = Rectangle.Intersect(visibleCell, c.RectangleToScreen(c.ClientRectangle));
            if (visibleCell.Width <= 0 || visibleCell.Height <= 0) { ClearHover(); return; }
            Rectangle available = Rectangle.Intersect(Screen.FromRectangle(cell).WorkingArea, owner.RectangleToScreen(owner.ClientRectangle));
            available.Inflate(-Theme.S(4), -Theme.S(4));
            if (popup == null || popup.IsDisposed) popup = new TokenHeatmapPopup();
            popup.Present(owner, cell, available, TooltipTitle, TooltipValue, .75f + .25f * hoverAmount);
        }

        void WireAncestors()
        {
            if (rewiring || disposed) return;
            rewiring = true;
            try
            {
                foreach (Control c in watched) Unwatch(c);
                watched.Clear();
                for (Control c = Parent; c != null; c = c.Parent)
                {
                    watched.Add(c); c.ParentChanged += AncestorParentChanged; c.VisibleChanged += AncestorChanged;
                    c.LocationChanged += AncestorChanged; c.SizeChanged += AncestorChanged;
                    var scroll = c as ScrollableControl; if (scroll != null) scroll.Scroll += AncestorScroll;
                    var form = c as Form; if (form != null) form.Deactivate += AncestorChanged;
                }
            }
            finally { rewiring = false; }
        }
        void Unwatch(Control c)
        {
            c.ParentChanged -= AncestorParentChanged; c.VisibleChanged -= AncestorChanged; c.LocationChanged -= AncestorChanged; c.SizeChanged -= AncestorChanged;
            var scroll = c as ScrollableControl; if (scroll != null) scroll.Scroll -= AncestorScroll;
            var form = c as Form; if (form != null) form.Deactivate -= AncestorChanged;
        }
        void AncestorParentChanged(object sender, EventArgs e) { ClearHover(); WireAncestors(); }
        void AncestorChanged(object sender, EventArgs e) { ClearHover(); }
        void AncestorScroll(object sender, ScrollEventArgs e) { ClearHover(); }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); if (animation != null) { ClearHover(); WireAncestors(); } }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); WireAncestors(); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (animation != null) ClearHover(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible && animation != null) ClearHover(); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHoverDate(DateAt(e.Location)); }
        protected override void OnMouseLeave(EventArgs e) { ClearHover(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); var date = DateAt(e.Location);
            if (e.Button == MouseButtons.Left && date.HasValue) { selected = date.Value; Focus(); SetHoverDate(date, true); }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); SetHoverDate(selected, true); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { ClearHover(); base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData)
        { Keys key = keyData & Keys.KeyCode; return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { ClearHover(); e.Handled = e.SuppressKeyPress = true; return; }
            int delta = e.KeyCode == Keys.Left ? -7 : e.KeyCode == Keys.Right ? 7 : e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
            if (delta == 0 && e.KeyCode != Keys.Home && e.KeyCode != Keys.End) return;
            selected = e.KeyCode == Keys.Home ? FirstDate : e.KeyCode == Keys.End ? LastDate : selected.AddDays(delta);
            if (selected < FirstDate) selected = FirstDate; if (selected > LastDate) selected = LastDate;
            var host = Parent as ScrollableControl;
            Rectangle r = GetCellBounds(selected);
            if (host != null)
            {
                int scroll = -host.AutoScrollPosition.X;
                if (r.Left < scroll || r.Right > scroll + host.ClientSize.Width)
                    host.AutoScrollPosition = new Point(Math.Max(0, r.Left - labelWidth), 0);
            }
            SetHoverDate(selected, true); Invalidate(); e.Handled = e.SuppressKeyPress = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                ClearHover(); foreach (Control c in watched) Unwatch(c);
                watched.Clear(); disposed = true; small.Dispose(); animation.Dispose();
                if (popup != null) popup.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Owned and click-through: it escapes chart clipping without stealing the pointer or focus.
    sealed class TokenHeatmapPopup : Form
    {
        Bitmap bitmap;
        string title, value;
        float dpi;
        public TokenHeatmapPopup()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; AccessibleName = "每日 Token 用量";
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00080000; return p; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; }
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
            base.WndProc(ref m);
        }

        internal static Rectangle CalculatePopupBounds(Rectangle cell, Size size, Rectangle available)
        {
            if (available.Width <= 0 || available.Height <= 0) return Rectangle.Empty;
            int gap = Theme.S(6);
            int x = cell.Left + cell.Width / 2 - size.Width / 2;
            x = Math.Max(available.Left, Math.Min(x, available.Right - size.Width));
            int y = cell.Top - size.Height - gap;
            if (y < available.Top) y = cell.Bottom + gap;
            if (y + size.Height > available.Bottom)
            {
                y = Math.Max(available.Top, Math.Min(cell.Top, available.Bottom - size.Height));
                int right = cell.Right + gap, left = cell.Left - size.Width - gap;
                if (right + size.Width <= available.Right) x = right;
                else if (left >= available.Left) x = left;
            }
            y = Math.Max(available.Top, Math.Min(y, available.Bottom - size.Height));
            return new Rectangle(x, y, Math.Min(size.Width, available.Width), Math.Min(size.Height, available.Height));
        }

        internal static Bitmap RenderBubble(string title, string value, float dpi)
        {
            float scale = dpi / 96f;
            using (var titleFont = new Font(Theme.SansFamily, 11f, FontStyle.Bold))
            using (var valueFont = new Font(Theme.SansFamily, 10.5f))
            using (var probe = new Bitmap(1, 1))
            {
                probe.SetResolution(dpi, dpi);
                SizeF titleSize, valueSize;
                using (Graphics g = Graphics.FromImage(probe))
                { titleSize = g.MeasureString(title, titleFont); valueSize = g.MeasureString(value, valueFont); }
                int shadow = (int)Math.Ceiling(10 * scale), padding = (int)Math.Ceiling(16 * scale);
                int bodyWidth = Math.Max((int)Math.Ceiling(160 * scale), (int)Math.Ceiling(Math.Max(titleSize.Width, valueSize.Width)) + padding * 2);
                int line = (int)Math.Ceiling(23 * scale), bodyHeight = (int)Math.Ceiling(20 * scale) + line * 2;
                var result = new Bitmap(bodyWidth + shadow * 2, bodyHeight + shadow * 2, PixelFormat.Format32bppArgb);
                result.SetResolution(dpi, dpi);
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    var body = new RectangleF(shadow, shadow - 2 * scale, bodyWidth, bodyHeight);
                    for (int i = shadow; i >= 1; i--)
                    {
                        var spread = body; spread.Offset(0, 3 * scale); spread.Inflate(i, i);
                        using (var shape = Theme.Round(spread, 16 * scale + i))
                        using (var brush = new SolidBrush(Color.FromArgb(3 + (shadow - i) / 3, 0, 0, 0))) g.FillPath(brush, shape);
                    }
                    using (var shape = Theme.Round(body, 16 * scale))
                    using (var brush = new SolidBrush(Color.FromArgb(255, 26, 29, 31))) g.FillPath(brush, shape);
                    using (var brush = new SolidBrush(Color.White))
                    using (var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                    {
                        g.DrawString(title, titleFont, brush, new RectangleF(body.X + padding, body.Y + 8 * scale, body.Width - padding * 2, line), format);
                        g.DrawString(value, valueFont, brush, new RectangleF(body.X + padding, body.Y + 8 * scale + line, body.Width - padding * 2, line), format);
                    }
                }
                return result;
            }
        }

        internal void Present(Form owner, Rectangle anchor, Rectangle available, string heading, string amount, float fade)
        {
            if (bitmap == null || title != heading || value != amount || dpi != Theme.Dpi)
            {
                if (bitmap != null) bitmap.Dispose();
                title = heading; value = amount; dpi = Theme.Dpi; bitmap = RenderBubble(title, value, dpi);
                AccessibleDescription = title + "，" + value;
            }
            Rectangle bounds = CalculatePopupBounds(anchor, bitmap.Size, available);
            if (bounds.IsEmpty) { Hide(); return; }
            Bounds = bounds;
            if (Owner != owner) Owner = owner;
            SetFade(fade);
            if (!Visible) Show(owner);
        }

        internal void SetFade(float opacity)
        {
            if (bitmap == null || IsDisposed) return;
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), native = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                native = bitmap.GetHbitmap(Color.FromArgb(0)); previous = SelectObject(memory, native);
                var location = new NativePoint(Left, Top); var source = new NativePoint(0, 0); var size = new NativeSize(Width, Height);
                var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = (byte)(255 * Math.Max(0, Math.Min(1, opacity))), AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref location, ref size, memory, ref source, 0, ref blend, 2);
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (native != IntPtr.Zero) DeleteObject(native);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing && bitmap != null) { bitmap.Dispose(); bitmap = null; } base.Dispose(disposing); }
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; public NativePoint(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int Width, Height; public NativeSize(int width, int height) { Width = width; Height = height; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref NativePoint destination,
            ref NativeSize size, IntPtr source, ref NativePoint sourcePoint, int colorKey, ref BlendFunction blend, int flags);
    }
}
