using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Collections.Generic;



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

        _logger.LogInformation("Offline queue initialized at {ConnectionString}", _connectionString);
    }

    public void Enqueue(QueuedEvent evt)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO queued_events
                (EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, CreatedAt)
            VALUES
                ($eventId, $terminalUserId, $timestamp, $verifyMethod, $eventType, $createdAt);
            """;
        command.Parameters.AddWithValue("$eventId", evt.EventId);
        command.Parameters.AddWithValue("$terminalUserId", evt.TerminalUserId);
        command.Parameters.AddWithValue("$timestamp", evt.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$verifyMethod", evt.VerifyMethod);
        command.Parameters.AddWithValue("$eventType", evt.EventType);
        command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public List<QueuedEvent> GetPending(int maxBatchSize = 20)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, EventId, TerminalUserId, Timestamp, VerifyMethod, EventType, Attempts, LastError, CreatedAt
            FROM queued_events
            ORDER BY CreatedAt ASC
            LIMIT $maxBatchSize;
            """;
        command.Parameters.AddWithValue("$maxBatchSize", maxBatchSize);

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

    // Simple, visible growth signal for the "unbounded queue" risk flagged
    // earlier — Worker logs a warning if this climbs too high, rather than
    // silently growing forever.
    public int GetPendingCount()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM queued_events;";
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

    public void MarkFailedAttempt(long id, string error)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queued_events
            SET Attempts = Attempts + 1, LastError = $error
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$error", error);
        command.ExecuteNonQuery();
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