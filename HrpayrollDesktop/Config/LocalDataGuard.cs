using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HrpayrollDesktop.Config;

/// <summary>
/// Keeps this PC's local data (its activation key, the branch's names and fingerprints, the scan log and the
/// scans waiting to be sent) tied to the one organisation server it belongs to.
///
/// A PC that was set up for one organisation (or a test branch) and is then installed for another must not
/// carry anything over: the old key is unknown to the new server, the old branch's names would show, and the
/// sign-in would be refused. So at every start the data folder's owner (<c>data-owner.json</c>) is compared
/// with the server this PC is configured for, and on any difference all organisation data is removed before
/// the app reads any of it. The PC then starts exactly like a new one: a Back Office or Branch Manager of the
/// new organisation signs in and activates it for their branch.
/// </summary>
public static class LocalDataGuard
{
    private const string OwnerFile = "data-owner.json";

    /// <summary>Everything that belongs to one organisation. server.json (which server to use) is kept.</summary>
    private static readonly string[] OrganisationFiles =
    {
        "device-credentials.dat",
        "local-attendance.db", "local-attendance.db-wal", "local-attendance.db-shm",
        "offline-queue.db", "offline-queue.db-wal", "offline-queue.db-shm",
        "enrolled-templates.json", "backed-up-templates.json",
        "app-busy",
    };

    public sealed record Result(bool Cleared, string Reason);

    private sealed record Owner(string Server, DateTime SinceUtc);

    /// <summary>
    /// Makes sure the local data belongs to <paramref name="server"/>, clearing it if not. Data from before this
    /// check existed (no owner recorded yet) is kept only if the server still accepts this PC's key; when the
    /// server can't be reached, it is kept for now and checked again at the next start.
    /// </summary>
    public static async Task<Result> EnsureBelongsToAsync(string server, Func<CancellationToken, Task<bool?>> serverAcceptsSavedKey)
    {
        var wanted = Normalise(server);
        var owner = ReadOwner();

        if (owner is not null)
        {
            if (Normalise(owner.Server) == wanted) return new Result(false, "same server");
            Clear();
            WriteOwner(wanted);
            return new Result(true, $"this PC was set up for {owner.Server}, now {wanted}");
        }

        // No owner yet: a new PC, or data from an earlier version.
        if (!HasOrganisationData())
        {
            WriteOwner(wanted);
            return new Result(false, "new PC");
        }

        if (!File.Exists(AppPaths.Data("device-credentials.dat")))
        {
            // Never activated: its scans can still be sent once someone activates it, so they are kept.
            // (Leftovers of an uninstalled copy are removed by the installer, which knows that case for certain.)
            WriteOwner(wanted);
            return new Result(false, "not activated yet");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        bool? accepted;
        try { accepted = await serverAcceptsSavedKey(timeout.Token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { accepted = null; }

        switch (accepted)
        {
            case true:
                WriteOwner(wanted);
                return new Result(false, "key accepted by this server");
            case false:
                Clear();
                WriteOwner(wanted);
                return new Result(true, "the saved key belongs to another server or branch set-up");
            default:
                return new Result(false, "server not reachable; checked again at the next start");
        }
    }

    /// <summary>Removes all organisation data now (used when the server refuses this PC's key at sign-in).</summary>
    public static void ClearNow(string server)
    {
        Clear();
        WriteOwner(Normalise(server));
    }

    public static string Normalise(string? url) => (url ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();

    private static bool HasOrganisationData() =>
        OrganisationFiles.Any(name => File.Exists(AppPaths.Data(name)));

    private static void Clear()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();   // no pooled handle may keep a database file open
        foreach (var name in OrganisationFiles)
        {
            var path = AppPaths.Data(name);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* in use by nothing at start-up; ignore a stray lock */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Owner? ReadOwner()
    {
        try
        {
            var path = AppPaths.Data(OwnerFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<Owner>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteOwner(string server)
    {
        try { File.WriteAllText(AppPaths.Data(OwnerFile), JsonSerializer.Serialize(new Owner(server, DateTime.UtcNow))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
