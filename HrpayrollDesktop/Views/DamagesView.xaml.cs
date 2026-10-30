using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Local;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HrpayrollDesktop;

/// <summary>
/// Back Office damages: record a damage, then the employee agrees by placing their finger on this
/// reader (the engine hands that scan to us instead of clocking them in), or the officer overrides
/// with a reason. The server does the approving and the audit trail; this screen only asks.
/// </summary>
public partial class DamagesView : UserControl
{
    private sealed record DamageRow(long Id, string Name, string Initials, string Sub, string AmountText,
        Brush AvatarBg, Brush AvatarFg, DamageItem Damage);

    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(60);

    private static readonly (Brush Bg, Brush Fg)[] AvatarPalette =
    {
        (Br("#DDF3EF"), Br("#0E7C66")), (Br("#FDEFD2"), Br("#B7791F")), (Br("#E4ECFB"), Br("#2F5FB3")),
        (Br("#FBE3DD"), Br("#C2512F")), (Br("#EBE4F7"), Br("#6B46C1")), (Br("#E2F0DA"), Br("#3F7D20")),
    };

    private AttendanceEngine? _engine;
    private ITerminalAdapter? _adapter;
    private BackendApiClient? _apiClient;
    private LocalAttendanceStore? _store;
    private AuthResponse? _session;

    private long? _branchId;
    private List<EmployeeSummary> _employees = new();
    private List<DamageItem> _damages = new();
    private DamageItem? _selected;

    private bool _loading;
    private bool _busy;
    private bool _capturing;
    private DispatcherTimer? _captureTimer;

    /// <summary>message, kind ("success" | "warning" | "error") — the console shows it as a toast.</summary>
    public event Action<string, string>? Notify;

    /// <summary>True while a fingerprint capture or a server call is in progress (blocks the idle auto-lock).</summary>
    public bool IsBusy => _capturing || _busy;

    public DamagesView()
    {
        InitializeComponent();
        ShowDetail();
    }

    public void Initialize(AttendanceEngine engine, ITerminalAdapter adapter, BackendApiClient apiClient, LocalAttendanceStore store)
    {
        _engine = engine;
        _adapter = adapter;
        _apiClient = apiClient;
        _store = store;

        // Raised on background threads.
        engine.ConsentScanned += scan => Dispatcher.InvokeAsync(() => OnConsentScanned(scan));
        engine.FingerCaptured += () => Dispatcher.InvokeAsync(OnFingerCaptured);
        engine.FingerNotRecognized += () => Dispatcher.InvokeAsync(OnFingerNotRecognized);
    }

    // ===================================================================
    //  Called by the console
    // ===================================================================

    public void StartSession(AuthResponse session)
    {
        _session = session;
        _damages = new List<DamageItem>();
        _selected = null;
        DamageList.ItemsSource = null;
        RecordCard.Visibility = Visibility.Collapsed;
        CountText.Text = string.Empty;
        ShowDetail();
    }

    public void EndSession()
    {
        StopCapture(null, "info");
        _session = null;
        _damages = new List<DamageItem>();
        _selected = null;
        DamageList.ItemsSource = null;
        RecordCard.Visibility = Visibility.Collapsed;
        ShowDetail();
    }

    public void SetBranch(long? branchId) => _branchId = branchId;

    public void SetEmployees(IEnumerable<EmployeeSummary> employees)
    {
        _employees = employees.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        EmployeeCombo.ItemsSource = _employees;
    }

    public Task RefreshAsync() => LoadDamagesAsync();

    // ===================================================================
    //  Loading
    // ===================================================================

    private async Task LoadDamagesAsync(bool selectNewest = false)
    {
        var session = _session;
        if (session is null || _apiClient is null || _loading) return;

        _loading = true;
        CountText.Text = "Loading damages…";

        try
        {
            var all = await _apiClient.GetDamagesAsync("PENDING_CONSENT", session.AccessToken, CancellationToken.None);
            if (!ReferenceEquals(session, _session)) return;   // signed out while loading

            if (all is null)
            {
                CountText.Text = "Couldn't load damages — check connection";
                Notify?.Invoke("Couldn't load damages. Check the internet connection.", "error");
                return;
            }

            // This reader only knows this branch's fingerprints, so only this branch's damages can be agreed here.
            _damages = _branchId is long branchId ? all.Where(d => d.BranchId == branchId).ToList() : all;

            long? keep = selectNewest
                ? _damages.OrderByDescending(d => d.Id).FirstOrDefault()?.Id
                : _selected?.Id;
            BindList(keep);
        }
        catch (Exception)
        {
            if (ReferenceEquals(session, _session))
            {
                CountText.Text = "Couldn't load damages — check connection";
                Notify?.Invoke("Couldn't load damages. Check the internet connection.", "error");
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void BindList(long? selectId)
    {
        var rows = _damages.Select(d =>
        {
            var name = d.EmployeeName ?? $"Employee #{d.EmployeeId}";
            var (bg, fg) = AvatarColors($"emp-{d.EmployeeId}");
            var sub = string.Join(" • ", new[] { d.EmployeeNumber, d.DamageDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture) }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            return new DamageRow(d.Id, name, Initials(name), sub, FormatKes(d.Amount), bg, fg, d);
        }).ToList();

        DamageList.ItemsSource = rows;
        CountText.Text = rows.Count == 0 ? "Nothing is waiting for consent." : $"{rows.Count} damage(s) awaiting consent";

        if (selectId is long id)
            DamageList.SelectedItem = rows.FirstOrDefault(r => r.Id == id);

        _selected = (DamageList.SelectedItem as DamageRow)?.Damage;
        ShowDetail();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadDamagesAsync();

    // ===================================================================
    //  Selection + detail
    // ===================================================================

    private void DamageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_capturing) return;
        _selected = (DamageList.SelectedItem as DamageRow)?.Damage;
        ShowDetail();
    }

    private void ShowDetail()
    {
        if (_selected is null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyDetailText.Visibility = Visibility.Visible;
            ApplyActionState();
            return;
        }

        var d = _selected;
        DetailNameText.Text = d.EmployeeName ?? $"Employee #{d.EmployeeId}";
        DetailSubText.Text = d.EmployeeNumber ?? string.Empty;
        DetailAmountText.Text = FormatKes(d.Amount);
        DetailDescriptionText.Text = d.Description;
        DetailDateText.Text = $"Recorded for {d.DamageDate.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture)}";

        OverridePanel.Visibility = Visibility.Collapsed;
        ReasonBox.Text = string.Empty;
        ReasonErrorText.Visibility = Visibility.Collapsed;
        SetStatus("Press Capture fingerprint, then ask the employee to place their finger on the reader.", "info");

        EmptyDetailText.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        ApplyActionState();
    }

    private void ApplyActionState()
    {
        var hasSelection = _selected is not null;
        var idle = !_capturing && !_busy;

        CaptureButton.Visibility = _capturing ? Visibility.Collapsed : Visibility.Visible;
        StopCaptureButton.Visibility = _capturing ? Visibility.Visible : Visibility.Collapsed;

        CaptureButton.IsEnabled = hasSelection && idle;
        OverrideButton.IsEnabled = hasSelection && idle;
        CancelDamageButton.IsEnabled = hasSelection && idle;
        ConfirmOverrideButton.IsEnabled = idle;
        RecordSaveButton.IsEnabled = idle;
        RecordToggleButton.IsEnabled = idle;
        RefreshButton.IsEnabled = idle;
        DamageList.IsEnabled = idle;
    }

    private void SetStatus(string text, string kind)
    {
        StatusText.Text = text;
        (StatusBox.Background, StatusText.Foreground) = kind switch
        {
            "success" => (Br("#DCFCE7"), Br("#15803D")),
            "warning" => (Br("#FFF1DC"), Br("#B45309")),
            "error" => (Br("#FDE8E4"), Br("#C2410C")),
            _ => (Br("#F1F6F6"), Br("#10302D")),
        };
    }

    // ===================================================================
    //  Fingerprint consent
    // ===================================================================

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing || _busy || _selected is null || _engine is null || _adapter is null) return;

        var damage = _selected;
        var name = damage.EmployeeName ?? $"Employee #{damage.EmployeeId}";
        var terminalUserId = $"emp-{damage.EmployeeId}";

        if (!_adapter.IsEnrolled(terminalUserId))
        {
            SetStatus($"{name} has no fingerprint on this reader. Enroll them first, or use Override.", "warning");
            return;
        }

        _capturing = true;
        _engine.BeginConsentCapture(damage.Id, terminalUserId);
        SetStatus($"Ask {name} to place their finger on the reader…", "info");
        ApplyActionState();

        _captureTimer?.Stop();
        _captureTimer = new DispatcherTimer { Interval = CaptureTimeout };
        _captureTimer.Tick += (_, _) => StopCapture("No fingerprint was received in time. Press Capture fingerprint to try again.", "warning");
        _captureTimer.Start();
    }

    private void StopCaptureButton_Click(object sender, RoutedEventArgs e) =>
        StopCapture("Capture stopped. Press Capture fingerprint to try again.", "info");

    private void StopCapture(string? message, string kind)
    {
        _captureTimer?.Stop();
        _engine?.CancelConsentCapture();
        _capturing = false;

        if (message is not null) SetStatus(message, kind);
        ApplyActionState();
    }

    private void OnFingerCaptured()
    {
        if (_capturing) SetStatus("Reading fingerprint…", "info");
    }

    private void OnFingerNotRecognized()
    {
        if (_capturing) SetStatus("That fingerprint wasn't recognised. Press firmly and try again.", "warning");
    }

    private async void OnConsentScanned(ConsentScan scan)
    {
        if (!_capturing || _selected is null || scan.DamageId != _selected.Id) return;

        var damage = _selected;
        var name = damage.EmployeeName ?? $"Employee #{damage.EmployeeId}";

        if (!scan.IsExpectedPerson)
        {
            // The engine keeps the capture armed so the right person can still try.
            var who = _store?.GetEmployee(scan.MatchedTerminalUserId)?.FullName ?? ShellWindow.FallbackName(scan.MatchedTerminalUserId);
            SetStatus($"That fingerprint belongs to {who}, not {name}. Ask {name} to place their finger.", "warning");
            return;
        }

        // Matched: the engine has already disarmed itself.
        _captureTimer?.Stop();
        _capturing = false;
        _busy = true;
        SetStatus("Fingerprint matched — saving…", "info");
        ApplyActionState();

        var session = _session;
        var approved = false;

        try
        {
            if (_apiClient is null) return;

            var result = await _apiClient.SendConsentAsync(
                scan.EventId, scan.MatchedTerminalUserId, scan.DamageId, scan.TimestampUtc, CancellationToken.None);

            if (!ReferenceEquals(session, _session)) return;

            if (result.Succeeded)
            {
                approved = true;
                Notify?.Invoke($"{name} agreed to the damage by fingerprint.", "success");
            }
            else
            {
                var message = result.ShouldPauseSync
                    ? "The server isn't available right now. The damage is still waiting — try again shortly."
                    : FriendlyMessages.Translate(result.Error);
                SetStatus(message, "error");
                Notify?.Invoke(message, "error");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            const string message = "Couldn't reach the server. The damage is still waiting — check the connection and try again.";
            SetStatus(message, "error");
            Notify?.Invoke(message, "error");
        }
        catch (Exception)
        {
            const string message = "Something went wrong while saving. The damage is still waiting — please try again.";
            SetStatus(message, "error");
            Notify?.Invoke(message, "error");
        }
        finally
        {
            _busy = false;
            ApplyActionState();
        }

        if (approved) await LoadDamagesAsync();
    }

    // ===================================================================
    //  Override + cancel
    // ===================================================================

    private void OverrideButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing || _busy || _selected is null) return;
        OverridePanel.Visibility = Visibility.Visible;
        ReasonErrorText.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => ReasonBox.Focus(), DispatcherPriority.Input);
    }

    private void OverrideBackButton_Click(object sender, RoutedEventArgs e)
    {
        OverridePanel.Visibility = Visibility.Collapsed;
        ReasonBox.Text = string.Empty;
    }

    private async void ConfirmOverrideButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing || _busy || _selected is null || _apiClient is null || _session is null) return;

        var reason = ReasonBox.Text.Trim();
        if (reason.Length < 10)
        {
            ReasonErrorText.Visibility = Visibility.Visible;
            return;
        }

        var session = _session;
        var damage = _selected;
        var name = damage.EmployeeName ?? $"Employee #{damage.EmployeeId}";
        var done = false;

        _busy = true;
        ApplyActionState();

        try
        {
            var result = await _apiClient.OverrideDamageAsync(damage.Id, reason, session.AccessToken, CancellationToken.None);
            if (!ReferenceEquals(session, _session)) return;

            if (result.Succeeded)
            {
                done = true;
                Notify?.Invoke($"{name}'s damage was approved by override.", "success");
            }
            else
            {
                var message = FriendlyMessages.Translate(result.Error);
                SetStatus(message, "error");
                Notify?.Invoke(message, "error");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Notify?.Invoke("Couldn't reach the server. The override was not saved.", "error");
        }
        finally
        {
            _busy = false;
            ApplyActionState();
        }

        if (done) await LoadDamagesAsync();
    }

    private async void CancelDamageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing || _busy || _selected is null || _apiClient is null || _session is null) return;

        var damage = _selected;
        var name = damage.EmployeeName ?? $"Employee #{damage.EmployeeId}";

        var answer = System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            $"Cancel the {FormatKes(damage.Amount)} damage for {name}?\n\nNothing will be deducted from their pay.",
            "Cancel damage", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var session = _session;
        var done = false;

        _busy = true;
        ApplyActionState();

        try
        {
            var result = await _apiClient.CancelDamageAsync(damage.Id, session.AccessToken, CancellationToken.None);
            if (!ReferenceEquals(session, _session)) return;

            if (result.Succeeded)
            {
                done = true;
                Notify?.Invoke("Damage cancelled.", "success");
            }
            else
            {
                var message = FriendlyMessages.Translate(result.Error);
                SetStatus(message, "error");
                Notify?.Invoke(message, "error");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Notify?.Invoke("Couldn't reach the server. The damage was not cancelled.", "error");
        }
        finally
        {
            _busy = false;
            ApplyActionState();
        }

        if (done) await LoadDamagesAsync();
    }

    // ===================================================================
    //  Record a damage
    // ===================================================================

    private void RecordToggleButton_Click(object sender, RoutedEventArgs e)
    {
        RecordCard.Visibility = RecordCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ShowRecordError(null);
    }

    private void RecordCloseButton_Click(object sender, RoutedEventArgs e) => RecordCard.Visibility = Visibility.Collapsed;

    private async void RecordSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing || _busy || _apiClient is null || _session is null) return;

        if (EmployeeCombo.SelectedItem is not EmployeeSummary employee)
        {
            ShowRecordError("Choose the employee.");
            return;
        }

        if (!decimal.TryParse(AmountBox.Text.Trim().Replace(",", string.Empty), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            ShowRecordError("Enter an amount greater than zero.");
            return;
        }

        var description = DescriptionBox.Text.Trim();
        if (description.Length == 0)
        {
            ShowRecordError("Describe what happened.");
            return;
        }

        var session = _session;
        var recorded = false;

        _busy = true;
        ShowRecordError(null);
        ApplyActionState();

        try
        {
            var result = await _apiClient.RecordDamageAsync(employee.Id, Math.Round(amount, 2), description, session.AccessToken, CancellationToken.None);
            if (!ReferenceEquals(session, _session)) return;

            if (result.Succeeded)
            {
                recorded = true;
                Notify?.Invoke($"Damage recorded for {employee.FullName}. Ask them to place their finger on the reader.", "success");
                AmountBox.Text = string.Empty;
                DescriptionBox.Text = string.Empty;
                EmployeeCombo.SelectedItem = null;
                RecordCard.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowRecordError(FriendlyMessages.Translate(result.Error));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ShowRecordError("Couldn't reach the server. The damage was not saved.");
        }
        finally
        {
            _busy = false;
            ApplyActionState();
        }

        if (recorded) await LoadDamagesAsync(selectNewest: true);
    }

    private void ShowRecordError(string? message)
    {
        RecordErrorText.Text = message ?? string.Empty;
        RecordErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ===================================================================
    //  Helpers
    // ===================================================================

    private static string FormatKes(decimal amount) => $"KES {amount.ToString("N2", CultureInfo.InvariantCulture)}";

    private static string Initials(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][0].ToString().ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    // Same stable per-person colour the rest of the console uses.
    private static (Brush Bg, Brush Fg) AvatarColors(string key) =>
        AvatarPalette[key.Sum(c => (int)c) % AvatarPalette.Length];

    private static SolidColorBrush Br(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
