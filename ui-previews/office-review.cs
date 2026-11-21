using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    static class OfficeReview
    {
        static int checks;
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr handle, int index, int value);
        const BindingFlags HiddenMethod = BindingFlags.Instance | BindingFlags.NonPublic;

        static void Check(bool result, string description)
        {
            if (!result) throw new Exception("Office UI: " + description);
            checks++;
        }

        static Control Named(Control form, string name)
        {
            return form.Controls.Find(name, true).Single();
        }

        static void ClickDesk(OfficeMap map, Point point)
        {
            var args = new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0);
            typeof(OfficeMap).GetMethod("OnMouseDown", HiddenMethod).Invoke(map, new object[] { args });
            typeof(OfficeMap).GetMethod("OnMouseUp", HiddenMethod).Invoke(map, new object[] { args });
        }

        static Dictionary<string, Rectangle> LocateDesks(OfficeMap map)
        {
            var result = new Dictionary<string, Rectangle>();
            for (int y = 2; y < map.Height; y += 8)
                for (int x = 2; x < map.Width; x += 8)
                {
                    OfficeDesk hit = map.HitTest(new Point(x, y));
                    if (hit == null) continue;
                    Rectangle pixel = new Rectangle(x, y, 1, 1), bounds;
                    result[hit.Name] = result.TryGetValue(hit.Name, out bounds) ? Rectangle.Union(bounds, pixel) : pixel;
                }
            return result;
        }

        static void Save(PetGallery gallery, string path)
        {
            using (var bitmap = new Bitmap(gallery.Width, gallery.Height))
            {
                gallery.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        static Point Center(Rectangle bounds)
        {
            return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        static void CheckFrame(PetGallery gallery, OfficeMap map)
        {
            Control details = Named(gallery, "officeName").Parent;
            Control tab = Named(gallery, "officeTab");
            int tolerance = Math.Max(2, Theme.S(3));
            double ratio = (double)map.Width / details.Width;
            Check(ratio >= 1.58 && ratio <= 1.79,
                "room and archive panel follow the approved column ratio, got " + ratio.ToString("0.00"));
            Check(map.Width == map.Height && map.Top == details.Top && map.Bottom == details.Bottom,
                "square room and archive panel have matching top and bottom edges");
            using (var view = new Bitmap(gallery.Width, gallery.Height))
            {
                gallery.DrawToBitmap(view, new Rectangle(Point.Empty, view.Size));
                int firstInk = map.Width;
                int background = gallery.BackColor.ToArgb();
                for (int y = 1; y < map.Top * 3 / 4; y++)
                    for (int x = 0; x < Math.Min(firstInk, map.Left + map.Top / 2); x++)
                        if (view.GetPixel(x, y).ToArgb() != background) { firstInk = x; break; }
                Check(Math.Abs(firstInk - map.Left) <= tolerance && Math.Abs(tab.Right - details.Right) <= tolerance,
                    "header aligns with outer body edges: icon/room " + firstInk + "/" + map.Left +
                    ", tab/details " + tab.Right + "/" + details.Right);
            }
            Check(map.Right < details.Left && gallery.ClientSize.Height <= Theme.S(536) &&
                gallery.ClientSize.Width > gallery.ClientSize.Height,
                "map and archive panel fit the compact landscape client area");
        }

        static void CheckConstrainedWorkAreas()
        {
            // 1366x768 monitor and a compact multi-window desktop; sizes already exclude window chrome.
            Theme.Dpi = 144;
            foreach (Size available in new[] { new Size(1320, 696), new Size(912, 540), new Size(760, 486) })
            {
                Size client = PetGallery.OfficeClientSize(available, 144);
                Check(client.Width > 0 && client.Height > 0 && client.Width <= available.Width && client.Height <= available.Height,
                    "constrained window fits the work area " + available);
                Check(Math.Abs((double)client.Width / client.Height - 788.0 / 536.0) < .01,
                    "constrained window preserves the office proportions " + available);
                using (var details = new OfficeDetails())
                {
                    details.Size = new Size((int)Math.Round(276 * client.Width / 788.0), (int)Math.Round(460 * client.Height / 536.0));
                    details.SelectDesk(new OfficeDesk { Name = "窗口缩放检查", Started = new DateTime(2025, 9, 8), Ended = new DateTime(2025, 9, 24) });
                    CheckArchiveText(details);
                }
            }
        }

        static void CheckArchiveText(Control gallery)
        {
            var period = (Label)Named(gallery, "officePeriod");
            var status = (Label)Named(gallery, "officeStatus");
            var name = (Label)Named(gallery, "officeName");
            Check(!name.Bounds.IntersectsWith(status.Bounds) && name.ClientSize.Width > 0 && status.ClientSize.Width > 0,
                "name and status have their own space");
            string[] lines = period.Text.Split(new [] {"\r\n", "\n"}, StringSplitOptions.None);
            Check(lines.Length == 2, "both dates have their own line");
            foreach (string line in lines)
                Check(TextRenderer.MeasureText(line, period.Font).Width <= period.ClientSize.Width,
                    "date line fits horizontally: " + line);
            Check(period.Font.Height * lines.Length <= period.ClientSize.Height,
                "both date lines fit vertically");
            Check(TextRenderer.MeasureText(status.Text, status.Font).Width <= status.ClientSize.Width,
                "status text remains visible, required " + TextRenderer.MeasureText(status.Text, status.Font).Width + ", available " + status.Width + ", panel " + status.Parent.Size);
        }

        static bool WaitFor(Func<bool> done)
        {
            var wait = Stopwatch.StartNew();
            while (!done() && wait.Elapsed.TotalSeconds < 6)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(15);
            }
            return done();
        }

        static void Review(string output, Engine engine, string resident, string theme, int dpi)
        {
            Theme.Init(theme);
            Theme.Dpi = dpi;
            using (var gallery = new PetGallery(engine.OfficeDesks, () => resident))
            {
                gallery.FormBorderStyle = FormBorderStyle.None;
                // DrawToBitmap paints the client even with a transparent outer window.
                // This exercises normal visibility and layout while keeping the fixture
                // invisible if the tab switch clamps it to the monitor working area.
                gallery.Opacity = 0;
                gallery.StartPosition = FormStartPosition.Manual;
                gallery.Location = new Point(SystemInformation.VirtualScreen.Right + 200, SystemInformation.VirtualScreen.Bottom + 200);
                int style = GetWindowLong(gallery.Handle, -20);
                SetWindowLong(gallery.Handle, -20, style | 0x08000000);
                gallery.Show();
                gallery.PerformLayout();
                var daily = gallery.Controls.OfType<PetGalleryCanvas>().Single();
                Check(daily.Visible, "gallery opens on daily view");
                if (theme == "light") Save(gallery, Path.Combine(output, "office-review-daily.png"));
                // Call the same registered click handlers the tab buttons use.
                var officeTab = Named(gallery, "officeTab");
                officeTab.GetType().GetMethod("OnClick", HiddenMethod).Invoke(officeTab, new object[] { EventArgs.Empty });
                gallery.PerformLayout();
                var map = (OfficeMap)Named(gallery, "officeMap");
                Check(WaitFor(() => map.SelectedDesk != null), "office snapshot arrives from its asynchronous reader");
                Check(map.Visible && !daily.Visible, "office tab replaces the gallery canvas");
                var desks = LocateDesks(map);
                Check(desks.Count == 3 && desks.ContainsKey("青枫") && desks.ContainsKey("杉木") && desks.ContainsKey("旧云杉"),
                    "map has exactly three selectable named desks");
                Rectangle dormant = desks["旧云杉"];
                Check(dormant.Left + dormant.Width / 2 > map.Width / 2 &&
                    dormant.Top > Math.Max(desks["青枫"].Top, desks["杉木"].Top),
                    "archived workstation occupies lower right corner away from active desks");
                ClickDesk(map, new Point(dormant.Left + dormant.Width / 2, dormant.Top + dormant.Height / 2));
                Check(map.SelectedDesk.Name == "旧云杉" && map.SelectedDesk.Archived, "mouse selects archived desk");
                Check(Named(gallery, "officeName").Text == "旧云杉" && Named(gallery, "officeStatus").Text.Contains("封存"),
                    "click populates the archived name and status");
                string period = Named(gallery, "officePeriod").Text;
                Check(period.Contains("2025.09.08") && period.Contains("2025.09.24") && Named(gallery, "officeDays").Text.Contains("17"),
                    "archive panel shows historical span and inclusive 17 days");
                CheckFrame(gallery, map);
                CheckArchiveText(gallery);
                string file = theme == "light" ? "office-review.png" : "office-review-dark-144dpi.png";
                Save(gallery, Path.Combine(output, file));
                Rectangle live = desks["杉木"];
                ClickDesk(map, new Point(live.Left + live.Width / 2, live.Top + live.Height / 2));
                Check(map.SelectedDesk.Name == "杉木" && Named(gallery, "officeName").Text == "杉木",
                    "selecting a different desk updates the details");
                var otherWindow = new Engine(null, false, engine.ClaudeDir, engine.StateDir);
                const string longName = "采集样本并核对今日实验记录的梧桐工位";
                Profile added = otherWindow.AddProfile(longName);
                var counts = Named(gallery, "officeCounts");
                Check(WaitFor(() => counts.Text.Contains("在用 3")), "open map automatically reflects a newly added account");
                desks = LocateDesks(map);
                Check(desks.Count == 4, "four accounts have four selectable desks");
                ClickDesk(map, Center(desks[longName]));
                Label nickname = (Label)Named(gallery, "officeName");
                var badge = (Label)Named(gallery, "officeStatus");
                Check(nickname.Text == longName && nickname.AutoEllipsis && !nickname.Bounds.IntersectsWith(badge.Bounds) &&
                    badge.Right <= badge.Parent.ClientSize.Width && TextRenderer.MeasureText(badge.Text, badge.Font).Width <= badge.Width,
                    "long nickname remains identifiable, without truncating or covering the status");
                otherWindow.Rename(added, "梧桐");
                Check(WaitFor(() => map.SelectedDesk.Name == "梧桐"), "open map follows the current label");
                Check(Named(gallery, "officeName").Text == "梧桐" && badge.Text.Contains("在用") &&
                    Named(gallery, "officePeriod").Text.Contains("至今"), "active dossier shows the selected short name and open-ended dates");
                CheckArchiveText(gallery);
                if (theme == "light") Save(gallery, Path.Combine(output, "office-review-active.png"));
                ClickDesk(map, Center(desks["旧云杉"]));
                if (theme == "light") Save(gallery, Path.Combine(output, "office-review-four.png"));
                ClickDesk(map, Center(desks[longName]));
                otherWindow.Forget(added);
                Check(WaitFor(() => map.SelectedDesk.Name == "梧桐" && map.SelectedDesk.Archived &&
                    counts.Text.Contains("封存 2") && Named(gallery, "officeDays").Text.Contains("1")),
                    "an open map moves a selected forgotten account into the archive and updates its details");
                var dailyTab = Named(gallery, "dailyTab");
                dailyTab.GetType().GetMethod("OnClick", HiddenMethod).Invoke(dailyTab, new object[] { EventArgs.Empty });
                Check(daily.Visible && !map.Visible, "daily view remains accessible after office interactions");
            }
        }

        static void ReviewLargerMap()
        {
            var desks = new List<OfficeDesk>();
            for (int i = 0; i < 20; i++)
                desks.Add(new OfficeDesk { Key = "test" + i, Name = "Desk " + i, ProfileId = i < 11 ? "profile" + i : null,
                    Started = new DateTime(2026, 1, 1), Ended = i < 11 ? (DateTime?)null : new DateTime(2026, 2, 1) });
            using (var map = new OfficeMap())
            {
                map.Size = new Size(460, 460);
                map.SetDesks(desks, "profile3");
                Check(map.SelectedDesk.ProfileId == "profile3", "resident desk initially selected");
                Check(LocateDesks(map).Count == 20, "all twenty account desks share one map");
                MethodInfo key = typeof(OfficeMap).GetMethod("OnKeyDown", HiddenMethod);
                key.Invoke(map, new object[] { new KeyEventArgs(Keys.Home) });
                var visited = new HashSet<string> { map.SelectedDesk.Key };
                for (int i = 1; i < desks.Count; i++)
                {
                    key.Invoke(map, new object[] { new KeyEventArgs(Keys.Right) });
                    visited.Add(map.SelectedDesk.Key);
                }
                Check(visited.Count == desks.Count, "keyboard can visit all desks including archived corner");
                string selected = map.SelectedDesk.Key;
                map.SetDesks(desks, "profile2");
                Check(map.SelectedDesk.Key == selected, "periodic refresh preserves user selection");
                map.SetDesks(new List<OfficeDesk>(), null);
                Check(map.SelectedDesk == null && map.HitTest(new Point(230, 230)) == null, "empty office leaves no phantom desk or selection");
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string output = Path.GetFullPath(args[0]);
            string root = Path.Combine(Path.GetTempPath(), "cas-office-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                foreach (string theme in new [] {"light", "dark"})
                {
                    string state = Path.Combine(root, theme, "state"), claude = Path.Combine(root, theme, "claude");
                    Directory.CreateDirectory(state);
                    string past = Path.Combine(state, "profiles", "p-abcdef01");
                    File.WriteAllLines(Path.Combine(state, "sync.log"), new[] {
                        "2025-09-08 10:00:00  added account 旧云杉 at " + past,
                        "2025-09-24 10:00:00  removed from list: 旧云杉 (folder kept: " + past + ")" });
                    Engine engine = new Engine(null, false, claude, state);
                    Profile resident = engine.AddProfile("青枫");
                    engine.AddProfile("杉木");
                    Review(output, engine, resident.Id, theme, theme == "light" ? 96 : 144);
                }
                Theme.Init("light"); Theme.Dpi = 96;
                ReviewLargerMap();
                CheckConstrainedWorkAreas();
                Console.WriteLine("Office UI checks: {0}. Rendered isolated gallery views.", checks);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
