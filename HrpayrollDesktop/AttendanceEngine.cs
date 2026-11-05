using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HrpayrollDesktop.Config;
using HrpayrollDesktop.Local;
using HrpayrollDesktop.OfflineQueue;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;

namespace HrpayrollDesktop;

public class AttendanceEngine : BackgroundService
{
    private const int QueueDepthWarningThreshold = 200;

    // The reader is polled quickly so a finger is noticed (and lifts are detected) promptly.
    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(200);

    // Someone who lifts and scans again within this window sees "already recorded"
    // instead of accidentally flipping CLOCK_IN to CLOCK_OUT.
    private static readonly TimeSpan ScanCooldown = TimeSpan.FromSeconds(30);

    // Time allowed for lunch. The server applies the same limit when it records who came back late.
    public static readonly TimeSpan LunchAllowance = TimeSpan.FromMinutes(30);

    // How often the kiosk refreshes employee names, mappings and fingerprints from the server (also a
    // "device online" heartbeat). This is also the longest someone moved here waits to be recognised.
    private static readonly TimeSpan MappingRefreshInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<AttendanceEngine> _logger;
    private readonly ITerminalAdapter _terminalAdapter;
    private readonly OfflineQueueStore _queueStore;
    private readonly LocalAttendanceStore _localStore;
    private readonly BackendApiClient _apiClient;
    private readonly AgentSettings _settings;

    private DateTime _lastMappingAttemptUtc = DateTime.MinValue;

    public event Action<string, string, DateTime>? MatchOccurred;   // terminalUserId, eventType, timestampUtc
    public event Action<string, string, DateTime>? DuplicateScan;   // terminalUserId, lastEventType, lastTimestampUtc
    public event Action<ScanChoiceRequest>? ChoiceRequired;         // someone at work scanned: lunch, temporary check-out or clock out?
    public event Action<BreakReturn>? ReturnedFromBreak;             // someone out on a break scanned back in
    public event Action? FingerCaptured;
    public event Action? FingerNotRecognized;
    public event Action? FingerRemoved;
    public event Action? FingerPoorQuality;
    public event Action<string>? DeviceFault;
    public event Action? DeviceReady;

    // Back Office consent capture: while armed, the next matched finger answers a damage instead of clocking in/out.
    public event Action<ConsentScan>? ConsentScanned;

    public bool DeviceConnected { get; private set; }

    /// <summary>Scans still on their way to the server.</summary>
    public int PendingSyncCount => _queueStore.GetPendingCount();

    /// <summary>Scans the server refused for a business reason (see the rejected list).</summary>
    public int RejectedSyncCount => _queueStore.GetRejectedCount();

    public HashSet<string> GetUnsentEventIds() => _queueStore.GetUnsentEventIds();

    public DateTime? LastSyncSuccessUtc { get; private set; }
    public string? LastSyncError { get; private set; }

    /// <summary>Forces the next sync cycle to immediately re-fetch employee mappings from the server.</summary>
    public void InvalidateMappingCache() => _lastMappingAttemptUtc = DateTime.MinValue;

    private readonly object _choiceLock = new();
    private TerminalEvent? _pendingChoice;

    private readonly object _consentLock = new();
    private long? _consentDamageId;
    private string? _consentTerminalUserId;

    /// <summary>True while the reader is waiting for an employee to agree to a damage.</summary>
    public bool IsConsentCaptureActive
    {
        get { lock (_consentLock) return _consentDamageId is not null; }
    }

    /// <summary>
    /// Arms the reader for one damage. Until a matching scan arrives (or the capture is cancelled) scans are NOT
    /// recorded as clock-ins/outs, so agreeing to a damage can never clock anyone in by accident.
    /// </summary>
    public void BeginConsentCapture(long damageId, string expectedTerminalUserId)
    {
        lock (_consentLock)
        {
            _consentDamageId = damageId;
            _consentTerminalUserId = expectedTerminalUserId;
        }
    }

    public void CancelConsentCapture()
    {
        lock (_consentLock)
        {
            _consentDamageId = null;
            _consentTerminalUserId = null;
        }
    }

    public AttendanceEngine(
        ILogger<AttendanceEngine> logger,
        ITerminalAdapter terminalAdapter,
        OfflineQueueStore queueStore,
        LocalAttendanceStore localStore,
        BackendApiClient apiClient,
        IOptions<AgentSettings> settings)
    {
        _logger = logger;
        _terminalAdapter = terminalAdapter;
        _queueStore = queueStore;
        _localStore = localStore;
        _apiClient = apiClient;
        _settings = settings.Value;

        _terminalAdapter.FingerCaptured += () => FingerCaptured?.Invoke();
        _terminalAdapter.FingerNotRecognized += () => FingerNotRecognized?.Invoke();
        _terminalAdapter.FingerRemoved += () => FingerRemoved?.Invoke();
        _terminalAdapter.FingerPoorQuality += () => FingerPoorQuality?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _queueStore.Initialize();
        _localStore.Initialize();

        try
        {
            await _terminalAdapter.ConnectAsync(stoppingToken);
            DeviceConnected = true;
            DeviceReady?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device failed to connect at startup. Continuing without biometric capture.");
            DeviceConnected = false;
            DeviceFault?.Invoke(ex.Message);
        }

        var pollingLoop = DeviceConnected ? RunPollingLoopAsync(stoppingToken) : Task.CompletedTask;
        var syncLoop = RunSyncLoopAsync(stoppingToken);

        await Task.WhenAll(pollingLoop, syncLoop);
    }

    private async Task RunPollingLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollTerminalAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error polling terminal; will retry next cycle.");
            }

            await Task.Delay(CapturePollInterval, stoppingToken);
        }
    }

    private async Task RunSyncLoopAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_settings.SyncIntervalSeconds, 1));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshMappingsIfDueAsync(stoppingToken);
                await SyncPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error in sync loop; will retry next cycle.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task PollTerminalAsync(CancellationToken stoppingToken)
    {
        var events = await _terminalAdapter.PollEventsAsync(stoppingToken);

        foreach (var evt in events)
        {
            // A Back Office consent capture is waiting: this scan answers the damage, not attendance.
            if (TryTakeConsentScan(evt)) continue;

            // The last person to scan is still choosing on screen. Nothing else is recorded until they finish.
            lock (_choiceLock)
            {
                if (_pendingChoice is not null) continue;
            }

            var last = _localStore.GetLastEventToday(evt.TerminalUserId);

            // Every touch gets exactly one visible outcome: success, already-recorded, or not-recognized.
            if (last is not null && (evt.Timestamp - last.TimestampUtc) < ScanCooldown)
            {
                DuplicateScan?.Invoke(evt.TerminalUserId, last.EventType, last.TimestampUtc);
                continue;
            }

            HandleScan(evt, last);
        }
    }

    // What a scan means depends on where the person is: not in yet, at work, or out on a break.
    private void HandleScan(TerminalEvent evt, AttendanceLogEntry? last)
    {
        switch (last?.EventType)
        {
            case "CLOCK_IN":
            case "BREAK_RETURN":
                lock (_choiceLock)
                {
                    _pendingChoice = evt;
                }
                ChoiceRequired?.Invoke(new ScanChoiceRequest(evt.TerminalUserId, evt.Timestamp,
                    _localStore.GetLatestClockInToday(evt.TerminalUserId)?.TimestampUtc));
                break;

            case "LUNCH_OUT":
            case "EMERGENCY_OUT":
                Record(evt, "BREAK_RETURN");
                ReturnedFromBreak?.Invoke(new BreakReturn(evt.TerminalUserId, last.EventType == "LUNCH_OUT",
                    last.TimestampUtc, evt.Timestamp, last.ExpectedBackUtc));
                break;

            default:   // no scan yet today, or they already left and have come back
                Record(evt, "CLOCK_IN");
                MatchOccurred?.Invoke(evt.TerminalUserId, "CLOCK_IN", evt.Timestamp);
                break;
        }
    }

    private void Record(TerminalEvent evt, string eventType, DateTime? expectedBackUtc = null, string? reason = null)
    {
        _queueStore.Enqueue(new QueuedEvent
        {
            EventId = evt.EventId,
            TerminalUserId = evt.TerminalUserId,
            Timestamp = evt.Timestamp,
            VerifyMethod = evt.VerifyMethod,
            EventType = eventType,
            ExpectedBackUtc = expectedBackUtc,
            Reason = reason,
        });

        _localStore.AppendLog(evt.EventId, evt.TerminalUserId, eventType, evt.Timestamp, expectedBackUtc, reason);

        _logger.LogInformation("Queued event {EventId} ({EventType})", evt.EventId, eventType);
    }

    /// <summary>Goes to lunch from the moment of the scan. Returns when they are due back, or null if nothing was waiting.</summary>
    public DateTime? ChooseLunch()
    {
        var evt = TakePendingChoice();
        if (evt is null) return null;

        var due = evt.Timestamp + LunchAllowance;
        Record(evt, "LUNCH_OUT", due);
        return due;
    }

    /// <summary>A temporary check-out, which is never counted as late. Returns false if nothing was waiting.</summary>
    public bool ChooseTemporaryCheckout(string reason, DateTime expectedBackUtc)
    {
        var evt = TakePendingChoice();
        if (evt is null) return false;

        Record(evt, "EMERGENCY_OUT", expectedBackUtc, reason);
        return true;
    }

    /// <summary>Finishes the day. Returns false if nothing was waiting.</summary>
    public bool ChooseClockOut()
    {
        var evt = TakePendingChoice();
        if (evt is null) return false;

        Record(evt, "CLOCK_OUT");
        return true;
    }

    /// <summary>They walked away or pressed cancel, so the scan records nothing.</summary>
    public void CancelChoice() => TakePendingChoice();

    private TerminalEvent? TakePendingChoice()
    {
        lock (_choiceLock)
        {
            var evt = _pendingChoice;
            _pendingChoice = null;
            return evt;
        }
    }

#if DEBUG
    /// <summary>Test builds only: goes through exactly what a real finger does, without one.</summary>
    public void SimulateScan(string terminalUserId)
    {
        FingerCaptured?.Invoke();
        var evt = new TerminalEvent($"sim-{Guid.NewGuid():N}", terminalUserId, DateTime.UtcNow, "FINGERPRINT");
        lock (_choiceLock)
        {
            if (_pendingChoice is not null) return;
        }
        var last = _localStore.GetLastEventToday(terminalUserId);
        if (last is not null && (evt.Timestamp - last.TimestampUtc) < ScanCooldown)
        {
            DuplicateScan?.Invoke(terminalUserId, last.EventType, last.TimestampUtc);
            return;
        }
        HandleScan(evt, last);
    }
#endif

    // Returns true when a consent capture was armed and the scan was handed to it.
    private bool TryTakeConsentScan(TerminalEvent evt)
    {
        long damageId;
        string expected;

        lock (_consentLock)
        {
            if (_consentDamageId is null || _consentTerminalUserId is null) return false;

            damageId = _consentDamageId.Value;
            expected = _consentTerminalUserId;

            // The right person ends the capture; someone else's finger leaves it armed for another try.
            if (evt.TerminalUserId == expected)
            {
                _consentDamageId = null;
                _consentTerminalUserId = null;
            }
        }

        try
        {
            ConsentScanned?.Invoke(new ConsentScan(
                damageId, expected, evt.TerminalUserId, evt.TerminalUserId == expected, evt.EventId, evt.Timestamp));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A consent handler threw; ignoring.");
        }

        return true;
    }

    // Pulls "who is who" for this reader so the kiosk can greet people by name without a manager login.
    private async Task RefreshMappingsIfDueAsync(CancellationToken stoppingToken)
    {
        if (DateTime.UtcNow - _lastMappingAttemptUtc < MappingRefreshInterval) return;

        // On failure, try again in ~30s rather than waiting the full interval.
        _lastMappingAttemptUtc = DateTime.UtcNow - (MappingRefreshInterval - TimeSpan.FromSeconds(30));

        try
        {
            var mappings = await _apiClient.GetDeviceMappingsAsync(stoppingToken);
            if (mappings is null) return;

            var entries = mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.EmployeeName))
                .Select(m => new EmployeeDirectoryEntry(
                    m.TerminalUserId, m.EmployeeId, m.EmployeeName!, m.EmployeeNumber ?? string.Empty,
                    m.Position, m.BranchName))
                .ToList();

            if (entries.Count > 0)
                _localStore.UpsertEmployees(entries);

            _lastMappingAttemptUtc = DateTime.UtcNow;
            _logger.LogInformation("Refreshed {Count} employee mapping(s) from the server.", entries.Count);

            await SyncFingerprintsNowAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Mapping refresh skipped: {Reason}", ex.Message);
        }
    }

    /// <summary>
    /// Brings this reader in line with who works at its branch now: someone moved here is added, and someone
    /// moved away is removed. Returns false when the server couldn't be asked or the reader isn't open.
    /// </summary>
    public async Task<bool> SyncFingerprintsNowAsync(CancellationToken cancellationToken)
    {
        if (!DeviceConnected) return false;   // the local templates are only loaded once the reader is open

        var bundle = await _apiClient.GetFingerprintsAsync(cancellationToken);
        if (bundle is null) return false;

        var fingerprints = new List<ServerFingerprint>();
        foreach (var person in bundle.Templates.OrderBy(t => t.FingerSlot).GroupBy(t => t.TerminalUserId))
        {
            var first = person.First();   // the reader holds one finger per person
            try
            {
                fingerprints.Add(new ServerFingerprint(first.TerminalUserId, first.EmployeeId, Convert.FromBase64String(first.TemplateData)));
            }
            catch (FormatException)
            {
                _logger.LogWarning("Skipped an unreadable fingerprint for employee {EmployeeId}.", first.EmployeeId);
            }
        }

        var summary = await _terminalAdapter.ApplyServerFingerprintsAsync(fingerprints, bundle.EmployeeIds, cancellationToken);
        _localStore.RemoveEmployeesNotIn(bundle.EmployeeIds);
        if (summary.Changed)
        {
            _logger.LogInformation("Fingerprints synced: {Added} added, {Updated} updated, {Removed} removed.",
                summary.Added, summary.Updated, summary.Removed);
        }
        return true;
    }

    private async Task SyncPendingAsync(CancellationToken stoppingToken)
    {
        var depth = _queueStore.GetPendingCount();
        if (depth >= QueueDepthWarningThreshold)
            _logger.LogWarning("Offline queue depth is {Depth} — this device may have been offline a long time.", depth);

        foreach (var queued in _queueStore.GetPending())
        {
            SyncResult result;

            try
            {
                result = await _apiClient.SyncEventAsync(
                    queued.EventId, queued.TerminalUserId, queued.Timestamp, queued.VerifyMethod, queued.EventType,
                    queued.ExpectedBackUtc, queued.Reason, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidOperationException ex)
            {
                // Device credentials not configured yet.
                LastSyncError = "Device credentials are not configured.";
                _logger.LogDebug("Sync skipped: {Reason}", ex.Message);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Offline / server unreachable: scans stay safely queued; stop this cycle, no penalty.
                LastSyncError = "Can't reach the server — scans are saved and will sync automatically.";
                _logger.LogInformation("Server unreachable, will retry: {Reason}", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                _queueStore.MarkRetry(queued.Id, ex.Message);
                _logger.LogWarning(ex, "Unexpected error syncing {EventId}, will retry with backoff", queued.EventId);
                continue;
            }

            if (result.Succeeded)
            {
                _queueStore.MarkSynced(queued.Id);
                LastSyncSuccessUtc = DateTime.UtcNow;
                LastSyncError = null;
                _logger.LogInformation("Synced event {EventId}", queued.EventId);
                continue;
            }

            if (result.ShouldPauseSync)
            {
                LastSyncError = result.StatusCode is 401 or 403
                    ? "The server rejected this device's credentials. Check the device serial and token."
                    : "The server is temporarily unavailable — will retry.";
                _logger.LogWarning("Sync paused ({Status}): {Error}", result.StatusCode, result.Error);
                return;
            }

            if (result.IsPermanentRejection)
            {
                _queueStore.MarkRejected(queued.Id, result.Error ?? "Rejected by server");
                _logger.LogWarning("Server rejected {EventId} permanently: {Error}", queued.EventId, result.Error);
                continue;
            }

            _queueStore.MarkRetry(queued.Id, result.Error ?? "Unknown error");
            _logger.LogWarning("Sync failed for {EventId}, will retry with backoff: {Error}", queued.EventId, result.Error);
        }
    }
}

/// <summary>Someone already at work scanned, so they choose what happens next on screen.</summary>
public sealed record ScanChoiceRequest(string TerminalUserId, DateTime ScannedAtUtc, DateTime? ClockedInAtUtc);

/// <summary>Someone came back from lunch or a temporary check-out.</summary>
public sealed record BreakReturn(string TerminalUserId, bool WasLunch, DateTime LeftAtUtc, DateTime BackAtUtc, DateTime? ExpectedBackUtc);

/// <summary>One scan taken while a damage was waiting for the employee's consent.</summary>
public sealed record ConsentScan(
    long DamageId,
    string ExpectedTerminalUserId,
    string MatchedTerminalUserId,
    bool IsExpectedPerson,
    string EventId,
    DateTime TimestampUtc);