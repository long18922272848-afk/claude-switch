using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ClaudeAccountSync
{
    // Native HWND smoke test only. No Panel, Engine, account state, or transcript access.
    // Both the owner and the production popup stay completely outside every screen.
    sealed class PopupSmokeOwner : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000080; return value; }
        }
    }

    static class NativePopupSmoke
    {
        const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
        static readonly List<string> report = new List<string>();
        static int passed;
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);

        static void Check(bool ok, string label)
        {
            report.Add((ok ? "PASS " : "FAIL ") + label);
            if (!ok) throw new InvalidOperationException(label);
            passed++;
        }

        static object Native(string name, params object[] args)
        {
            return typeof(TokenHeatmapPopup).GetMethod(name, HiddenStatic).Invoke(null, args);
        }

        // Invoke the exact production declaration to observe the bool result its rendering path ignores.
        static bool SubmitAlpha(TokenHeatmapPopup popup)
        {
            var bitmap = (Bitmap)typeof(TokenHeatmapPopup).GetField("bitmap", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(popup);
            IntPtr screen = (IntPtr)Native("GetDC", IntPtr.Zero), memory = IntPtr.Zero, native = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                memory = (IntPtr)Native("CreateCompatibleDC", screen);
                Check(screen != IntPtr.Zero && memory != IntPtr.Zero, "native alpha DCs created");
                native = bitmap.GetHbitmap(Color.FromArgb(0));
                previous = (IntPtr)Native("SelectObject", memory, native);
                Type pointType = typeof(TokenHeatmapPopup).GetNestedType("NativePoint", BindingFlags.NonPublic);
                Type sizeType = typeof(TokenHeatmapPopup).GetNestedType("NativeSize", BindingFlags.NonPublic);
                Type blendType = typeof(TokenHeatmapPopup).GetNestedType("BlendFunction", BindingFlags.NonPublic);
                object location = Activator.CreateInstance(pointType, popup.Left, popup.Top);
                object source = Activator.CreateInstance(pointType, 0, 0);
                object size = Activator.CreateInstance(sizeType, popup.Width, popup.Height);
                object blend = Activator.CreateInstance(blendType);
                blendType.GetField("BlendOp").SetValue(blend, (byte)0);
                blendType.GetField("SourceConstantAlpha").SetValue(blend, (byte)255);
                blendType.GetField("AlphaFormat").SetValue(blend, (byte)1);
                return (bool)Native("UpdateLayeredWindow", popup.Handle, screen, location, size, memory, source, 0, blend, 2);
            }
            finally
            {
                if (previous != IntPtr.Zero) Native("SelectObject", memory, previous);
                if (native != IntPtr.Zero) Native("DeleteObject", native);
                if (memory != IntPtr.Zero) Native("DeleteDC", memory);
                if (screen != IntPtr.Zero) Native("ReleaseDC", IntPtr.Zero, screen);
            }
        }

        static bool Offscreen(Rectangle bounds)
        {
            foreach (var screen in Screen.AllScreens) if (screen.Bounds.IntersectsWith(bounds)) return false;
            return true;
        }

        [STAThread]
        static int Main(string[] args)
        {
            int exit = 0;
            try
            {
                SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Theme.Init("light");
                IntPtr foreground = GetForegroundWindow();
                int left = -20000, top = -20000;
                foreach (var screen in Screen.AllScreens) { left = Math.Min(left, screen.Bounds.Left - 20000); top = Math.Min(top, screen.Bounds.Top - 20000); }
                IntPtr ownerHandle, popupHandle;
                using (var owner = new PopupSmokeOwner { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Location = new Point(left, top), ClientSize = new Size(700, 400), AutoScaleMode = AutoScaleMode.None })
                using (var popup = new TokenHeatmapPopup())
                {
                    Check(Offscreen(owner.Bounds), "fixture owner configured outside all screens before Show");
                    owner.Show(); ownerHandle = owner.Handle;
                    Check(Offscreen(owner.Bounds), "fixture owner stays outside every screen after Show");
                    Check(GetForegroundWindow() == foreground, "owner does not change foreground window");
                    Rectangle available = owner.RectangleToScreen(owner.ClientRectangle);
                    Rectangle anchor = new Rectangle(available.Left + 320, available.Top + 200, 16, 16);
                    popup.Present(owner, anchor, available, "1月15日", "314.1万 个 Token", 1f);
                    popupHandle = popup.Handle;
                    Application.DoEvents();
                    Check(popup.Visible && IsWindowVisible(popupHandle), "production layered popup is natively visible");
                    Check(popup.Owner == owner && GetWindow(popupHandle, 4) == ownerHandle, "popup has the fixture owner in WinForms and Win32");
                    int styles = GetWindowLong(popupHandle, -20);
                    Check((styles & 0x08000000) != 0, "popup uses WS_EX_NOACTIVATE");
                    Check((styles & 0x00000020) != 0, "popup uses WS_EX_TRANSPARENT");
                    Check((styles & 0x00080000) != 0, "popup uses WS_EX_LAYERED");
                    Check(GetForegroundWindow() == foreground, "Present and message dispatch preserve foreground window");
                    Check(Offscreen(popup.Bounds) && available.Contains(popup.Bounds), "popup bounds stay inside the offscreen owner viewport");
                    Check(!anchor.IntersectsWith(popup.Bounds), "popup does not cover its anchor cell");
                    Check(SubmitAlpha(popup), "production UpdateLayeredWindow reports successful per-pixel alpha submission");
                    popup.Hide();
                    Check(!popup.Visible && !IsWindowVisible(popupHandle), "Hide removes the native popup");
                    Check(GetForegroundWindow() == foreground, "Hide preserves foreground window");
                }
                Check(!IsWindow(popupHandle), "Dispose releases the popup HWND");
                Check(!IsWindow(ownerHandle), "Dispose releases the isolated owner HWND");
                Check(GetForegroundWindow() == foreground, "fixture disposal preserves foreground window");
                report.Add("Passed=" + passed + "; Failures=0");
            }
            catch (Exception ex) { exit = 1; report.Add(ex.ToString()); }
            string folder = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
            File.WriteAllLines(Path.Combine(folder, "native-popup-smoke-report.txt"), report, new UTF8Encoding(false));
            foreach (string line in report) Console.WriteLine(line);
            return exit;
        }
    }
}
