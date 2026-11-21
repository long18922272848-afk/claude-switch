using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    // All hit targets and furniture share a single, square room coordinate system.
    sealed class OfficeMap : Control
    {
        const int RoomSize = 480;
        const float StationWidth = 154f, FurnitureWidth = 137f;
        const float BadgeTop = 5f, BadgeHeight = 22f, FurnitureTop = 32f, FootMargin = 7f;
        // Use the visible artwork proportions for both painting and hit testing.
        static readonly Rectangle LiveCrop = new Rectangle(203, 85, 1042, 928);
        static readonly Rectangle SealedCrop = new Rectangle(169, 109, 1112, 910);
        readonly List<OfficeDesk> desks = new List<OfficeDesk>();
        readonly List<RectangleF> stations = new List<RectangleF>();
        int[] readingOrder = new int[0];
        readonly Font deskFont = new Font(Theme.SansFamily, 13f, FontStyle.Bold, GraphicsUnit.Pixel);
        readonly Font roomFont = new Font(Theme.SansFamily, 12f, GraphicsUnit.Pixel);
        readonly ToolTip tip = new ToolTip();
        Bitmap floor, liveDesk, sealedDesk, dust;
        string occupant;
        int selected = -1, hover = -1;
        float zoom = 1;
        PointF offset;
        public event EventHandler SelectionChanged;
        public OfficeDesk SelectedDesk { get { return selected < 0 ? null : desks[selected]; } }

        public OfficeMap()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg; TabStop = true; AccessibleName = "账号办公室地图";
            tip.InitialDelay = 300; tip.ReshowDelay = 100;
        }

        public void SetDesks(IList<OfficeDesk> records, string residentId)
        {
            if (records == null) throw new ArgumentNullException("records");
            if (desks.Count == records.Count && desks.Zip(records, SameDesk).All(same => same) && occupant == residentId) return;
            string selectedKey = SelectedDesk == null ? null : SelectedDesk.Key;
            desks.Clear(); desks.AddRange(records); occupant = residentId;
            selected = desks.FindIndex(d => d.Key == selectedKey);
            if (selected < 0 && residentId != null) selected = desks.FindIndex(d => !d.Archived && d.ProfileId == residentId);
            if (selected < 0 && desks.Count != 0) selected = 0;
            hover = -1; Cursor = Cursors.Default; tip.SetToolTip(this, null);
            LayoutStations(); AnnounceSelection(); Invalidate();
        }

        static bool SameDesk(OfficeDesk a, OfficeDesk b)
        {
            return a.Key == b.Key && a.Name == b.Name && a.Archived == b.Archived &&
                a.ProfileId == b.ProfileId && a.Started == b.Started && a.Ended == b.Ended;
        }

        void LayoutStations()
        {
            stations.Clear();
            readingOrder = new int[0];
            if (desks.Count == 0) return;
            // Allocate the dusty desks nearest the far corner, then fill the other seats in reading order.
            int columns = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(desks.Count)));
            int cellCount = columns * columns;
            var cells = Enumerable.Range(0, cellCount);
            var cornerCells = cells.OrderBy(slot =>
            {
                int dx = columns - 1 - slot % columns, dy = columns - 1 - slot / columns;
                return dx * dx + dy * dy;
            }).ThenByDescending(slot => slot).Take(desks.Count(d => d.Archived)).ToArray();
            var reserved = new HashSet<int>(cornerCells);
            var free = new Queue<int>(cells.Where(slot => !reserved.Contains(slot)));
            var corner = new Queue<int>(cornerCells);
            float width = 414f / columns, height = 407f / columns;
            float scale = Math.Min(1.04f, Math.Min(width * .90f / StationWidth, height * .92f / StationHeight(false)));
            var cellByStation = new List<int>(desks.Count);
            foreach (OfficeDesk desk in desks)
            {
                int cell = desk.Archived ? corner.Dequeue() : free.Dequeue();
                cellByStation.Add(cell);
                float w = StationWidth * scale, h = StationHeight(desk.Archived) * scale;
                float left = 50 + (cell % columns) * width, top = 58 + (cell / columns) * height;
                float x = desk.Archived ? left + width - w - 1 : left + (width - w) * .5f;
                float y = desk.Archived ? top + height - h - 1 : top + (height - h) * .48f;
                stations.Add(new RectangleF(x, y, w, h));
            }
            readingOrder = Enumerable.Range(0, desks.Count).OrderBy(i => cellByStation[i]).ToArray();
        }

        static float StationHeight(bool archived)
        {
            Rectangle crop = archived ? SealedCrop : LiveCrop;
            return FurnitureTop + FurnitureWidth * crop.Height / crop.Width + FootMargin;
        }

        RectangleF View
        {
            get
            {
                float side = Math.Max(1, Math.Min(ClientSize.Width, ClientSize.Height) - 2);
                return new RectangleF((ClientSize.Width - side) / 2, (ClientSize.Height - side) / 2, side, side);
            }
        }

        PointF MapPoint(Point p)
        {
            RectangleF view = View; float scale = RoomSize / (view.Width * zoom);
            return new PointF(offset.X + (p.X - view.X) * scale, offset.Y + (p.Y - view.Y) * scale);
        }

        int HitIndex(Point client)
        {
            if (!View.Contains(client)) return -1;
            PointF local = MapPoint(client);
            return stations.FindIndex(station => station.Contains(local));
        }
        internal OfficeDesk HitTest(Point client) { int index = HitIndex(client); return index < 0 ? null : desks[index]; }

        void AnnounceSelection()
        {
            OfficeDesk desk = SelectedDesk;
            AccessibleDescription = desk == null ? "目前没有工位" :
                desk.Name + (desk.Archived ? "，已封存。" : "，在用。") + "方向键选择工位，滚轮缩放，双击查看全景。";
            AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
            var handler = SelectionChanged; if (handler != null) handler(this, EventArgs.Empty);
        }

        void SelectDesk(int index)
        {
            if (index < 0 || index == selected || index >= desks.Count) return;
            selected = index;
            if (zoom > 1)
            {
                RectangleF box = stations[index];
                CenterOn(new PointF(box.X + box.Width / 2, box.Y + box.Height / 2), .5f, .5f);
            }
            AnnounceSelection(); Invalidate();
        }

        void CenterOn(PointF anchor, float fractionX, float fractionY)
        {
            float span = RoomSize / zoom;
            offset = new PointF(Math.Max(0, Math.Min(RoomSize - span, anchor.X - span * fractionX)),
                Math.Max(0, Math.Min(RoomSize - span, anchor.Y - span * fractionY)));
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (View.Contains(e.Location))
            {
                RectangleF view = View; PointF anchor = MapPoint(e.Location);
                zoom = (float)Math.Max(1, Math.Min(Math.Max(3, Math.Ceiling(Math.Sqrt(desks.Count) / 2)),
                    zoom * Math.Pow(1.25, e.Delta / 120.0)));
                CenterOn(anchor, (e.X - view.X) / view.Width, (e.Y - view.Y) / view.Height); Invalidate();
            }
            base.OnMouseWheel(e);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        { if (e.Button == MouseButtons.Left) { zoom = 1; offset = PointF.Empty; Invalidate(); } base.OnMouseDoubleClick(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        { if (e.Button == MouseButtons.Left) { Focus(); SelectDesk(HitIndex(e.Location)); } base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int index = HitIndex(e.Location);
            if (index != hover)
            {
                hover = index; Cursor = index < 0 ? Cursors.Default : Cursors.Hand;
                tip.SetToolTip(this, index < 0 ? null : desks[index].Name + (desks[index].Archived ? " · 已封存" : " · 在用"));
                Invalidate();
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        { hover = -1; Cursor = Cursors.Default; tip.SetToolTip(this, null); Invalidate(); base.OnMouseLeave(e); }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (selected >= 0)
            {
                int at = Array.IndexOf(readingOrder, selected), cols = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(desks.Count)));
                switch (e.KeyCode)
                {
                    case Keys.Home: at = 0; break;
                    case Keys.End: at = readingOrder.Length - 1; break;
                    case Keys.Right: at++; break;
                    case Keys.Left: at--; break;
                    case Keys.Up: at -= cols; break;
                    case Keys.Down: at += cols; break;
                    default: base.OnKeyDown(e); return;
                }
                SelectDesk(readingOrder[Math.Max(0, Math.Min(readingOrder.Length - 1, at))]); e.Handled = true; e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (floor == null) floor = LoadImage("OfficeRoom.png", Rectangle.Empty);
            // Pixel bounds of the visible furniture in the approved transparent assets,
            // padded by four pixels to preserve the soft outside edge.
            if (liveDesk == null) liveDesk = LoadImage("OfficeDeskLive.png", LiveCrop);
            if (sealedDesk == null) sealedDesk = LoadImage("OfficeDeskDusty.png", SealedCrop);
            if (dust == null) dust = DustLayer();
            var g = e.Graphics; var view = View; var saved = g.Save();
            try
            {
                g.SetClip(view); g.TranslateTransform(view.X, view.Y); float scale = view.Width / RoomSize * zoom;
                g.ScaleTransform(scale, scale); g.TranslateTransform(-offset.X, -offset.Y);
                g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(floor, new Rectangle(0, 0, RoomSize, RoomSize));
                var floorClip = g.Save();
                g.SetClip(new RectangleF(14, 17, 452, 449), CombineMode.Intersect);
                foreach (var i in Enumerable.Range(0, desks.Count).Where(i => desks[i].Archived))
                {
                    RectangleF box = stations[i];
                    g.DrawImage(dust, new RectangleF(box.X - box.Width * .48f, box.Y - box.Height * .43f,
                        box.Width * 1.85f, box.Height * 1.9f));
                }
                g.Restore(floorClip);
                for (int i = 0; i < desks.Count; i++) DrawStation(g, i);
                string count = desks.Count + " 个账号 · " + desks.Count + " 张办公桌";
                var caption = new RectangleF(19, 21, g.MeasureString(count, roomFont).Width + 13, 22);
                Label(g, caption, count, roomFont, null);
            }
            finally { g.Restore(saved); }
        }

        void DrawStation(Graphics g, int i)
        {
            var box = stations[i]; var desk = desks[i]; var saved = g.Save();
            g.TranslateTransform(box.X, box.Y); float scale = box.Width / StationWidth; g.ScaleTransform(scale, scale);
            var sprite = desk.Archived ? sealedDesk : liveDesk;
            float height = StationHeight(desk.Archived);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(sprite, new RectangleF((StationWidth - FurnitureWidth) / 2, FurnitureTop,
                FurnitureWidth, height - FurnitureTop - FootMargin));
            if (!desk.Archived && occupant != null && desk.ProfileId == occupant)
            {
                // Set the pet's grounding shadow level with the chair feet in the side aisle.
                const float petSize = .84f;
                var petState = g.Save(); g.TranslateTransform(7f, height - FootMargin - 36f * petSize);
                float petScale = petSize * 96f / Theme.Dpi; g.ScaleTransform(petScale, petScale);
                PetScene.Sprite(g, 0, 0, PetState.Waiting, 16, false); g.Restore(petState);
            }
            float badgeWidth = Math.Min(StationWidth - 14, Math.Max(44, g.MeasureString(desk.Name ?? "", deskFont).Width + 25));
            Label(g, new RectangleF((StationWidth - badgeWidth) / 2, BadgeTop, badgeWidth, BadgeHeight), desk.Name,
                deskFont, desk.Archived ? Color.FromArgb(119, 115, 109) : Color.FromArgb(71, 129, 73));
            if (i == selected || i == hover)
            {
                using (var p = new Pen(i == selected ? Color.FromArgb(193, 97, 62) : Color.FromArgb(160, 135, 109), 1.7f))
                using (var path = Theme.Round(new RectangleF(1, 1, StationWidth - 2, height - 2), 6))
                { p.DashStyle = i == selected ? DashStyle.Dash : DashStyle.Dot; g.DrawPath(p, path); }
            }
            g.Restore(saved);
        }

        static void Label(Graphics g, RectangleF bounds, string text, Font font, Color? dot)
        {
            var state = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var path = Theme.Round(bounds, Math.Min(6, bounds.Height / 3)))
            using (var edge = new Pen(Color.FromArgb(150, 186, 159, 130), 1))
            using (var surface = new SolidBrush(Color.FromArgb(252, 248, 236)))
            using (var ink = new SolidBrush(Color.FromArgb(55, 46, 38)))
            using (var format = new StringFormat(StringFormatFlags.NoWrap))
            {
                g.FillPath(surface, path); g.DrawPath(edge, path);
                format.Alignment = StringAlignment.Center; format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                if (dot.HasValue)
                {
                    const float dotSize = 6f, gap = 4f;
                    float textWidth = Math.Min(bounds.Width - 14 - dotSize - gap,
                        (float)Math.Ceiling(g.MeasureString(text ?? "", font).Width) + 1);
                    float groupLeft = bounds.X + (bounds.Width - textWidth - dotSize - gap) / 2;
                    using (var b = new SolidBrush(dot.Value))
                        g.FillEllipse(b, groupLeft, bounds.Y + (bounds.Height - dotSize) / 2, dotSize, dotSize);
                    bounds = new RectangleF(groupLeft + dotSize + gap, bounds.Y, textWidth, bounds.Height);
                }
                g.DrawString(text ?? "", font, ink, bounds, format);
            }
            g.Restore(state);
        }

        static Bitmap LoadImage(string manifestName, Rectangle visibleBounds)
        {
            using (var input = typeof(OfficeMap).Assembly.GetManifestResourceStream(manifestName))
            {
                if (input == null) throw new InvalidOperationException("Office artwork missing: " + manifestName);
                using (var source = new Bitmap(input))
                {
                    var bounds = visibleBounds.IsEmpty ? new Rectangle(Point.Empty, source.Size) : visibleBounds;
                    return source.Clone(bounds, PixelFormat.Format32bppPArgb);
                }
            }
        }

        static Bitmap DustLayer()
        {
            const int size = 192;
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            var random = new Random(494);
            // Premade alpha texture: underlying floorboards still show through the dust.
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double dx = (x - size * .65) / (size * .63), dy = (y - size * .62) / (size * .64);
                    double weight = Math.Max(0, 1 - dx * dx - dy * dy);
                    double alpha = weight * weight * (3 - 2 * weight);
                    int grain = random.Next(-4, 5) + (random.Next(24) == 0 ? 20 : 0);
                    bitmap.SetPixel(x, y, Color.FromArgb((int)(193 * alpha), 105 + grain, 101 + grain, 94 + grain));
                }
            return bitmap;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var bitmap in new[] { floor, liveDesk, sealedDesk, dust }) if (bitmap != null) bitmap.Dispose();
                roomFont.Dispose(); deskFont.Dispose(); tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
