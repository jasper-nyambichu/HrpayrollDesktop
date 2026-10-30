using System;
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

    // How often the kiosk refreshes employee names/mappings from the server (also a "device online" heartbeat).
    private static readonly TimeSpan MappingRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<AttendanceEngine> _logger;
    private readonly ITerminalAdapter _terminalAdapter;
    private readonly OfflineQueueStore _queueStore;
    private readonly LocalAttendanceStore _localStore;
    private readonly BackendApiClient _apiClient;
    private readonly AgentSettings _settings;

    private DateTime _lastMappingAttemptUtc = DateTime.MinValue;

    public event Action<string, string, DateTime>? MatchOccurred;   // terminalUserId, eventType, timestampUtc
    public event Action<string, string, DateTime>? DuplicateScan;   // terminalUserId, lastEventType, lastTimestampUtc
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

    public DateTime? LastSyncSuccessUtc { get; private set; }
    public string? LastSyncError { get; private set; }

    /// <summary>Forces the next sync cycle to immediately re-fetch employee mappings from the server.</summary>
    public void InvalidateMappingCache() => _lastMappingAttemptUtc = DateTime.MinValue;

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

            var last = _localStore.GetLastEventToday(evt.TerminalUserId);

            // Every touch gets exactly one visible outcome: success, already-recorded, or not-recognized.
            if (last is not null && (evt.Timestamp - last.TimestampUtc) < ScanCooldown)
            {
                DuplicateScan?.Invoke(evt.TerminalUserId, last.EventType, last.TimestampUtc);
                continue;
            }

            // Toggle is per calendar day: no scan yet today => always CLOCK_IN.
            var eventType = last?.EventType == "CLOCK_IN" ? "CLOCK_OUT" : "CLOCK_IN";

            _queueStore.Enqueue(new QueuedEvent
            {
                EventId = evt.EventId,
                TerminalUserId = evt.TerminalUserId,
                Timestamp = evt.Timestamp,
                VerifyMethod = evt.VerifyMethod,
                EventType = eventType,
            });

            _localStore.AppendLog(evt.EventId, evt.TerminalUserId, eventType, evt.Timestamp);

            _logger.LogInformation("Queued event {EventId} ({EventType})", evt.EventId, eventType);

            MatchOccurred?.Invoke(evt.TerminalUserId, eventType, evt.Timestamp);
        }
    }

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
                    queued.EventId, queued.TerminalUserId, queued.Timestamp, queued.VerifyMethod, queued.EventType, stoppingToken);
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

/// <summary>One scan taken while a damage was waiting for the employee's consent.</summary>
public sealed record ConsentScan(
    long DamageId,
    string ExpectedTerminalUserId,
    string MatchedTerminalUserId,
    bool IsExpectedPerson,
    string EventId,
    DateTime TimestampUtc);