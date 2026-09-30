using System;
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

    // Same person scanning again within this window is ignored silently
    // (finger still resting on the glass while the success screen shows).
    private static readonly TimeSpan SilentRepeatWindow = TimeSpan.FromSeconds(6);

    // Within this window a repeat scan shows "already recorded" instead of
    // flipping clock-in to clock-out by accident.
    private static readonly TimeSpan ScanCooldown = TimeSpan.FromSeconds(60);

    private readonly ILogger<AttendanceEngine> _logger;
    private readonly ITerminalAdapter _terminalAdapter;
    private readonly OfflineQueueStore _queueStore;
    private readonly LocalAttendanceStore _localStore;
    private readonly BackendApiClient _apiClient;
    private readonly AgentSettings _settings;

    public event Action<string, string, DateTime>? MatchOccurred;    // terminalUserId, eventType, timestampUtc
    public event Action<string, string, DateTime>? DuplicateScan;    // terminalUserId, lastEventType, lastTimestampUtc
    public event Action? FingerCaptured;
    public event Action? FingerNotRecognized;
    public event Action<string>? DeviceFault;
    public event Action? DeviceReady;

    public bool DeviceConnected { get; private set; }

    public int PendingSyncCount => _queueStore.GetPendingCount();

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
        var interval = TimeSpan.FromSeconds(Math.Max(_settings.PollIntervalSeconds, 1));

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

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunSyncLoopAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_settings.SyncIntervalSeconds, 1));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
            var last = _localStore.GetLastEventToday(evt.TerminalUserId);

            if (last is not null)
            {
                var since = evt.Timestamp - last.TimestampUtc;

                if (since < SilentRepeatWindow)
                    continue;

                if (since < ScanCooldown)
                {
                    DuplicateScan?.Invoke(evt.TerminalUserId, last.EventType, last.TimestampUtc);
                    continue;
                }
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

    private async Task SyncPendingAsync(CancellationToken stoppingToken)
    {
        var depth = _queueStore.GetPendingCount();
        if (depth >= QueueDepthWarningThreshold)
            _logger.LogWarning("Offline queue depth is {Depth} — this device may have been offline a long time.", depth);

        var pending = _queueStore.GetPending();

        foreach (var queued in pending)
        {
            try
            {
                var result = await _apiClient.SyncEventAsync(
                    queued.EventId, queued.TerminalUserId, queued.Timestamp, queued.VerifyMethod, queued.EventType, stoppingToken);

                if (result.Succeeded)
                {
                    _queueStore.MarkSynced(queued.Id);
                    _logger.LogInformation("Synced event {EventId}", queued.EventId);
                }
                else
                {
                    _queueStore.MarkFailedAttempt(queued.Id, result.Error ?? "unknown error");
                    _logger.LogWarning("Sync failed for {EventId}, will retry: {Error}", queued.EventId, result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug("Sync skipped: {Reason}", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                _queueStore.MarkFailedAttempt(queued.Id, ex.Message);
                _logger.LogWarning(ex, "Unexpected error syncing {EventId}, will retry", queued.EventId);
            }
        }
    }
}