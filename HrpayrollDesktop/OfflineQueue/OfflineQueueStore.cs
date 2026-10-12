using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;

namespace HrpayrollDesktop.OfflineQueue;

public class OfflineQueueStore
{
    private readonly string _connectionString;
    private readonly ILogger<OfflineQueueStore> _logger;

    public OfflineQueueStore(ILogger<OfflineQueueStore> logger)
    {
        _logger = logger;
        var dbPath = Path.Combine(AppContext.BaseDirectory, "offline-queue.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS queued_events (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE,
                TerminalUserId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                VerifyMethod TEXT NOT NULL,
                EventType TEXT NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                LastError TEXT,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS terminal_last_state (
                TerminalUserId TEXT PRIMARY KEY,
                LastEventType TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        // Upgrade older databases in place (nothing queued is lost).
        EnsureColumn(connection, "queued_events", "Status", "Status TEXT NOT NULL DEFAULT 'PENDING'");
        EnsureColumn(connection, "queued_events", "NextAttemptAt", "NextAttemptAt TEXT");
        EnsureColumn(connection, "queued_events", "RejectedAt", "RejectedAt TEXT");

        _logger.LogInformation("Offline queue initialized at {ConnectionString}", _connectionString);
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        var exists = false;

        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {definition};";
        alter.ExecuteNonQuery();
    }

    public void Enqueue(QueuedEvent evt)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO queued_events
                (EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, CreatedAt, Status)
            VALUES
                ($eventId, $terminalUserId, $timestamp, $verifyMethod, $eventType, $createdAt, 'PENDING');
            """;
        command.Parameters.AddWithValue("$eventId", evt.EventId);
        command.Parameters.AddWithValue("$terminalUserId", evt.TerminalUserId);
        command.Parameters.AddWithValue("$timestamp", evt.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$verifyMethod", evt.VerifyMethod);
        command.Parameters.AddWithValue("$eventType", evt.EventType);
        command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>Events that are due now, oldest first. Rejected events and events still backing off are skipped,
    /// so one bad event can never block the ones behind it.</summary>
    public List<QueuedEvent> GetPending(int maxBatchSize = 20)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, Attempts, LastError, CreatedAt
            FROM queued_events
            WHERE Status = 'PENDING'
              AND (NextAttemptAt IS NULL OR NextAttemptAt <= $now)
            ORDER BY CreatedAt ASC, Id ASC
            LIMIT $maxBatchSize;
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$maxBatchSize", maxBatchSize);

        return ReadEvents(command);
    }

    public List<QueuedEvent> GetRejected(int maxCount = 50)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, Attempts, LastError, CreatedAt
            FROM queued_events
            WHERE Status = 'REJECTED'
            ORDER BY CreatedAt DESC
            LIMIT $max;
            """;
        command.Parameters.AddWithValue("$max", maxCount);

        return ReadEvents(command);
    }

    private static List<QueuedEvent> ReadEvents(SqliteCommand command)
    {
        var results = new List<QueuedEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new QueuedEvent
            {
                Id = reader.GetInt64(0),
                EventId = reader.GetString(1),
                TerminalUserId = reader.GetString(2),
                Timestamp = DateTime.Parse(reader.GetString(3)),
                VerifyMethod = reader.GetString(4),
                EventType = reader.GetString(5),
                Attempts = reader.GetInt32(6),
                LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = DateTime.Parse(reader.GetString(8)),
            });
        }

        return results;
    }

    /// <summary>Scans still waiting to reach the server.</summary>
    public int GetPendingCount() => Count("PENDING");

    /// <summary>Scans the server refused for a business reason (kept for review, not retried).</summary>
    public int GetRejectedCount() => Count("REJECTED");

    private int Count(string status)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM queued_events WHERE Status = $status;";
        command.Parameters.AddWithValue("$status", status);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void MarkSynced(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM queued_events WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Server answered with a problem that may clear up: retry later with exponential backoff (15s … 15min).</summary>
    public void MarkRetry(long id, string error)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var read = connection.CreateCommand();
        read.CommandText = "SELECT Attempts FROM queued_events WHERE Id = $id;";
        read.Parameters.AddWithValue("$id", id);
        var attempts = Convert.ToInt32(read.ExecuteScalar() ?? 0) + 1;

        var delaySeconds = Math.Min(900, 15 * Math.Pow(2, Math.Min(attempts - 1, 6)));

        var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE queued_events
            SET Attempts = $attempts, LastError = $error, NextAttemptAt = $next
            WHERE Id = $id;
            """;
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$attempts", attempts);
        update.Parameters.AddWithValue("$error", error);
        update.Parameters.AddWithValue("$next", DateTime.UtcNow.AddSeconds(delaySeconds).ToString("O"));
        update.ExecuteNonQuery();
    }

    /// <summary>Kept for older callers.</summary>
    public void MarkFailedAttempt(long id, string error) => MarkRetry(id, error);

    /// <summary>The server refused this scan for good (business rule). Keep it for review; stop retrying.</summary>
    public void MarkRejected(long id, string error)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queued_events
            SET Status = 'REJECTED', LastError = $error, RejectedAt = $now, Attempts = Attempts + 1
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>Puts every rejected scan back in the queue (e.g. after an admin fixed the cause).</summary>
    public int RequeueRejected()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queued_events
            SET Status = 'PENDING', Attempts = 0, NextAttemptAt = NULL, RejectedAt = NULL
            WHERE Status = 'REJECTED';
            """;
        return command.ExecuteNonQuery();
    }

    public string? GetLastEventType(string terminalUserId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT LastEventType FROM terminal_last_state WHERE TerminalUserId = $terminalUserId;";
        command.Parameters.AddWithValue("$terminalUserId", terminalUserId);

        return command.ExecuteScalar() as string;
    }

    public void SetLastEventType(string terminalUserId, string eventType)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO terminal_last_state (TerminalUserId, LastEventType, UpdatedAt)
            VALUES ($terminalUserId, $eventType, $updatedAt)
            ON CONFLICT(TerminalUserId) DO UPDATE SET
                LastEventType = excluded.LastEventType,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$terminalUserId", terminalUserId);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}

public class QueuedEvent
{
    public long Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string TerminalUserId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string VerifyMethod { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}