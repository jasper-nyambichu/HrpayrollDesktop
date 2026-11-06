using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Local;
using HrpayrollDesktop.Sync;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HrpayrollDesktop;

/// <summary>One employee on the manual check-in list.</summary>
public sealed class ManualCheckInRow : INotifyPropertyChanged
{
    private bool _isChecked;

    public required long EmployeeId { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required string Status { get; init; }
    public required Brush StatusBg { get; init; }
    public required Brush StatusFg { get; init; }
    public required Brush AvatarBg { get; init; }
    public required Brush AvatarFg { get; init; }
    public required bool CanCheckIn { get; init; }
    public required string SearchText { get; init; }
    public string Initials => PopupHost.Initials(Name);

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The Branch Manager's fallback for a reader that isn't working: every active employee of the branch, ticked and
/// checked in together. The server records the time and the manager, so this works only online. Each person checked
/// in is also written to this PC's log, so their next fingerprint scan offers lunch or check-out, not a second check-in.
/// </summary>
public partial class ManualCheckInView : UserControl
{
    private static readonly (string Bg, string Fg)[] AvatarPalette =
    {
        ("#D2E4FF", "#005592"), ("#E5F4E2", "#2E7D24"), ("#FFF4D6", "#8A5F00"), ("#FFE3D1", "#B4530F"), ("#E6F0F8", "#0B2A45"),
    };

    private BackendApiClient? _apiClient;
    private LocalAttendanceStore? _store;
    private AuthResponse? _session;
    private List<ManualCheckInRow> _rows = new();
    private bool _busy;

    /// <summary>The manager pressed Done.</summary>
    public event Action? DoneRequested;

    /// <summary>Someone was checked in, so the home screen's figures should refresh.</summary>
    public event Action? CheckedIn;

    public ManualCheckInView()
    {
        InitializeComponent();
    }

    /// <summary>True while loading or saving, so the idle lock waits.</summary>
    public bool IsBusy => _busy;

    public void Initialize(BackendApiClient apiClient, LocalAttendanceStore store)
    {
        _apiClient = apiClient;
        _store = store;
    }

    public void StartSession(AuthResponse session)
    {
        _session = session;
        _apiClient?.BeginStaffSession(session);
        ManagerNameText.Text = string.IsNullOrWhiteSpace(session.DisplayName) ? session.Username : session.DisplayName;
        SearchBox.Text = string.Empty;
        _rows = new List<ManualCheckInRow>();
        ApplyFilter();
        _ = LoadAsync();
    }

    public void EndSession()
    {
        _apiClient?.EndStaffSession();
        _session = null;
        _rows = new List<ManualCheckInRow>();
        EmployeeList.ItemsSource = null;
    }

    // ----- Loading -----

    private async Task LoadAsync()
    {
        if (_apiClient is null || _store is null || _session is not { BranchId: long branchId } session) return;

        _busy = true;
        SetStatus("Loading your branch's employees…", false);
        EmptyText.Visibility = Visibility.Collapsed;
        try
        {
            var employeesTask = _apiClient.GetEmployeesByBranchAsync(branchId, session.AccessToken, CancellationToken.None);
            var attendanceTask = _apiClient.GetBranchAttendanceAsync(branchId, DateTime.Today, DateTime.Today, session.AccessToken, CancellationToken.None);
            await Task.WhenAll(employeesTask, attendanceTask);
            if (!ReferenceEquals(session, _session)) return;   // signed out while loading

            var employees = employeesTask.Result;
            if (employees is null)
            {
                _rows = new List<ManualCheckInRow>();
                ApplyFilter("Couldn't load the employees. Check the internet connection, then press Refresh.");
                SetStatus("Couldn't load the employee list.", true);
                return;
            }

            var attendance = attendanceTask.Result ?? new List<AttendanceRecord>();
            var todayLog = _store.GetTodayLog();
            _rows = employees
                .Where(e => string.Equals(e.EmploymentStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => BuildRow(e, attendance, todayLog))
                .ToList();

            ApplyFilter("No active employees at this branch.");
            SetStatus(attendanceTask.Result is null
                ? "Couldn't load today's attendance from the server, so who is already in may not show. The server still refuses anyone already in."
                : "Each check-in is recorded at the server's time and kept in the audit trail under your name.", attendanceTask.Result is null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ApplyFilter("Can't reach the server. Manual check-in needs the internet, because the server records it.");
            SetStatus("Can't reach the server.", true);
        }
        finally
        {
            _busy = false;
        }
    }

    private ManualCheckInRow BuildRow(EmployeeSummary e, List<AttendanceRecord> attendance, List<AttendanceLogEntry> todayLog)
    {
        // Where they are now: the later of the server's record for today and this PC's own scans.
        var server = attendance
            .Where(a => a.EmployeeId == e.Id && a.ClockIn is not null && a.AttendanceDate.Date == DateTime.Today)
            .OrderBy(a => a.ClockIn)
            .LastOrDefault();
        var local = todayLog.Where(l => l.TerminalUserId == $"emp-{e.Id}").OrderBy(l => l.TimestampUtc).LastOrDefault();

        string kind = "NONE";
        DateTime at = default;
        if (server is not null)
        {
            // Server times are the company's wall clock, which is this PC's local time.
            (kind, at) = server.ClockOut is { } outAt ? ("CLOCK_OUT", outAt) : ("CLOCK_IN", server.ClockIn!.Value);
        }
        if (local is not null)
        {
            var localAt = local.TimestampUtc.ToLocalTime();
            if (kind == "NONE" || localAt >= at) (kind, at) = (local.EventType, localAt);
        }

        var time = at.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var (status, tone, canCheckIn) = kind switch
        {
            "CLOCK_IN" or "BREAK_RETURN" => ($"In since {time}", "Green", false),
            "LUNCH_OUT" => ($"At lunch since {time}", "Amber", false),
            "EMERGENCY_OUT" => ($"Out temporarily since {time}", "Amber", false),
            "CLOCK_OUT" => ($"Left at {time}", "Muted", true),
            _ => ("Not in yet", "Blue", true),
        };

        var (statusBg, statusFg) = tone switch
        {
            "Green" => ("GreenSoftBrush", "GreenBrush"),
            "Amber" => ("AmberSoftBrush", "AmberInkBrush"),
            "Muted" => ("SurfaceLowBrush", "InkSoftBrush"),
            _ => ("BlueSoftBrush", "BlueBrush"),
        };

        var converter = new BrushConverter();
        var (avatarBg, avatarFg) = AvatarPalette[$"emp-{e.Id}".Sum(c => (int)c) % AvatarPalette.Length];
        var detail = string.Join("  •  ", new[] { e.EmployeeNumber, e.Position }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new ManualCheckInRow
        {
            EmployeeId = e.Id,
            Name = e.FullName,
            Detail = detail,
            Status = status,
            StatusBg = (Brush)FindResource(statusBg),
            StatusFg = (Brush)FindResource(statusFg),
            AvatarBg = (Brush)converter.ConvertFromString(avatarBg)!,
            AvatarFg = (Brush)converter.ConvertFromString(avatarFg)!,
            CanCheckIn = canCheckIn,
            SearchText = $"{e.FullName} {e.EmployeeNumber} {e.Position}",
        };
    }

    // ----- Filtering and ticking -----

    private void ApplyFilter(string? emptyWhenNone = null)
    {
        var query = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var shown = query.Length == 0
            ? _rows
            : _rows.Where(r => r.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        EmployeeList.ItemsSource = shown;

        if (shown.Count == 0)
        {
            EmptyText.Text = _rows.Count == 0 ? (emptyWhenNone ?? EmptyText.Text) : $"Nobody matches \"{query}\".";
            EmptyText.Visibility = string.IsNullOrEmpty(EmptyText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        var ticked = _rows.Count(r => r.IsChecked);
        var notIn = _rows.Count(r => r.CanCheckIn);
        CountText.Text = _rows.Count == 0 ? string.Empty
            : $"{ticked} ticked  •  {notIn} of {_rows.Count} not checked in";
        CheckInButton.IsEnabled = ticked > 0 && !_busy;
        CheckInButtonText.Text = ticked == 0 ? "Check in" : ticked == 1 ? "Check in 1 employee" : $"Check in {ticked} employees";
    }

    private void SetStatus(string text, bool problem)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(problem ? "ErrorBrush" : "MutedBrush");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Row_Click(object sender, RoutedEventArgs e) => UpdateCount();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        // Only the people showing, so a search narrows what gets ticked.
        if (EmployeeList.ItemsSource is IEnumerable<ManualCheckInRow> shown)
            foreach (var row in shown.Where(r => r.CanCheckIn)) row.IsChecked = true;
        UpdateCount();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsChecked = false;
        UpdateCount();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) _ = LoadAsync();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) DoneRequested?.Invoke();
    }

    // ----- Checking in -----

    private async void CheckIn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _apiClient is null || _store is null || _session is not { } session) return;

        var ticked = _rows.Where(r => r.IsChecked && r.CanCheckIn).ToList();
        if (ticked.Count == 0) return;

        var who = ticked.Count == 1 ? ticked[0].Name : $"{ticked.Count} employees";
        var yes = PopupHost.Current is { } popups && await popups.ConfirmAsync($"Check in {who}?",
            $"They will be recorded as checked in now, at {DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture)}, by you. " +
            "Use this only when the fingerprint reader can't be used.", "Yes, check in");
        if (!yes || !ReferenceEquals(session, _session)) return;

        _busy = true;
        CheckInButton.IsEnabled = false;
        SetStatus("Checking in…", false);
        try
        {
            var (results, error) = await _apiClient.ManualCheckInAsync(ticked.Select(r => r.EmployeeId).ToList(), session.AccessToken, CancellationToken.None);
            if (results is null)
            {
                SetStatus(error ?? "Manual check-in failed.", true);
                PopupHost.Current?.ShowMessage(PopupKind.Error, "Nobody was checked in", error ?? "Manual check-in failed.", autoCloseSeconds: 0);
                return;
            }

            RecordLocally(results, ticked);
            ShowOutcome(results, ticked);
            if (results.Any(r => r.CheckedIn)) CheckedIn?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SetStatus("Can't reach the server. Nobody was checked in.", true);
            PopupHost.Current?.ShowMessage(PopupKind.Error, "Nobody was checked in",
                "Can't reach the server. Manual check-in needs the internet, because the server records it. Please try again.", autoCloseSeconds: 0);
            return;
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    // This PC's log decides what someone's next scan means, so a manual check-in goes there too. It is not queued
    // for sending: the server already holds it.
    private void RecordLocally(List<ManualCheckInResult> results, List<ManualCheckInRow> ticked)
    {
        var names = ticked.ToDictionary(r => r.EmployeeId);
        var directory = new List<EmployeeDirectoryEntry>();

        foreach (var r in results.Where(r => r.CheckedIn && r.AttendanceId is not null))
        {
            var terminalUserId = $"emp-{r.EmployeeId}";
            var atUtc = (r.ClockInUtc ?? DateTime.UtcNow).ToUniversalTime();
            _store!.AppendLog($"manual-{r.AttendanceId}", terminalUserId, "CLOCK_IN", atUtc);

            if (_store.GetEmployee(terminalUserId) is null && names.TryGetValue(r.EmployeeId, out var row))
            {
                var parts = row.Detail.Split("  •  ");
                directory.Add(new EmployeeDirectoryEntry(terminalUserId, r.EmployeeId, row.Name,
                    parts.ElementAtOrDefault(0) ?? string.Empty, parts.ElementAtOrDefault(1), null));
            }
        }

        if (directory.Count > 0) _store!.UpsertEmployees(directory);
    }

    private static void ShowOutcome(List<ManualCheckInResult> results, List<ManualCheckInRow> ticked)
    {
        var names = ticked.ToDictionary(r => r.EmployeeId, r => r.Name);
        string Name(ManualCheckInResult r) => r.EmployeeName ?? (names.TryGetValue(r.EmployeeId, out var n) ? n : $"Employee #{r.EmployeeId}");

        var done = results.Where(r => r.CheckedIn).ToList();
        var refused = results.Where(r => !r.CheckedIn).ToList();
        var refusedNote = refused.Count == 0 ? null
            : "Not checked in:\n" + string.Join("\n", refused.Select(r => $"• {Name(r)}: {r.Message}"));

        if (done.Count == 0)
        {
            PopupHost.Current?.ShowMessage(PopupKind.Warning, "Nobody was checked in", "The server refused every one.", refusedNote, autoCloseSeconds: 0);
            return;
        }

        var at = done[0].ClockInUtc is { } utc ? PopupHost.Clock(utc) : DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var title = done.Count == 1 ? $"{Name(done[0])} is checked in" : $"{done.Count} employees checked in";
        PopupHost.Current?.ShowMessage(refused.Count == 0 ? PopupKind.Success : PopupKind.Warning, title,
            $"Recorded at {at}. Their next fingerprint scan offers lunch, a temporary check-out or check-out as usual.",
            refusedNote, autoCloseSeconds: refused.Count == 0 ? 6 : 0);
    }
}
