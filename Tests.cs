using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace HotspotReconnect
{
    static class SelfTests
    {
        sealed class FakeWifi : IWifi
        {
            public List<Adapter> Items = new List<Adapter>();
            public int Calls, Reads; public bool Fail; public Guid LastAdapter; public string LastProfile;
            public List<Adapter> Adapters() { Reads++; return Items; }
            public List<Profile> Profiles(Adapter adapter) { return new List<Profile>(); }
            public void Connect(Guid id, string name) { Calls++; LastAdapter = id; LastProfile = name; if (Fail) throw new Win32Exception(5023); }
            public void Dispose() { }
        }
        static int assertions;
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        static bool NativeDark(Window window)
        {
            int value;
            int result = DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 20, out value, 4);
            if (result < 0) throw new Exception("Unable to query native dark mode: " + result.ToString("X8"));
            return value != 0;
        }
        static void Assert(bool value, string label) { assertions++; if (!value) throw new Exception("FAILED: " + label); }
        static void PumpDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }
        static void Pump(Task task)
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && timeout.ElapsedMilliseconds < 5000) { PumpDispatcher(); Thread.Sleep(5); }
            if (!task.IsCompleted) throw new Exception("Async test timed out");
            task.GetAwaiter().GetResult();
        }
        static void TestWakeEvents(Profile target)
        {
            var fake = new FakeWifi(); fake.Items.Add(new Adapter { Id = target.AdapterId, State = 4 });
            int delayCalls = 0; TaskCompletionSource<int> delay = null;
            using (var form = new MainForm(true, delegate { return fake; }, delegate(CancellationToken token)
            {
                delayCalls++; delay = new TaskCompletionSource<int>();
                var pending = delay; token.Register(delegate { pending.TrySetCanceled(); }); return pending.Task;
            }))
            {
                form.Show(); PumpDispatcher();
                form.Profiles.Items.Add(target); form.Profiles.SelectedIndex = 0;
                form.HandlePowerEvent(18);
                Assert(delayCalls == 0 && fake.Reads == 0, "disabled resume ignored");
                form.StartWatching(); Assert(fake.Reads == 0, "enabling does not check wifi");
                form.HandlePowerEvent(7); Assert(delayCalls == 0, "ignore user-present followup event");
                form.HandlePowerEvent(18); form.HandlePowerEvent(18); form.HandlePowerEvent(7);
                Assert(delayCalls == 1 && fake.Reads == 0, "one pending check with recovery delay");
                delay.SetResult(0); Pump(form.WakeTask);
                Assert(fake.Reads == 1 && fake.Calls == 1, "one read and one connect per wake");
                form.HandlePowerEvent(7); PumpDispatcher(); Assert(fake.Reads == 1, "no duplicate user-present check");
                form.HandlePowerEvent(18); form.StopWatching(); Pump(form.WakeTask);
                Assert(fake.Reads == 1, "pause cancels delayed wake check");
                form.StartWatching(); form.HandlePowerEvent(18); form.HandlePowerEvent(4); Pump(form.WakeTask);
                Assert(fake.Reads == 1, "another suspend cancels delayed check");
                form.HandlePowerEvent(18); delay.SetResult(0); Pump(form.WakeTask);
                Assert(fake.Reads == 2 && fake.Calls == 2, "next wake gets exactly one check");
                form.HandlePowerEvent(18); form.Dispose(); Pump(form.WakeTask);
                Assert(fake.Reads == 2, "exit cancels pending wake check");
            }
        }
        static void RenderPreview(string path, bool dark)
        {
            using (var window = new MainForm(true))
            {
                window.ThemeOverride = dark; window.ApplyTheme(); window.Show(); PumpDispatcher();
                window.UpdateLayout();
                var root = (FrameworkElement)window.Content;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    // RenderTargetBitmap cannot capture the compositor's Mica surface.
                    // Use the matching solid fallback only for this layout preview.
                    dc.DrawRectangle((Brush)window.Resources["FallbackBrush"], null, new Rect(0, 0, root.ActualWidth + 56, root.ActualHeight + 42));
                    dc.PushTransform(new TranslateTransform(28, 20));
                    dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                    dc.Pop();
                }
                int width = (int)Math.Ceiling(root.ActualWidth + 56), height = (int)Math.Ceiling(root.ActualHeight + 42);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(path)) encoder.Save(stream);
                Console.WriteLine("Mica requested: " + window.MicaApplied + "; HRESULT: 0x" + window.MicaResult.ToString("X8"));
                Assert(root.ActualWidth > 600 && root.ActualHeight > 600, "layout has usable dimensions");
            }
        }
        static void TestMenus(string previewPath)
        {
            using (var owner = new MainForm(true))
            {
                int opens = 0, toggles = 0, exits = 0;
                for (int command = 0; command < 3; command++)
                {
                    var menu = new TrayMenu(owner, true, delegate { opens++; }, delegate { toggles++; }, delegate { exits++; });
                    menu.CloseOnDeactivate = false;
                    menu.Show(); PumpDispatcher();
                    menu.InvokeCommand(command); menu.InvokeCommand(command);
                }
                Assert(opens == 1 && toggles == 1 && exits == 1, "each menu command fires once");
                var escapeMenu = new TrayMenu(owner, false, delegate { opens++; }, delegate { toggles++; }, delegate { exits++; });
                escapeMenu.CloseOnDeactivate = false; escapeMenu.Show(); PumpDispatcher();
                Keyboard.ClearFocus();
                var up = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(escapeMenu), 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                escapeMenu.RaiseEvent(up);
                Assert(Keyboard.FocusedElement == ((FrameworkElement)escapeMenu.Content).FindName("Exit"), "Up initially focuses last menu entry");
                var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(escapeMenu), 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                escapeMenu.RaiseEvent(down);
                Assert(Keyboard.FocusedElement == ((FrameworkElement)escapeMenu.Content).FindName("Open"), "Down wraps to first menu entry");
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(escapeMenu), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                escapeMenu.RaiseEvent(key);
                Assert(!escapeMenu.IsVisible && opens == 1 && toggles == 1 && exits == 1, "Escape dismisses without invoking a command");
                var lowerRight = TrayMenu.Placement(1910, 1030, 250, 220, 0, 0, 1920, 1040);
                Assert(lowerRight.X >= 0 && lowerRight.X + 250 <= 1920 && lowerRight.Y >= 0 && lowerRight.Y + 220 <= 1040, "menu stays inside bottom-right work area");
                var negativeMonitor = TrayMenu.Placement(-1900, -50, 375, 330, -1920, -1080, 0, 0);
                Assert(negativeMonitor.X >= -1920 && negativeMonitor.X + 375 <= 0 && negativeMonitor.Y >= -1080 && negativeMonitor.Y + 330 <= 0, "menu handles negative coordinates and scaled dimensions");
                if (previewPath != null)
                {
                    foreach (bool dark in new bool[] { false, true })
                    {
                        owner.ThemeOverride = dark; owner.ApplyTheme();
                        var menu = new TrayMenu(owner, true, delegate { }, delegate { }, delegate { });
                        menu.CloseOnDeactivate = false; menu.Show(); PumpDispatcher(); menu.UpdateLayout();
                        var content = (FrameworkElement)menu.Content;
                        var visual = new DrawingVisual();
                        using (var dc = visual.RenderOpen())
                        {
                            dc.DrawRoundedRectangle((Brush)owner.Resources["FallbackBrush"], null, new Rect(0, 0, menu.ActualWidth, menu.ActualHeight), 8, 8);
                            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                        }
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(visual);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        string suffix = dark ? "-menu-dark.png" : "-menu-light.png";
                        using (var stream = File.Create(Path.Combine(Path.GetDirectoryName(previewPath), Path.GetFileNameWithoutExtension(previewPath) + suffix))) encoder.Save(stream);
                        Console.WriteLine("Menu Mica requested: " + menu.MicaApplied + "; HRESULT: 0x" + menu.MicaResult.ToString("X8"));
                        Assert(menu.ActualWidth == 250 && menu.ActualHeight > 180, "menu layout has intended dimensions");
                        menu.Close();
                    }
                }
            }
        }
        static void TestSystemTheme()
        {
            bool systemDark = false;
            var wifi = new FakeWifi();
            using (var window = new MainForm(true, delegate { return wifi; }, delegate(CancellationToken token) { return Task.FromResult(0); }, delegate { return systemDark; }))
            {
                Assert(window.ThemeOverride == null && !window.DarkTheme, "theme defaults to system without an override");
                window.Show(); PumpDispatcher();
                var menu = new TrayMenu(window, false, delegate { }, delegate { }, delegate { });
                menu.CloseOnDeactivate = false; menu.Show(); PumpDispatcher();
                var openButton = (Button)((FrameworkElement)menu.Content).FindName("Open");
                systemDark = true;
                Pump(Task.Run(delegate { window.RequestThemeRefresh(); })); PumpDispatcher();
                Assert(window.DarkTheme && ((SolidColorBrush)window.Resources["TextBrush"]).Color == Color.FromRgb(245,245,245), "system event changes main window to dark mode");
                Assert(((SolidColorBrush)openButton.Foreground).Color == Color.FromRgb(245,245,245), "already-open menu inherits updated dark palette");
                if (window.MicaApplied) Assert(NativeDark(window), "native title bar and Mica switched to dark");
                if (menu.MicaApplied) Assert(NativeDark(menu), "open menu native Mica switched to dark");
                window.Hide(); systemDark = false;
                Pump(Task.Run(delegate { window.RequestThemeRefresh(); })); PumpDispatcher();
                Assert(!window.IsVisible && !window.DarkTheme && ((SolidColorBrush)window.Resources["TextBrush"]).Color == Color.FromRgb(27,27,27), "hidden tray window follows system back to light");
                Assert(((SolidColorBrush)openButton.Foreground).Color == Color.FromRgb(27,27,27), "open menu follows system back to light");
                if (window.MicaApplied) Assert(!NativeDark(window), "hidden native window switched back to light");
                if (menu.MicaApplied) Assert(!NativeDark(menu), "native menu switched back to light");
                Assert(wifi.Reads == 0 && wifi.Calls == 0, "theme changes never check or reconnect wifi");
                menu.Close();
            }
        }
        public static int Run(string previewPath, bool includeDesktop = true)
        {
            try
            {
                NativeWifi.CheckLayout();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var id = Guid.NewGuid(); var target = new Profile { AdapterId = id, Name = "测试热点 中文 & \"quote\"" };
                var fake = new FakeWifi(); var w = new Watcher(); var token = CancellationToken.None;
                w.CheckOnce(fake, target, token); Assert(fake.Calls == 0, "missing adapter");
                var a = new Adapter { Id = id, State = 4 }; fake.Items.Add(a);
                w.CheckOnce(fake, target, token); Assert(fake.Calls == 1, "single disconnected attempt");
                Assert(fake.LastAdapter == id && fake.LastProfile == target.Name, "exact profile including unicode and quotes");
                foreach (int state in new int[] { 0, 1, 2, 3, 5, 6, 7 }) { a.State = state; w.CheckOnce(fake, target, token); }
                Assert(fake.Calls == 1, "do not interrupt connected or busy adapter");
                a.State = 4; w.CheckOnce(fake, target, token); Assert(fake.Calls == 2, "another explicit check can reconnect");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); bool threw = false;
                    try { w.CheckOnce(fake, target, canceled.Token); } catch (OperationCanceledException) { threw = true; }
                    Assert(threw && fake.Calls == 2, "cancellation prevents reconnect");
                }
                fake.Fail = true; try { w.CheckOnce(fake, target, token); } catch (Win32Exception) { }
                Assert(fake.Calls == 3, "failure does not retry in a loop");
                a.Id = Guid.NewGuid(); w.CheckOnce(fake, target, token); Assert(fake.Calls == 3, "never use another adapter");
                var original = new Settings { Profile = target.Name, Adapter = id.ToString(), Watch = true };
                var serializer = new XmlSerializer(typeof(Settings));
                using (var stream = new MemoryStream())
                {
                    serializer.Serialize(stream, original); stream.Position = 0;
                    var restored = (Settings)serializer.Deserialize(stream);
                    Assert(restored.Profile == original.Profile && restored.Adapter == original.Adapter && restored.Watch, "settings unicode round trip");
                }
                if (includeDesktop)
                {
                    TestWakeEvents(target);
                    TestMenus(previewPath);
                    TestSystemTheme();
                }
                if (includeDesktop && previewPath != null)
                {
                    RenderPreview(previewPath, false);
                    RenderPreview(Path.Combine(Path.GetDirectoryName(previewPath), Path.GetFileNameWithoutExtension(previewPath) + "-dark.png"), true);
                }
                Console.WriteLine("PASS: " + assertions + " assertions; native layouts validated."); return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); return 1; }
        }
    }
}
