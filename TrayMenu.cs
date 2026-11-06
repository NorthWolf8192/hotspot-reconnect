using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace HotspotReconnect
{
    // A regular, non-layered HWND lets DWM render real Mica behind this menu.
    // WPF's layered ContextMenu popup would obscure the system backdrop.
    internal sealed class TrayMenu : Window
    {
        readonly MainForm ownerWindow;
        readonly Action open, toggle, exit;
        readonly Button[] entries;
        HwndSource source;
        bool dismissed;
        internal bool MicaApplied;
        internal int MicaResult;
        internal bool CloseOnDeactivate = true;
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        internal TrayMenu(MainForm owner, bool enabled, Action onOpen, Action onToggle, Action onExit)
        {
            ownerWindow = owner; open = onOpen; toggle = onToggle; exit = onExit;
            Title = "热点自动重连 · 托盘菜单";
            Width = 250; SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; Topmost = true; ShowActivated = true;
            FontFamily = owner.FontFamily; FontSize = 13;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Resources.MergedDictionaries.Add(owner.Resources);
            Background = (Brush)owner.Resources["FallbackBrush"];
            Border root;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HotspotReconnect.TrayMenu.xaml"))
                root = (Border)XamlReader.Load(stream);
            Content = root;
            ((Image)root.FindName("MenuIcon")).Source = owner.Icon;
            ((TextBlock)root.FindName("ModeLabel")).Text = enabled ? "唤醒重连已开启" : "唤醒重连已暂停";
            ((TextBlock)root.FindName("ToggleLabel")).Text = enabled ? "暂停唤醒重连" : "启用唤醒重连";
            ((TextBlock)root.FindName("ToggleIcon")).Text = enabled ? "\uE769" : "\uE768";
            entries = new Button[] { (Button)root.FindName("Open"), (Button)root.FindName("Toggle"), (Button)root.FindName("Exit") };
            entries[0].Click += delegate { InvokeCommand(0); };
            entries[1].Click += delegate { InvokeCommand(1); };
            entries[2].Click += delegate { InvokeCommand(2); };
            ownerWindow.ThemeChanged += RefreshBackdrop;
            SourceInitialized += delegate
            {
                source = (HwndSource)PresentationSource.FromVisual(this);
                source.AddHook(WindowMessage); RefreshBackdrop();
            };
            Deactivated += delegate { if (CloseOnDeactivate && !dismissed) Close(); };
            Closed += delegate
            {
                dismissed = true; ownerWindow.ThemeChanged -= RefreshBackdrop;
                if (source != null) source.RemoveHook(WindowMessage);
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
                else if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Home || e.Key == Key.End)
                {
                    int i = Array.FindIndex(entries, delegate(Button b) { return b.IsKeyboardFocusWithin; });
                    int next;
                    if (e.Key == Key.Home) next = 0;
                    else if (e.Key == Key.End) next = entries.Length - 1;
                    else if (i < 0) next = e.Key == Key.Up ? entries.Length - 1 : 0;
                    else next = (i + (e.Key == Key.Down ? 1 : entries.Length - 1)) % entries.Length;
                    entries[next].Focus(); e.Handled = true;
                }
            };
        }
        internal void InvokeCommand(int command)
        {
            if (dismissed) return;
            if (command < 0 || command > 2) throw new ArgumentOutOfRangeException("command");
            dismissed = true; Close();
            if (command == 0) open(); else if (command == 1) toggle(); else exit();
        }
        internal void RefreshBackdrop()
        {
            Background = (Brush)ownerWindow.Resources["FallbackBrush"]; MicaApplied = false;
            if (source == null || dismissed) return;
            try
            {
                int dark = ownerWindow.DarkTheme ? 1 : 0; DwmSetWindowAttribute(source.Handle, 20, ref dark, 4);
                int corners = 2; DwmSetWindowAttribute(source.Handle, 33, ref corners, 4);
                int material = SystemParameters.HighContrast ? 1 : 2;
                MicaResult = DwmSetWindowAttribute(source.Handle, 38, ref material, 4);
                if (MicaResult >= 0 && material == 2)
                {
                    var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    MicaResult = DwmExtendFrameIntoClientArea(source.Handle, ref margins);
                    if (MicaResult >= 0) { source.CompositionTarget.BackgroundColor = Colors.Transparent; Background = Brushes.Transparent; MicaApplied = true; }
                }
            }
            catch (DllNotFoundException) { MicaResult = -1; }
            catch (EntryPointNotFoundException) { MicaResult = -1; }
        }
        IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x31E) RefreshBackdrop();
            if (message == 0x1A || message == 0x31A) ownerWindow.RequestThemeRefresh();
            return IntPtr.Zero;
        }
        internal static NativePoint Placement(int pointerX, int pointerY, int width, int height, int left, int top, int right, int bottom)
        {
            int x = pointerX, y = pointerY + 4;
            if (x + width > right) x = pointerX - width;
            if (y + height > bottom) y = pointerY - height - 4;
            return new NativePoint { X = Math.Max(left, Math.Min(x, right - width)), Y = Math.Max(top, Math.Min(y, bottom - height)) };
        }
        internal void ShowAtCursor()
        {
            NativePoint point; if (!GetCursorPos(out point)) return;
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            bool found = GetMonitorInfo(MonitorFromPoint(point, 2), ref info);
            // Move the ordinary HWND onto the target monitor before measuring
            // at its DPI. Keep opacity at 1 so it never becomes a layered HWND.
            var handle = new WindowInteropHelper(this).EnsureHandle();
            SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0, 0x15);
            Show(); UpdateLayout();
            uint dpi = 96; try { dpi = GetDpiForWindow(handle); } catch (EntryPointNotFoundException) { }
            if (dpi == 0) dpi = 96;
            int width = (int)Math.Ceiling(ActualWidth * dpi / 96.0), height = (int)Math.Ceiling(ActualHeight * dpi / 96.0);
            NativePoint position = found ? Placement(point.X, point.Y, width, height, info.Work.Left + 4, info.Work.Top + 4, info.Work.Right - 4, info.Work.Bottom - 4) : point;
            SetWindowPos(handle, new IntPtr(-1), position.X, position.Y, width, height, 0x40);
            SetForegroundWindow(handle); Activate();
        }
    }
}
