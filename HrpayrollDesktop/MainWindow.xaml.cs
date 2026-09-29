using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HrpayrollDesktop;

public partial class MainWindow : System.Windows.Window
{
    private record EmployeePickerItem(long Id, string DisplayLabel);
    private record AttendanceRow(string EmployeeName, string Summary);

    private readonly ITerminalAdapter _terminalAdapter;
    private readonly BackendApiClient _apiClient;
    private readonly AuthResponse _session;
    private DispatcherTimer? _toastTimer;
    private bool _sidebarExpanded = true;

    public MainWindow(AttendanceEngine engine, ITerminalAdapter terminalAdapter, BackendApiClient apiClient, AuthResponse session)
    {
        InitializeComponent();

        _terminalAdapter = terminalAdapter;
        _apiClient = apiClient;
        _session = session;

        ApplySessionToUi();

        engine.MatchOccurred += (terminalUserId, eventType, timestamp) =>
        {
            Dispatcher.Invoke(() => { });
            _ = RefreshTodayAttendanceAsync();
        };

        engine.DeviceReady += () =>
        {
            Dispatcher.Invoke(() =>
            {
                DeviceStatusText.Text = "Fingerprint reader: connected";
                DeviceStatusCardText.Text = "Connected";
                DeviceSerialText.Text = _terminalAdapter.DeviceSerial ?? "SecuGen Reader";
                DeviceNavDot.Fill = (System.Windows.Media.Brush)FindResource("GreenBrush");
                SystemAlertBorder.Visibility = Visibility.Collapsed;
            });
        };

        engine.DeviceFault += message =>
        {
            Dispatcher.Invoke(() =>
            {
                DeviceStatusText.Text = "Fingerprint reader error";
                DeviceStatusCardText.Text = "Not Detected";
                DeviceNavDot.Fill = (System.Windows.Media.Brush)FindResource("ErrorBrush");
                SystemAlertText.Text = message;
                SystemAlertBorder.Visibility = Visibility.Visible;
            });
        };

        _ = LoadEmployeesAsync();
        _ = RefreshTodayAttendanceAsync();
    }

    private void ApplySessionToUi()
    {
        var initial = string.IsNullOrWhiteSpace(_session.DisplayName) ? "B" : _session.DisplayName[0].ToString().ToUpper();

        GreetingText.Text = $"Hello, {_session.DisplayName}";
        SessionText.Text = "Welcome back. Here's your branch today.";
        TitleBarText.Text = $"Dhwarsh HR Desktop — {_session.DisplayName} (Branch Manager)";

        SidebarUserNameText.Text = _session.DisplayName;
        SidebarUserRoleText.Text = "Branch Manager";
        TopbarUserNameText.Text = _session.DisplayName;

        UserInitialText.Text = initial;
        TopbarUserInitialText.Text = initial;
    }

    // ===== Title bar =====

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { MaximizeButton_Click(sender, e); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ===== Sidebar collapse =====

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

        // Fade the text groups out/in rather than hard-cutting them, matching
        // the smooth collapse behavior from the reference design.
        var textVisibility = _sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
        SidebarTextGroup.Visibility = textVisibility;
        SidebarUserTextGroup.Visibility = textVisibility;
        NavDashboardLabel.Visibility = textVisibility;
        NavEnrollLabel.Visibility = textVisibility;
        NavAttendanceLabel.Visibility = textVisibility;
        NavDeviceLabel.Visibility = textVisibility;
        NavHelpLabel.Visibility = textVisibility;
    }

    // ===== Sidebar navigation =====

    private void NavDashboard_Click(object sender, RoutedEventArgs e)
    {
        NavDashboard.IsChecked = true;
        NavEnroll.IsChecked = false;
        DashboardView.Visibility = Visibility.Visible;
        EnrollView.Visibility = Visibility.Collapsed;
    }

    private void NavEnroll_Click(object sender, RoutedEventArgs e)
    {
        NavEnroll.IsChecked = true;
        NavDashboard.IsChecked = false;
        DashboardView.Visibility = Visibility.Collapsed;
        EnrollView.Visibility = Visibility.Visible;
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    // ===== Toast =====

    private void ShowToast(string message, string kind)
    {
        var brush = kind switch
        {
            "success" => (System.Windows.Media.Brush)FindResource("SuccessBrush"),
            "warning" => (System.Windows.Media.Brush)FindResource("WarningBrush"),
            _ => (System.Windows.Media.Brush)FindResource("ErrorBrush"),
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

    // ===== Data loading =====

    private async Task RefreshTodayAttendanceAsync()
    {
        if (_session.BranchId is null) return;

        var records = await _apiClient.GetTodayAttendanceForBranchAsync(_session.BranchId.Value, _session.AccessToken, CancellationToken.None);

        var rows = records
            .OrderByDescending(r => r.ClockIn)
            .Select(r => new AttendanceRow(
                r.EmployeeName ?? $"Employee #{r.EmployeeId}",
                r.ClockOut is not null
                    ? $"{r.ClockIn:HH:mm} – {r.ClockOut:HH:mm}"
                    : $"In at {r.ClockIn:HH:mm}"))
            .ToList();

        Dispatcher.Invoke(() =>
        {
            TodayAttendanceList.ItemsSource = rows;
            TodayAttendanceEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PresentTodayCountText.Text = records.Count(r => r.ClockOut is null).ToString();
        });
    }

    private async Task LoadEmployeesAsync()
    {
        if (_session.BranchId is null)
        {
            ShowToast("No branch assigned to this account. Contact your administrator.", "error");
            return;
        }

        var employees = await _apiClient.GetEmployeesByBranchAsync(_session.BranchId.Value, _session.AccessToken, CancellationToken.None);

        EmployeeComboBox.ItemsSource = employees
            .Select(e => new EmployeePickerItem(e.Id, $"{e.FullName} ({e.EmployeeNumber})"))
            .ToList();

        EmployeesEnrolledText.Text = employees.Count.ToString();

        if (employees.Count == 0)
        {
            ShowToast("No employees found for this branch.", "warning");
        }
    }

    // ===== Enrollment =====

    private async void EnrollButton_Click(object sender, RoutedEventArgs e)
    {
        if (EmployeeComboBox.SelectedItem is not EmployeePickerItem selected)
        {
            ShowToast("Select an employee from the list first.", "warning");
            return;
        }

        var employeeId = selected.Id;
        var terminalUserId = $"emp-{employeeId}";

        EnrollButton.IsEnabled = false;
        ShowToast("Place finger on the reader...", "warning");

        try
        {
            var captured = await Task.Run(() => _terminalAdapter.EnrollAsync(terminalUserId, CancellationToken.None));

            if (!captured)
            {
                ShowToast("No fingerprint captured. Please try again.", "error");
                return;
            }

            var result = await _apiClient.RegisterEnrollmentAsync(employeeId, terminalUserId, _session.AccessToken, CancellationToken.None);

            if (!result.Succeeded)
            {
                await _terminalAdapter.RemoveEnrollmentAsync(terminalUserId);
                ShowToast(FriendlyMessages.Translate(result.Error), "error");
            }
            else
            {
                ShowToast($"{selected.DisplayLabel} enrolled successfully.", "success");
                _ = LoadEmployeesAsync();
            }
        }
        finally
        {
            EnrollButton.IsEnabled = true;
        }
    }
}