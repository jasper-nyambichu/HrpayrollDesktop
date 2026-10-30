using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Sync;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HrpayrollDesktop;

/// <summary>
/// Back Office days worked: for a branch and pay period the officer reviews how many days each employee
/// reported (counted from attendance), corrects any with a reason, then locks the period so payroll can
/// use the figures. The server does the counting, the checks and the audit trail; this screen only asks.
/// Reopening a locked period is an Administrator's decision, so there is deliberately no unlock here.
/// </summary>
public partial class DaysWorkedView : UserControl
{
    private BackendApiClient? _apiClient;
    private AuthResponse? _session;
    private long? _branchId;

    private List<DaysRowVm> _rows = new();
    private bool _loading;
    private bool _busy;
    private bool _locked;
    private string _loadedStart = string.Empty;
    private string _loadedEnd = string.Empty;

    /// <summary>message, kind ("success" | "warning" | "error") — the console shows it as a toast.</summary>
    public event Action<string, string>? Notify;

    /// <summary>True while a server call is in progress (blocks the idle auto-lock).</summary>
    public bool IsBusy => _busy;

    public DaysWorkedView()
    {
        InitializeComponent();
        ResetPeriod();
        SetEmpty("Choose the pay period and press Show.");
    }

    public void Initialize(BackendApiClient apiClient) => _apiClient = apiClient;

    // ===================================================================
    //  Called by the console
    // ===================================================================

    public void StartSession(AuthResponse session)
    {
        _session = session;
        _rows = new List<DaysRowVm>();
        ResetPeriod();
        ShowPeriodError(null);
        SetEmpty("Choose the pay period and press Show.");
    }

    public void EndSession()
    {
        _session = null;
        _rows = new List<DaysRowVm>();
        RowsControl.ItemsSource = null;
        SetEmpty("Choose the pay period and press Show.");
    }

    public void SetBranch(long? branchId) => _branchId = branchId;

    /// <summary>Reloads the period that is currently chosen (called whenever the page is opened).</summary>
    public Task RefreshAsync() => LoadAsync();

    // ===================================================================
    //  Loading
    // ===================================================================

    private void ResetPeriod()
    {
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        FromPicker.SelectedDate = first;
        ToPicker.SelectedDate = first.AddMonths(1).AddDays(-1);
    }

    private void ShowButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var session = _session;
        if (session is null || _apiClient is null || _loading || _busy) return;

        if (_branchId is not long branchId)
        {
            SetEmpty("Choose a branch first — sign in again and pick the branch this terminal serves.");
            return;
        }

        if (FromPicker.SelectedDate is not DateTime from || ToPicker.SelectedDate is not DateTime to)
        {
            ShowPeriodError("Choose both dates.");
            return;
        }
        if (to < from)
        {
            ShowPeriodError("The last day can't be before the first day.");
            return;
        }
        if ((to - from).TotalDays + 1 > 31)
        {
            ShowPeriodError("A pay period can't be longer than 31 days.");
            return;
        }
        ShowPeriodError(null);

        var start = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        _loading = true;
        ShowButton.IsEnabled = false;
        SetEmpty("Loading…");

        try
        {
            var items = await _apiClient.GetDaysWorkedAsync(branchId, start, end, session.AccessToken, CancellationToken.None);
            if (!ReferenceEquals(session, _session)) return;   // signed out while loading

            if (items is null)
            {
                SetEmpty("Couldn't load the days — check the connection and press Show again.");
                Notify?.Invoke("Couldn't load the days worked. Check the internet connection.", "error");
                return;
            }

            if (items.Count == 0)
            {
                SetEmpty("This branch has no active employees for that period.");
                return;
            }

            _loadedStart = start;
            _loadedEnd = end;

            var maxDays = (int)(to - from).TotalDays + 1;
            _rows = items.Select(item => new DaysRowVm(item, maxDays, UpdateFooter)).ToList();
            _locked = _rows.All(r => r.Locked);

            RowsControl.ItemsSource = _rows;
            TableTitleText.Text = $"{from.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} to {to.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
            TableSubText.Text = $"{_rows.Count} employees";
            LockedPill.Visibility = _locked ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            TablePanel.Visibility = Visibility.Visible;
            LockButton.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;

            UpdateFooter(lockedAt: items.FirstOrDefault(i => i.Locked)?.LockedAt);
        }
        catch (Exception)
        {
            if (ReferenceEquals(session, _session))
            {
                SetEmpty("Couldn't load the days — check the connection and press Show again.");
                Notify?.Invoke("Couldn't load the days worked. Check the internet connection.", "error");
            }
        }
        finally
        {
            _loading = false;
            ShowButton.IsEnabled = true;
        }
    }

    private void SetEmpty(string message)
    {
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
        TablePanel.Visibility = Visibility.Collapsed;
        LockedPill.Visibility = Visibility.Collapsed;
        TableTitleText.Text = "Employees";
        TableSubText.Text = string.Empty;
    }

    private void ShowPeriodError(string? message)
    {
        PeriodErrorText.Text = message ?? string.Empty;
        PeriodErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ===================================================================
    //  Footer + locking
    // ===================================================================

    private void UpdateFooter() => UpdateFooter(null);

    private void UpdateFooter(DateTime? lockedAt)
    {
        if (_rows.Count == 0) return;

        var total = _rows.Where(r => r.IsValid).Sum(r => r.Value);
        var changed = _rows.Count(r => r.IsChanged);
        var invalid = _rows.Any(r => !r.IsValid);
        var missingReason = _rows.Any(r => r.IsChanged && string.IsNullOrWhiteSpace(r.Reason));

        TotalText.Text = changed > 0
            ? $"Total: {total} days · {changed} adjusted"
            : $"Total: {total} days";

        if (_locked)
        {
            var when = lockedAt is DateTime at ? $" on {at.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}" : string.Empty;
            HintText.Text = $"Locked{when}. Payroll can use these figures. Only an Administrator can reopen the period.";
            return;
        }

        HintText.Text = invalid
            ? $"Days must be whole numbers from 0 to {_rows[0].MaxDays}."
            : missingReason
                ? "Give a reason for every row you changed before locking."
                : "Rows you change need a reason. Everyone else is locked as attendance shows.";

        LockButton.IsEnabled = !_busy && !invalid && !missingReason;
    }

    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null || _apiClient is null || _busy || _locked || _rows.Count == 0) return;
        if (_branchId is not long branchId) return;
        if (_rows.Any(r => !r.IsValid) || _rows.Any(r => r.IsChanged && string.IsNullOrWhiteSpace(r.Reason))) return;

        var adjustments = _rows
            .Where(r => r.IsChanged)
            .Select(r => new DaysWorkedAdjustmentBody(r.EmployeeId, r.Value, r.Reason.Trim()))
            .ToList();

        var answer = System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            $"Lock the days for {_rows.Count} employees ({_loadedStart} to {_loadedEnd})?\n\n" +
            "Payroll will use these figures. Only an Administrator can reopen the period afterwards.",
            "Lock days worked", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var done = false;
        _busy = true;
        LockButton.IsEnabled = false;
        ShowButton.IsEnabled = false;

        try
        {
            var result = await _apiClient.LockDaysWorkedAsync(
                new DaysWorkedLockBody(branchId, _loadedStart, _loadedEnd, adjustments), session.AccessToken, CancellationToken.None);
            if (!ReferenceEquals(session, _session)) return;

            if (result.Succeeded)
            {
                done = true;
                Notify?.Invoke("Days worked locked for payroll.", "success");
            }
            else
            {
                var message = FriendlyMessages.Translate(result.Error);
                HintText.Text = message;
                Notify?.Invoke(message, "error");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Notify?.Invoke("Couldn't reach the server. The days were not locked.", "error");
        }
        finally
        {
            _busy = false;
            ShowButton.IsEnabled = true;
            UpdateFooter();
        }

        if (done)
        {
            // Show the locked result for exactly the period that was locked.
            FromPicker.SelectedDate = DateTime.ParseExact(_loadedStart, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            ToPicker.SelectedDate = DateTime.ParseExact(_loadedEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            await LoadAsync();
        }
    }
}

/// <summary>One employee's row: the days figure and reason are editable until the period is locked.</summary>
internal sealed class DaysRowVm : INotifyPropertyChanged
{
    private static readonly Brush NormalBg = Frozen("#F1F5F6");
    private static readonly Brush ErrorBg = Frozen("#FDE8E4");
    private static readonly Brush LockedBg = Frozen("#E7EEEF");

    private readonly DaysWorkedItem _item;
    private readonly Action _onChanged;
    private string _daysText;
    private string _reason = string.Empty;

    public DaysRowVm(DaysWorkedItem item, int maxDays, Action onChanged)
    {
        _item = item;
        MaxDays = maxDays;
        _onChanged = onChanged;
        _daysText = item.DaysWorked.ToString(CultureInfo.InvariantCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long EmployeeId => _item.EmployeeId;
    public string Name => _item.EmployeeName;
    public string Number => _item.EmployeeNumber ?? string.Empty;
    public int SystemDays => _item.SystemDays;
    public bool Locked => _item.Locked;
    public int MaxDays { get; }

    public bool Editable => !Locked;

    public string DaysText
    {
        get => _daysText;
        set
        {
            if (_daysText == value) return;
            _daysText = value;
            Raise(nameof(DaysText));
            Raise(nameof(ReasonVisibility));
            Raise(nameof(DaysBackground));
            Raise(nameof(Note));
            _onChanged();
        }
    }

    public string Reason
    {
        get => _reason;
        set
        {
            if (_reason == value) return;
            _reason = value;
            Raise(nameof(Reason));
            _onChanged();
        }
    }

    public bool IsValid => int.TryParse(_daysText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= MaxDays;

    public int Value => int.Parse(_daysText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);

    // A row only needs a reason when the officer moved it away from what attendance says.
    public bool IsChanged => Editable && IsValid && Value != SystemDays;

    public Visibility ReasonVisibility => IsChanged ? Visibility.Visible : Visibility.Collapsed;

    public Brush DaysBackground => !Editable ? LockedBg : IsValid ? NormalBg : ErrorBg;

    public string Note =>
        Locked ? (_item.Adjusted ? $"Adjusted: {_item.AdjustmentReason}" : string.Empty)
        : !IsValid ? $"Whole number from 0 to {MaxDays}"
        : string.Empty;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
