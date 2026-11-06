using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HrpayrollDesktop.Local;

public record EmployeeDirectoryEntry(
    string TerminalUserId,
    long EmployeeId,
    string FullName,
    string EmployeeNumber,
    string? Position,
    string? BranchName);

public record AttendanceLogEntry(
    string EventId,
    string TerminalUserId,
    string EventType,
    DateTime TimestampUtc,
    DateTime? ExpectedBackUtc = null,
    string? Reason = null);

/// <summary>
/// Device-local data that must survive syncing: who each terminal user is
/// (so the kiosk can greet by name with no login and no network) and a
/// permanent log of scans (the sync queue deletes rows once synced).
/// </summary>
public class LocalAttendanceStore
{
    private readonly string _connectionString;
    private readonly ILogger<LocalAttendanceStore> _logger;

    public LocalAttendanceStore(ILogger<LocalAttendanceStore> logger)
    {
        _logger = logger;
        var dbPath = HrpayrollDesktop.Config.AppPaths.Data("local-attendance.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS employee_directory (
                TerminalUserId TEXT PRIMARY KEY,
                EmployeeId INTEGER NOT NULL,
                FullName TEXT NOT NULL,
                EmployeeNumber TEXT NOT NULL,
                Position TEXT,
                BranchName TEXT,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS attendance_log (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE,
                TerminalUserId TEXT NOT NULL,
                EventType TEXT NOT NULL,
                Timestamp TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_attendance_log_ts ON attendance_log (Timestamp);
            CREATE INDEX IF NOT EXISTS ix_attendance_log_user ON attendance_log (TerminalUserId, Timestamp);
            """;
        command.ExecuteNonQuery();

        // Upgrade an older database in place: nothing already logged is lost.
        EnsureLogColumn(connection, "ExpectedBackUtc");
        EnsureLogColumn(connection, "Reason");

        _logger.LogInformation("Local attendance store initialized at {ConnectionString}", _connectionString);
    }

    private static void EnsureLogColumn(SqliteConnection connection, string column)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(attendance_log);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE attendance_log ADD COLUMN {column} TEXT;";
        alter.ExecuteNonQuery();
    }

    public void UpsertEmployees(IEnumerable<EmployeeDirectoryEntry> entries)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();

        foreach (var e in entries)
        {
            var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO employee_directory
                    (TerminalUserId, EmployeeId, FullName, EmployeeNumber, Position, BranchName, UpdatedAt)
                VALUES
                    ($terminalUserId, $employeeId, $fullName, $employeeNumber, $position, $branchName, $updatedAt)
                ON CONFLICT(TerminalUserId) DO UPDATE SET
                    EmployeeId = excluded.EmployeeId,
                    FullName = excluded.FullName,
                    EmployeeNumber = excluded.EmployeeNumber,
                    Position = excluded.Position,
                    BranchName = excluded.BranchName,
                    UpdatedAt = excluded.UpdatedAt;
                """;
            command.Parameters.AddWithValue("$terminalUserId", e.TerminalUserId);
            command.Parameters.AddWithValue("$employeeId", e.EmployeeId);
            command.Parameters.AddWithValue("$fullName", e.FullName);
            command.Parameters.AddWithValue("$employeeNumber", e.EmployeeNumber);
            command.Parameters.AddWithValue("$position", (object?)e.Position ?? DBNull.Value);
            command.Parameters.AddWithValue("$branchName", (object?)e.BranchName ?? DBNull.Value);
            command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Makes the directory exactly this list, so nobody from another branch or an earlier setup lingers.</summary>
    public void ReplaceEmployees(IReadOnlyCollection<EmployeeDirectoryEntry> entries)
    {
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM employee_directory;";
            command.ExecuteNonQuery();
        }

        UpsertEmployees(entries);
    }

    /// <summary>Drops anyone no longer on the branch's roster, for a kiosk that has nobody signed in.</summary>
    public void RemoveEmployeesNotIn(IReadOnlyCollection<long> roster)
    {
        var stillHere = new HashSet<long>(roster);
        foreach (var entry in GetAllEmployees())
        {
            if (stillHere.Contains(entry.EmployeeId)) continue;

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM employee_directory WHERE TerminalUserId = $id;";
            command.Parameters.AddWithValue("$id", entry.TerminalUserId);
            command.ExecuteNonQuery();
        }
    }

    public EmployeeDirectoryEntry? GetEmployee(string terminalUserId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TerminalUserId, EmployeeId, FullName, EmployeeNumber, Position, BranchName
            FROM employee_directory WHERE TerminalUserId = $id;
            """;
        command.Parameters.AddWithValue("$id", terminalUserId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEmployee(reader) : null;
    }

    /// <summary>Every employee the console has loaded for this branch (enrolled or not), A-Z.</summary>
    public List<EmployeeDirectoryEntry> GetAllEmployees()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TerminalUserId, EmployeeId, FullName, EmployeeNumber, Position, BranchName
            FROM employee_directory ORDER BY FullName COLLATE NOCASE;
            """;

        var results = new List<EmployeeDirectoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(ReadEmployee(reader));
        return results;
    }

    public void AppendLog(string eventId, string terminalUserId, string eventType, DateTime timestampUtc,
        DateTime? expectedBackUtc = null, string? reason = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO attendance_log (EventId, TerminalUserId, EventType, Timestamp, ExpectedBackUtc, Reason)
            VALUES ($eventId, $terminalUserId, $eventType, $timestamp, $expectedBack, $reason);
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$terminalUserId", terminalUserId);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$timestamp", timestampUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$expectedBack", expectedBackUtc is { } back ? back.ToUniversalTime().ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public AttendanceLogEntry? GetLastEventToday(string terminalUserId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, TerminalUserId, EventType, Timestamp, ExpectedBackUtc, Reason
            FROM attendance_log
            WHERE TerminalUserId = $id AND Timestamp >= $start
            ORDER BY Timestamp DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", terminalUserId);
        command.Parameters.AddWithValue("$start", DateTime.Today.ToUniversalTime().ToString("O"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    /// <summary>Whether this person has a scan of this kind today, for example lunch, which is once a day.</summary>
    public bool HasEventToday(string terminalUserId, string eventType)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM attendance_log
            WHERE TerminalUserId = $id AND Timestamp >= $start AND EventType = $type
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", terminalUserId);
        command.Parameters.AddWithValue("$start", DateTime.Today.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$type", eventType);
        return command.ExecuteScalar() is not null;
    }

    public AttendanceLogEntry? GetLatestClockInToday(string terminalUserId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, TerminalUserId, EventType, Timestamp, ExpectedBackUtc, Reason
            FROM attendance_log
            WHERE TerminalUserId = $id AND Timestamp >= $start AND EventType = 'CLOCK_IN'
            ORDER BY Timestamp DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", terminalUserId);
        command.Parameters.AddWithValue("$start", DateTime.Today.ToUniversalTime().ToString("O"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    /// <summary>Today's scans (local calendar day), oldest first.</summary>
    public List<AttendanceLogEntry> GetTodayLog() => GetLogForDay(DateTime.Today);

    /// <summary>Scans on one local calendar day, oldest first.</summary>
    public List<AttendanceLogEntry> GetLogForDay(DateTime day) =>
        GetLogBetween(day.Date, day.Date.AddDays(1));

    /// <summary>Scans from the start of the given local day until now, oldest first.</summary>
    public List<AttendanceLogEntry> GetLogSince(DateTime fromLocalDay) =>
        GetLogBetween(fromLocalDay.Date, DateTime.Today.AddDays(1));

    public List<AttendanceLogEntry> GetLogBetween(DateTime fromLocal, DateTime toLocalExclusive)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, TerminalUserId, EventType, Timestamp, ExpectedBackUtc, Reason
            FROM attendance_log
            WHERE Timestamp >= $from AND Timestamp < $to
            ORDER BY Timestamp ASC;
            """;
        command.Parameters.AddWithValue("$from", fromLocal.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", toLocalExclusive.ToUniversalTime().ToString("O"));

        var results = new List<AttendanceLogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(ReadEntry(reader));
        return results;
    }

    private static EmployeeDirectoryEntry ReadEmployee(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5));

    private static AttendanceLogEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(5) ? null : reader.GetString(5));
}