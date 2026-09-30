using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Local;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HrpayrollDesktop;

public partial class ConsoleView : UserControl
{
    private record EmployeePickerItem(long Id, string DisplayLabel, EmployeeSummary Employee);

    private record AttendanceRow(
        string EmployeeName,
        string Summary,
        string Status,
        Brush StatusBackground,
        Brush StatusForeground);

    private enum EnrollUiState { Ready, Capturing, Registering, Success, Failed }

    private AttendanceEngine? _engine;
    private ITerminalAdapter? _adapter;
    private BackendApiClient? _apiClient;
    private LocalAttendanceStore? _store;
    private AuthResponse? _session;

    private DispatcherTimer? _toastTimer;
    private bool _sidebarExpanded = true;
    private bool _enrolling;
    private bool _deviceConnected;
    private string? _deviceMessage;

    public event Action? LockRequested;
    public event Action? ExitRequested;

    /// <summary>True while a fingerprint enrollment is in progress (blocks the idle auto-lock).</summary>
    public bool IsBusy => _enrolling;

    public ConsoleView()
    {
        InitializeComponent();
        UpdateSteps(1);
    }

    public void Initialize(AttendanceEngine engine, ITerminalAdapter adapter, BackendApiClient apiClient, LocalAttendanceStore store)
    {
        _engine = engine;
        _adapter = adapter;
        _apiClient = apiClient;
        _store = store;
    }

    // ===== Session lifecycle =====

    public void StartSession(AuthResponse session)
    {
        _session = session;
        ApplySessionToUi();
        ApplyDeviceState();
        SelectPage("Dashboard");
        SetEnrollState(EnrollUiState.Ready);
        RefreshAttendance();
        RefreshDeviceInfo();
        _ = LoadEmployeesAsync();
    }

    public void EndSession()
    {
        _session = null;
        EmployeeComboBox.ItemsSource = null;
        PreviewCard.Visibility = Visibility.Collapsed;
        ToastBorder.Visibility = Visibility.Collapsed;
        StopEnrollAnimations();
    }

    private void ApplySessionToUi()
    {
        if (_session is null) return;

        var name = string.IsNullOrWhiteSpace(_session.DisplayName) ? "Branch Manager" : _session.DisplayName;
        var initial = name[0].ToString().ToUpperInvariant();

        GreetingText.Text = $"Hello, {name}";
        SessionText.Text = "Welcome back. Here's your branch today.";
        TitleBarText.Text = $"Dhwarsh HR Desktop — {name} (Branch Manager)";
        SidebarUserNameText.Text = name;
        SidebarUserRoleText.Text = "Branch Manager";
        TopbarUserNameText.Text = name;
        UserInitialText.Text = initial;
        TopbarUserInitialText.Text = initial;
    }

    // ===== Called by the shell =====

    public void SetDeviceState(bool connected, string? message)
    {
        _deviceConnected = connected;
        _deviceMessage = message;
        ApplyDeviceState();
    }

    public void NotifyClockEvent(string employeeName, string eventType, DateTime timestampUtc)
    {
        var action = eventType == "CLOCK_OUT" ? "clocked out" : "clocked in";
        ShowToast($"{employeeName} {action} at {timestampUtc.ToLocalTime().ToString("hh:mm tt", CultureInfo.InvariantCulture)}", "success");
        RefreshAttendance();
        RefreshDeviceInfo();
    }

    private void ApplyDeviceState()
    {
        if (_deviceConnected)
        {
            DeviceStatusText.Text = "Fingerprint reader: connected";
            DeviceStatusText.Foreground = Br("#047857");
            DeviceStatusPill.Background = Br("#ECFDF5");
            DeviceStatusPill.BorderBrush = Br("#A7F3D0");
            DeviceStatusDot.Fill = Br("#10B981");
            DeviceStatusCardText.Text = "Connected";
            DeviceSerialText.Text = _adapter?.DeviceSerial ?? "SecuGen Reader";
            DeviceNavDot.Fill = (Brush)FindResource("GreenBrush");
            SystemAlertBorder.Visibility = Visibility.Collapsed;
            EnrollSensorText.Text = "Ready";
            EnrollSensorText.Foreground = Br("#059669");
        }
        else
        {
            var hasFault = !string.IsNullOrWhiteSpace(_deviceMessage);
            DeviceStatusText.Text = hasFault ? "Fingerprint reader error" : "Fingerprint reader: connecting...";
            DeviceStatusText.Foreground = Br("#C2410C");
            DeviceStatusPill.Background = Br("#FFF7ED");
            DeviceStatusPill.BorderBrush = Br("#FED7AA");
            DeviceStatusDot.Fill = Br("#F5821F");
            DeviceStatusCardText.Text = hasFault ? "Not Detected" : "Connecting";
            DeviceNavDot.Fill = (Brush)FindResource(hasFault ? "ErrorBrush" : "OrangeBrush");
            SystemAlertText.Text = _deviceMessage ?? string.Empty;
            SystemAlertBorder.Visibility = hasFault ? Visibility.Visible : Visibility.Collapsed;
            EnrollSensorText.Text = hasFault ? "Not detected" : "Connecting…";
            EnrollSensorText.Foreground = Br("#C2410C");
        }

        UpdateEnrollAvailability();
        RefreshDeviceInfo();
    }

    // ===== Title strip / sidebar =====

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is not null) window.WindowState = WindowState.Minimized;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            "Exit the application? Fingerprint clock-in will stop on this computer until it is started again.",
            "Exit Dhwarsh HR", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes)
            ExitRequested?.Invoke();
    }

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_enrolling)
        {
            ShowToast("Please wait for the fingerprint capture to finish.", "warning");
            return;
        }
        LockRequested?.Invoke();
    }

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarExpanded = !_sidebarExpanded;
        double targetWidth = _sidebarExpanded ? 240 : 72;

        var animation = new DoubleAnimation
        {
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, animation);

        var textVisibility = _sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
        SidebarTextGroup.Visibility = textVisibility;
        SidebarUserTextGroup.Visibility = textVisibility;
        NavDashboardLabel.Visibility = textVisibility;
        NavEnrollLabel.Visibility = textVisibility;
        NavAttendanceLabel.Visibility = textVisibility;
        NavDeviceLabel.Visibility = textVisibility;
        NavHelpLabel.Visibility = textVisibility;
    }

    // ===== Navigation =====

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string page })
            SelectPage(page);
    }

    private void SelectPage(string page)
    {
        NavDashboard.IsChecked = page == "Dashboard";
        NavEnroll.IsChecked = page == "Enroll";
        NavAttendance.IsChecked = page == "Attendance";
        NavDevice.IsChecked = page == "Device";
        NavHelp.IsChecked = page == "Help";

        DashboardPage.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        EnrollPage.Visibility = page == "Enroll" ? Visibility.Visible : Visibility.Collapsed;
        AttendancePage.Visibility = page == "Attendance" ? Visibility.Visible : Visibility.Collapsed;
        DevicePage.Visibility = page == "Device" ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = page == "Help" ? Visibility.Visible : Visibility.Collapsed;

        if (page is "Dashboard" or "Attendance") RefreshAttendance();
        if (page == "Device") RefreshDeviceInfo();
    }

    private void RefreshAttendanceButton_Click(object sender, RoutedEventArgs e) => RefreshAttendance();
    private void RefreshDeviceButton_Click(object sender, RoutedEventArgs e) => RefreshDeviceInfo();

    // ===== Toast =====

    public void ShowToast(string message, string kind)
    {
        var brush = kind switch
        {
            "success" => (Brush)FindResource("SuccessBrush"),
            "warning" => (Brush)FindResource("WarningBrush"),
            _ => (Brush)FindResource("ErrorBrush"),
        };

        ToastBorder.Background = brush;
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) =>
        {
            ToastBorder.Visibility = Visibility.Collapsed;
            _toastTimer!.Stop();
        };
        _toastTimer.Start();
    }

    // ===== Data =====

    private void RefreshAttendance()
    {
        if (_store is null) return;

        var log = _store.GetTodayLog();
        var rows = new List<AttendanceRow>();
        var present = 0;

        var inBg = Br("#DCFCE7"); var inFg = Br("#15803D");
        var outBg = Br("#E0EBFA"); var outFg = Br("#2563A8");

        foreach (var group in log.GroupBy(l => l.TerminalUserId).OrderByDescending(g => g.Max(x => x.TimestampUtc)))
        {
            var ordered = group.OrderBy(x => x.TimestampUtc).ToList();
            var clockedIn = ordered[^1].EventType == "CLOCK_IN";
            if (clockedIn) present++;

            var name = _store.GetEmployee(group.Key)?.FullName ?? ShellWindow.FallbackName(group.Key);
            var summary = string.Join("  ·  ", ordered.Select(x =>
                $"{(x.EventType == "CLOCK_IN" ? "In" : "Out")} {x.TimestampUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}"));

            rows.Add(new AttendanceRow(
                name, summary,
                clockedIn ? "IN" : "OUT",
                clockedIn ? inBg : outBg,
                clockedIn ? inFg : outFg));
        }

        DashboardAttendanceList.ItemsSource = rows;
        AttendanceList.ItemsSource = rows;
        DashboardEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AttendanceEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PresentTodayCountText.Text = present.ToString();

        var pending = _engine?.PendingSyncCount ?? 0;
        AttendanceSummaryText.Text = $"{rows.Count} employee(s) scanned  •  {present} currently in  •  {pending} scan(s) waiting to sync";
    }

    private void RefreshDeviceInfo()
    {
        if (_adapter is null) return;

        var enrolled = _adapter.EnrolledCount;
        EmployeesEnrolledText.Text = enrolled.ToString();
        DevEnrolledText.Text = enrolled.ToString();
        DevReaderText.Text = _deviceConnected ? "Connected" : (string.IsNullOrWhiteSpace(_deviceMessage) ? "Connecting…" : "Not detected");
        DevSerialText.Text = _adapter.DeviceSerial ?? "—";
        DevPendingText.Text = (_engine?.PendingSyncCount ?? 0).ToString();
    }

    private async Task LoadEmployeesAsync()
    {
        var session = _session;
        if (session is null || _apiClient is null || _store is null) return;

        if (session.BranchId is null)
        {
            ShowToast("No branch assigned to this account. Contact your administrator.", "error");
            return;
        }

        try
        {
            var employees = await _apiClient.GetEmployeesByBranchAsync(session.BranchId.Value, session.AccessToken, CancellationToken.None);

            if (!ReferenceEquals(session, _session)) return; // signed out while loading

            // Cache names locally so the kiosk can greet people without any login.
            _store.UpsertEmployees(employees.Select(e => new EmployeeDirectoryEntry(
                $"emp-{e.Id}", e.Id, e.FullName, e.EmployeeNumber, e.Position, e.BranchName)));

            EmployeeComboBox.ItemsSource = employees
                .Select(e => new EmployeePickerItem(e.Id, $"{e.FullName} ({e.EmployeeNumber})", e))
                .ToList();

            if (employees.Count == 0)
                ShowToast("No employees found for this branch.", "warning");

            RefreshAttendance();
            RefreshDeviceInfo();
        }
        catch (Exception)
        {
            if (ReferenceEquals(session, _session))
                ShowToast("Couldn't load employees. Check the internet connection.", "error");
        }
    }

    // ===== Enrollment =====

    private void EmployeeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_enrolling) return;

        if (EmployeeComboBox.SelectedItem is not EmployeePickerItem item)
        {
            PreviewCard.Visibility = Visibility.Collapsed;
            UpdateEnrollAvailability();
            return;
        }

        var emp = item.Employee;
        var enrolled = _adapter?.IsEnrolled($"emp-{emp.Id}") ?? false;

        PreviewInitialsText.Text = Initials(emp.FullName);
        PreviewNameText.Text = emp.FullName;
        PreviewPositionText.Text = string.IsNullOrWhiteSpace(emp.Position) ? "—" : emp.Position;
        PreviewNumberText.Text = emp.EmployeeNumber;
        PreviewBranchText.Text = string.IsNullOrWhiteSpace(emp.BranchName) ? "—" : emp.BranchName;

        PreviewStatusText.Text = enrolled ? "Already enrolled" : "Fingerprint pending";
        PreviewStatusText.Foreground = Br(enrolled ? "#047857" : "#B45309");
        PreviewStatusPill.Background = Br(enrolled ? "#ECFDF5" : "#FFFBEB");
        PreviewStatusPill.BorderBrush = Br(enrolled ? "#A7F3D0" : "#FDE68A");
        PreviewCard.Visibility = Visibility.Visible;

        if (enrolled)
        {
            SetEnrollState(EnrollUiState.Ready, "This employee already has a fingerprint on this terminal.");
        }
        else
        {
            SetEnrollState(EnrollUiState.Ready);
        }

        UpdateEnrollAvailability();
    }

    private void UpdateEnrollAvailability()
    {
        var canEnroll = !_enrolling && _deviceConnected && _adapter is not null
                        && EmployeeComboBox.SelectedItem is EmployeePickerItem item
                        && !_adapter.IsEnrolled($"emp-{item.Id}");
        EnrollButton.IsEnabled = canEnroll;
    }

    private async void EnrollButton_Click(object sender, RoutedEventArgs e)
    {
        if (_enrolling || _adapter is null || _apiClient is null || _store is null || _session is null) return;

        if (EmployeeComboBox.SelectedItem is not EmployeePickerItem selected)
        {
            ShowToast("Select an employee from the list first.", "warning");
            return;
        }

        var session = _session;
        var terminalUserId = $"emp-{selected.Id}";

        if (_adapter.IsEnrolled(terminalUserId))
        {
            ShowToast("This employee is already enrolled on this terminal.", "warning");
            return;
        }

        _enrolling = true;
        UpdateEnrollAvailability();
        SetEnrollState(EnrollUiState.Capturing);

        try
        {
            var captured = await Task.Run(() => _adapter.EnrollAsync(terminalUserId, CancellationToken.None));

            if (!captured)
            {
                SetEnrollState(EnrollUiState.Failed, "No clear fingerprint was captured. Press firmly, keep still and try again.");
                ShowToast("No fingerprint captured. Please try again.", "error");
                return;
            }

            SetEnrollState(EnrollUiState.Registering);

            SyncResult result;
            try
            {
                result = await _apiClient.RegisterEnrollmentAsync(selected.Id, terminalUserId, session.AccessToken, CancellationToken.None);
            }
            catch (Exception)
            {
                // Server unreachable: undo the local template so we never keep an unmapped fingerprint.
                await _adapter.RemoveEnrollmentAsync(terminalUserId);
                SetEnrollState(EnrollUiState.Failed, "Couldn't reach the server, so the enrollment was cancelled. Check the connection and try again.");
                ShowToast("Couldn't reach the server. Enrollment cancelled.", "error");
                return;
            }

            if (!result.Succeeded)
            {
                await _adapter.RemoveEnrollmentAsync(terminalUserId);
                var friendly = FriendlyMessages.Translate(result.Error);
                SetEnrollState(EnrollUiState.Failed, friendly);
                ShowToast(friendly, "error");
                return;
            }

            _store.UpsertEmployees(new[]
            {
                new EmployeeDirectoryEntry(terminalUserId, selected.Id, selected.Employee.FullName,
                    selected.Employee.EmployeeNumber, selected.Employee.Position, selected.Employee.BranchName)
            });

            SetEnrollState(EnrollUiState.Success, $"{selected.Employee.FullName} can now clock in at the kiosk.");
            ShowToast($"{selected.DisplayLabel} enrolled successfully.", "success");

            PreviewStatusText.Text = "Already enrolled";
            PreviewStatusText.Foreground = Br("#047857");
            PreviewStatusPill.Background = Br("#ECFDF5");
            PreviewStatusPill.BorderBrush = Br("#A7F3D0");
            RefreshDeviceInfo();
        }
        catch (Exception)
        {
            await _adapter.RemoveEnrollmentAsync(terminalUserId);
            SetEnrollState(EnrollUiState.Failed, "Something went wrong while enrolling. Please try again.");
            ShowToast("Enrollment failed. Please try again.", "error");
        }
        finally
        {
            _enrolling = false;
            UpdateEnrollAvailability();
        }
    }

    private void SetEnrollState(EnrollUiState state, string? detail = null)
    {
        switch (state)
        {
            case EnrollUiState.Ready:
                EnrollHeadlineText.Text = "Ready to enroll";
                EnrollSubtext.Text = detail ?? "Select an employee, then press Enroll Fingerprint.";
                SetStagePill("Ready", "#DBEAFE", "#2563A8");
                UpdateSteps(1);
                StopEnrollAnimations();
                break;

            case EnrollUiState.Capturing:
                EnrollHeadlineText.Text = "Place finger firmly on the reader…";
                EnrollSubtext.Text = "Hold still until the capture finishes (up to 5 seconds).";
                SetStagePill("Capturing", "#FFEDD5", "#C2410C");
                UpdateSteps(2);
                StartEnrollAnimations();
                break;

            case EnrollUiState.Registering:
                EnrollHeadlineText.Text = "Fingerprint captured";
                EnrollSubtext.Text = "Saving to the server…";
                SetStagePill("Saving", "#DBEAFE", "#2563A8");
                UpdateSteps(3);
                StopEnrollAnimations();
                break;

            case EnrollUiState.Success:
                EnrollHeadlineText.Text = "Fingerprint enrolled";
                EnrollSubtext.Text = detail ?? string.Empty;
                SetStagePill("Enrolled", "#DCFCE7", "#15803D");
                UpdateSteps(4);
                StopEnrollAnimations();
                break;

            case EnrollUiState.Failed:
                EnrollHeadlineText.Text = "Enrollment didn't complete";
                EnrollSubtext.Text = detail ?? "Please try again.";
                SetStagePill("Failed", "#FEE2E2", "#B91C1C");
                UpdateSteps(1);
                StopEnrollAnimations();
                break;
        }
    }

    private void SetStagePill(string text, string background, string foreground)
    {
        EnrollStagePillText.Text = text;
        EnrollStagePillText.Foreground = Br(foreground);
        EnrollStagePill.Background = Br(background);
    }

    // current: 1 = selecting, 2 = scanning, 3 = registering, 4 = everything done
    private void UpdateSteps(int current)
    {
        StyleStep(Step1Circle, Step1Text, Step1Label, "1", 1, current);
        StyleStep(Step2Circle, Step2Text, Step2Label, "2", 2, current);
        StyleStep(Step3Circle, Step3Text, Step3Label, "3", 3, current);
    }

    private static void StyleStep(Border circle, TextBlock number, TextBlock label, string n, int step, int current)
    {
        if (step < current)
        {
            circle.Background = Br("#D1FAE5"); number.Text = "✓"; number.Foreground = Br("#059669"); label.Foreground = Br("#059669");
        }
        else if (step == current)
        {
            circle.Background = Br("#DBEAFE"); number.Text = n; number.Foreground = Br("#2563A8"); label.Foreground = Br("#2563A8");
        }
        else
        {
            circle.Background = Br("#F1F5F9"); number.Text = n; number.Foreground = Br("#94A3B8"); label.Foreground = Br("#94A3B8");
        }
    }

    private void StartEnrollAnimations()
    {
        EnrollPulseRing.Visibility = Visibility.Visible;
        EnrollLaser.Visibility = Visibility.Visible;

        var pulse = new DoubleAnimation(0.95, 1.08, TimeSpan.FromSeconds(1.25))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        EnrollPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        EnrollPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);

        EnrollLaserTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 104, TimeSpan.FromSeconds(1.1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private void StopEnrollAnimations()
    {
        EnrollPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        EnrollPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        EnrollLaserTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        EnrollPulseRing.Visibility = Visibility.Hidden;
        EnrollLaser.Visibility = Visibility.Hidden;
    }

    // ===== Helpers =====

    private static string Initials(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][0].ToString().ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    private static SolidColorBrush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}