using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Local;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HrpayrollDesktop;

public partial class ConsoleView : UserControl
{
    // ===== View models =====

    private sealed record PickerItem(long Id, string Name, string Initials, string Sub, string StatusText, Brush StatusFg,
        Brush AvatarBg, Brush AvatarFg, bool Enrolled, EmployeeSummary Employee);

    private sealed record LogRow(int Number, string UserId, string EmployeeName, string EmployeeNumber, string Subtitle,
        string Initials, Brush AvatarBg, Brush AvatarFg, string ClockIn, string ClockOut, string Hours, string Status,
        Brush StatusBg, Brush StatusFg, string StatusKey, string Punches, DateTime? FirstKey, DateTime? LastKey,
        string Breaks, Brush BreaksFg, bool OnBreak, int MinutesLate);

    private sealed record LiveCard(string Name, string Position, string Initials, Brush AvatarBg, Brush AvatarFg,
        string StatusText, Brush StatusBg, Brush StatusFg, string DateText);

    // One lunch break or temporary check-out, in UTC, from the server or from this PC's own scans.
    private sealed record BreakInfo(bool IsLunch, DateTime LeftUtc, DateTime? DueUtc, DateTime? BackUtc, string? Reason)
    {
        public bool IsOut => BackUtc is null;

        // Only a lunch can be late. Rounded up, as the server does, so a few seconds never reads as 0 minutes.
        public int MinutesLate => IsLunch && BackUtc is { } back && DueUtc is { } due && back > due
            ? (int)Math.Ceiling((back - due).TotalMinutes) : 0;
    }

    private sealed record ChartBar(string Label, string Tip, int Dark, int Mid, int Light);

    // ===== Palette =====

    private static readonly Brush InBg = Br("#E5F4E2"), InFg = Br("#2E7D24");
    private static readonly Brush OutBg = Br("#E6F0F8"), OutFg = Br("#1C6EB4");
    private static readonly Brush AbsBg = Br("#F0F4F8"), AbsFg = Br("#717782");
    private static readonly Brush WarnBg = Br("#FFF4D6"), WarnFg = Br("#8A5F00");
    private static readonly Brush RedBg = Br("#FFDAD6"), RedFg = Br("#BA1A1A");
    private static readonly Brush InkSoft = Br("#414751");
    private static readonly Brush DarkTeal = Br("#005592"), MidTeal = Br("#5BA7E6"), TrackGray = Br("#E4EDF4"), Gold = Br("#F2A900");

    private static readonly (Brush Bg, Brush Fg)[] AvatarPalette =
    {
        (Br("#D2E4FF"), Br("#2E7D24")), (Br("#FFF4D6"), Br("#8A5F00")), (Br("#E6F0F8"), Br("#005592")),
        (Br("#FFE3D1"), Br("#B4530F")), (Br("#E6F0F8"), Br("#0B2A45")), (Br("#E5F4E2"), Br("#2E7D24")),
    };

    // ===== State =====

    private AttendanceEngine? _engine;
    private ITerminalAdapter? _adapter;
    private BackendApiClient? _apiClient;
    private LocalAttendanceStore? _store;
    private AuthResponse? _session;
    private string? _branchName;

    // What the server recorded for this branch, from every reader it has. Null until it has loaded once.
    private List<AttendanceRecord>? _branchAttendance;
    private readonly Dictionary<string, string> _branchAttendanceNames = new();
    private DateTime _activityFrom = DateTime.MaxValue;
    private readonly Dictionary<DateTime, Dictionary<string, List<BreakInfo>>> _serverBreaks = new();
    private DateTime _activityFetchedUtc;
    private bool _loadingActivity;
    private const int ActivityDays = 30;

    private readonly bool _initialized;
    private readonly DispatcherTimer _autoRefresh;
    private DispatcherTimer? _toastTimer;

    private bool _userSidebarExpanded = true;
    private bool _sidebarExpanded = true;
    private int _layoutMode;

    private bool _enrolling;
    private bool _loadingEmployees;
    private bool _deviceConnected;
    private string? _deviceMessage;

    private string _search = string.Empty;
    private string _chartMode = "Today";
    private DateTime _pageDate = DateTime.Today;
    private string _pageFilter = "All";

    private List<ChartBar>? _chartBars;
    private List<EmployeeSummary> _employeeSummaries = new();
    private long? _selectedEmployeeId;
    private List<LogRow> _dashRows = new();
    private List<LogRow> _pageRows = new();

    public event Action? LockRequested;
    public event Action? ExitRequested;
    public event Action<MouseButtonEventArgs>? HeaderDragRequested;

    /// <summary>True while a fingerprint enrollment is in progress (blocks the idle auto-lock).</summary>
    public bool IsBusy => _enrolling || DamagesPage.IsBusy || DaysWorkedPage.IsBusy;

    public ConsoleView()
    {
        InitializeComponent();

        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoRefresh.Tick += (_, _) =>
        {
            if (_session is null) return;
            RefreshAttendance();
            _ = LoadBranchActivityAsync();
            if (AttendancePage.Visibility == Visibility.Visible && _pageDate == DateTime.Today) RefreshPageLog();
        };

        _initialized = true;
        UpdateSteps(1);
    }

    public void Initialize(AttendanceEngine engine, ITerminalAdapter adapter, BackendApiClient apiClient, LocalAttendanceStore store)
    {
        _engine = engine;
        _adapter = adapter;
        _apiClient = apiClient;
        _store = store;

        DamagesPage.Initialize(engine, adapter, apiClient, store);
        DamagesPage.Notify += ShowToast;

        DaysWorkedPage.Initialize(apiClient);
        DaysWorkedPage.Notify += ShowToast;
    }

    // ===================================================================
    //  Session lifecycle
    // ===================================================================

    public void StartSession(AuthResponse session)
    {
        _session = session;
        _apiClient?.BeginStaffSession(session);
        DamagesPage.StartSession(session);
        DaysWorkedPage.StartSession(session);
        DaysWorkedPage.SetBranch(session.BranchId);
        ApplySessionToUi();
        ApplyDeviceState();

        _search = string.Empty;
        HeaderSearchBox.Text = string.Empty;

        _pageDate = DateTime.Today;
        _layoutMode = 0;
        ApplyDashboardLayout();
        ApplySidebar();

        SelectPage("Dashboard");
        SetEnrollState(EnrollUiState.Ready);
        RefreshDeviceInfo();
        _autoRefresh.Start();
        _engine?.InvalidateMappingCache();
        _ = LoadEmployeesAsync();
        _ = BackUpLocalFingerprintsAsync(session);
    }

    public void EndSession()
    {
        _autoRefresh.Stop();
        DamagesPage.EndSession();
        DaysWorkedPage.EndSession();
        _apiClient?.EndStaffSession();
        _session = null;
        _branchAttendance = null;
        _branchAttendanceNames.Clear();
        _serverBreaks.Clear();
        _activityFrom = DateTime.MaxValue;
        EmployeeListBox.ItemsSource = null;
        _employeeSummaries = new List<EmployeeSummary>();
        _selectedEmployeeId = null;
        ToastBorder.Visibility = Visibility.Collapsed;
        NotifyPopup.IsOpen = false;
        StopEnrollAnimations();
    }

    private void ApplySessionToUi()
    {
        if (_session is null) return;

        var name = string.IsNullOrWhiteSpace(_session.DisplayName) ? "Back Office" : _session.DisplayName;
        HeaderNameText.Text = name;
        HeaderAvatarText.Text = Initials(name);
        HeaderSubText.Text = _branchName ?? "Back Office";
        HeroDateText.Text = DateTime.Now.ToString("MMMM d, yyyy  •  dddd", CultureInfo.InvariantCulture);
        HeroGreetingText.Text = $"Welcome back, {PopupHost.FirstName(name)}!";
    }

    // ===================================================================
    //  Called by the shell
    // ===================================================================

    public void SetDeviceState(bool connected, string? message)
    {
        _deviceConnected = connected;
        _deviceMessage = message;
        ApplyDeviceState();
    }

    public void NotifyClockEvent(string employeeName, string eventType, DateTime timestampUtc)
    {
        var action = eventType switch
        {
            "CLOCK_OUT" => "clocked out",
            "LUNCH_OUT" => "went for lunch",
            "EMERGENCY_OUT" => "checked out temporarily",
            "BREAK_RETURN" => "came back",
            _ => "clocked in",
        };
        ShowToast($"{employeeName} {action} at {timestampUtc.ToLocalTime().ToString("hh:mm tt", CultureInfo.InvariantCulture)}", "success");
        RefreshAttendance();
        if (AttendancePage.Visibility == Visibility.Visible && _pageDate == DateTime.Today) RefreshPageLog();
        RefreshDeviceInfo();
    }

    private void ApplyDeviceState()
    {
        if (_deviceConnected)
        {
            DeviceNavDot.Fill = Br("#9CD84F");
            SystemAlertBorder.Visibility = Visibility.Collapsed;
            EnrollSensorText.Text = "Ready";
            EnrollSensorText.Foreground = Br("#3B9A2F");
        }
        else
        {
            var hasFault = !string.IsNullOrWhiteSpace(_deviceMessage);
            DeviceNavDot.Fill = Br(hasFault ? "#BA1A1A" : "#F2A900");
            SystemAlertText.Text = _deviceMessage ?? string.Empty;
            SystemAlertBorder.Visibility = hasFault ? Visibility.Visible : Visibility.Collapsed;
            EnrollSensorText.Text = hasFault ? "Not detected" : "Connecting…";
            EnrollSensorText.Foreground = Br("#BA1A1A");
        }

        UpdateEnrollAvailability();
        RefreshDeviceInfo();
        UpdateBellBadge();
    }

    // ===================================================================
    //  Window chrome, sidebar, responsiveness
    // ===================================================================

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => HeaderDragRequested?.Invoke(e);

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialized) return;
        ApplySidebar();
        ApplyDashboardLayout();
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _userSidebarExpanded = !_userSidebarExpanded;
        ApplySidebar();
        ApplyDashboardLayout();
    }

    private void ApplySidebar()
    {
        // Auto-collapse on narrow windows, otherwise honour the user's choice.
        var expanded = _userSidebarExpanded && ActualWidth >= 1100;
        SidebarColumn.Width = new GridLength(expanded ? 190 : 72);
        LogoTile.Width = LogoTile.Height = expanded ? 84 : 44;
        LogoTile.CornerRadius = new CornerRadius(expanded ? 20 : 12);
        LogoTile.Padding = new Thickness(expanded ? 9 : 5);
        _sidebarExpanded = expanded;

        var v = expanded ? Visibility.Visible : Visibility.Collapsed;
        NavDashboardLabel.Visibility = v;
        NavAttendanceLabel.Visibility = v;
        NavEnrollLabel.Visibility = v;
        NavDaysWorkedLabel.Visibility = v;
        NavDamagesLabel.Visibility = v;
        NavDeviceLabel.Visibility = v;
        NavHelpLabel.Visibility = v;
        NavSignOutLabel.Visibility = v;
    }

    /// <summary>The design's side column on a wide window. On a narrow one it moves underneath.</summary>
    private void ApplyDashboardLayout()
    {
        if (!_initialized) return;

        var mode = ActualWidth - SidebarColumn.Width.Value >= 1000 ? 2 : 1;
        if (mode == _layoutMode) return;
        _layoutMode = mode;

        var twoColumns = mode == 2;
        DashSideColumn.Width = twoColumns ? new GridLength(290) : new GridLength(0);
        Grid.SetColumn(DashSide, twoColumns ? 1 : 0);
        Grid.SetRow(DashSide, twoColumns ? 0 : 1);
    }

    // ===================================================================
    //  Navigation
    // ===================================================================

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string page })
            SelectPage(page);
    }

    private void NavSignOut_Click(object sender, RoutedEventArgs e)
    {
        NavSignOut.IsChecked = false;
        RequestLock();
    }

    private void SelectPage(string page)
    {
        NavDashboard.IsChecked = page == "Dashboard";
        NavAttendance.IsChecked = page == "Attendance";
        NavEnroll.IsChecked = page == "Enroll";
        NavDaysWorked.IsChecked = page == "DaysWorked";
        NavDamages.IsChecked = page == "Damages";
        NavDevice.IsChecked = page == "Device";
        NavHelp.IsChecked = page == "Help";

        DashboardPage.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        AttendancePage.Visibility = page == "Attendance" ? Visibility.Visible : Visibility.Collapsed;
        EnrollPage.Visibility = page == "Enroll" ? Visibility.Visible : Visibility.Collapsed;
        DaysWorkedPage.Visibility = page == "DaysWorked" ? Visibility.Visible : Visibility.Collapsed;
        DamagesPage.Visibility = page == "Damages" ? Visibility.Visible : Visibility.Collapsed;
        DevicePage.Visibility = page == "Device" ? Visibility.Visible : Visibility.Collapsed;
        HelpPage.Visibility = page == "Help" ? Visibility.Visible : Visibility.Collapsed;

        if (page == "Dashboard") RefreshAttendance();
        if (page == "Attendance") RefreshPageLog();
        if (page == "Device") RefreshDeviceInfo();
        if (page == "Enroll") { _loadingEmployees = false; _ = LoadEmployeesAsync(); }
        if (page == "DaysWorked") _ = DaysWorkedPage.RefreshAsync();
        if (page == "Damages") _ = DamagesPage.RefreshAsync();
    }

#if DEBUG
    /// <summary>Test builds only: opens a page without a mouse.</summary>
    internal void TestSelectPage(string page) => SelectPage(page);
#endif

    private void RequestLock()
    {
        if (IsBusy)
        {
            ShowToast("Please wait for the fingerprint capture to finish.", "warning");
            return;
        }
        LockRequested?.Invoke();
    }

    // Gear menu
    private void GearButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("GearMenu");
        menu.PlacementTarget = GearButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void GearDevice_Click(object sender, RoutedEventArgs e) => SelectPage("Device");
    private void GearHelp_Click(object sender, RoutedEventArgs e) => SelectPage("Help");
    private void GearSignOut_Click(object sender, RoutedEventArgs e) => RequestLock();

    private async void GearExit_Click(object sender, RoutedEventArgs e)
    {
        if (PopupHost.Current is not { } popups) return;

        var yes = await popups.ConfirmAsync("Exit Dhwarsh HR?",
            "Fingerprint attendance will stop on this computer until the app is started again.", "Yes, exit", dangerous: true);

        if (yes) ExitRequested?.Invoke();
    }

    // ===================================================================
    //  Search (filters the time log)
    // ===================================================================

    private void HeaderSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        HeaderSearchPlaceholder.Visibility = string.IsNullOrEmpty(HeaderSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!_initialized) return;

        _search = HeaderSearchBox.Text.Trim();

        // Searching from a page without a table jumps to the full time log.
        if (!string.IsNullOrEmpty(_search) && DashboardPage.Visibility != Visibility.Visible
            && AttendancePage.Visibility != Visibility.Visible)
            SelectPage("Attendance");

        RefreshTables();
    }

    // ===================================================================
    //  Notifications (bell)
    // ===================================================================

    private void BellButton_Click(object sender, RoutedEventArgs e)
    {
        BuildNotifications();
        NotifyPopup.IsOpen = true;
    }

    private void BuildNotifications()
    {
        NotifyList.Children.Clear();

        AddNote(_deviceConnected ? "Fingerprint reader connected" : "Fingerprint reader not detected", _deviceConnected);

        var pending = _engine?.PendingSyncCount ?? 0;
        AddNote(pending == 0 ? "All scans are synced to the server" : $"{pending} scan(s) waiting to sync", pending == 0);

        if (_store is not null && _adapter is not null)
        {
            var directory = _store.GetAllEmployees();
            if (directory.Count == 0)
            {
                AddNote("Employee list not loaded yet (needs a connection)", false);
            }
            else
            {
                var notEnrolled = directory.Count(d => !_adapter.IsEnrolled(d.TerminalUserId));
                AddNote(notEnrolled == 0 ? "Everyone has a fingerprint enrolled" : $"{notEnrolled} employee(s) not enrolled yet", notEnrolled == 0);
            }
        }
    }

    private void AddNote(string text, bool ok)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = ok ? Br("#5BA7E6") : Br("#F2A900"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Br("#171C1F"),
            Margin = new Thickness(10, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 230
        });
        NotifyList.Children.Add(row);
    }

    private void UpdateBellBadge()
    {
        var pending = _engine?.PendingSyncCount ?? 0;
        BellBadge.Visibility = (!_deviceConnected || pending > 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================================================================
    //  Toast
    // ===================================================================

    public void ShowToast(string message, string kind)
    {
        ToastBorder.Background = kind switch
        {
            "success" => Br("#1C6EB4"),
            "warning" => Br("#8A5F00"),
            _ => Br("#BA1A1A"),
        };
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

    // ===================================================================
    //  Time-log data (shared by dashboard + full page + export)
    // ===================================================================

    private static TimeSpan Worked(IReadOnlyList<AttendanceLogEntry> events, bool isToday)
    {
        var total = TimeSpan.Zero;
        DateTime? open = null;

        foreach (var e in events)
        {
            if (e.EventType == "CLOCK_IN") open ??= e.TimestampUtc;
            else if (open is not null) { total += e.TimestampUtc - open.Value; open = null; }
        }

        if (open is not null && isToday) total += DateTime.UtcNow - open.Value;
        return total < TimeSpan.Zero ? TimeSpan.Zero : total;
    }

    private static string FormatHours(TimeSpan t) =>
        t.TotalMinutes < 1 ? "—" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m";

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("hh:mm tt", CultureInfo.InvariantCulture);

    /// <summary>
    /// The branch's activity for the period: what the server recorded from all its readers, plus this PC's scans
    /// that haven't reached it yet. Offline, this PC's own scans of the branch's current staff stand in.
    /// A scan the server refused is not shown as attendance, and nor is anything from another branch.
    /// </summary>
    private List<AttendanceLogEntry> BranchLog(DateTime fromLocal, DateTime toLocalExclusive)
    {
        if (_store is null) return new List<AttendanceLogEntry>();

        var staff = _store.GetAllEmployees().Select(e => e.TerminalUserId).ToHashSet();
        var local = _store.GetLogBetween(fromLocal, toLocalExclusive)
            .Where(l => staff.Contains(l.TerminalUserId) && l.EventType is "CLOCK_IN" or "CLOCK_OUT");

        if (_branchAttendance is null)
            return local.ToList();

        DateTime fromUtc = fromLocal.ToUniversalTime(), toUtc = toLocalExclusive.ToUniversalTime();
        var events = new List<AttendanceLogEntry>();
        foreach (var record in _branchAttendance)
        {
            var id = $"emp-{record.EmployeeId}";
            if (record.ClockIn is { } clockIn) events.Add(new AttendanceLogEntry($"server-{record.Id}-in", id, "CLOCK_IN", clockIn.ToUniversalTime()));
            if (record.ClockOut is { } clockOut) events.Add(new AttendanceLogEntry($"server-{record.Id}-out", id, "CLOCK_OUT", clockOut.ToUniversalTime()));
        }
        events.RemoveAll(e => e.TimestampUtc < fromUtc || e.TimestampUtc >= toUtc);

        // A scan newer than the last fetch may simply not have been picked up yet, so it shows until it is.
        var unsent = _engine?.GetUnsentEventIds() ?? new HashSet<string>();
        var onItsWay = local
            .Where(l => unsent.Contains(l.EventId) || l.TimestampUtc >= _activityFetchedUtc)
            .Where(l => !events.Exists(e => e.TerminalUserId == l.TerminalUserId && e.EventType == l.EventType
                                            && (e.TimestampUtc - l.TimestampUtc).Duration() < TimeSpan.FromMinutes(2)))
            .ToList();

        events.AddRange(onItsWay);
        return events.OrderBy(e => e.TimestampUtc).ToList();
    }

    private async Task LoadBranchActivityAsync()
    {
        var session = _session;
        if (session?.BranchId is not long branchId || _apiClient is null || _loadingActivity) return;

        var from = DateTime.Today.AddDays(-(ActivityDays - 1));
        if (_pageDate < from) from = _pageDate;

        _loadingActivity = true;
        _activityFrom = from;   // set up front, so a failed load isn't retried on every redraw
        try
        {
            var fetchedAt = DateTime.UtcNow;
            var records = await _apiClient.GetBranchAttendanceAsync(branchId, from, DateTime.Today, session.AccessToken, CancellationToken.None);
            if (records is null || !ReferenceEquals(session, _session)) return;

            _branchAttendance = records;
            _activityFetchedUtc = fetchedAt;
            foreach (var record in records.Where(r => !string.IsNullOrWhiteSpace(r.EmployeeName)))
                _branchAttendanceNames[$"emp-{record.EmployeeId}"] = record.EmployeeName!;

            await LoadBreaksAsync(DateTime.Today, session, branchId);
            if (_pageDate != DateTime.Today) await LoadBreaksAsync(_pageDate, session, branchId);

            RefreshAttendance();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline: keep showing what we have. The 30 second refresh tries again.
        }
        finally
        {
            _loadingActivity = false;
        }
    }

    private async Task LoadBreaksAsync(DateTime day, AuthResponse session, long branchId)
    {
        if (_apiClient is null) return;

        var records = await _apiClient.GetBranchDayRecordsAsync(branchId, day, session.AccessToken, CancellationToken.None);
        if (records is null || !ReferenceEquals(session, _session)) return;

        // Times from the server are the company's local time, so ToUniversalTime reads them correctly.
        _serverBreaks[day.Date] = records
            .Where(r => r.Checkouts is { Count: > 0 })
            .GroupBy(r => $"emp-{r.EmployeeId}")
            .ToDictionary(g => g.Key, g => g.SelectMany(r => r.Checkouts!).Select(b => new BreakInfo(
                b.Type == "LUNCH", b.CheckedOutAt.ToUniversalTime(), b.ExpectedBackAt?.ToUniversalTime(),
                b.CheckedInAt?.ToUniversalTime(), b.Reason)).OrderBy(b => b.LeftUtc).ToList());
    }

    // This PC's own scans come first, as they include anything still on its way to the server.
    private List<BreakInfo> BreaksFor(string userId, IReadOnlyList<AttendanceLogEntry> dayLog, DateTime day)
    {
        var breaks = new List<BreakInfo>();
        foreach (var scan in dayLog.Where(l => l.TerminalUserId == userId))
        {
            if (scan.EventType is "LUNCH_OUT" or "EMERGENCY_OUT")
                breaks.Add(new BreakInfo(scan.EventType == "LUNCH_OUT", scan.TimestampUtc, scan.ExpectedBackUtc, null, scan.Reason));
            else if (scan.EventType == "BREAK_RETURN" && breaks.Count > 0 && breaks[^1].IsOut)
                breaks[^1] = breaks[^1] with { BackUtc = scan.TimestampUtc };
        }

        if (breaks.Count > 0) return breaks;

        return _serverBreaks.TryGetValue(day.Date, out var fromServer) && fromServer.TryGetValue(userId, out var list)
            ? list : breaks;
    }

    private static string DescribeBreak(BreakInfo b)
    {
        var name = b.IsLunch ? "Lunch" : "Temporary";
        var reason = string.IsNullOrWhiteSpace(b.Reason) ? string.Empty : $" ({b.Reason})";

        if (b.BackUtc is not { } back)
            return $"{name}: out since {Clock(b.LeftUtc)}" + (b.DueUtc is { } due ? $", {(b.IsLunch ? "due" : "expected")} {Clock(due)}" : string.Empty) + reason;

        var late = b.MinutesLate > 0 ? $": {b.MinutesLate} min late" : b.IsLunch ? ": on time" : string.Empty;
        return $"{name} {Clock(b.LeftUtc)}–{Clock(back)}{late}{reason}";
    }

    private List<LogRow> BuildDayRows(DateTime day)
    {
        if (_store is null) return new List<LogRow>();

        var isToday = day.Date == DateTime.Today;
        var byUser = BranchLog(day.Date, day.Date.AddDays(1)).GroupBy(l => l.TerminalUserId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.TimestampUtc).ToList());
        var directory = _store.GetAllEmployees().ToDictionary(d => d.TerminalUserId);
        var dayLog = _store.GetLogForDay(day);

        var rows = new List<LogRow>();
        foreach (var id in directory.Keys.Union(byUser.Keys))
        {
            directory.TryGetValue(id, out var emp);
            byUser.TryGetValue(id, out var ev);
            ev ??= new List<AttendanceLogEntry>();

            // Someone who has since moved away still shows by name on the days they worked here.
            var name = emp?.FullName ?? _branchAttendanceNames.GetValueOrDefault(id) ?? ShellWindow.FallbackName(id);
            var number = emp?.EmployeeNumber ?? string.Empty;
            var sub = string.Join(" • ", new[] { emp?.Position, number }.Where(s => !string.IsNullOrWhiteSpace(s)));

            string key, status; Brush sBg, sFg;
            if (ev.Count > 0)
            {
                if (ev[^1].EventType == "CLOCK_IN")
                {
                    if (isToday) { key = "IN"; status = "IN"; sBg = InBg; sFg = InFg; }
                    else { key = "NOOUT"; status = "NO CLOCK-OUT"; sBg = WarnBg; sFg = WarnFg; }
                }
                else { key = "OUT"; status = "OUT"; sBg = OutBg; sFg = OutFg; }
            }
            else if (isToday)
            {
                if (_adapter?.IsEnrolled(id) ?? false) { key = "ABSENT"; status = "NOT IN YET"; sBg = AbsBg; sFg = AbsFg; }
                else { key = "NOENROLL"; status = "NOT ENROLLED"; sBg = WarnBg; sFg = WarnFg; }
            }
            else { key = "ABSENT"; status = "ABSENT"; sBg = RedBg; sFg = RedFg; }

            var breaks = BreaksFor(id, dayLog, day);
            var openBreak = breaks.LastOrDefault(b => b.IsOut);
            var onBreak = isToday && key == "IN" && openBreak is not null;
            var minutesLate = breaks.Sum(b => b.MinutesLate);
            if (onBreak)
            {
                status = openBreak!.IsLunch ? "AT LUNCH" : "OUT TEMPORARILY";
                sBg = WarnBg;
                sFg = WarnFg;
            }

            var firstIn = ev.FirstOrDefault(x => x.EventType == "CLOCK_IN");
            var clockIn = firstIn is null ? "—" : Clock(firstIn.TimestampUtc);
            var clockOut = ev.Count > 0 && ev[^1].EventType == "CLOCK_OUT" ? Clock(ev[^1].TimestampUtc) : "—";

            var punches = ev.Count == 0
                ? "No scans recorded"
                : string.Join("\n", ev.Select(x => $"{(x.EventType == "CLOCK_IN" ? "In " : "Out")}  {Clock(x.TimestampUtc)}")
                    .Concat(breaks.Select(DescribeBreak)));
            var breaksText = breaks.Count == 0 ? "—" : string.Join("\n", breaks.Select(DescribeBreak));
            var breaksFg = minutesLate > 0 ? RedFg : onBreak ? WarnFg : breaks.Count > 0 ? InkSoft : AbsFg;

            var (bg, fg) = AvatarColors(id);
            rows.Add(new LogRow(0, id, name, number, sub, Initials(name), bg, fg, clockIn, clockOut,
                ev.Count == 0 ? "—" : FormatHours(Worked(ev, isToday)), status, sBg, sFg, key, punches,
                ev.Count > 0 ? ev[0].TimestampUtc : null, ev.Count > 0 ? ev[^1].TimestampUtc : null,
                breaksText, breaksFg, onBreak, minutesLate));
        }

        // Reported first (by first clock-in), then everyone else A-Z.
        return rows
            .OrderBy(r => r.FirstKey is null ? 1 : 0)
            .ThenBy(r => r.FirstKey ?? DateTime.MaxValue)
            .ThenBy(r => r.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<LogRow> ApplyFilter(IEnumerable<LogRow> rows, string filter, string search)
    {
        var q = rows;

        if (filter != "All")
            q = q.Where(r => filter switch
            {
                "In" => r.StatusKey == "IN" && !r.OnBreak,
                "Break" => r.OnBreak,
                "Late" => r.MinutesLate > 0,
                "Out" => r.StatusKey is "OUT" or "NOOUT",
                "Absent" => r.StatusKey is "ABSENT" or "NOENROLL",
                _ => true
            });

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(r => r.EmployeeName.Contains(search, StringComparison.OrdinalIgnoreCase)
                             || r.EmployeeNumber.Contains(search, StringComparison.OrdinalIgnoreCase));

        return q.Select((r, i) => r with { Number = i + 1 }).ToList();
    }

    // ===================================================================
    //  Dashboard refresh
    // ===================================================================

    private void RefreshAttendance()
    {
        if (!_initialized || _store is null || _adapter is null) return;

        _dashRows = BuildDayRows(DateTime.Today);

        var inCount = _dashRows.Count(r => r.StatusKey == "IN");
        var outCount = _dashRows.Count(r => r.StatusKey == "OUT");
        var absent = _dashRows.Count(r => r.StatusKey == "ABSENT");
        var notEnrolled = _dashRows.Count(r => r.StatusKey == "NOENROLL");
        var reported = inCount + outCount;
        var expected = _dashRows.Count;
        var notIn = absent + notEnrolled;
        var rate = expected > 0 ? Math.Min(100, reported * 100 / expected) : 0;
        var directoryCount = _store.GetAllEmployees().Count;
        var coverage = directoryCount > 0 ? Math.Min(100, _adapter.EnrolledCount * 100 / directoryCount) : 0;
        var outShare = reported > 0 ? outCount * 100 / reported : 0;

        // Hero
        HeroPresentText.Text = inCount.ToString();
        HeroNotInRun.Text = notIn.ToString();
        HeroOutRun.Text = outCount.ToString();
        HeroRateRun.Text = $"{rate}%";
        var lateFromLunch = _dashRows.Count(r => r.MinutesLate > 0);
        var onBreak = _dashRows.Count(r => r.OnBreak);
        HeroLateRun.Text = string.Join(" ", new[]
        {
            onBreak > 0 ? $"{onBreak} on a break now." : null,
            lateFromLunch > 0 ? $"{lateFromLunch} back late from lunch." : null,
        }.Where(s => s is not null));
        BuildHeroAvatars();

        // Pie + legend
        DrawPie(inCount, outCount, notIn);
        PieSubText.Text = expected > 0 ? $"{reported} of {expected} reported" : "No employees loaded";
        LegendInText.Text = $"{inCount} employee(s)";
        LegendOutText.Text = $"{outCount} employee(s)";
        LegendAbsentText.Text = $"{notIn} employee(s)";

        // Rings
        SetRing(RingA, RingAText, rate);
        SetRing(RingB, RingBText, coverage);
        SetRing(RingC, RingCText, outShare);

        RefreshLiveCards();
        RebuildChart();
        RefreshTables();
        UpdateBellBadge();
    }

    private void RefreshTables()
    {
        if (!_initialized || _store is null) return;

        var filtered = ApplyFilter(_dashRows, "All", _search);
        DashboardLogList.ItemsSource = filtered.Take(8).ToList();
        DashboardEmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DashboardFooterText.Text = filtered.Count == 0 ? string.Empty
            : $"Showing {Math.Min(8, filtered.Count)} of {filtered.Count} employee(s)";

        if (AttendancePage.Visibility == Visibility.Visible) RefreshPageLog();
    }

    private void BuildHeroAvatars()
    {
        HeroAvatars.Children.Clear();

        var inNow = _dashRows.Where(r => r.StatusKey == "IN").OrderByDescending(r => r.LastKey).ToList();
        if (inNow.Count == 0)
            HeroAvatars.Children.Add(new TextBlock
            {
                Text = "Nobody has clocked in yet.",
                FontSize = 12,
                Foreground = Br("#717782"),
                VerticalAlignment = VerticalAlignment.Center,
            });

        for (var i = 0; i < Math.Min(3, inNow.Count); i++)
            HeroAvatars.Children.Add(AvatarBubble(inNow[i].Initials, inNow[i].AvatarBg, inNow[i].AvatarFg, i, inNow[i].EmployeeName));

        if (inNow.Count > 3)
            HeroAvatars.Children.Add(AvatarBubble($"+{inNow.Count - 3}", Br("#D2E4FF"), Br("#0B2A45"), 3, $"{inNow.Count - 3} more"));
    }

    private static Border AvatarBubble(string text, Brush bg, Brush fg, int index, string tip) => new()
    {
        Width = 36,
        Height = 36,
        CornerRadius = new CornerRadius(18),
        Background = bg,
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(2),
        Margin = new Thickness(index == 0 ? 0 : -10, 0, 0, 0),
        ToolTip = tip,
        Child = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    // ----- Latest activity -----

    private void RefreshLiveCards()
    {
        var cards = _dashRows.Where(r => r.LastKey is not null && r.StatusKey is "IN" or "OUT")
            .OrderByDescending(r => r.LastKey).Take(5)
            .Select(r =>
            {
                var isIn = r.StatusKey == "IN";
                return new LiveCard(r.EmployeeName, r.Subtitle, r.Initials, r.AvatarBg, r.AvatarFg,
                    r.OnBreak ? "ON BREAK" : isIn ? "AT WORK" : "LEFT",
                    r.OnBreak ? WarnBg : isIn ? InBg : OutBg,
                    r.OnBreak ? WarnFg : isIn ? InFg : OutFg,
                    $"In {r.ClockIn}" + (r.ClockOut != "—" ? $"  •  Out {r.ClockOut}" : string.Empty));
            })
            .ToList();

        LiveList.ItemsSource = cards;
        LiveEmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Pie & rings -----

    private void DrawPie(int a, int b, int c)
    {
        PieCanvas.Children.Clear();
        const double cx = 66, cy = 66, r = 62;
        var total = a + b + c;

        if (total == 0)
        {
            PieCanvas.Children.Add(new Ellipse { Width = r * 2, Height = r * 2, Fill = Br("#E4EDF4") });
            Canvas.SetLeft(PieCanvas.Children[0], cx - r); Canvas.SetTop(PieCanvas.Children[0], cy - r);
            var none = MakeText("No data yet", 11, Br("#717782"), FontWeights.SemiBold);
            none.Width = 132; none.TextAlignment = TextAlignment.Center;
            Canvas.SetTop(none, cy - 8);
            PieCanvas.Children.Add(none);
            return;
        }

        var slices = new[] { (a, DarkTeal, Brushes.White), (b, MidTeal, Brushes.White), (c, Gold, (Brush)Br("#171C1F")) };
        double start = 0;

        foreach (var (value, fill, labelBrush) in slices)
        {
            if (value <= 0) continue;
            var sweep = 360.0 * value / total;

            PieCanvas.Children.Add(new Path { Data = Slice(cx, cy, r, start, sweep), Fill = fill, Stroke = Brushes.White, StrokeThickness = 2 });

            var pct = (int)Math.Round(100.0 * value / total);
            if (pct >= 8)
            {
                var mid = start + sweep / 2;
                var p = PointAt(cx, cy, r * 0.62, mid);
                var label = MakeText($"{pct}%", 11, labelBrush, FontWeights.Bold);
                label.Width = 34; label.TextAlignment = TextAlignment.Center;
                Canvas.SetLeft(label, p.X - 17); Canvas.SetTop(label, p.Y - 8);
                PieCanvas.Children.Add(label);
            }
            start += sweep;
        }
    }

    private static Point PointAt(double cx, double cy, double r, double degFromTop)
    {
        var rad = degFromTop * Math.PI / 180.0;
        return new Point(cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
    }

    private static Geometry Slice(double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 359.99) return new EllipseGeometry(new Point(cx, cy), r, r);

        var figure = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
        figure.Segments.Add(new LineSegment(PointAt(cx, cy, r, startDeg), true));
        figure.Segments.Add(new ArcSegment(PointAt(cx, cy, r, startDeg + sweepDeg), new Size(r, r), 0,
            sweepDeg > 180, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { figure });
    }

    private static void SetRing(Path ring, TextBlock label, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        label.Text = $"{percent}%";

        if (percent == 0) { ring.Data = Geometry.Empty; return; }

        const double cx = 42, cy = 42, r = 38;
        var sweep = Math.Min(359.9, 360.0 * percent / 100.0);
        var figure = new PathFigure { StartPoint = PointAt(cx, cy, r, 0), IsClosed = false };
        figure.Segments.Add(new ArcSegment(PointAt(cx, cy, r, sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        ring.Data = new PathGeometry(new[] { figure });
    }

    // ===================================================================
    //  Analytics chart (stacked bars drawn on a canvas, redrawn on resize)
    // ===================================================================

    private void ChartRange_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string mode }) return;
        _chartMode = mode;
        RebuildChart();
    }

    private void RebuildChart()
    {
        if (!_initialized || _store is null) return;

        var bars = new List<ChartBar>();

        if (_chartMode == "Today")
        {
            var log = BranchLog(DateTime.Today, DateTime.Today.AddDays(1));
            int minH = 6, maxH = 19;
            if (log.Count > 0)
            {
                minH = Math.Min(minH, log.Min(l => l.TimestampUtc.ToLocalTime().Hour));
                maxH = Math.Max(maxH, log.Max(l => l.TimestampUtc.ToLocalTime().Hour));
            }

            for (var h = minH; h <= maxH; h++)
            {
                var ins = log.Count(l => l.EventType == "CLOCK_IN" && l.TimestampUtc.ToLocalTime().Hour == h);
                var outs = log.Count(l => l.EventType == "CLOCK_OUT" && l.TimestampUtc.ToLocalTime().Hour == h);
                bars.Add(new ChartBar(h.ToString("00"), $"{h:00}:00 — {ins} clock-in(s), {outs} clock-out(s)", ins, outs, 0));
            }

            ChartSubText.Text = "Scans per hour, today";
            ChartLegendAText.Text = "Clock-ins";
            ChartLegendBText.Text = "Clock-outs";
            ChartLegendC.Visibility = Visibility.Collapsed;
        }
        else
        {
            var days = _chartMode == "7" ? 7 : 30;
            var start = DateTime.Today.AddDays(-(days - 1));
            var expected = _store.GetAllEmployees().Count;
            var byDay = BranchLog(start, DateTime.Today.AddDays(1)).GroupBy(l => l.TimestampUtc.ToLocalTime().Date)
                .ToDictionary(g => g.Key, g => g.ToList());

            for (var i = 0; i < days; i++)
            {
                var day = start.AddDays(i);
                var stillIn = 0; var finished = 0;
                if (byDay.TryGetValue(day, out var ev))
                {
                    foreach (var g in ev.GroupBy(x => x.TerminalUserId))
                    {
                        var last = g.OrderBy(x => x.TimestampUtc).Last();
                        if (last.EventType == "CLOCK_IN") stillIn++; else finished++;
                    }
                }

                var notReported = Math.Max(0, expected - stillIn - finished);
                bars.Add(new ChartBar(days == 7 ? day.ToString("ddd") : day.Day.ToString(),
                    $"{day:ddd d MMM} — {stillIn + finished} reported, {finished} clocked out, {notReported} not reported",
                    stillIn, finished, notReported));
            }

            ChartSubText.Text = days == 7 ? "Employees per day, last 7 days" : "Employees per day, last 30 days";
            ChartLegendAText.Text = "Still in / no clock-out";
            ChartLegendBText.Text = "Clocked out";
            ChartLegendCText.Text = "Not reported";
            ChartLegendC.Visibility = Visibility.Visible;
        }

        _chartBars = bars;
        RedrawChart();
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawChart();

    private static (double Max, double Step) NiceScale(double rawMax)
    {
        if (rawMax <= 4) return (4, 1);

        var step = Math.Pow(10, Math.Floor(Math.Log10(rawMax / 4)));
        foreach (var m in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            if (rawMax / (step * m) <= 4) { step *= m; break; }
        }
        return (Math.Ceiling(rawMax / step) * step, step);
    }

    private void RedrawChart()
    {
        if (!_initialized || _chartBars is null) return;

        var w = ChartCanvas.ActualWidth;
        var h = ChartCanvas.ActualHeight;
        if (w < 120 || h < 100) return;

        ChartCanvas.Children.Clear();

        const double left = 38, right = 6, top = 8, bottom = 26;
        var plotW = w - left - right;
        var plotH = h - top - bottom;
        var n = _chartBars.Count;
        if (n == 0) return;

        var rawMax = _chartBars.Max(b => b.Dark + b.Mid + b.Light);
        var (max, step) = NiceScale(rawMax);

        // Grid lines + y labels
        for (double v = 0; v <= max + 0.001; v += step)
        {
            var y = top + plotH - (v / max) * plotH;
            ChartCanvas.Children.Add(new Line
            {
                X1 = left,
                X2 = w - right,
                Y1 = y,
                Y2 = y,
                Stroke = Br("#E9EFF5"),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });

            var label = MakeText(((int)Math.Round(v)).ToString(), 10, Br("#717782"), FontWeights.Normal);
            label.Width = left - 8; label.TextAlignment = TextAlignment.Right;
            Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 7);
            ChartCanvas.Children.Add(label);
        }

        var slot = plotW / n;
        var barW = Math.Min(30, slot * 0.56);
        var stride = slot < 20 ? 5 : slot < 34 ? 2 : 1;

        for (var i = 0; i < n; i++)
        {
            var bar = _chartBars[i];
            var x = left + slot * i + (slot - barW) / 2;
            var baseY = top + plotH;

            void Layer(int value, Brush fill)
            {
                if (value <= 0) return;
                var height = value / max * plotH;
                baseY -= height;
                var rect = new Rectangle { Width = barW, Height = Math.Max(height, 1), Fill = fill, RadiusX = 3, RadiusY = 3 };
                Canvas.SetLeft(rect, x); Canvas.SetTop(rect, baseY);
                ChartCanvas.Children.Add(rect);
            }

            Layer(bar.Dark, DarkTeal);
            Layer(bar.Mid, MidTeal);
            Layer(bar.Light, TrackGray);

            if (i % stride == 0)
            {
                var xl = MakeText(bar.Label, 10, Br("#717782"), FontWeights.Normal);
                xl.Width = slot * stride; xl.TextAlignment = TextAlignment.Center;
                Canvas.SetLeft(xl, left + slot * i - (slot * (stride - 1)) / 2); Canvas.SetTop(xl, top + plotH + 6);
                ChartCanvas.Children.Add(xl);
            }
        }

        if (rawMax == 0)
        {
            var empty = MakeText("No activity yet", 12, Br("#717782"), FontWeights.SemiBold);
            empty.Width = plotW; empty.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(empty, left); Canvas.SetTop(empty, top + plotH / 2 - 8);
            ChartCanvas.Children.Add(empty);
        }

        // Transparent hover targets so tooltips are easy to trigger.
        for (var i = 0; i < n; i++)
        {
            var hit = new Rectangle { Width = slot, Height = plotH, Fill = Brushes.Transparent, ToolTip = _chartBars[i].Tip };
            Canvas.SetLeft(hit, left + slot * i); Canvas.SetTop(hit, top);
            ChartCanvas.Children.Add(hit);
        }
    }

    // ===================================================================
    //  Full time-log page (date navigation + filter + export)
    // ===================================================================

    private void PageFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton { Tag: string filter }) return;
        _pageFilter = filter;
        RefreshPageLog();
    }

    private void PrevDay_Click(object sender, RoutedEventArgs e) { _pageDate = _pageDate.AddDays(-1); RefreshPageLog(); }

    private void NextDay_Click(object sender, RoutedEventArgs e)
    {
        if (_pageDate < DateTime.Today) _pageDate = _pageDate.AddDays(1);
        RefreshPageLog();
    }

    private void TodayDay_Click(object sender, RoutedEventArgs e) { _pageDate = DateTime.Today; RefreshPageLog(); }

    private void ViewAll_Click(object sender, RoutedEventArgs e)
    {
        _pageDate = DateTime.Today;
        SelectPage("Attendance");
    }

    private void RefreshPageLog()
    {
        if (!_initialized || _store is null) return;

        if (_session is not null && _pageDate < _activityFrom) _ = LoadBranchActivityAsync();
        if (_session is { BranchId: long branchId } session && !_serverBreaks.ContainsKey(_pageDate.Date))
            _ = LoadPageBreaksAsync(_pageDate.Date, session, branchId);

        _pageRows = BuildDayRows(_pageDate);
        var shown = ApplyFilter(_pageRows, _pageFilter, _search);

        PageLogList.ItemsSource = shown;
        PageEmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var isToday = _pageDate == DateTime.Today;
        PageDateText.Text = isToday ? "Today" : _pageDate.ToString("ddd, d MMM yyyy");
        PageNextButton.IsEnabled = !isToday;
        PageTodayButton.IsEnabled = !isToday;

        var reported = _pageRows.Count(r => r.StatusKey is "IN" or "OUT" or "NOOUT");
        var inNow = _pageRows.Count(r => r.StatusKey == "IN");
        PageSummaryText.Text = $"{_pageDate:dddd, d MMMM yyyy}  •  {reported} reported" +
                               (isToday ? $"  •  {inNow} currently in" : string.Empty) +
                               $"  •  showing {shown.Count} of {_pageRows.Count}";
    }

    private async Task LoadPageBreaksAsync(DateTime day, AuthResponse session, long branchId)
    {
        try
        {
            await LoadBreaksAsync(day, session, branchId);
            if (_serverBreaks.ContainsKey(day) && ReferenceEquals(session, _session)) RefreshPageLog();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline: the page keeps showing this PC's own scans.
        }
    }

    private void ExportToday_Click(object sender, RoutedEventArgs e) =>
        ExportCsv(ApplyFilter(_dashRows, "All", _search), DateTime.Today);

    private void ExportPage_Click(object sender, RoutedEventArgs e) =>
        ExportCsv(ApplyFilter(_pageRows, _pageFilter, _search), _pageDate);

    private void ExportCsv(IReadOnlyList<LogRow> rows, DateTime day)
    {
        if (rows.Count == 0)
        {
            ShowToast("There is nothing to export.", "warning");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export time log",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"attendance-{day:yyyy-MM-dd}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

        var sb = new StringBuilder();
        sb.AppendLine("Employee,Employee No,Date,Clock in,Clock out,Hours,Breaks,Status");
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", Q(r.EmployeeName), Q(r.EmployeeNumber), Q(day.ToString("yyyy-MM-dd")),
                Q(r.ClockIn), Q(r.ClockOut), Q(r.Hours), Q(r.Breaks.Replace("\n", "; ")), Q(r.Status)));

        try
        {
            System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            ShowToast($"Exported {rows.Count} row(s).", "success");
        }
        catch (Exception)
        {
            ShowToast("Couldn't save the file. Is it open in another program?", "error");
        }
    }

    // ===================================================================
    //  Device page
    // ===================================================================

    private void RefreshDevice_Click(object sender, RoutedEventArgs e) => RefreshDeviceInfo();

    private void RefreshDeviceInfo()
    {
        if (_adapter is null) return;

        DevReaderText.Text = _deviceConnected ? "Connected" : (string.IsNullOrWhiteSpace(_deviceMessage) ? "Connecting…" : "Not detected");
        DevSerialText.Text = _adapter.DeviceSerial ?? "—";
        DevEnrolledText.Text = _adapter.EnrolledCount.ToString();
        DevEmployeesText.Text = (_store?.GetAllEmployees().Count ?? 0).ToString();
        DevPendingText.Text = (_engine?.PendingSyncCount ?? 0).ToString();
        UpdateBellBadge();
    }

    // ===================================================================
    //  Enrollment
    // ===================================================================

    private enum EnrollUiState { Ready, Capturing, Registering, Success, Failed }

    private async Task LoadEmployeesAsync()
    {
        var session = _session;
        if (session is null || _apiClient is null || _store is null) return;
        if (_loadingEmployees) return;

        // The sign-in refuses an account with no branch, so this is only a safety net.
        if (session.BranchId is not long branchId) return;

        _loadingEmployees = true;
        EmployeeCountText.Text = "Loading employees…";
        EmployeeListBox.ItemsSource = null;

        try
        {
            var loaded = await _apiClient.GetEmployeesByBranchAsync(branchId, session.AccessToken, CancellationToken.None);

            if (!ReferenceEquals(session, _session)) return;   // signed out while loading

            if (loaded is null)
            {
                EmployeeCountText.Text = "Failed to load employees — check connection";
                ShowToast("Couldn't load this branch's employees. Check the internet connection.", "error");
                return;
            }

            var employees = loaded.Where(e => string.Equals(e.EmploymentStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase)).ToList();

            // This branch's people and no one else. Also lets the kiosk greet them without any login.
            _store.ReplaceEmployees(employees.Select(e => new EmployeeDirectoryEntry(
                $"emp-{e.Id}", e.Id, e.FullName, e.EmployeeNumber, e.Position, e.BranchName)).ToList());

            _employeeSummaries = employees;
            DamagesPage.SetEmployees(employees);

            _branchName = employees.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.BranchName))?.BranchName ?? _branchName;
            ApplySessionToUi();
            ApplyPickerFilter();

            if (employees.Count == 0)
                ShowToast("No employees found for this branch.", "warning");

            RefreshAttendance();
            RefreshDeviceInfo();
            _ = LoadBranchActivityAsync();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(session, _session))
            {
                EmployeeCountText.Text = "Failed to load employees — check connection";
                ShowToast("Couldn't load employees. Check the internet connection.", "error");
            }
        }
        finally
        {
            _loadingEmployees = false;
        }
    }

    // Fingerprints enrolled here before they were stored centrally only exist on this PC. Uploading takes a
    // signed-in officer, so it happens at sign-in. Anything that fails stays on this PC and is tried next time.
    private async Task BackUpLocalFingerprintsAsync(AuthResponse session)
    {
        if (_adapter is null || _apiClient is null || _apiClient.Identity is null) return;

        try
        {
            // Pull first: whatever the server already holds must not be overwritten by an older local copy.
            if (_engine is null || !await _engine.SyncFingerprintsNowAsync(CancellationToken.None)) return;

            int saved = 0, failed = 0;
            foreach (var (terminalUserId, template) in _adapter.GetTemplatesNotBackedUp())
            {
                if (!ReferenceEquals(session, _session)) return;   // signed out meanwhile

                if (!terminalUserId.StartsWith("emp-", StringComparison.Ordinal)
                    || !long.TryParse(terminalUserId.AsSpan(4), out var employeeId))
                {
                    continue;
                }

                var result = await _apiClient.SaveFingerprintAsync(employeeId, terminalUserId, template, session.AccessToken, CancellationToken.None);
                if (result.Succeeded)
                {
                    await _adapter.MarkBackedUpAsync(terminalUserId);
                    saved++;
                }
                else
                {
                    failed++;
                }
            }

            if (!ReferenceEquals(session, _session)) return;
            if (failed > 0)
                ShowToast($"{failed} fingerprint(s) couldn't be saved to the server. They will be tried again at the next sign-in.", "warning");
            else if (saved > 0)
                ShowToast($"{saved} fingerprint(s) saved to the server.", "success");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline: nothing lost, this runs again at the next sign-in.
        }
    }

    private void EmployeeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        EmployeeSearchPlaceholder.Visibility = string.IsNullOrEmpty(EmployeeSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_initialized) ApplyPickerFilter();
    }

    private void ApplyPickerFilter()
    {
        if (!_initialized || _adapter is null) return;

        var term = EmployeeSearchBox.Text.Trim();
        var items = _employeeSummaries
            .Where(emp => term.Length == 0
                          || emp.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || emp.EmployeeNumber.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(emp =>
            {
                var enrolled = _adapter.IsEnrolled($"emp-{emp.Id}");
                var (bg, fg) = AvatarColors($"emp-{emp.Id}");
                return new PickerItem(
                    emp.Id, emp.FullName, Initials(emp.FullName),
                    string.Join(" • ", new[] { emp.Position, emp.EmployeeNumber }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    enrolled ? "Enrolled" : "Pending", enrolled ? InFg : WarnFg, bg, fg, enrolled, emp);
            })
            .ToList();

        EmployeeListBox.ItemsSource = items;
        EmployeeCountText.Text = $"{items.Count} of {_employeeSummaries.Count} employee(s)  •  {_employeeSummaries.Count(emp => _adapter.IsEnrolled($"emp-{emp.Id}"))} enrolled";

        // Keep the current selection if it is still visible.
        if (_selectedEmployeeId is long id)
            EmployeeListBox.SelectedItem = items.FirstOrDefault(i => i.Id == id);
    }

    private void EmployeeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _enrolling) return;

        if (EmployeeListBox.SelectedItem is not PickerItem item)
        {
            if (EmployeeListBox.Items.Count > 0 || string.IsNullOrEmpty(EmployeeSearchBox.Text)) _selectedEmployeeId = null;
            EnrollTargetText.Text = "Select an employee to begin";
            UpdateEnrollAvailability();
            return;
        }

        _selectedEmployeeId = item.Id;
        EnrollTargetText.Text = $"{item.Name}  •  {item.Employee.EmployeeNumber}";

        SetEnrollState(EnrollUiState.Ready,
            item.Enrolled ? "This employee already has a fingerprint on this terminal." : null);
        UpdateEnrollAvailability();
    }

    private void UpdateEnrollAvailability()
    {
        EnrollButton.IsEnabled = !_enrolling && _deviceConnected && _adapter is not null
                                 && EmployeeListBox.SelectedItem is PickerItem item && !item.Enrolled;
    }

    private async void EnrollButton_Click(object sender, RoutedEventArgs e)
    {
        if (_enrolling || _adapter is null || _apiClient is null || _store is null || _session is null) return;

        if (EmployeeListBox.SelectedItem is not PickerItem selected)
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

            var template = _adapter.GetTemplate(terminalUserId)
                           ?? throw new InvalidOperationException("The captured fingerprint is missing.");

            SyncResult result;
            try
            {
                result = await _apiClient.SaveFingerprintAsync(selected.Id, terminalUserId, template, session.AccessToken, CancellationToken.None);
            }
            catch (Exception)
            {
                await _adapter.RemoveEnrollmentAsync(terminalUserId);   // never keep an unmapped fingerprint
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

            await _adapter.MarkBackedUpAsync(terminalUserId);

            _store.UpsertEmployees(new[]
            {
                new EmployeeDirectoryEntry(terminalUserId, selected.Id, selected.Employee.FullName,
                    selected.Employee.EmployeeNumber, selected.Employee.Position, selected.Employee.BranchName)
            });

            SetEnrollState(EnrollUiState.Success, $"{selected.Employee.FullName} can now use the reader.");
            ShowToast($"{selected.Name} enrolled successfully.", "success");

            ApplyPickerFilter();
            RefreshAttendance();
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
                EnrollSubtext.Text = detail ?? "Select an employee, then press Enroll fingerprint.";
                SetStagePill("Ready", "#D2E4FF", "#1C6EB4");
                UpdateSteps(1);
                StopEnrollAnimations();
                break;

            case EnrollUiState.Capturing:
                EnrollHeadlineText.Text = "Place finger firmly on the reader…";
                EnrollSubtext.Text = "Hold still until the capture finishes (up to 5 seconds).";
                SetStagePill("Capturing", "#FFF4D6", "#BA1A1A");
                UpdateSteps(2);
                StartEnrollAnimations();
                break;

            case EnrollUiState.Registering:
                EnrollHeadlineText.Text = "Fingerprint captured";
                EnrollSubtext.Text = "Saving to the server…";
                SetStagePill("Saving", "#D2E4FF", "#1C6EB4");
                UpdateSteps(3);
                StopEnrollAnimations();
                break;

            case EnrollUiState.Success:
                EnrollHeadlineText.Text = "Fingerprint enrolled";
                EnrollSubtext.Text = detail ?? string.Empty;
                SetStagePill("Enrolled", "#E5F4E2", "#2E7D24");
                UpdateSteps(4);
                StopEnrollAnimations();
                break;

            case EnrollUiState.Failed:
                EnrollHeadlineText.Text = "Enrollment didn't complete";
                EnrollSubtext.Text = detail ?? "Please try again.";
                SetStagePill("Failed", "#FFDAD6", "#BA1A1A");
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
            circle.Background = Br("#E5F4E2"); number.Text = "\uE5CA"; number.FontFamily = (FontFamily)circle.FindResource("IconFont"); number.Foreground = Br("#3B9A2F"); label.Foreground = Br("#3B9A2F");
        }
        else if (step == current)
        {
            circle.Background = Br("#D2E4FF"); number.Text = n; number.ClearValue(TextBlock.FontFamilyProperty); number.Foreground = Br("#1C6EB4"); label.Foreground = Br("#1C6EB4");
        }
        else
        {
            circle.Background = Br("#F0F4F8"); number.Text = n; number.ClearValue(TextBlock.FontFamilyProperty); number.Foreground = Br("#717782"); label.Foreground = Br("#717782");
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

    // ===================================================================
    //  Helpers
    // ===================================================================

    private static string Initials(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][0].ToString().ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    // Stable per-person avatar colour (string.GetHashCode changes between runs, so sum the characters).
    private static (Brush Bg, Brush Fg) AvatarColors(string key) =>
        AvatarPalette[key.Sum(c => (int)c) % AvatarPalette.Length];

    private static TextBlock MakeText(string text, double size, Brush foreground, FontWeight weight) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground,
        FontWeight = weight
    };

    private static SolidColorBrush Br(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}