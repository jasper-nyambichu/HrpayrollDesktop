using HrpayrollDesktop.Auth;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HrpayrollDesktop.Sync;

/// <summary>The tokens of whoever is signed in to the console. Shared because the HTTP handler chain can be rebuilt.</summary>
public sealed class StaffTokenStore
{
    public readonly SemaphoreSlim RefreshGate = new(1, 1);

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public DateTime IssuedAtUtc { get; private set; }

    public void Set(AuthResponse session)
    {
        AccessToken = session.AccessToken;
        RefreshToken = session.RefreshToken;
        IssuedAtUtc = DateTime.UtcNow;
    }

    public void Clear()
    {
        AccessToken = null;
        RefreshToken = null;
    }
}

/// <summary>
/// The server's access token lasts 15 minutes, which a long enrolment session outlives. This renews it before
/// it expires and sends the newest one on every signed-in request, so callers can keep their original session.
/// </summary>
public sealed class StaffTokenRefreshHandler : DelegatingHandler
{
    private static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly StaffTokenStore _tokens;

    public StaffTokenRefreshHandler(StaffTokenStore tokens) => _tokens = tokens;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Scheme == "Bearer" && _tokens.AccessToken is not null)
        {
            await RenewIfDueAsync(request.RequestUri!, cancellationToken);

            if (_tokens.AccessToken is { } latest)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", latest);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task RenewIfDueAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _tokens.IssuedAtUtc < RenewAfter) return;

        await _tokens.RefreshGate.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow - _tokens.IssuedAtUtc < RenewAfter || _tokens.RefreshToken is not { } refreshToken) return;

            using var renew = new HttpRequestMessage(HttpMethod.Post, new Uri(requestUri, "/auth/refresh"))
            {
                Content = JsonContent.Create(new { refreshToken })
            };
            using var response = await base.SendAsync(renew, cancellationToken);
            if (!response.IsSuccessStatusCode) return;   // the original request then reports the failure as usual

            var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>(WebJson, cancellationToken);
            if (envelope?.Data is { } renewed && _tokens.RefreshToken is not null)
                _tokens.Set(renewed);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Offline or a bad reply: keep the current token. The original request reports the problem.
        }
        finally
        {
            _tokens.RefreshGate.Release();
        }
    }
}
