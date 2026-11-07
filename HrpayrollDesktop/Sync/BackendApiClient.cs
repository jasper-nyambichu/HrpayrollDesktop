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
    // The console enrols one finger per employee.
    private const short PrimaryFingerSlot = 1;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<BackendApiClient> _logger;
    private readonly StaffTokenStore _staffTokens;
    private DeviceIdentity? _deviceIdentity;

    public BackendApiClient(HttpClient httpClient, IOptions<AgentSettings> settings, ILogger<BackendApiClient> logger, StaffTokenStore staffTokens)
    {
        _logger = logger;
        _staffTokens = staffTokens;
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

    /// <summary>Keeps this sign-in alive past the server's 15 minute token while the console is open.</summary>
    public void BeginStaffSession(AuthResponse session) => _staffTokens.Set(session);

    public void EndStaffSession() => _staffTokens.Clear();

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var request = new LoginRequest(username, password);
        var response = await _httpClient.PostAsJsonAsync("/auth/login", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Login failed: {StatusCode} {Body}", response.StatusCode, body);

            // Only a real refusal of the credentials may blame them. A server or tunnel fault says so instead.
            var (fromBackend, message) = ReadBackendError(body);
            return LoginResult.Failure((int)response.StatusCode switch
            {
                401 => "Invalid username or password.",
                >= 500 => "The server isn't available right now. Your username and password were not checked. Please try again shortly.",
                _ when fromBackend => FriendlyMessages.Translate(message),
                _ => "The server isn't available right now. Your username and password were not checked. Please try again shortly.",
            });
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<AuthResponse>>(body, WebJson);

        if (envelope is null || !envelope.Success || envelope.Data is null)
        {
            _logger.LogWarning("Backend reported login failure: {Message}", envelope?.Message);
            return LoginResult.Failure(envelope?.Message ?? "Login failed.");
        }

        return LoginResult.Ok(envelope.Data);
    }

    /// <summary>
    /// Signs in the Branch Manager or Back Office whose finger this PC just recognised. The PC proves itself with
    /// its reader key (sent by the device handler); the server checks the finger belongs to that login, at this branch.
    /// </summary>
    public async Task<LoginResult> StaffFingerprintLoginAsync(string terminalUserId, string role, CancellationToken cancellationToken)
    {
        if (_deviceIdentity is null)
            return LoginResult.Failure("This computer isn't activated yet. A Back Office must sign in here once with the password.");

        var response = await _httpClient.PostAsJsonAsync("/attendance/biometric/staff-login", new { terminalUserId, role }, WebJson, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Fingerprint sign-in failed: {StatusCode} {Body}", response.StatusCode, body);
            var (fromBackend, message) = ReadBackendError(body);
            return LoginResult.Failure((int)response.StatusCode switch
            {
                404 => "This server doesn't support fingerprint sign-in yet. Sign in with your password.",
                >= 500 => "The server isn't available right now. Please try again shortly.",
                _ when fromBackend => FriendlyMessages.Translate(message),
                _ => "The server isn't available right now. Please try again shortly.",
            });
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<AuthResponse>>(body, WebJson);
        return envelope?.Data is { } session && envelope.Success
            ? LoginResult.Ok(session)
            : LoginResult.Failure(envelope?.Message ?? "Sign-in failed.");
    }

    // ----- Forgotten password: the same three steps as the web sign-in pages -----

    /// <summary>
    /// Asks for a 6-digit code to be emailed to the account's address. The server answers the same way for any
    /// username or email, so success only means the request arrived. Null on success, otherwise what to show.
    /// </summary>
    public async Task<string?> RequestPasswordResetCodeAsync(string identifier, CancellationToken cancellationToken)
    {
        var (_, error) = await PostAnonymousAsync<JsonElement>("/auth/forgot-password", new { identifier }, cancellationToken);
        return error;
    }

    /// <summary>Checks the emailed code. Returns the short-lived token that allows setting a new password.</summary>
    public async Task<(string? ResetToken, string? Error)> VerifyPasswordResetCodeAsync(string identifier, string code, CancellationToken cancellationToken)
    {
        var (data, error) = await PostAnonymousAsync<ResetTokenData>("/auth/verify-reset-code", new { identifier, code }, cancellationToken);
        if (error is null && string.IsNullOrEmpty(data?.ResetToken))
            return (null, "The server didn't confirm the code. Please try again.");
        return (data?.ResetToken, error);
    }

    /// <summary>Sets the new password. Null on success, otherwise what to show.</summary>
    public async Task<string?> ResetPasswordAsync(string resetToken, string newPassword, CancellationToken cancellationToken)
    {
        var (_, error) = await PostAnonymousAsync<JsonElement>("/auth/reset-password", new { resetToken, newPassword }, cancellationToken);
        return error;
    }

    private sealed record ResetTokenData(string ResetToken);

    private async Task<(T? Data, string? Error)> PostAnonymousAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(path, body, WebJson, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Never the body: it can hold the code.
            _logger.LogWarning("{Path} failed: {StatusCode}", path, response.StatusCode);
            var (fromBackend, message) = ReadBackendError(text);
            if ((int)response.StatusCode == 404 && !fromBackend)
                return (default, "The server doesn't offer password resets yet. Ask your administrator to update it.");
            if ((int)response.StatusCode == 429)
                return (default, "Too many tries. Wait a few minutes, then try again.");
            return (default, fromBackend && (int)response.StatusCode < 500
                ? message
                : "The server isn't available right now. Please try again shortly.");
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(text, WebJson);
        return (envelope is null ? default : envelope.Data, null);
    }

    /// <summary>Everyone at the branch. Null when it can't be loaded, so that is never mistaken for an empty branch.</summary>
    public async Task<List<EmployeeSummary>?> GetEmployeesByBranchAsync(long branchId, string staffAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/employees/by-branch/{branchId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load employees for branch {BranchId}: {StatusCode} {Body}", branchId, response.StatusCode, body);
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<EmployeeSummary>>>(body, WebJson);
        return envelope?.Data;
    }

    /// <summary>
    /// What was recorded at this branch over the period, from every reader it has. Null when it can't be loaded.
    /// </summary>
    public async Task<List<AttendanceRecord>?> GetBranchAttendanceAsync(long branchId, DateTime from, DateTime to, string staffAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/attendance/branch/{branchId}?start={from:yyyy-MM-dd}&end={to:yyyy-MM-dd}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load attendance for branch {BranchId}: {StatusCode} {Body}", branchId, response.StatusCode, body);
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<AttendanceRecord>>>(body, WebJson);
        return envelope?.Data;
    }

    /// <summary>
    /// Lunch breaks and temporary check-outs recorded at this branch on one day, with who was late. Null when they can't be loaded.
    /// </summary>
    public async Task<List<AttendanceDayRecord>?> GetBranchDayRecordsAsync(long branchId, DateTime day, string staffAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/attendance/dashboard/records?branchId={branchId}&start={day:yyyy-MM-dd}&end={day:yyyy-MM-dd}&size=200");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<ApiResponse<AttendanceDayPage>>(body, WebJson)?.Data?.Content;
    }

    /// <summary>
    /// Saves a finger to the company database and links the terminal id to the employee in one step, so every
    /// branch PC can recognise them after a move. Also used to back up a template this PC already holds.
    /// </summary>
    public Task<SyncResult> SaveFingerprintAsync(long employeeId, string terminalUserId, byte[] template, string staffAccessToken, CancellationToken cancellationToken)
    {
        if (_deviceIdentity is null)
            throw new InvalidOperationException("Cannot save the fingerprint: device credentials have not been configured.");

        return SendStaffAsync(HttpMethod.Post, "/attendance/enrollment/fingerprints", staffAccessToken,
            new FingerprintSaveBody(employeeId, _deviceIdentity.Serial, terminalUserId, PrimaryFingerSlot, Convert.ToBase64String(template), null),
            cancellationToken);
    }

    /// <summary>The readers registered to a branch. Null when the list can't be loaded.</summary>
    public async Task<List<BranchDevice>?> GetDevicesByBranchAsync(long branchId, string staffAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/attendance/devices/by-branch/{branchId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load the branch's readers: {StatusCode} {Body}", (int)response.StatusCode, body);
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<BranchDevice>>>(body, WebJson);
        return envelope?.Data;
    }

    /// <summary>
    /// Branch Manager only: checks the ticked employees in now, at the server's time, for when the reader fails.
    /// Returns one result per employee, or the server's refusal as Error.
    /// </summary>
    public async Task<(List<ManualCheckInResult>? Results, string? Error)> ManualCheckInAsync(
        IReadOnlyCollection<long> employeeIds, string staffAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/attendance/manual-check-in");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffAccessToken);
        request.Content = JsonContent.Create(new ManualCheckInBody(employeeIds), mediaType: null, options: WebJson);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Manual check-in failed: {StatusCode} {Body}", (int)response.StatusCode, body);
            var (fromBackend, message) = ReadBackendError(body);
            return (null, (int)response.StatusCode switch
            {
                403 => "Only a Branch Manager can check employees in by hand.",
                404 when !fromBackend => "The server doesn't offer manual check-in yet. Ask your administrator to update it.",
                _ when fromBackend => FriendlyMessages.Translate(message),
                _ => "The server isn't available right now. Nobody was checked in. Please try again shortly.",
            });
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<List<ManualCheckInResult>>>(body, WebJson);
        return envelope?.Data is { } results ? (results, null) : (null, envelope?.Message ?? "Manual check-in failed.");
    }

    // ------------------------------------------------------------------ device-token calls

    public async Task<SyncResult> SyncEventAsync(string eventId, string terminalUserId, DateTime timestamp, string verifyMethod, string eventType,
        DateTime? expectedBackUtc, string? reason, CancellationToken cancellationToken)
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
            TimestampUtc: timestamp.ToUniversalTime(),      // exact instant; the server converts it to company time
            ExpectedBackAtUtc: expectedBackUtc?.ToUniversalTime(),
            Reason: reason
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
    /// Tells the server this PC is running, whether its reader is plugged in, and how many scans it still holds,
    /// so the web shows an unplugged reader or a quiet PC. True when the server took it.
    /// </summary>
    public async Task<bool> SendHeartbeatAsync(bool readerConnected, string appVersion, int pendingScans, CancellationToken cancellationToken)
    {
        if (_deviceIdentity is null) return false;
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/attendance/biometric/heartbeat",
                new { readerConnected, appVersion, pendingScans }, WebJson, cancellationToken);
            return response.IsSuccessStatusCode;   // an older server answers 404: nothing to do
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the server still accepts this PC's reader key: false when it refuses it (the reader was set up on
    /// another PC since, which issues a new key), null when that can't be told (offline, server fault).
    /// </summary>
    public async Task<bool?> CheckDeviceCredentialsAsync(CancellationToken cancellationToken)
    {
        if (_deviceIdentity is null) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/attendance/biometric/mappings");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return true;
            if ((int)response.StatusCode is 401 or 403)
            {
                var (fromBackend, _) = ReadBackendError(await response.Content.ReadAsStringAsync(cancellationToken));
                return fromBackend ? false : null;
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
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

    /// <summary>
    /// The fingerprints of everyone who works at this reader's branch right now, plus the whole roster. This is
    /// how a moved employee reaches their new branch's reader. Null when the server can't be asked.
    /// </summary>
    public async Task<FingerprintSyncBundle?> GetFingerprintsAsync(CancellationToken cancellationToken)
    {
        if (_deviceIdentity is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/attendance/biometric/fingerprints");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var (_, message) = ReadBackendError(body);
            _logger.LogWarning("Could not load fingerprints: {StatusCode} {Message}", (int)response.StatusCode, message);
            return null;
        }

        var envelope = JsonSerializer.Deserialize<ApiResponse<FingerprintSyncBundle>>(body, WebJson);
        return envelope?.Data;
    }

    // ------------------------------------------------------------------ back office: damages (JWT)

    /// <summary>
    /// Registers this reader for the signed-in Back Office's branch. The token in the result is shown
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
    // status null = every damage at this branch, whatever its state.
    public async Task<List<DamageItem>?> GetDamagesAsync(string? status, string staffAccessToken, CancellationToken cancellationToken)
    {
        var path = status is null ? "/damages" : $"/damages?status={Uri.EscapeDataString(status)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
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
    DateTime TimestampUtc,
    DateTime? ExpectedBackAtUtc,
    string? Reason
);

public record ApiResponse<T>(bool Success, string? Message, T? Data, DateTime Timestamp);

public record DeviceMapping(
    string TerminalUserId,
    long EmployeeId,
    string? EmployeeName,
    string? EmployeeNumber,
    string? Position,
    string? BranchName,
    string? StaffRole = null);   // BRANCH_MANAGER or BACK_OFFICE when this employee is also that login

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