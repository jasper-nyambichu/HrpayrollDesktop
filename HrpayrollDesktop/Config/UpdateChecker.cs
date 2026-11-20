using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HrpayrollDesktop.Config;

/// <summary>A newer version of the app published on GitHub, ready for the updater to install.</summary>
public sealed record AvailableUpdate(Version Version, string Notes);

/// <summary>
/// Finds out whether a newer version has been published, and asks the updater to install it now.
///
/// The installing itself is done by the "Dhwarsh HR\Updater" scheduled task (installer\updater\update.ps1), which
/// runs as SYSTEM, so nobody at the branch needs an administrator password, and which only installs a setup signed
/// with the release key. When the person at the PC presses "Update now", the app leaves a note for the updater
/// (update-requested), starts that task and closes; the updater installs the new version and opens the app again.
/// A Windows restart is not needed.
/// </summary>
public static class UpdateChecker
{
    private const string LatestRelease = "https://api.github.com/repos/jasper-nyambichu/HrpayrollDesktop/releases/latest";
    private const string UpdaterTask = @"\Dhwarsh HR\Updater";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>The newer published version, or null when this one is the newest (or GitHub can't be reached).</summary>
    public static async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DhwarshHR-App");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestRelease, cancellationToken));
            var root = doc.RootElement;

            var tag = (root.TryGetProperty("tag_name", out var t) ? t.GetString() : null)?.TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            if (latest <= Current) return null;

            // Only a release the updater can actually install: the setup and its signature.
            var names = root.TryGetProperty("assets", out var assets)
                ? assets.EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToHashSet()
                : new System.Collections.Generic.HashSet<string?>();
            if (!names.Contains($"DhwarshHR-Setup-{tag}.exe") || !names.Contains($"DhwarshHR-Setup-{tag}.exe.sig")) return null;

            var notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? string.Empty).Trim() : string.Empty;
            return new AvailableUpdate(latest, notes);
        }
        catch (Exception)
        {
            return null;   // offline, rate-limited or no release yet: ask again later
        }
    }

    /// <summary>
    /// Asks the updater to install the new version now. True when it was started; the caller then closes the app,
    /// and the updater opens it again once installed. False when Windows wouldn't start it (it then installs by itself
    /// at its next hourly check, and still reopens the app).
    /// </summary>
    public static bool RequestUpdateNow()
    {
        try
        {
            File.WriteAllText(AppPaths.Data("update-requested"), DateTime.UtcNow.ToString("O"));
            UpdateGate.Report(false);   // nobody is working in the app: the updater must not wait for it

            using var schtasks = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = $"/Run /TN \"{UpdaterTask}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (schtasks is null) return false;
            schtasks.WaitForExit(15000);
            return schtasks.HasExited && schtasks.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
