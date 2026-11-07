using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Sync;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace HrpayrollDesktop;

/// <summary>
/// Back Office days worked: for the branch and a pay period, how many days each employee clocked in,
/// counted by the server from attendance. Nothing is locked any more: the Admin's agent payment
/// calculation reads the same count whenever it is run, so this page only shows it.
/// </summary>
public partial class DaysWorkedView : UserControl
{
    private BackendApiClient? _apiClient;
    private AuthResponse? _session;
    private long? _branchId;
    private bool _loading;

    /// <summary>message, kind ("success" | "warning" | "error") — the console shows it as a toast.</summary>
    public event Action<string, string>? Notify;

    /// <summary>True while a server call is in progress (blocks the idle auto-lock).</summary>
    public bool IsBusy => _loading;

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
        ResetPeriod();
        ShowPeriodError(null);
        SetEmpty("Choose the pay period and press Show.");
    }

    public void EndSession()
    {
        _session = null;
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
        if (session is null || _apiClient is null || _loading) return;

        if (_branchId is not long branchId)
        {
            SetEmpty("This account isn't assigned to a branch yet. Ask your administrator.");
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

            var rows = items.Select(item => new DaysRow(
                item.EmployeeName,
                item.EmployeeNumber ?? string.Empty,
                item.DaysWorked,
                item.Adjusted && !string.IsNullOrWhiteSpace(item.AdjustmentReason) ? $"Adjusted earlier: {item.AdjustmentReason}" : string.Empty))
                .ToList();

            RowsControl.ItemsSource = rows;
            TableTitleText.Text = $"{from.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} to {to.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
            TableSubText.Text = $"{rows.Count} employees";
            TotalText.Text = $"Total: {rows.Sum(r => r.Days)} days";
            EmptyText.Visibility = Visibility.Collapsed;
            TablePanel.Visibility = Visibility.Visible;
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
        TableTitleText.Text = "Employees";
        TableSubText.Text = string.Empty;
    }

    private void ShowPeriodError(string? message)
    {
        PeriodErrorText.Text = message ?? string.Empty;
        PeriodErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>One employee's row: the days counted from attendance.</summary>
internal sealed record DaysRow(string Name, string Number, int Days, string Note);
