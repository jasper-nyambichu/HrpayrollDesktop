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