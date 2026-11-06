using System;
using System.IO;

namespace HrpayrollDesktop.Config;

/// <summary>
/// Tells the automatic updater (installer\updater\update.ps1, run by Windows as SYSTEM) when someone is signed in,
/// so an update never closes the app in the middle of their work. While busy, the "app-busy" file in the data folder
/// is kept fresh; the updater waits while it is less than 10 minutes old, so a file left behind by a crash stops
/// blocking updates by itself.
/// </summary>
public static class UpdateGate
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(1);
    private static DateTime _lastWriteUtc = DateTime.MinValue;

    public static void Report(bool busy)
    {
        try
        {
            var path = AppPaths.Data("app-busy");
            if (busy)
            {
                if (DateTime.UtcNow - _lastWriteUtc < Refresh && File.Exists(path)) return;
                File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
                _lastWriteUtc = DateTime.UtcNow;
            }
            else if (_lastWriteUtc != DateTime.MinValue || File.Exists(path))
            {
                File.Delete(path);
                _lastWriteUtc = DateTime.MinValue;
            }
        }
        catch (Exception)
        {
            // Only a courtesy to the updater: it must never disturb the app.
        }
    }
}
