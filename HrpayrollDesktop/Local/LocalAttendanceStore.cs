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
    DateTime TimestampUtc);

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
        var dbPath = Path.Combine(AppContext.BaseDirectory, "local-attendance.db");
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

        _logger.LogInformation("Local attendance store initialized at {ConnectionString}", _connectionString);
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
        if (!reader.Read()) return null;

        return new EmployeeDirectoryEntry(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public void AppendLog(string eventId, string terminalUserId, string eventType, DateTime timestampUtc)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO attendance_log (EventId, TerminalUserId, EventType, Timestamp)
            VALUES ($eventId, $terminalUserId, $eventType, $timestamp);
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$terminalUserId", terminalUserId);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$timestamp", timestampUtc.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }

    public AttendanceLogEntry? GetLastEventToday(string terminalUserId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, TerminalUserId, EventType, Timestamp
            FROM attendance_log
            WHERE TerminalUserId = $id AND Timestamp >= $start
            ORDER BY Timestamp DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", terminalUserId);
        command.Parameters.AddWithValue("$start", StartOfTodayUtc());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    /// <summary>Today's scans (local calendar day), oldest first.</summary>
    public List<AttendanceLogEntry> GetTodayLog()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, TerminalUserId, EventType, Timestamp
            FROM attendance_log
            WHERE Timestamp >= $start
            ORDER BY Timestamp ASC;
            """;
        command.Parameters.AddWithValue("$start", StartOfTodayUtc());

        var results = new List<AttendanceLogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(ReadEntry(reader));
        return results;
    }

    private static string StartOfTodayUtc() => DateTime.Today.ToUniversalTime().ToString("O");

    private static AttendanceLogEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}