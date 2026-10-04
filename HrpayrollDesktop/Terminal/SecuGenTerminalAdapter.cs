using System.Text.Json;
using Microsoft.Extensions.Logging;
using SecuGen.FDxSDKPro.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HrpayrollDesktop.Terminal;

public class SecuGenTerminalAdapter : ITerminalAdapter, IDisposable
{
    // ---- Tuning knobs (SecuGen image quality is 0-100) ----
    private const int MinCaptureQuality = 50;      // good enough to match
    private const int PresenceQuality = 20;        // below this = nothing on the glass
    private const int LowQualityLiftMs = 700;      // weak signal this long = treat as lifted
    private const int PoorQualityHintMs = 2000;    // finger present but too weak this long = show hint
    private const int SameFingerGuardMs = 1500;    // same finger bouncing/resting within this window is ignored
    private const int MatchWindowMs = 1200;        // total time allowed for settle retries
    private const int MaxExtraAttempts = 3;
    private const int ExtraAttemptDelayMs = 150;
    private const int TemplateSize = 400;
    private const int EnrollTimeoutMs = 5000;

    // SecuGen security levels: 3 = LOW, 4 = BELOW_NORMAL, 5 = NORMAL ... 9 = HIGHEST.
    private const int IdentifyLevel = 4;
    private static readonly int[] NarrowingLevels = { 6, 8 };   // used only if several employees match
    private const int SameFingerLevel = 4;

    // Flip to true to log the quality of every frame (for diagnosing reader behaviour).
    private const bool TraceFrames = false;

    private static readonly string TemplateStorePath =
        Path.Combine(AppContext.BaseDirectory, "enrolled-templates.json");

    private enum TouchState { Idle, WaitingForRemoval }

    private readonly ILogger<SecuGenTerminalAdapter> _logger;
    private SGFingerPrintManager? _fpm;
    private int _imageWidth;
    private int _imageHeight;

    private Dictionary<string, byte[]> _templates = new();
    private readonly object _templateSync = new();

    // Guards every SDK call. All touch-state fields below are only touched while holding it.
    private readonly SemaphoreSlim _deviceLock = new(1, 1);

    private TouchState _touch = TouchState.Idle;
    private DateTime? _belowMinSinceUtc;
    private DateTime? _weakSinceUtc;
    private bool _poorQualityRaised;
    private byte[]? _lastTouchTemplate;      // the finger we most recently handled
    private DateTime _lastTouchUtc = DateTime.MinValue;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;

    public event Action? FingerCaptured;
    public event Action? FingerNotRecognized;
    public event Action? FingerRemoved;
    public event Action? FingerPoorQuality;

    public SecuGenTerminalAdapter(ILogger<SecuGenTerminalAdapter> logger)
    {
        _logger = logger;
    }

    private string? _deviceSerial;
    public string? DeviceSerial => _deviceSerial;

    public int EnrolledCount
    {
        get { lock (_templateSync) return _templates.Count; }
    }

    public bool IsEnrolled(string terminalUserId)
    {
        lock (_templateSync) return _templates.ContainsKey(terminalUserId);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _deviceLock.WaitAsync(cancellationToken);
        try
        {
            _fpm = new SGFingerPrintManager();

            var initError = _fpm.Init(SGFPMDeviceName.DEV_AUTO);
            var openError = _fpm.OpenDevice((int)SGFPMPortAddr.USB_AUTO_DETECT);

            if (openError != (int)SGFPMError.ERROR_NONE)
            {
                _logger.LogError("SecuGen device failed to open. Init error={InitError}, Open error={OpenError}", initError, openError);
                throw new InvalidOperationException($"SecuGen device open failed (error {openError}). Check the reader is plugged in and the driver is installed.");
            }

            var info = new SGFPMDeviceInfoParam();
            var infoError = _fpm.GetDeviceInfo(info);
            if (infoError == (int)SGFPMError.ERROR_NONE)
            {
                _imageWidth = info.ImageWidth;
                _imageHeight = info.ImageHeight;
                _deviceSerial = Encoding.ASCII.GetString(info.DeviceSN).TrimEnd('\0');
                _logger.LogInformation("SecuGen device connected. Serial={Serial}, {Width}x{Height}",
                    _deviceSerial, _imageWidth, _imageHeight);
            }
            else
            {
                _logger.LogWarning("SecuGen device opened but GetDeviceInfo failed (error {Error}); using fallback image size.", infoError);
                _imageWidth = 260;
                _imageHeight = 300;
            }

            LoadTemplates();
            ResetTouchState(TouchState.Idle);
            _lastTouchTemplate = null;
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    /// <summary>
    /// Called every ~200ms. One touch = one result:
    /// Idle -> good print -> identify ONCE -> WaitingForRemoval.
    /// While waiting: the same finger is ignored, a DIFFERENT finger is scanned right away,
    /// and lifting the finger re-arms the reader.
    /// </summary>
    public async Task<IReadOnlyList<TerminalEvent>> PollEventsAsync(CancellationToken cancellationToken)
    {
        if (_fpm is null)
            throw new InvalidOperationException("Device not connected. Call ConnectAsync first.");

        if (EnrolledCount == 0)
        {
            Heartbeat("No employees enrolled yet — nothing to match against.");
            return Array.Empty<TerminalEvent>();
        }

        if (!await _deviceLock.WaitAsync(TimeSpan.FromMilliseconds(100), cancellationToken))
            return Array.Empty<TerminalEvent>();

        try
        {
            var image = new byte[_imageWidth * _imageHeight];
            var quality = GrabFrame(image);
            if (quality is null)
                return Array.Empty<TerminalEvent>();

            var q = quality.Value;
            var now = DateTime.UtcNow;

            if (TraceFrames)
                _logger.LogInformation("frame quality={Quality} state={State}", q, _touch);

            return _touch == TouchState.WaitingForRemoval
                ? await HandleWaitingAsync(image, q, now, cancellationToken)
                : await HandleIdleAsync(image, q, now, cancellationToken);
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    private async Task<IReadOnlyList<TerminalEvent>> HandleIdleAsync(byte[] image, int q, DateTime now, CancellationToken cancellationToken)
    {
        // Nothing on the glass.
        if (q < PresenceQuality)
        {
            if (_poorQualityRaised)
            {
                _poorQualityRaised = false;
                SafeRaise(FingerRemoved);
            }
            _weakSinceUtc = null;
            Heartbeat("Ready — place your finger on the reader to clock in or out.");
            return Array.Empty<TerminalEvent>();
        }

        // Finger present but too weak to read.
        if (q < MinCaptureQuality)
        {
            _weakSinceUtc ??= now;
            if (!_poorQualityRaised && (now - _weakSinceUtc.Value).TotalMilliseconds >= PoorQualityHintMs)
            {
                _poorQualityRaised = true;
                _logger.LogInformation("Finger present but image too weak (quality={Quality}); showing hint.", q);
                SafeRaise(FingerPoorQuality);
            }
            return Array.Empty<TerminalEvent>();
        }

        _weakSinceUtc = null;
        _poorQualityRaised = false;

        var template = CreateTemplate(image);
        if (template is null)
            return Array.Empty<TerminalEvent>();

        // The same finger we just handled bouncing back onto the glass: not a new scan.
        if (_lastTouchTemplate is not null
            && (now - _lastTouchUtc).TotalMilliseconds < SameFingerGuardMs
            && Matches(template, _lastTouchTemplate))
        {
            EnterWaiting(now);
            return Array.Empty<TerminalEvent>();
        }

        return await ProcessTouchAsync(q, template, image, cancellationToken);
    }

    private async Task<IReadOnlyList<TerminalEvent>> HandleWaitingAsync(byte[] image, int q, DateTime now, CancellationToken cancellationToken)
    {
        // Finger lifted.
        if (q < PresenceQuality)
        {
            Release("Finger removed — ready for the next scan.");
            return Array.Empty<TerminalEvent>();
        }

        // Weak signal for a while (partial lift / smudge): treat as lifted.
        if (q < MinCaptureQuality)
        {
            _belowMinSinceUtc ??= now;
            if ((now - _belowMinSinceUtc.Value).TotalMilliseconds >= LowQualityLiftMs)
                Release("Finger signal faded — ready for the next scan.");
            return Array.Empty<TerminalEvent>();
        }

        _belowMinSinceUtc = null;

        // A good print is on the glass: same finger still resting, or a different finger placed without lifting?
        var template = CreateTemplate(image);
        if (template is null)
            return Array.Empty<TerminalEvent>();

        if (_lastTouchTemplate is not null && !Matches(template, _lastTouchTemplate))
        {
            _logger.LogInformation("A different finger was placed — scanning it.");
            return await ProcessTouchAsync(q, template, image, cancellationToken);
        }

        _lastTouchUtc = now;   // same finger still resting
        return Array.Empty<TerminalEvent>();
    }

    private async Task<IReadOnlyList<TerminalEvent>> ProcessTouchAsync(int quality, byte[] template, byte[] image, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Finger detected (quality={Quality}) — matching...", quality);

        // This touch is now "consumed": no more scans for this finger until it lifts.
        var now = DateTime.UtcNow;
        EnterWaiting(now);
        _lastTouchTemplate = template;
        _lastTouchUtc = now;

        SafeRaise(FingerCaptured);

        var matchedUserId = Identify(template);

        // A still-settling finger can give a poor first frame; allow a few quick retries before a mismatch.
        if (matchedUserId is null)
        {
            var stopwatch = Stopwatch.StartNew();
            for (var attempt = 0; attempt < MaxExtraAttempts && matchedUserId is null && stopwatch.ElapsedMilliseconds < MatchWindowMs; attempt++)
            {
                await Task.Delay(ExtraAttemptDelayMs, cancellationToken);

                var retryQuality = GrabFrame(image);
                if (retryQuality is null || retryQuality < PresenceQuality)
                    break;                       // finger already gone
                if (retryQuality < MinCaptureQuality)
                    continue;                    // still settling

                var retryTemplate = CreateTemplate(image);
                if (retryTemplate is null)
                    continue;

                matchedUserId = Identify(retryTemplate);
            }
        }

        if (matchedUserId is null)
        {
            _logger.LogWarning("Fingerprint not recognized. Contact HR to enroll this finger.");
            SafeRaise(FingerNotRecognized);
            return Array.Empty<TerminalEvent>();
        }

        var evt = new TerminalEvent(
            EventId: $"secugen-evt-{Guid.NewGuid():N}",
            TerminalUserId: matchedUserId,
            Timestamp: DateTime.UtcNow,
            VerifyMethod: "FINGERPRINT");

        _logger.LogInformation("Matched! terminalUserId={TerminalUserId} at {Time:HH:mm:ss}",
            matchedUserId, evt.Timestamp.ToLocalTime());

        return new List<TerminalEvent> { evt };
    }

    private void EnterWaiting(DateTime now)
    {
        _touch = TouchState.WaitingForRemoval;
        _belowMinSinceUtc = null;
        _weakSinceUtc = null;
        _poorQualityRaised = false;
        _lastTouchUtc = now;
    }

    private void Release(string reason)
    {
        ResetTouchState(TouchState.Idle);
        _logger.LogInformation("{Reason}", reason);
        SafeRaise(FingerRemoved);
    }

    // Note: _lastTouchTemplate / _lastTouchUtc are kept on purpose so a bouncing finger can be recognised.
    private void ResetTouchState(TouchState state)
    {
        _touch = state;
        _belowMinSinceUtc = null;
        _weakSinceUtc = null;
        _poorQualityRaised = false;
    }

    public async Task<bool> EnrollAsync(string terminalUserId, CancellationToken cancellationToken)
    {
        if (_fpm is null)
            throw new InvalidOperationException("Device not connected. Call ConnectAsync first.");

        await _deviceLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Place finger on the reader to enroll terminalUserId={TerminalUserId}...", terminalUserId);

            var template = TryCapture(EnrollTimeoutMs, MinCaptureQuality);
            if (template is null)
            {
                _logger.LogWarning("Enrollment failed: no acceptable capture within {Timeout}ms.", EnrollTimeoutMs);
                return false;
            }

            lock (_templateSync)
            {
                _templates[terminalUserId] = template;
                SaveTemplates();
            }

            // The enrolled finger is usually still on the glass: remember it so it isn't scanned as a clock-in.
            _lastTouchTemplate = template;

            _logger.LogInformation("Enrollment successful for terminalUserId={TerminalUserId}.", terminalUserId);
            return true;
        }
        finally
        {
            EnterWaiting(DateTime.UtcNow);
            _deviceLock.Release();
        }
    }

    public async Task RemoveEnrollmentAsync(string terminalUserId)
    {
        await _deviceLock.WaitAsync();
        try
        {
            lock (_templateSync)
            {
                if (_templates.Remove(terminalUserId))
                {
                    SaveTemplates();
                    _logger.LogWarning("Rolled back local enrollment for terminalUserId={TerminalUserId} after backend registration failure.", terminalUserId);
                }
            }
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    // ----- Helpers -----

    private void SafeRaise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "A terminal event handler threw; ignoring."); }
    }

    private void Heartbeat(string message)
    {
        if ((DateTime.UtcNow - _lastHeartbeatUtc).TotalSeconds < 30) return;
        _lastHeartbeatUtc = DateTime.UtcNow;
        _logger.LogInformation("{Message}", message);
    }

    /// <summary>Grabs one frame and returns its quality (0 = empty glass), or null if the grab failed.</summary>
    private int? GrabFrame(byte[] image)
    {
        if (_fpm is null) return null;

        var error = _fpm.GetImage(image);
        if (error != (int)SGFPMError.ERROR_NONE) return null;

        var quality = 0;
        _fpm.GetImageQuality(_imageWidth, _imageHeight, image, ref quality);
        return quality;
    }

    /// <summary>Builds a fresh template (new array every call) from the frame, or null on failure.</summary>
    private byte[]? CreateTemplate(byte[] image)
    {
        if (_fpm is null) return null;

        var template = new byte[TemplateSize];
        var error = _fpm.CreateTemplate(null, image, template);
        if (error != (int)SGFPMError.ERROR_NONE)
        {
            _logger.LogWarning("CreateTemplate failed with error {Error}.", error);
            return null;
        }

        return template;
    }

    private bool Matches(byte[] a, byte[] b, int level = SameFingerLevel)
    {
        if (_fpm is null) return false;

        var matched = false;
        var error = _fpm.MatchTemplate(a, b, (SGFPMSecurityLevel)level, ref matched);
        return error == (int)SGFPMError.ERROR_NONE && matched;
    }

    // Used by enrollment only: keeps trying until a good-quality template is captured or time runs out.
    private byte[]? TryCapture(int timeoutMs, int minQuality)
    {
        if (_fpm is null) return null;

        var image = new byte[_imageWidth * _imageHeight];
        var stopwatch = Stopwatch.StartNew();

        do
        {
            var quality = GrabFrame(image);
            if (quality is not null)
            {
                if (quality > 0 && quality < minQuality)
                    _logger.LogInformation("Finger detected but image quality is low ({Quality}) — press a bit more firmly.", quality);

                if (quality >= minQuality)
                {
                    var template = CreateTemplate(image);
                    if (template is not null)
                        return template;
                }
            }
        } while (stopwatch.ElapsedMilliseconds < timeoutMs);

        return null;
    }

    // Called only while holding _deviceLock (every template mutation holds it too), so iterating is safe.
    // Collects EVERY employee that matches, then narrows with stricter levels, so with many employees
    // a finger is never given to the wrong person just because they happen to be first in the list.
    private string? Identify(byte[] captured)
    {
        if (_fpm is null) return null;

        var candidates = new List<string>();
        foreach (var (terminalUserId, storedTemplate) in _templates)
        {
            if (Matches(captured, storedTemplate, IdentifyLevel))
                candidates.Add(terminalUserId);
        }

        if (candidates.Count == 0)
            return null;

        foreach (var level in NarrowingLevels)
        {
            if (candidates.Count <= 1) break;

            var narrowed = candidates.Where(id => Matches(captured, _templates[id], level)).ToList();
            if (narrowed.Count > 0)
                candidates = narrowed;
        }

        if (candidates.Count > 1)
        {
            _logger.LogWarning("Fingerprint matched {Count} enrolled employees ({Ids}); using the first. Re-enrolling with a clearer print is recommended.",
                candidates.Count, string.Join(", ", candidates));
        }

        return candidates[0];
    }

    private void LoadTemplates()
    {
        if (!File.Exists(TemplateStorePath))
        {
            lock (_templateSync) _templates = new Dictionary<string, byte[]>();
            return;
        }

        try
        {
            var json = File.ReadAllText(TemplateStorePath);
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            var loaded = stored.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value));
            lock (_templateSync) _templates = loaded;
            _logger.LogInformation("Loaded {Count} enrolled template(s) from {Path}.", loaded.Count, TemplateStorePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load enrolled templates from {Path}; starting empty.", TemplateStorePath);
            lock (_templateSync) _templates = new Dictionary<string, byte[]>();
        }
    }

    // Caller must hold _templateSync.
    private void SaveTemplates()
    {
        var toStore = _templates.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value));
        var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(TemplateStorePath, json);
    }

    public void Dispose()
    {
        _fpm?.CloseDevice();
        _deviceLock.Dispose();
    }
}