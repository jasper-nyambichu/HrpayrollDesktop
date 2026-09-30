using System.Text.Json;
using Microsoft.Extensions.Logging;
using SecuGen.FDxSDKPro.Windows;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace HrpayrollDesktop.Terminal;

public class SecuGenTerminalAdapter : ITerminalAdapter, IDisposable
{
    private readonly ILogger<SecuGenTerminalAdapter> _logger;
    private SGFingerPrintManager? _fpm;
    private int _imageWidth;
    private int _imageHeight;

    private const int TemplateSize = 400;
    private const int MinCaptureQuality = 50;
    private const int CaptureTimeoutMs = 800;
    private const int EnrollTimeoutMs = 5000;

    private static readonly string TemplateStorePath =
        Path.Combine(AppContext.BaseDirectory, "enrolled-templates.json");

    private Dictionary<string, byte[]> _templates = new();
    private readonly object _templateSync = new();
    private int _idleTicks;

    // Guards every SDK call so the UI thread and the capture loop can never
    // touch the device at the same instant. Lock waits have timeouts so a
    // stuck UI call can't freeze capture forever.
    private readonly SemaphoreSlim _deviceLock = new(1, 1);

    public event Action? FingerCaptured;
    public event Action? FingerNotRecognized;

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
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    public async Task<IReadOnlyList<TerminalEvent>> PollEventsAsync(CancellationToken cancellationToken)
    {
        if (_fpm is null)
            throw new InvalidOperationException("Device not connected. Call ConnectAsync first.");

        if (EnrolledCount == 0)
        {
            LogIdleHeartbeat("No employees enrolled yet — nothing to match against.");
            return Array.Empty<TerminalEvent>();
        }

        if (!await _deviceLock.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken))
            return Array.Empty<TerminalEvent>();

        try
        {
            var template = TryCapture(CaptureTimeoutMs, MinCaptureQuality);
            if (template is null)
            {
                LogIdleHeartbeat("Ready — place your finger on the reader to clock in or out.");
                return Array.Empty<TerminalEvent>();
            }

            _logger.LogInformation("Finger detected — matching...");
            SafeRaise(FingerCaptured);

            var matchedUserId = IdentifyTemplate(template);
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
                VerifyMethod: "FINGERPRINT"
            );

            _logger.LogInformation("Matched! terminalUserId={TerminalUserId} at {Time:HH:mm:ss}",
                matchedUserId, evt.Timestamp.ToLocalTime());

            return new List<TerminalEvent> { evt };
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    public async Task<bool> EnrollAsync(string terminalUserId, CancellationToken cancellationToken)
    {
        if (_fpm is null)
            throw new InvalidOperationException("Device not connected. Call ConnectAsync first.");

        await _deviceLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Place finger on the reader to enroll terminalUserId={TerminalUserId}...", terminalUserId);

            var template = TryCapture(EnrollTimeoutMs, MinCaptureQuality, retryUntilTimeout: true);
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

            _logger.LogInformation("Enrollment successful for terminalUserId={TerminalUserId}.", terminalUserId);
            return true;
        }
        finally
        {
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

    private void SafeRaise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "A terminal event handler threw; ignoring."); }
    }

    private void LogIdleHeartbeat(string message)
    {
        _idleTicks++;
        if (_idleTicks % 10 == 1)
            _logger.LogInformation("{Message}", message);
    }

    private byte[]? TryCapture(int timeoutMs, int minQuality, bool retryUntilTimeout = false)
    {
        if (_fpm is null) return null;

        var image = new byte[_imageWidth * _imageHeight];
        var template = new byte[TemplateSize];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        do
        {
            var captureError = _fpm.GetImage(image);
            if (captureError == (int)SGFPMError.ERROR_NONE)
            {
                var quality = 0;
                _fpm.GetImageQuality(_imageWidth, _imageHeight, image, ref quality);

                if (quality > 0 && quality < minQuality)
                    _logger.LogInformation("Finger detected but image quality is low ({Quality}) — press a bit more firmly.", quality);

                if (quality >= minQuality)
                {
                    var templateError = _fpm.CreateTemplate(null, image, template);
                    if (templateError == (int)SGFPMError.ERROR_NONE)
                        return template;

                    _logger.LogWarning("CreateTemplate failed with error {Error}.", templateError);
                }
            }

            if (!retryUntilTimeout)
                break;

        } while (stopwatch.ElapsedMilliseconds < timeoutMs);

        return null;
    }

    // Called only from PollEventsAsync, which holds _deviceLock. Every
    // template mutation also holds _deviceLock, so iterating here is safe.
    private string? IdentifyTemplate(byte[] capturedTemplate)
    {
        if (_fpm is null) return null;

        foreach (var (terminalUserId, storedTemplate) in _templates)
        {
            var matched = false;
            var matchError = _fpm.MatchTemplate(storedTemplate, capturedTemplate, (SGFPMSecurityLevel)3, ref matched);

            if (matchError == (int)SGFPMError.ERROR_NONE && matched)
                return terminalUserId;
        }

        return null;
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