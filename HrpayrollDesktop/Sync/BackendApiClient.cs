using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HrpayrollDesktop.Sync;

public class BackendApiClient
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<BackendApiClient> _logger;
    private DeviceIdentity? _deviceIdentity;

    public BackendApiClient(HttpClient httpClient, IOptions<AgentSettings> settings, ILogger<BackendApiClient> logger)
    {
        _logger = logger;
        httpClient.BaseAddress = new Uri(settings.Value.BackendBaseUrl);
        httpClient.Timeout = TimeSpan.FromSeconds(20);   // an unreachable server must not block sync for minutes
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient = httpClient;
    }

    public DeviceIdentity? Identity => _deviceIdentity;

    public void ConfigureDeviceCredentials(string deviceSerial, string deviceToken)
    {
        if (string.IsNullOrWhiteSpace(deviceSerial))
            throw new ArgumentException("Device serial cannot be blank.", nameof(deviceSerial));
        if (string.IsNullOrWhiteSpace(deviceToken))
            throw new ArgumentException("Device token cannot be blank.", nameof(deviceToken));

        _deviceIdentity = new DeviceIdentity(deviceSerial.Trim(), deviceToken.Trim());

        _httpClient.DefaultRequestHeaders.Remove("X-Device-Serial");
        _httpClient.DefaultRequestHeaders.Remove("X-Device-Token");
        _httpClient.DefaultRequestHeaders.Add("X-Device-Serial", _deviceIdentity.Serial);
        _httpClient.DefaultRequestHeaders.Add("X-Device-Token", _deviceIdentity.Token);

        _logger.LogInformation("Device credentials configured for serial={Serial}", _deviceIdentity.Serial);
    }

    // ------------------------------------------------------------------ manager (JWT) calls

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var request = new LoginRequest(username, password);
        var response = await _httpClient.PostAsJsonAsync("/auth/login", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Login failed: {StatusCode} {Body}", response.StatusCode, body);
            return LoginResult.Failure("Invalid username or password.");
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<AuthResponse>>(body, WebJson);

        if (envelope is null || !envelope.Success || envelope.Data is null)
        {
            _logger.LogWarning("Backend reported login failure: {Message}", envelope?.Message);
            return LoginResult.Failure(envelope?.Message ?? "Login failed.");
        }

        return LoginResult.Ok(envelope.Data);
    }

    public async Task<List<EmployeeSummary>> GetEmployeesByBranchAsync(long branchId, string staffAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/employees/by-branch/{branchId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load employees for branch {BranchId}: {StatusCode} {Body}", branchId, response.StatusCode, body);
            return new List<EmployeeSummary>();
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<EmployeeSummary>>>(body, WebJson);
        return envelope?.Data ?? new List<EmployeeSummary>();
    }

    public async Task<List<AttendanceRecord>> GetTodayAttendanceForBranchAsync(long branchId, string staffAccessToken, CancellationToken cancellationToken)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/attendance/branch/{branchId}?start={today}&end={today}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load today's attendance for branch {BranchId}: {StatusCode} {Body}", branchId, response.StatusCode, body);
            return new List<AttendanceRecord>();
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<AttendanceRecord>>>(body, WebJson);
        return envelope?.Data ?? new List<AttendanceRecord>();
    }

    public async Task<SyncResult> RegisterEnrollmentAsync(long employeeId, string terminalUserId, string staffAccessToken, CancellationToken cancellationToken)
    {
        if (_deviceIdentity is null)
            throw new InvalidOperationException("Cannot register enrollment: device credentials have not been configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/attendance/enrollment");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);
        request.Content = JsonContent.Create(new EnrollmentRequest(employeeId, _deviceIdentity.Serial, terminalUserId));

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Enrollment mapping push failed for employeeId={EmployeeId}: {StatusCode} {Body}", employeeId, response.StatusCode, body);
            var (fromBackend, message) = ReadBackendError(body);
            return SyncResult.Failure(message, (int)response.StatusCode, fromBackend);
        }

        return SyncResult.Ok();
    }

    // ------------------------------------------------------------------ device-token calls

    public async Task<SyncResult> SyncEventAsync(string eventId, string terminalUserId, DateTime timestamp, string verifyMethod, string eventType, CancellationToken cancellationToken)
    {
        if (_deviceIdentity is null)
            throw new InvalidOperationException("Cannot sync: device credentials have not been configured. Call ConfigureDeviceCredentials first.");

        var request = new SyncEventRequest(
            EventId: eventId,
            TerminalUserId: terminalUserId,
            DeviceSerial: _deviceIdentity.Serial,
            Timestamp: timestamp,                           // PC wall-clock time (older backends use this)
            VerifyMethod: verifyMethod,
            EventType: eventType,
            TimestampUtc: timestamp.ToUniversalTime()       // exact instant; the server converts it to company time
        );

        var response = await _httpClient.PostAsJsonAsync("/attendance/biometric/sync", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var (fromBackend, message) = ReadBackendError(body);
            _logger.LogWarning("Sync failed for eventId={EventId}: {StatusCode} {Message}", request.EventId, (int)response.StatusCode, message);
            return SyncResult.Failure(message, (int)response.StatusCode, fromBackend);
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<JsonElement>>(body, WebJson);

        if (envelope is null || !envelope.Success)
        {
            _logger.LogWarning("Backend reported failure for eventId={EventId}: {Message}", request.EventId, envelope?.Message);
            return SyncResult.Failure(envelope?.Message ?? "Unknown backend error", (int)response.StatusCode, fromBackend: true);
        }

        return SyncResult.Ok();
    }

    /// <summary>
    /// Employees mapped to this reader, with names. Returns null when the server can't be reached or
    /// refuses the device, so callers can tell "no mappings" apart from "couldn't ask".
    /// </summary>
    public async Task<List<DeviceMapping>?> GetDeviceMappingsAsync(CancellationToken cancellationToken)
    {
        if (_deviceIdentity is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/attendance/biometric/mappings");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Could not load employee mappings: {StatusCode} {Body}", (int)response.StatusCode, body);
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<DeviceMapping>>>(body, WebJson);
        return envelope?.Data;
    }

    // ------------------------------------------------------------------ back office: damages (JWT)

    /// <summary>Active branches (the Back Office works across all of them). Null when the server can't be reached.</summary>
    public async Task<List<BranchOption>?> GetBranchesAsync(string staffAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/branches");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load branches: {StatusCode} {Body}", (int)response.StatusCode, body);
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<BranchOption>>>(body, WebJson);
        return envelope?.Data;
    }

    /// <summary>
    /// Registers this reader for a branch. Back Office or Administrator. The token in the result is shown
    /// once by the server, so the caller must save it immediately. Error is the server's message.
    /// </summary>
    public async Task<(DeviceRegistration? Registration, string? Error)> RegisterDeviceAsync(
        long branchId, string deviceSerial, string staffAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/attendance/devices");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);
        request.Content = JsonContent.Create(new DeviceRegistrationBody(branchId, deviceSerial), mediaType: null, options: WebJson);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Device registration failed: {StatusCode} {Body}", (int)response.StatusCode, body);
            var (_, message) = ReadBackendError(body);
            return (null, message);
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<DeviceRegistration>>(body, WebJson);
        return envelope?.Data is { } registration
            ? (registration, null)
            : (null, envelope?.Message ?? "Registration failed.");
    }

    /// <summary>Damages with the given status (e.g. PENDING_CONSENT), newest first. Null when they can't be loaded.</summary>
    public async Task<List<DamageItem>?> GetDamagesAsync(string status, string staffAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/damages?status={Uri.EscapeDataString(status)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load damages: {StatusCode} {Body}", (int)response.StatusCode, body);
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<DamageItem>>>(body, WebJson);
        return envelope?.Data;
    }

    public Task<SyncResult> RecordDamageAsync(long employeeId, decimal amount, string description, string staffAccessToken, CancellationToken cancellationToken)
        => SendStaffAsync(HttpMethod.Post, "/damages", staffAccessToken,
            new DamageCreateRequest(employeeId, amount, description), cancellationToken);

    /// <summary>Approves a damage without the employee's fingerprint. The server requires a reason and flags it as overridden for good.</summary>
    public Task<SyncResult> OverrideDamageAsync(long damageId, string reason, string staffAccessToken, CancellationToken cancellationToken)
        => SendStaffAsync(HttpMethod.Put, $"/damages/{damageId}/override", staffAccessToken,
            new DamageOverrideBody(reason), cancellationToken);

    public Task<SyncResult> CancelDamageAsync(long damageId, string staffAccessToken, CancellationToken cancellationToken)
        => SendStaffAsync(HttpMethod.Put, $"/damages/{damageId}/cancel", staffAccessToken, null, cancellationToken);

    // ------------------------------------------------------------------ back office: days worked (JWT)

    /// <summary>
    /// Every active employee of the branch for the period: the locked figure once the period is locked,
    /// otherwise the live count from attendance. Dates are "yyyy-MM-dd". Null when it can't be loaded.
    /// </summary>
    public async Task<List<DaysWorkedItem>?> GetDaysWorkedAsync(long branchId, string periodStart, string periodEnd, string staffAccessToken, CancellationToken cancellationToken)
    {
        var path = $"/days-worked?branchId={branchId}&periodStart={Uri.EscapeDataString(periodStart)}&periodEnd={Uri.EscapeDataString(periodEnd)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load days worked: {StatusCode} {Body}", (int)response.StatusCode, body);
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<DaysWorkedItem>>>(body, WebJson);
        return envelope?.Data;
    }

    /// <summary>Freezes the branch's days for the period so payroll can read them. Only an Administrator can reopen it.</summary>
    public Task<SyncResult> LockDaysWorkedAsync(DaysWorkedLockBody body, string staffAccessToken, CancellationToken cancellationToken)
        => SendStaffAsync(HttpMethod.Post, "/days-worked/lock", staffAccessToken, body, cancellationToken);

    // ------------------------------------------------------------------ back office: fingerprint consent (device token)

    /// <summary>
    /// Reports that the employee agreed to a damage at this reader. Safe to retry: the server recognises
    /// a repeated eventId and answers with the same result.
    /// </summary>
    public async Task<SyncResult> SendConsentAsync(string eventId, string terminalUserId, long damageId, DateTime occurredAtUtc, CancellationToken cancellationToken)
    {
        if (_deviceIdentity is null)
            throw new InvalidOperationException("Cannot send consent: device credentials have not been configured.");

        var payload = new ConsentRequestBody(eventId, _deviceIdentity.Serial, terminalUserId, damageId, occurredAtUtc.ToUniversalTime());

        var response = await _httpClient.PostAsJsonAsync("/attendance/biometric/consent", payload, WebJson, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var (fromBackend, message) = ReadBackendError(body);
            _logger.LogWarning("Consent failed for damageId={DamageId}: {StatusCode} {Message}", damageId, (int)response.StatusCode, message);
            return SyncResult.Failure(message, (int)response.StatusCode, fromBackend);
        }

        return SyncResult.Ok();
    }

    // One place for the staff (JWT) calls that only need success/failure back.
    private async Task<SyncResult> SendStaffAsync(HttpMethod method, string path, string staffAccessToken, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);
        if (body is not null)
            request.Content = JsonContent.Create(body, mediaType: null, options: WebJson);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("{Method} {Path} failed: {StatusCode} {Body}", method, path, (int)response.StatusCode, responseBody);
            var (fromBackend, message) = ReadBackendError(responseBody);
            return SyncResult.Failure(message, (int)response.StatusCode, fromBackend);
        }

        return SyncResult.Ok();
    }

    // Our backend always answers with {"success":..,"message":..}. Anything else (an HTML 404/502
    // from a proxy or tunnel) is NOT a real rejection of the scan and must never discard it.
    private static (bool FromBackend, string Message) ReadBackendError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (false, "Empty response from server");

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("success", out _)
                && doc.RootElement.TryGetProperty("message", out var message))
            {
                return (true, message.GetString() ?? "Request failed");
            }
        }
        catch (JsonException)
        {
        }

        return (false, body.Length > 300 ? body[..300] : body);
    }
}

// Immutable — a device's identity is set once and never partially updated.
public sealed record DeviceIdentity(string Serial, string Token);

public class LocalDateTimeJsonConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.ffffff";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTime.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(Format, System.Globalization.CultureInfo.InvariantCulture));
}

public record SyncEventRequest(
    string EventId,
    string TerminalUserId,
    string DeviceSerial,
    [property: JsonConverter(typeof(LocalDateTimeJsonConverter))] DateTime Timestamp,
    string VerifyMethod,
    string EventType,
    DateTime TimestampUtc
);

public record EnrollmentRequest(long EmployeeId, string DeviceSerial, string TerminalUserId);
public record ApiResponse<T>(bool Success, string? Message, T? Data, DateTime Timestamp);

public record DeviceMapping(
    string TerminalUserId,
    long EmployeeId,
    string? EmployeeName,
    string? EmployeeNumber,
    string? Position,
    string? BranchName);

public record SyncResult(bool Succeeded, string? Error, int? StatusCode = null, bool FromBackend = false)
{
    public static SyncResult Ok() => new(true, null);

    public static SyncResult Failure(string error, int? statusCode = null, bool fromBackend = false)
        => new(false, error, statusCode, fromBackend);

    // Things an administrator can fix on the server (register the device, map the employee):
    // keep retrying slowly instead of discarding the scan.
    public bool IsFixableByAdmin =>
        Error is not null &&
        (Error.Contains("No employee mapped", StringComparison.OrdinalIgnoreCase)
         || Error.Contains("Unknown device", StringComparison.OrdinalIgnoreCase));

    // The server understood the scan and refused it for a business rule: retrying can't help.
    public bool IsPermanentRejection =>
        FromBackend
        && StatusCode is >= 400 and < 500 and not 401 and not 403 and not 408 and not 429
        && !IsFixableByAdmin;

    // Server/tunnel/credentials trouble: stop this sync cycle without penalising the scan.
    public bool ShouldPauseSync => StatusCode is 401 or 403 or 408 or 429 or 502 or 503 or 504;
}