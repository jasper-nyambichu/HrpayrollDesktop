using System;
using System.IO;

namespace HrpayrollDesktop.Config;

/// <summary>
/// Where the app keeps what it writes: the scan queue, the local log, this PC's reader credentials and fingerprints.
/// An installed app lives in Program Files, which ordinary users can't write to, so all of it goes to
/// C:\ProgramData\Dhwarsh HR, shared by every Windows user of the branch PC. The installer creates that folder
/// and lets every user change it.
/// </summary>
public static class AppPaths
{
    public const string FolderName = "Dhwarsh HR";

    // Files an older copy of the app kept next to the .exe.
    private static readonly string[] DataFiles =
    {
        "local-attendance.db", "offline-queue.db", "device-credentials.dat",
        "enrolled-templates.json", "backed-up-templates.json",
    };

    private static readonly Lazy<string> Directory_ = new(Prepare);

    public static string DataDirectory => Directory_.Value;

    public static string Data(string fileName) => Path.Combine(DataDirectory, fileName);

    private static string Prepare()
    {
        // Tests run against a folder of their own, never the PC's real scans.
        var testDir = Environment.GetEnvironmentVariable("DHWARSH_TEST_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(testDir))
        {
            Directory.CreateDirectory(testDir);
            return testDir;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName);
        try
        {
            Directory.CreateDirectory(dir);
            MoveOldData(dir);
            return dir;
        }
        catch (Exception)
        {
            // Last resort, so the app still starts: this user's own folder.
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    // A PC that ran the app from a plain folder keeps its activation, unsent scans and fingerprints after
    // updating. Copied, never deleted, and only when the new place has nothing yet.
    private static void MoveOldData(string dir)
    {
        var oldDir = AppContext.BaseDirectory;
        if (string.Equals(Path.GetFullPath(oldDir).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

        foreach (var name in DataFiles)
        {
            if (!File.Exists(Path.Combine(oldDir, name)) || File.Exists(Path.Combine(dir, name))) continue;

            // SQLite side files first, so the database is never seen without them.
            foreach (var file in new[] { name + "-wal", name + "-shm", name })
            {
                var from = Path.Combine(oldDir, file);
                if (!File.Exists(from)) continue;
                try { File.Copy(from, Path.Combine(dir, file), overwrite: true); } catch (IOException) { }
            }
        }
    }
}
