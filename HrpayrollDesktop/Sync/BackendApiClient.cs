using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
namespace HrpayrollDesktop.Sync;

// FROZEN CONTRACT — confirmed working against the live backend on
// 2026-09-24. Do not change field names or shapes here without updating
// BiometricSyncController/BiometricSyncRequest on the backend FIRST and
// confirming it end-to-end.
public class BackendApiClient
{

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

        var envelope = JsonSerializer.Deserialize<ApiResponse<AuthResponse>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

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

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<EmployeeSummary>>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return envelope?.Data ?? new List<EmployeeSummary>();
    }

    private readonly HttpClient _httpClient;
    private readonly ILogger<BackendApiClient> _logger;
    private DeviceIdentity? _deviceIdentity;

    public BackendApiClient(HttpClient httpClient, IOptions<AgentSettings> settings, ILogger<BackendApiClient> logger)
    {
        _logger = logger;
        httpClient.BaseAddress = new Uri(settings.Value.BackendBaseUrl);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient = httpClient;
    }

    // The single source of truth for this machine's device identity. Set
    // once, at startup (from the DPAPI-protected store in segment 3), and
    // read from here everywhere it's needed — the header AND the request
    // body always come from the same value, so they can never drift apart
    // the way they did during today's "device serial mismatch" debugging.
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

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<AttendanceRecord>>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return envelope?.Data ?? new List<AttendanceRecord>();
    }

    public async Task<SyncResult> SyncEventAsync(string eventId, string terminalUserId, DateTime timestamp, string verifyMethod, string eventType, CancellationToken cancellationToken)
    {
        // Fail fast and loud rather than silently sending an empty serial
        // and producing a confusing 500 three layers away on the backend —
        // exactly the failure mode that cost hours today.
        if (_deviceIdentity is null)
            throw new InvalidOperationException("Cannot sync: device credentials have not been configured. Call ConfigureDeviceCredentials first.");

        var request = new SyncEventRequest(
            EventId: eventId,
            TerminalUserId: terminalUserId,
            DeviceSerial: _deviceIdentity.Serial,
            Timestamp: timestamp,
            VerifyMethod: verifyMethod,
            EventType: eventType
        );

        var response = await _httpClient.PostAsJsonAsync("/attendance/biometric/sync", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Sync failed for eventId={EventId}: {StatusCode} {Body}", request.EventId, response.StatusCode, body);
            return SyncResult.Failure(body);
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<JsonElement>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        if (envelope is null || !envelope.Success)
        {
            _logger.LogWarning("Backend reported failure for eventId={EventId}: {Message}", request.EventId, envelope?.Message);
            return SyncResult.Failure(envelope?.Message ?? "Unknown backend error");
        }

        return SyncResult.Ok();
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
            return SyncResult.Failure(body);
        }

        return SyncResult.Ok();
    }
}

// Immutable — a device's identity is set once at configuration time and
// never partially updated. Prevents exactly the class of bug where a
// serial and token silently drift apart across separate mutable fields.
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
    string EventType
);

public record EnrollmentRequest(long EmployeeId, string DeviceSerial, string TerminalUserId);
public record ApiResponse<T>(bool Success, string? Message, T? Data, DateTime Timestamp);

public record SyncResult(bool Succeeded, string? Error)
{
    public static SyncResult Ok() => new(true, null);
    public static SyncResult Failure(string error) => new(false, error);
}