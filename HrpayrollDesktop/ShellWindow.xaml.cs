using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Local;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HrpayrollDesktop;

public partial class ShellWindow : Window
{
    private const double TitleStripHeight = 36;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(3);

    private readonly AttendanceEngine _engine;
    private readonly ITerminalAdapter _adapter;
    private readonly LocalAttendanceStore _store;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _recentTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _allowExit;
    private bool _exitPromptOpen;
    private readonly BackendApiClient _apiClient;
    private bool _readerConnected;

    public ShellWindow(AttendanceEngine engine, ITerminalAdapter adapter, BackendApiClient apiClient, LocalAttendanceStore store)
    {
        InitializeComponent();

        _engine = engine;
        _adapter = adapter;
        _store = store;
        _apiClient = apiClient;

        Popups.MakeCurrent();
        Login.Initialize(apiClient, adapter);
        ManagerConsole.Initialize(engine, adapter, apiClient, store);

        // ---- Navigation between the surfaces ----
        Kiosk.ManagerLoginRequested += ShowLogin;
        Kiosk.ScanRequested += () => Popups.ShowScanPrompt(_readerConnected);
        Kiosk.SeeAllRequested += ShowTodayList;
        Kiosk.HelpRequested += ShowHowItWorks;
        Login.Cancelled += HideLogin;
        Login.LoginSucceeded += OnLoginSucceeded;
        ManagerConsole.LockRequested += LockToKiosk;
        ManagerConsole.ExitRequested += ExitApplication;
        ManagerConsole.HeaderDragRequested += OnConsoleHeaderMouseDown;

        // ---- Engine events (raised on background threads) ----
        engine.FingerCaptured += () => Dispatcher.InvokeAsync(() => Popups.BeginScan());
        engine.FingerNotRecognized += () => Dispatcher.InvokeAsync(OnNotRecognized);
        engine.FingerRemoved += () => Dispatcher.InvokeAsync(() => Popups.NotifyFingerRemoved());
        engine.FingerPoorQuality += () => Dispatcher.InvokeAsync(() => Popups.ShowHint("Press a little firmer and keep your finger still."));
        engine.MatchOccurred += (userId, eventType, ts) => Dispatcher.InvokeAsync(() => OnClockedIn(userId, eventType, ts));
        engine.DuplicateScan += (userId, lastType, lastTs) => Dispatcher.InvokeAsync(() => OnDuplicate(userId, lastType, lastTs));
        engine.ChoiceRequired += request => Dispatcher.InvokeAsync(() => OnChoiceRequired(request));
        engine.ReturnedFromBreak += back => Dispatcher.InvokeAsync(() => OnReturnedFromBreak(back));

        engine.DeviceReady += () => Dispatcher.InvokeAsync(() =>
        {
            var serial = _adapter.DeviceSerial;
            _readerConnected = true;
            Kiosk.SetDeviceConnected(true);
            UpdateStationLabel();
            Login.SetTerminalLabel(string.IsNullOrWhiteSpace(serial) ? "Attendance terminal" : $"Terminal {serial}");
            ManagerConsole.SetDeviceState(true, null);
        });

        engine.DeviceFault += message => Dispatcher.InvokeAsync(() =>
        {
            _readerConnected = false;
            Kiosk.SetDeviceConnected(false);
            ManagerConsole.SetDeviceState(false, message);
        });

        // ---- Idle auto-lock for the Back Office console ----
        PreviewMouseMove += (_, _) => Touch();
        PreviewMouseDown += (_, _) => Touch();
        PreviewKeyDown += (_, _) => Touch();

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _idleTimer.Tick += (_, _) =>
        {
            if (ManagerConsole.Visibility != Visibility.Visible) return;
            if (ManagerConsole.IsBusy) { Touch(); return; }
            if (DateTime.UtcNow - _lastActivityUtc > IdleLimit) LockToKiosk();
        };

        // ---- The figures on the home screen ----
        _recentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _recentTimer.Tick += (_, _) => RefreshToday();
        _recentTimer.Start();
        Loaded += (_, _) =>
        {
            Window_StateChanged(this, EventArgs.Empty);
            RefreshToday();
        };

#if DEBUG
        StartScanSimulator();
#endif
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
        MaxButton.Content = maximized ? "" : "";
        MaxButton.ToolTip = maximized ? "Restore down" : "Maximize";

        RootGrid.Margin = maximized ? MaximizedMargin() : new Thickness(0);
    }

    // A maximized window covers the whole monitor and hangs over its edges by the invisible resize border, so
    // the content is pulled in by that border and by the taskbar, wherever on this monitor the taskbar is.
    private Thickness MaximizedMargin()
    {
        var edge = SystemParameters.WindowResizeBorderThickness;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return edge;

        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        var (bounds, work) = (screen.Bounds, screen.WorkingArea);
        var dpi = VisualTreeHelper.GetDpi(this);

        return new Thickness(
            edge.Left + (work.Left - bounds.Left) / dpi.DpiScaleX,
            edge.Top + (work.Top - bounds.Top) / dpi.DpiScaleY,
            edge.Right + (bounds.Right - work.Right) / dpi.DpiScaleX,
            edge.Bottom + (bounds.Bottom - work.Bottom) / dpi.DpiScaleY);
    }

    // Drag the window by the strip along the top, on every screen.
    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.GetPosition(this).Y > TitleStripHeight) return;

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

    private async void ConfirmExit()
    {
        if (_exitPromptOpen) return;
        _exitPromptOpen = true;
        try
        {
            var yes = await Popups.ConfirmAsync("Exit Dhwarsh HR?",
                "Fingerprint attendance will stop on this computer until the app is started again.",
                "Yes, exit", dangerous: true);

            if (yes) ExitApplication();
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

    // ===== What each scan did (UI thread) =====

    private void OnClockedIn(string terminalUserId, string eventType, DateTime timestampUtc)
    {
        var (name, detail) = Resolve(terminalUserId);
        Popups.ShowMessage(PopupKind.Success, $"Welcome, {name}",
            $"You checked in at {PopupHost.Clock(timestampUtc)}. Have a productive day!", detail);
        AfterRecorded(name, eventType, timestampUtc);
    }

    private void OnChoiceRequired(ScanChoiceRequest request)
    {
        var (name, detail) = Resolve(request.TerminalUserId);

        Popups.ShowChoice(name, detail, request.ClockedInAtUtc, request.ScannedAtUtc + AttendanceEngine.LunchAllowance,
            (choice, reason, returnAtLocal) => OnChoiceMade(request, name, choice, reason, returnAtLocal));
    }

    private void OnChoiceMade(ScanChoiceRequest request, string name, ScanChoice choice, string? reason, DateTime? returnAtLocal)
    {
        var first = PopupHost.FirstName(name);
        var at = request.ScannedAtUtc;

        switch (choice)
        {
            case ScanChoice.Lunch:
                if (_engine.ChooseLunch() is not { } due) return;
                Popups.ShowMessage(PopupKind.Success, $"Enjoy your lunch, {first}",
                    $"Your break started at {PopupHost.Clock(at)}. Scan your finger again when you return.",
                    $"Be back by {PopupHost.Clock(due)}. After that, the time you are late is recorded.", autoCloseSeconds: 7);
                AfterRecorded(name, "LUNCH_OUT", at);
                break;

            case ScanChoice.Temporary:
                var back = returnAtLocal!.Value.ToUniversalTime();
                if (!_engine.ChooseTemporaryCheckout(reason!, back)) return;
                Popups.ShowMessage(PopupKind.Success, $"Take care, {first}",
                    $"You are checked out from {PopupHost.Clock(at)}. Scan your finger again when you return.",
                    $"Expected back at {PopupHost.Clock(back)} today.", autoCloseSeconds: 7);
                AfterRecorded(name, "EMERGENCY_OUT", at);
                break;

            case ScanChoice.ClockOut:
                if (!_engine.ChooseClockOut()) return;
                Popups.ShowMessage(PopupKind.Success, $"Goodbye, {first}",
                    $"You checked out for the day at {PopupHost.Clock(at)}. Your day has been recorded. Rest well!");
                AfterRecorded(name, "CLOCK_OUT", at);
                break;

            default:
                _engine.CancelChoice();
                break;
        }
    }

    private void OnReturnedFromBreak(BreakReturn back)
    {
        var (name, _) = Resolve(back.TerminalUserId);
        var first = PopupHost.FirstName(name);
        var away = Duration(back.BackAtUtc - back.LeftAtUtc);

        if (!back.WasLunch)
        {
            // A temporary check-out is never counted as late, whatever time they said.
            Popups.ShowMessage(PopupKind.Success, $"Welcome back, {first}", $"You were out for {away}. You are back at work.");
        }
        else if (back.ExpectedBackUtc is { } due && back.BackAtUtc > due)
        {
            var late = (int)Math.Ceiling((back.BackAtUtc - due).TotalMinutes);
            Popups.ShowMessage(PopupKind.Warning, $"Welcome back, {first}",
                $"Your lunch break lasted {away}. It was due to end at {PopupHost.Clock(due)}.",
                $"You are {late} minute{(late == 1 ? "" : "s")} late. This has been recorded.", autoCloseSeconds: 8);
        }
        else
        {
            Popups.ShowMessage(PopupKind.Success, $"Welcome back, {first}", $"Your lunch break lasted {away}. You are back on time.");
        }

        AfterRecorded(name, "BREAK_RETURN", back.BackAtUtc);
    }

    private void OnDuplicate(string terminalUserId, string lastEventType, DateTime lastTimestampUtc)
    {
        var (name, _) = Resolve(terminalUserId);
        Popups.ShowMessage(PopupKind.Info, "Already recorded",
            $"{name}, you {PastTense(lastEventType)} at {PopupHost.Clock(lastTimestampUtc)}. There's no need to scan again.");
    }

    private void OnNotRecognized() =>
        Popups.ShowMessage(PopupKind.Warning, "Fingerprint not recognized",
            "We couldn't match that fingerprint. If this keeps happening, please see the Back Office.",
            "Lift your finger, then place it again.", autoCloseSeconds: 8, closeText: "Try again");

    private void AfterRecorded(string name, string eventType, DateTime timestampUtc)
    {
        RefreshToday();
        if (ManagerConsole.Visibility == Visibility.Visible)
            ManagerConsole.NotifyClockEvent(name, eventType, timestampUtc);
    }

    private static string PastTense(string eventType) => eventType switch
    {
        "CLOCK_IN" => "checked in",
        "CLOCK_OUT" => "checked out for the day",
        "LUNCH_OUT" => "went for lunch",
        "EMERGENCY_OUT" => "checked out temporarily",
        _ => "came back",
    };

    private static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} minutes";
    }

    private (string Name, string? Detail) Resolve(string terminalUserId)
    {
        var employee = _store.GetEmployee(terminalUserId);
        if (employee is null)
            return (FallbackName(terminalUserId), "Your name will appear once this computer has synced.");

        var parts = new[] { employee.Position, employee.EmployeeNumber }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return (employee.FullName, string.Join(" • ", parts));
    }

    internal static string FallbackName(string terminalUserId) =>
        terminalUserId.StartsWith("emp-", StringComparison.Ordinal)
            ? $"Employee #{terminalUserId[4..]}"
            : terminalUserId;

    // ===== The home screen's figures =====

    private static readonly (string Bg, string Fg)[] AvatarPalette =
    {
        ("#D2E4FF", "#005592"), ("#E5F4E2", "#2E7D24"), ("#FFF4D6", "#8A5F00"), ("#FFE3D1", "#B4530F"), ("#E6F0F8", "#0B2A45"),
    };

    private static (Brush Bg, Brush Fg) AvatarFor(string key)
    {
        var (bg, fg) = AvatarPalette[key.Sum(c => (int)c) % AvatarPalette.Length];
        var converter = new BrushConverter();
        return ((Brush)converter.ConvertFromString(bg)!, (Brush)converter.ConvertFromString(fg)!);
    }

    private static string ActivityLine(AttendanceLogEntry entry)
    {
        var verb = entry.EventType switch
        {
            "CLOCK_IN" => "Checked in",
            "CLOCK_OUT" => "Checked out for the day",
            "LUNCH_OUT" => "Went for lunch",
            "EMERGENCY_OUT" => "Temporary check-out",
            _ => "Came back",
        };
        return $"{verb}  •  {PopupHost.Clock(entry.TimestampUtc)}";
    }

    private List<AttendanceLogEntry> TodayLog()
    {
        // Only the branch's current staff: this PC's log can hold scans from before it moved branch.
        var staff = _store.GetAllEmployees().Select(e => e.TerminalUserId).ToHashSet();
        return _store.GetTodayLog().Where(l => staff.Contains(l.TerminalUserId)).ToList();   // oldest first
    }

    private void RefreshToday()
    {
        try
        {
            var log = TodayLog();

            RecentScan Row(AttendanceLogEntry l)
            {
                var (bg, fg) = AvatarFor(l.TerminalUserId);
                return new RecentScan(Resolve(l.TerminalUserId).Name, ActivityLine(l), bg, fg);
            }

            var latest = log.GroupBy(l => l.TerminalUserId).Select(g => g.OrderBy(x => x.TimestampUtc).Last()).ToList();
            var atWork = latest.Where(x => x.EventType is "CLOCK_IN" or "BREAK_RETURN").ToList();

            Kiosk.SetToday(
                log.AsEnumerable().Reverse().Take(4).Select(Row).ToList(),
                new KioskToday(atWork.Count, latest.Count(x => x.EventType is "LUNCH_OUT" or "EMERGENCY_OUT"),
                    latest.Count(x => x.EventType == "CLOCK_OUT"), atWork.Select(Row).ToList()));

            Kiosk.SetBranchName(_store.GetAllEmployees().Select(e => e.BranchName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)));
        }
        catch (Exception)
        {
            // These figures are a convenience; never let them disturb clock-in.
        }
    }

    private void ShowTodayList()
    {
        var rows = TodayLog().AsEnumerable().Reverse().Select(l =>
        {
            var (bg, fg) = AvatarFor(l.TerminalUserId);
            var name = Resolve(l.TerminalUserId).Name;
            var line = ActivityLine(l).Split("  •  ");
            var reason = string.IsNullOrWhiteSpace(l.Reason) ? line[0] : $"{line[0]}: {l.Reason}";
            return new PopupListRow(name, PopupHost.Initials(name), reason, line[1], bg, fg);
        }).ToList();

        Popups.ShowList("Today's activity", "Every scan on this reader today, newest first.", rows, "No scans yet today.");
    }

    private void ShowHowItWorks() =>
        Popups.ShowMessage(PopupKind.Info, "How it works",
            "In the morning: scan your finger to check in. You must check in first.\n\n" +
            "During the day: scan, then choose a lunch break (30 minutes) or a temporary check-out (give a reason and your return time).\n\n" +
            "Coming back: scan again. Only a lunch break can be recorded as late.\n\n" +
            "End of the day: scan, then choose \"Check out for the day\".",
            autoCloseSeconds: 0);

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

    // Until a Back Office has signed in once this reader belongs to no branch, so scans can't reach the server.
    private void UpdateStationLabel()
    {
        var serial = _adapter.DeviceSerial;
        var label = string.IsNullOrWhiteSpace(serial) ? "Biometric terminal" : $"Reader {serial}";
        Kiosk.SetStationLabel(_apiClient.Identity is null ? "Back Office sign-in needed to activate" : label);
    }

    private void OnLoginSucceeded(AuthResponse session)
    {
        HideLogin();
        UpdateStationLabel();
        ManagerConsole.StartSession(session);
        ManagerConsole.Visibility = Visibility.Visible;
        Kiosk.Visibility = Visibility.Collapsed;

        Touch();
        _idleTimer.Start();
    }

    private void LockToKiosk()
    {
        _idleTimer.Stop();
        Popups.CloseDialogs();
        ManagerConsole.EndSession();
        ManagerConsole.Visibility = Visibility.Collapsed;

        Kiosk.Visibility = Visibility.Visible;
        RefreshToday();   // picks up any names the Back Office session just loaded

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Maximized;
        Activate();
    }

#if DEBUG
    // Test builds only: put an employee's terminal id in sim-scan.txt and it acts as that person's finger.
    // Lets the whole path be checked, from scan to server, on a PC with no finger on the reader.
    private void StartScanSimulator()
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "sim-scan.txt");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            string id;
            try
            {
                if (!System.IO.File.Exists(file)) return;
                id = System.IO.File.ReadAllText(file).Trim();
                System.IO.File.Delete(file);
            }
            catch (System.IO.IOException)
            {
                return;   // still being written; the next tick reads it
            }
            if (id.StartsWith("page:", StringComparison.Ordinal)) ManagerConsole.TestSelectPage(id[5..]);
            else if (id.Length > 0) _engine.SimulateScan(id);
        };
        timer.Start();
    }
#endif
}
