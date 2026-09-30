using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Local;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace HrpayrollDesktop;

public partial class ShellWindow : Window
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(3);

    private readonly ITerminalAdapter _adapter;
    private readonly LocalAttendanceStore _store;
    private readonly DispatcherTimer _idleTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _allowExit;

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

        // ---- Engine events (raised on background threads) ----
        engine.FingerCaptured += () => Dispatcher.BeginInvoke(() => Kiosk.BeginScan());
        engine.FingerNotRecognized += () => Dispatcher.BeginInvoke(() => Kiosk.ShowNotRecognized());
        engine.MatchOccurred += (userId, eventType, ts) => Dispatcher.BeginInvoke(() => OnMatch(userId, eventType, ts));
        engine.DuplicateScan += (userId, lastType, lastTs) => Dispatcher.BeginInvoke(() => OnDuplicate(userId, lastType, lastTs));

        engine.DeviceReady += () => Dispatcher.BeginInvoke(() =>
        {
            var serial = _adapter.DeviceSerial;
            Kiosk.SetDeviceConnected(true);
            Kiosk.SetStationLabel(string.IsNullOrWhiteSpace(serial) ? "Biometric Attendance Terminal" : $"Biometric Attendance Terminal • {serial}");
            Login.SetTerminalLabel(string.IsNullOrWhiteSpace(serial) ? "Attendance terminal" : $"Terminal {serial}");
            ManagerConsole.SetDeviceState(true, null);
        });

        engine.DeviceFault += message => Dispatcher.BeginInvoke(() =>
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

            if (DateTime.UtcNow - _lastActivityUtc > IdleLimit)
                LockToKiosk();
        };
    }

    private void Touch() => _lastActivityUtc = DateTime.UtcNow;

    // ===== Engine event handlers (UI thread) =====

    private void OnMatch(string terminalUserId, string eventType, DateTime timestampUtc)
    {
        var (name, detail) = Resolve(terminalUserId);
        Kiosk.ShowSuccess(name, detail, eventType, timestampUtc);

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
            return (FallbackName(terminalUserId), null);

        var parts = new[] { employee.Position, employee.EmployeeNumber }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return (employee.FullName, string.Join(" • ", parts));
    }

    internal static string FallbackName(string terminalUserId) =>
        terminalUserId.StartsWith("emp-", StringComparison.Ordinal)
            ? $"Employee #{terminalUserId[4..]}"
            : terminalUserId;

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

        Touch();
        _idleTimer.Start();
    }

    private void LockToKiosk()
    {
        _idleTimer.Stop();
        ManagerConsole.EndSession();
        ManagerConsole.Visibility = Visibility.Collapsed;

        Kiosk.ShowIdle();
        Kiosk.Visibility = Visibility.Visible;

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Maximized;
        Activate();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 must not kill attendance capture on a branch kiosk.
        if (!_allowExit) e.Cancel = true;
        base.OnClosing(e);
    }

    private void Shell_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Touch();

#if DEBUG
        // Preview the kiosk states without a fingerprint: F1 idle, F2 scanning,
        // F3 clock-in, F4 clock-out, F5 not recognized, F6 already recorded.
        if (Kiosk.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case Key.F1: Kiosk.ShowIdle(); break;
            case Key.F2: Kiosk.BeginScan(); break;
            case Key.F3: Kiosk.ShowSuccess("Sarah Muteti", "Cashier • EMP-1001", "CLOCK_IN", DateTime.UtcNow); break;
            case Key.F4: Kiosk.ShowSuccess("Brian Kiprop", "Supervisor • EMP-1002", "CLOCK_OUT", DateTime.UtcNow); break;
            case Key.F5: Kiosk.ShowNotRecognized(); break;
            case Key.F6: Kiosk.ShowAlreadyRecorded("Sarah Muteti", "Cashier • EMP-1001", "CLOCK_IN", DateTime.UtcNow.AddSeconds(-20)); break;
        }
#endif
    }
}