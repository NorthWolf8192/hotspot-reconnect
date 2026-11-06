using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace HotspotReconnect
{
    public sealed class MainForm : Window, IDisposable
    {
        internal readonly ComboBox Profiles;
        readonly Button refresh, start, check;
        readonly CheckBox startup;
        readonly TextBlock status, emptyLog;
        readonly TextBox log;
        readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        TrayMenu trayMenu;
        readonly Watcher watcher = new Watcher();
        readonly bool testingMode;
        readonly Func<IWifi> wifiFactory;
        readonly Func<CancellationToken, Task> resumeDelay;
        readonly Func<bool> systemThemeReader;
        readonly Settings settings;
        CancellationTokenSource cancellation = new CancellationTokenSource();
        CancellationTokenSource pendingResume;
        internal Task WakeTask = Task.FromResult(0);
        HwndSource windowSource;
        IntPtr powerRegistration;
        int powerRegistrationError;
        Profile target;
        bool active, busy, exiting, initializing = true, closed;
        bool subscribedToThemeChanges;
        string lastLog = "";
        internal bool MicaApplied;
        internal int MicaResult;
        internal bool? ThemeOverride;
        internal event Action ThemeChanged;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "HotspotReconnect";

        [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnregisterSuspendResumeNotification(IntPtr registration);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        public MainForm(bool testing) : this(testing, delegate { return new NativeWifi(); }, delegate(CancellationToken token) { return Task.Delay(5000, token); }) { }
        internal MainForm(bool testing, Func<IWifi> factory, Func<CancellationToken, Task> delay) : this(testing, factory, delay, IsDarkTheme) { }
        internal MainForm(bool testing, Func<IWifi> factory, Func<CancellationToken, Task> delay, Func<bool> themeReader)
        {
            testingMode = testing; wifiFactory = factory; resumeDelay = delay;
            systemThemeReader = themeReader;
            settings = testing ? new Settings() : Settings.Load();
            Title = "热点自动重连"; Width = 800; Height = 760; MinWidth = 730; MinHeight = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            ApplyTheme();
            Grid root;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HotspotReconnect.MainWindow.xaml"))
                root = (Grid)XamlReader.Load(stream);
            Content = root;
            Profiles = (ComboBox)root.FindName("Profiles");
            refresh = (Button)root.FindName("Refresh"); start = (Button)root.FindName("Start"); check = (Button)root.FindName("Check");
            startup = (CheckBox)root.FindName("Startup"); status = (TextBlock)root.FindName("Status");
            emptyLog = (TextBlock)root.FindName("EmptyLog"); log = (TextBox)root.FindName("Log");
            refresh.Click += async delegate { await LoadProfiles(); };
            start.Click += delegate { if (active) StopWatching(); else StartWatching(); };
            check.Click += async delegate { if (active && !busy) { CancelPendingResume(); await CheckOnce(); } };
            startup.Checked += delegate { if (!initializing) ChangeStartup(); };
            startup.Unchecked += delegate { if (!initializing) ChangeStartup(); };
            tray.Icon = CreateIcon(); tray.Text = "热点自动重连";
            using (var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HotspotReconnect.AppIcon.png"))
            {
                var bitmap = new System.Windows.Media.Imaging.PngBitmapDecoder(iconStream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                bitmap.Freeze(); Icon = bitmap; ((Image)root.FindName("HeaderIcon")).Source = bitmap;
            }
            tray.MouseUp += delegate(object sender, Forms.MouseEventArgs e)
            { if (e.Button == Forms.MouseButtons.Right) Dispatcher.BeginInvoke(new Action(ShowTrayMenu)); };
            tray.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowWindow)); };
            SourceInitialized += delegate
            {
                windowSource = (HwndSource)PresentationSource.FromVisual(this);
                windowSource.AddHook(WindowMessage);
                ApplyBackdrop();
                if (!testingMode)
                {
                    powerRegistration = RegisterSuspendResumeNotification(windowSource.Handle, 0);
                    powerRegistrationError = powerRegistration == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                }
            };
            Closing += delegate(object sender, CancelEventArgs e)
            {
                if (!exiting && !testingMode) { e.Cancel = true; Hide(); }
            };
            Closed += delegate { Dispose(); };
            Activated += delegate { if (!closed) ApplyTheme(); };
            IsVisibleChanged += delegate { if (IsVisible && !closed) ApplyTheme(); };
            StateChanged += delegate { if (WindowState == WindowState.Minimized && !testingMode) Hide(); };
            if (!testingMode)
            {
                try { SystemEvents.UserPreferenceChanged += SystemPreferenceChanged; subscribedToThemeChanges = true; }
                catch (InvalidOperationException) { /* Native window notifications remain available. */ }
                catch (ExternalException) { /* Native window notifications remain available. */ }
            }
            if (!testingMode) Loaded += async delegate
            {
                tray.Visible = true;
                try { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) startup.IsChecked = key != null && key.GetValue(RunName) != null; }
                catch (Exception ex) { Report("无法读取自动启动设置：" + Explain(ex)); }
                initializing = false;
                await LoadProfiles();
                if (settings.Watch && Profiles.SelectedItem != null) StartWatching();
            };
        }

        static System.Drawing.Icon CreateIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HotspotReconnect.AppIcon.ico"))
            using (var icon = new System.Drawing.Icon(stream, Math.Max(16, GetSystemMetrics(49)), Math.Max(16, GetSystemMetrics(50))))
                return (System.Drawing.Icon)icon.Clone();
        }
        void ShowTrayMenu()
        {
            if (closed) return;
            ApplyTheme();
            if (trayMenu != null) { trayMenu.Close(); trayMenu = null; }
            var menu = new TrayMenu(this, active, ShowWindow,
                delegate { if (active) StopWatching(); else { StartWatching(); if (!active) ShowWindow(); } },
                delegate { exiting = true; Close(); });
            trayMenu = menu;
            menu.Closed += delegate { if (ReferenceEquals(trayMenu, menu)) trayMenu = null; };
            menu.ShowAtCursor();
        }
        internal bool DarkTheme { get; private set; }
        void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) { RequestThemeRefresh(); }
        internal void RequestThemeRefresh()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { Dispatcher.BeginInvoke(new Action(delegate { if (!closed) ApplyTheme(); })); }
            catch (InvalidOperationException) { /* Dispatcher may have stopped after the check. */ }
        }
        static bool IsDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch { return false; }
        }
        void Brush(string name, string value) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value); brush.Freeze(); Resources[name] = brush; }
        internal void ApplyTheme()
        {
            bool dark = ThemeOverride ?? systemThemeReader();
            DarkTheme = dark;
            Brush("TextBrush", dark ? "#F5F5F5" : "#1B1B1B");
            Brush("MutedBrush", dark ? "#C0C0C0" : "#616161");
            Brush("CardBrush", dark ? "#C92D2D2D" : "#BFFFFFFF");
            Brush("ControlBrush", dark ? "#C9404040" : "#D9FFFFFF");
            Brush("RaisedBrush", dark ? "#484848" : "#FAFAFA");
            Brush("PopupBrush", dark ? "#303030" : "#FCFCFC");
            Brush("StrokeBrush", dark ? "#28FFFFFF" : "#16000000");
            Brush("HoverBrush", dark ? "#14FFFFFF" : "#0C000000");
            Brush("SelectionBrush", dark ? "#213D50" : "#E3EFF9");
            Brush("AccentBrush", dark ? "#60CDFF" : "#0067C0");
            Brush("AccentTextBrush", dark ? "#062433" : "#FFFFFF");
            Brush("FocusBrush", dark ? "#FFFFFF" : "#161616");
            Brush("StatusBrush", dark ? "#54213340" : "#90EDF4FA");
            Brush("StatusStrokeBrush", dark ? "#2258ADDB" : "#1F0067C0");
            Brush("SwitchBrush", dark ? "#19FFFFFF" : "#08000000");
            Brush("LogBrush", dark ? "#80282828" : "#80FFFFFF");
            Brush("FallbackBrush", dark ? "#202020" : "#F3F3F3");
            if (SystemParameters.HighContrast)
            {
                foreach (string key in new string[] { "TextBrush", "MutedBrush", "FocusBrush" }) Resources[key] = SystemColors.WindowTextBrush;
                foreach (string key in new string[] { "CardBrush", "ControlBrush", "RaisedBrush", "PopupBrush", "StatusBrush", "LogBrush", "SwitchBrush", "FallbackBrush" }) Resources[key] = SystemColors.WindowBrush;
                Resources["StrokeBrush"] = SystemColors.WindowTextBrush;
                Resources["StatusStrokeBrush"] = SystemColors.WindowTextBrush;
                Resources["AccentBrush"] = SystemColors.HighlightBrush; Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
                Resources["SelectionBrush"] = SystemColors.ControlBrush;
            }
            Foreground = (Brush)Resources["TextBrush"];
            ApplyBackdrop();
            var changed = ThemeChanged; if (changed != null) changed();
        }
        internal void ApplyBackdrop()
        {
            Background = (Brush)Resources["FallbackBrush"]; MicaApplied = false;
            if (windowSource == null) return;
            try
            {
                int dark = DarkTheme ? 1 : 0;
                DwmSetWindowAttribute(windowSource.Handle, 20, ref dark, 4);
                int corners = 2; DwmSetWindowAttribute(windowSource.Handle, 33, ref corners, 4);
                int material = SystemParameters.HighContrast ? 1 : 2; // DWMSBT_MAINWINDOW is native Mica.
                MicaResult = DwmSetWindowAttribute(windowSource.Handle, 38, ref material, 4);
                if (MicaResult >= 0 && material == 2)
                {
                    var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    MicaResult = DwmExtendFrameIntoClientArea(windowSource.Handle, ref margins);
                    if (MicaResult >= 0)
                    {
                        windowSource.CompositionTarget.BackgroundColor = Colors.Transparent;
                        Background = Brushes.Transparent; MicaApplied = true;
                    }
                }
            }
            catch (DllNotFoundException) { MicaResult = -1; }
            catch (EntryPointNotFoundException) { MicaResult = -1; }
        }
        IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x218) HandlePowerEvent(wParam.ToInt32());
            if (message == 0x1A || message == 0x31A || message == 0x31E)
                RequestThemeRefresh();
            return IntPtr.Zero;
        }
        internal void HandlePowerEvent(int powerEvent)
        {
            if (powerEvent == 4) { CancelPendingResume(); return; }
            if (powerEvent == 18 && !closed) ApplyTheme();
            // ResumeSuspend (7) follows ResumeAutomatic (18); only handle 18.
            if (powerEvent != 18 || !active || pendingResume != null || closed) return;
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            pendingResume = source; WakeTask = CheckAfterResume(source);
        }
        void CancelPendingResume() { var source = pendingResume; pendingResume = null; if (source != null) source.Cancel(); }
        async Task CheckAfterResume(CancellationTokenSource source)
        {
            Report("电脑已唤醒，等待 5 秒让无线网卡恢复，然后仅检查一次……");
            try
            {
                await resumeDelay(source.Token); source.Token.ThrowIfCancellationRequested();
                if (active && !closed) await CheckOnce(source.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (active && !closed) Report("唤醒检查失败：" + Explain(ex)); }
            finally { if (ReferenceEquals(pendingResume, source)) pendingResume = null; source.Dispose(); }
        }
        public void Dispose()
        {
            if (closed) return;
            closed = true; exiting = true; cancellation.Cancel(); CancelPendingResume();
            if (subscribedToThemeChanges) { SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged; subscribedToThemeChanges = false; }
            if (trayMenu != null) { trayMenu.Close(); trayMenu = null; }
            if (powerRegistration != IntPtr.Zero) { UnregisterSuspendResumeNotification(powerRegistration); powerRegistration = IntPtr.Zero; }
            if (windowSource != null) windowSource.RemoveHook(WindowMessage);
            tray.Visible = false; var icon = tray.Icon; tray.Dispose(); if (icon != null) icon.Dispose();
            if (IsVisible) Close();
        }
        void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
        internal void Report(string message)
        {
            status.Text = message;
            if (lastLog == message) return;
            lastLog = message; emptyLog.Visibility = Visibility.Collapsed;
            if (log.Text.Length > 16000) log.Clear();
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "    " + message + Environment.NewLine); log.ScrollToEnd();
        }
        static string Explain(Exception error)
        {
            var e = error as Win32Exception;
            if (error is DllNotFoundException) return "当前环境没有 Windows 无线组件，请在有 Wi-Fi 的 Windows 电脑上运行。";
            if (e != null && e.NativeErrorCode == 1062) return "WLAN AutoConfig 服务没有运行，请在 Windows 服务中启动它。";
            if (e != null && e.NativeErrorCode == 5) return "Windows 拒绝访问无线配置，请检查系统权限或组织策略。（错误 5）";
            if (e != null && e.NativeErrorCode == 5023) return "无线网卡状态不允许连接，请开启 Wi-Fi 并关闭飞行模式。";
            return error.Message + (e == null ? "" : "（错误 " + e.NativeErrorCode + "）");
        }
        async Task LoadProfiles()
        {
            if (busy || active) return;
            busy = true; refresh.IsEnabled = start.IsEnabled = false;
            try
            {
                var all = await Task.Run(delegate
                {
                    using (IWifi wifi = wifiFactory())
                    {
                        var result = new List<Profile>();
                        foreach (var adapter in wifi.Adapters()) result.AddRange(wifi.Profiles(adapter));
                        return result;
                    }
                });
                if (closed) return;
                Profiles.Items.Clear(); foreach (var p in all) Profiles.Items.Add(p);
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Name == settings.Profile && all[i].AdapterId.ToString() == settings.Adapter) Profiles.SelectedIndex = i;
                if (all.Count == 0) Report("没有已保存的 Wi-Fi。请先在 Windows 中连接一次手机热点，再刷新列表。");
                else if (Profiles.SelectedItem == null) Report("请选择手机热点；如果找不到，请先在 Windows 中连接一次，再刷新。");
                else Report("已恢复上次选择，点击“启用唤醒重连”即可。");
            }
            catch (Exception ex) { if (!closed) Report("读取无线配置失败：" + Explain(ex)); }
            finally { busy = false; if (!closed) refresh.IsEnabled = start.IsEnabled = true; }
        }
        internal void StartWatching()
        {
            if (busy || closed) return;
            if (!testingMode && powerRegistration == IntPtr.Zero)
            { Report("无法订阅电脑唤醒通知：" + Explain(new Win32Exception(powerRegistrationError))); return; }
            target = Profiles.SelectedItem as Profile;
            if (target == null) { Report("请先从列表中选择手机热点。"); return; }
            settings.Profile = target.Name; settings.Adapter = target.AdapterId.ToString(); settings.Watch = true;
            try { if (!testingMode) settings.Save(); }
            catch (Exception ex) { Report("无法保存设置：" + Explain(ex)); return; }
            cancellation.Dispose(); cancellation = new CancellationTokenSource();
            active = true; start.Content = "暂停唤醒重连";
            Profiles.IsEnabled = refresh.IsEnabled = false; check.IsEnabled = true;
            Report("已启用：仅在电脑唤醒后检查一次。现在需要检查可点击“立即检查”。");
        }
        internal void StopWatching()
        {
            active = false; cancellation.Cancel(); CancelPendingResume(); settings.Watch = false;
            string saveError = "";
            try { if (!testingMode) settings.Save(); } catch (Exception ex) { saveError = " 无法保存暂停状态：" + Explain(ex); }
            start.Content = "启用唤醒重连"; Profiles.IsEnabled = refresh.IsEnabled = true; check.IsEnabled = false;
            Report("已暂停唤醒重连。" + saveError);
        }
        async Task CheckOnce(CancellationToken? requestToken = null)
        {
            if (!active || busy || closed) return;
            busy = true; check.IsEnabled = false;
            var token = requestToken ?? cancellation.Token; var selected = target;
            try
            {
                string result = await Task.Run(delegate { using (IWifi wifi = wifiFactory()) return watcher.CheckOnce(wifi, selected, token); }, token);
                if (!closed && active && !token.IsCancellationRequested) Report(result);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closed && active && !token.IsCancellationRequested) Report("本次检查未完成，可点击“立即检查”手动再试：" + Explain(ex)); }
            finally { busy = false; if (!closed) check.IsEnabled = active; }
        }
        void ChangeStartup()
        {
            if (testingMode) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (startup.IsChecked == true) key.SetValue(RunName, "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                    else key.DeleteValue(RunName, false);
                }
                Report(startup.IsChecked == true ? "已启用登录后自动启动。请保持程序文件位置不变。" : "已取消登录后自动启动。");
            }
            catch (Exception ex)
            {
                initializing = true; startup.IsChecked = startup.IsChecked != true; initializing = false;
                Report("无法更改自动启动：" + Explain(ex));
            }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test-core") return SelfTests.Run(null, false);
            if (args.Length > 0 && args[0] == "--diagnose")
            {
                try { using (var wifi = new NativeWifi()) Console.WriteLine("Wireless adapters: " + wifi.Adapters().Count); return 0; }
                catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); return 2; }
            }
            Forms.Application.EnableVisualStyles();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args.Length > 1 ? args[1] : null);
            if (args.Length > 0 && args[0] == "--preview")
            {
                var preview = new MainForm(true);
                preview.Title = "热点自动重连 · 界面预览";
                if (args.Length > 1) preview.ThemeOverride = args[1] == "dark";
                preview.ApplyTheme(); app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.Run(preview); return 0;
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\HotspotReconnect_" + Environment.UserName, out created))
            {
                if (!created) { MessageBox.Show("程序已在运行，请从任务栏右侧的托盘中打开。", "热点自动重连"); return 0; }
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                var window = new MainForm(false);
                app.SessionEnding += delegate { window.Dispose(); };
                app.Run(window);
            }
            return 0;
        }
    }
}
