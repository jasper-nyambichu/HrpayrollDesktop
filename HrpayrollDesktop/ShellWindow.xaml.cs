using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Local;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HrpayrollDesktop;

public partial class ShellWindow : Window
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(3);

    private readonly ITerminalAdapter _adapter;
    private readonly LocalAttendanceStore _store;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _recentTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _allowExit;
    private bool _exitPromptOpen;

    public ShellWindow(AttendanceEngine engine, ITerminalAdapter adapter, BackendApiClient apiClient, LocalAttendanceStore store)
    {
        InitializeComponent();

        _adapter = adapter;
        _store = store;

        Login.Initialize(apiClient);
        ManagerConsole.Initialize(engine, adapter, apiClient, store);

        // ---- Navigation between the three surfaces ----
        Kiosk.ManagerLoginRequested += ShowLogin;
        Login.Cancelled += HideLogin;
        Login.LoginSucceeded += OnLoginSucceeded;
        ManagerConsole.LockRequested += LockToKiosk;
        ManagerConsole.ExitRequested += ExitApplication;
        ManagerConsole.HeaderDragRequested += OnConsoleHeaderMouseDown;

        // ---- Engine events (raised on background threads) ----
        engine.FingerCaptured += () => Dispatcher.InvokeAsync(() => Kiosk.BeginScan());
        engine.FingerNotRecognized += () => Dispatcher.InvokeAsync(() => Kiosk.ShowNotRecognized());
        engine.FingerRemoved += () => Dispatcher.InvokeAsync(() => Kiosk.NotifyFingerRemoved());
        engine.FingerPoorQuality += () => Dispatcher.InvokeAsync(() => Kiosk.ShowHint("Press a little firmer and keep your finger still."));
        engine.MatchOccurred += (userId, eventType, ts) => Dispatcher.InvokeAsync(() => OnMatch(userId, eventType, ts));
        engine.DuplicateScan += (userId, lastType, lastTs) => Dispatcher.InvokeAsync(() => OnDuplicate(userId, lastType, lastTs));

        engine.DeviceReady += () => Dispatcher.InvokeAsync(() =>
        {
            var serial = _adapter.DeviceSerial;
            Kiosk.SetDeviceConnected(true);
            Kiosk.SetStationLabel(string.IsNullOrWhiteSpace(serial) ? "Biometric Attendance Terminal" : $"Biometric Attendance Terminal • {serial}");
            Login.SetTerminalLabel(string.IsNullOrWhiteSpace(serial) ? "Attendance terminal" : $"Terminal {serial}");
            ManagerConsole.SetDeviceState(true, null);
        });

        engine.DeviceFault += message => Dispatcher.InvokeAsync(() =>
        {
            Kiosk.SetDeviceConnected(false);
            ManagerConsole.SetDeviceState(false, message);
        });

        // ---- Idle auto-lock for the manager console ----
        PreviewMouseMove += (_, _) => Touch();
        PreviewMouseDown += (_, _) => Touch();
        PreviewKeyDown += Shell_PreviewKeyDown;

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _idleTimer.Tick += (_, _) =>
        {
            if (ManagerConsole.Visibility != Visibility.Visible) return;
            if (ManagerConsole.IsBusy) { Touch(); return; }
            if (DateTime.UtcNow - _lastActivityUtc > IdleLimit) LockToKiosk();
        };

        // ---- "Today's activity" panel on the kiosk ----
        _recentTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _recentTimer.Tick += (_, _) => RefreshRecent();
        _recentTimer.Start();
        Loaded += (_, _) => RefreshRecent();
    }

    private void Touch() => _lastActivityUtc = DateTime.UtcNow;

    // ===== Window controls =====

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();   // goes through OnClosing -> confirm

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaxButton is null) return;

        var maximized = WindowState == WindowState.Maximized;
        MaxButton.Content = maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maximized ? "Restore down" : "Maximize";
    }

    // Kiosk / login: drag by the header strip. (The console reports its own header drags.)
    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var onConsole = ManagerConsole.Visibility == Visibility.Visible && Login.Visibility != Visibility.Visible;
        if (onConsole) return;

        var onKiosk = Kiosk.Visibility == Visibility.Visible && Login.Visibility != Visibility.Visible;
        var dragZoneHeight = onKiosk ? 80 : 36;

        if (e.GetPosition(this).Y > dragZoneHeight) return;

        var p = e.GetPosition(CaptionBar);
        if (p.X >= 0 && p.X <= CaptionBar.ActualWidth && p.Y >= 0 && p.Y <= CaptionBar.ActualHeight) return;

        BeginWindowDragOrToggle(e);
    }

    private void OnConsoleHeaderMouseDown(MouseButtonEventArgs e) => BeginWindowDragOrToggle(e);

    private void BeginWindowDragOrToggle(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
        }
        else if (WindowState == WindowState.Normal)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void SetCaptionTheme(bool light)
    {
        CaptionBar.Background = Brushes.Transparent;
        CaptionBar.BorderThickness = new Thickness(0);
        MinButton.Style = (Style)FindResource(light ? "CaptionButtonLight" : "CaptionButton");
        MaxButton.Style = (Style)FindResource(light ? "CaptionButtonLight" : "CaptionButton");
        CloseButton.Style = (Style)FindResource(light ? "CaptionCloseButtonLight" : "CaptionCloseButton");
    }

    // Close (X) and Alt+F4 both land here: ask first, so attendance capture is never stopped by accident.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            Dispatcher.InvokeAsync(ConfirmExit);
        }
        base.OnClosing(e);
    }

    private void ConfirmExit()
    {
        if (_exitPromptOpen) return;
        _exitPromptOpen = true;
        try
        {
            var answer = System.Windows.MessageBox.Show(
                this,
                "Exit Dhwarsh HR?\n\nFingerprint clock-in will stop on this computer until the app is started again.",
                "Exit Dhwarsh HR",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer == MessageBoxResult.Yes)
                ExitApplication();
        }
        finally
        {
            _exitPromptOpen = false;
        }
    }

    private void ExitApplication()
    {
        _allowExit = true;
        System.Windows.Application.Current.Shutdown();
    }

    // ===== Engine event handlers (UI thread) =====

    private void OnMatch(string terminalUserId, string eventType, DateTime timestampUtc)
    {
        var (name, detail) = Resolve(terminalUserId);
        Kiosk.ShowSuccess(name, detail, eventType, timestampUtc);
        RefreshRecent();

        if (ManagerConsole.Visibility == Visibility.Visible)
            ManagerConsole.NotifyClockEvent(name, eventType, timestampUtc);
    }

    private void OnDuplicate(string terminalUserId, string lastEventType, DateTime lastTimestampUtc)
    {
        var (name, detail) = Resolve(terminalUserId);
        Kiosk.ShowAlreadyRecorded(name, detail, lastEventType, lastTimestampUtc);
    }

    private (string Name, string? Detail) Resolve(string terminalUserId)
    {
        var employee = _store.GetEmployee(terminalUserId);
        if (employee is null)
            return (FallbackName(terminalUserId), "Name not synced yet — a manager sign-in will load it");

        var parts = new[] { employee.Position, employee.EmployeeNumber }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return (employee.FullName, string.Join(" • ", parts));
    }

    internal static string FallbackName(string terminalUserId) =>
        terminalUserId.StartsWith("emp-", StringComparison.Ordinal)
            ? $"Employee #{terminalUserId[4..]}"
            : terminalUserId;

    private void RefreshRecent()
    {
        try
        {
            var log = _store.GetTodayLog();   // oldest first

            var recent = log.AsEnumerable().Reverse().Take(6)
                .Select(l => new RecentScan(Resolve(l.TerminalUserId).Name, l.EventType == "CLOCK_IN", l.TimestampUtc.ToLocalTime()))
                .ToList();

            var present = log
                .GroupBy(l => l.TerminalUserId)
                .Select(g => g.OrderBy(x => x.TimestampUtc).Last())
                .Count(x => x.EventType == "CLOCK_IN");

            Kiosk.SetRecent(recent, present);
        }
        catch (Exception)
        {
            // The activity panel is a convenience; never let it disturb clock-in.
        }
    }

    // ===== Surface switching =====

    private void ShowLogin()
    {
        Login.Reset();
        Login.Visibility = Visibility.Visible;
    }

    private void HideLogin()
    {
        Login.Visibility = Visibility.Collapsed;
    }

    private void OnLoginSucceeded(AuthResponse session)
    {
        HideLogin();
        ManagerConsole.StartSession(session);
        ManagerConsole.Visibility = Visibility.Visible;
        Kiosk.Visibility = Visibility.Collapsed;

        SetCaptionTheme(light: true);

        Touch();
        _idleTimer.Start();
    }

    private void LockToKiosk()
    {
        _idleTimer.Stop();
        ManagerConsole.EndSession();
        ManagerConsole.Visibility = Visibility.Collapsed;

        SetCaptionTheme(light: false);

        Kiosk.ShowIdle();
        Kiosk.Visibility = Visibility.Visible;
        RefreshRecent();   // picks up any names the manager session just loaded

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Maximized;
        Activate();
    }

    private void Shell_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Touch();

#if DEBUG
        // Preview kiosk states without a fingerprint: F1 idle, F2 scanning, F3 clock-in,
        // F4 clock-out, F5 not recognized, F6 already recorded, F7 weak-press hint.
        if (Kiosk.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case Key.F1: Kiosk.ShowIdle(); break;
            case Key.F2: Kiosk.BeginScan(); break;
            case Key.F3: Kiosk.ShowSuccess("Sarah Muteti", "Cashier • EMP-1001", "CLOCK_IN", DateTime.UtcNow); break;
            case Key.F4: Kiosk.ShowSuccess("Brian Kiprop", "Supervisor • EMP-1002", "CLOCK_OUT", DateTime.UtcNow); break;
            case Key.F5: Kiosk.ShowNotRecognized(); break;
            case Key.F6: Kiosk.ShowAlreadyRecorded("Sarah Muteti", "Cashier • EMP-1001", "CLOCK_IN", DateTime.UtcNow.AddSeconds(-20)); break;
            case Key.F7: Kiosk.ShowHint("Press a little firmer and keep your finger still."); break;
        }
#endif
    }
}