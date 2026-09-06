namespace Optimus.Core.Memory;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

/// <summary>
/// User-defined shorthand alias mapping (e.g. "my repo" -> "Optimus_VoiceOS").
/// </summary>
public sealed record AliasRecord(
    long Id,
    string Key,
    string Value,
    DateTimeOffset CreatedAt);

/// <summary>
/// User behavioral preference setting (e.g. "preferred_voice", "default_provider").
/// </summary>
public sealed record PreferenceRecord(
    long Id,
    string Key,
    string Value,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Record of one conversational turn including user input, objective, assistant response, and tool calls.
/// </summary>
public sealed record ConversationTurnRecord(
    long Id,
    string TurnId,
    string Device,
    string Transcript,
    string? Objective,
    string? Response,
    string? ToolCallsJson,
    DateTimeOffset Timestamp);

/// <summary>
/// Periodic concise digest summarizing conversation history across a time period.
/// </summary>
public sealed record SummaryRecord(
    long Id,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string SummaryText,
    DateTimeOffset CreatedAt);

/// <summary>
/// SQLite-backed persistent memory store for Optimus Voice OS.
/// Stores aliases, preferences, conversation history, and model-generated digests in %LOCALAPPDATA%/Optimus/memory.db
/// with Write-Ahead Logging (WAL) enabled for concurrent reads.
/// </summary>
public sealed class MemoryStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _ownsConnection;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the canonical path to the SQLite database file in the local app data folder.
    /// </summary>
    public static string DefaultDatabasePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Optimus",
            "memory.db");

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryStore"/> class.
    /// </summary>
    /// <param name="connectionStringOrPath">
    /// SQLite connection string or file path. If null, defaults to <see cref="DefaultDatabasePath"/>.
    /// </param>
    public MemoryStore(string? connectionStringOrPath = null)
    {
        string connStr = ResolveConnectionString(connectionStringOrPath);
        _connection = new SqliteConnection(connStr);
        _ownsConnection = true;
        _connection.Open();
        InitializeDatabase();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryStore"/> class wrapping an existing connection.
    /// </summary>
    /// <param name="connection">Existing open SQLite connection.</param>
    /// <param name="ownsConnection">Whether this instance should dispose the connection.</param>
    public MemoryStore(SqliteConnection connection, bool ownsConnection = false)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ownsConnection = ownsConnection;
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }
        InitializeDatabase();
    }

    /// <summary>
    /// Creates an isolated in-memory SQLite store for unit testing.
    /// </summary>
    public static MemoryStore CreateInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new MemoryStore(connection, ownsConnection: true);
    }

    private static string ResolveConnectionString(string? pathOrConnectionString)
    {
        if (string.IsNullOrWhiteSpace(pathOrConnectionString))
        {
            string dbPath = DefaultDatabasePath;
            EnsureDirectoryExists(dbPath);
            return new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();
        }

        if (pathOrConnectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) ||
            pathOrConnectionString.Contains("Mode=", StringComparison.OrdinalIgnoreCase))
        {
            return pathOrConnectionString;
        }

        EnsureDirectoryExists(pathOrConnectionString);
        return new SqliteConnectionStringBuilder
        {
            DataSource = pathOrConnectionString,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void InitializeDatabase()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;

                CREATE TABLE IF NOT EXISTS aliases (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    key TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    value TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS preferences (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    key TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    value TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS conversation_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    turn_id TEXT NOT NULL,
                    device TEXT NOT NULL,
                    transcript TEXT NOT NULL,
                    objective TEXT,
                    response TEXT,
                    tool_calls_json TEXT,
                    timestamp TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_conv_turn_id ON conversation_history(turn_id);
                CREATE INDEX IF NOT EXISTS idx_conv_timestamp ON conversation_history(timestamp);

                CREATE TABLE IF NOT EXISTS summaries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    period_start TEXT NOT NULL,
                    period_end TEXT NOT NULL,
                    summary_text TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    #region Aliases

    /// <summary>
    /// Saves or updates a user-defined shorthand alias.
    /// </summary>
    public void SaveAlias(string key, string value, DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO aliases (key, value, created_at)
                VALUES (@key, @value, @created_at)
                ON CONFLICT(key) DO UPDATE SET
                    value = excluded.value,
                    created_at = excluded.created_at;
                """;
            cmd.Parameters.AddWithValue("@key", key.Trim());
            cmd.Parameters.AddWithValue("@value", value.Trim());
            cmd.Parameters.AddWithValue("@created_at", (createdAt ?? DateTimeOffset.UtcNow).ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Resolves an alias by key (case-insensitive). Returns null if not found.
    /// </summary>
    public string? ResolveAlias(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM aliases WHERE key = @key COLLATE NOCASE LIMIT 1;";
            cmd.Parameters.AddWithValue("@key", key.Trim());
            object? result = cmd.ExecuteScalar();
            return result is string val ? val : null;
        }
    }

    /// <summary>
    /// Deletes an alias if present.
    /// </summary>
    public bool DeleteAlias(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM aliases WHERE key = @key COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("@key", key.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Returns all registered aliases.
    /// </summary>
    public IReadOnlyList<AliasRecord> GetAllAliases()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, key, value, created_at FROM aliases ORDER BY key ASC;";
            using var reader = cmd.ExecuteReader();
            var list = new List<AliasRecord>();
            while (reader.Read())
            {
                list.Add(new AliasRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
            }
            return list;
        }
    }

    #endregion

    #region Preferences

    /// <summary>
    /// Sets or updates a user preference.
    /// </summary>
    public void SetPreference(string key, string value, DateTimeOffset? updatedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO preferences (key, value, updated_at)
                VALUES (@key, @value, @updated_at)
                ON CONFLICT(key) DO UPDATE SET
                    value = excluded.value,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@key", key.Trim());
            cmd.Parameters.AddWithValue("@value", value.Trim());
            cmd.Parameters.AddWithValue("@updated_at", (updatedAt ?? DateTimeOffset.UtcNow).ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Gets a user preference by key (case-insensitive). Returns defaultValue if not found.
    /// </summary>
    public string? GetPreference(string key, string? defaultValue = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return defaultValue;

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM preferences WHERE key = @key COLLATE NOCASE LIMIT 1;";
            cmd.Parameters.AddWithValue("@key", key.Trim());
            object? result = cmd.ExecuteScalar();
            return result is string val ? val : defaultValue;
        }
    }

    /// <summary>
    /// Deletes a preference if present.
    /// </summary>
    public bool DeletePreference(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM preferences WHERE key = @key COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("@key", key.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Returns all registered preferences.
    /// </summary>
    public IReadOnlyList<PreferenceRecord> GetAllPreferences()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, key, value, updated_at FROM preferences ORDER BY key ASC;";
            using var reader = cmd.ExecuteReader();
            var list = new List<PreferenceRecord>();
            while (reader.Read())
            {
                list.Add(new PreferenceRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
            }
            return list;
        }
    }

    #endregion

    #region Conversation History

    /// <summary>
    /// Saves a conversational turn to conversation_history.
    /// </summary>
    public long SaveTurn(
        string turnId,
        string device,
        string transcript,
        string? objective = null,
        string? response = null,
        string? toolCallsJson = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO conversation_history (turn_id, device, transcript, objective, response, tool_calls_json, timestamp)
                VALUES (@turn_id, @device, @transcript, @objective, @response, @tool_calls_json, @timestamp);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@turn_id", turnId.Trim());
            cmd.Parameters.AddWithValue("@device", device.Trim());
            cmd.Parameters.AddWithValue("@transcript", transcript.Trim());
            cmd.Parameters.AddWithValue("@objective", (object?)objective?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@response", (object?)response?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tool_calls_json", (object?)toolCallsJson?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@timestamp", (timestamp ?? DateTimeOffset.UtcNow).ToString("O"));

            object? idObj = cmd.ExecuteScalar();
            return idObj is long id ? id : 0;
        }
    }

    /// <summary>
    /// Saves a conversational turn record to conversation_history.
    /// </summary>
    public long SaveTurn(ConversationTurnRecord turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return SaveTurn(
            turn.TurnId,
            turn.Device,
            turn.Transcript,
            turn.Objective,
            turn.Response,
            turn.ToolCallsJson,
            turn.Timestamp);
    }

    /// <summary>
    /// Updates the response column for an existing turn by turn_id.
    /// </summary>
    public bool UpdateResponse(string turnId, string response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        ArgumentNullException.ThrowIfNull(response);

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE conversation_history SET response = @response WHERE turn_id = @turn_id;";
            cmd.Parameters.AddWithValue("@response", response.Trim());
            cmd.Parameters.AddWithValue("@turn_id", turnId.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Retrieves recent conversational turns ordered from newest to oldest.
    /// </summary>
    /// <param name="count">Maximum number of turns to return.</param>
    public IReadOnlyList<ConversationTurnRecord> GetRecentTurns(int count = 20)
    {
        if (count <= 0) return Array.Empty<ConversationTurnRecord>();

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, turn_id, device, transcript, objective, response, tool_calls_json, timestamp
                FROM conversation_history
                ORDER BY timestamp DESC, id DESC
                LIMIT @count;
                """;
            cmd.Parameters.AddWithValue("@count", count);
            using var reader = cmd.ExecuteReader();
            var list = new List<ConversationTurnRecord>();
            while (reader.Read())
            {
                list.Add(new ConversationTurnRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)));
            }
            return list;
        }
    }

    /// <summary>
    /// Retrieves conversational turns between the specified start and end timestamps.
    /// </summary>
    public IReadOnlyList<ConversationTurnRecord> GetTurnsBetween(DateTimeOffset start, DateTimeOffset end)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, turn_id, device, transcript, objective, response, tool_calls_json, timestamp
                FROM conversation_history
                WHERE timestamp >= @start AND timestamp <= @end
                ORDER BY timestamp ASC, id ASC;
                """;
            cmd.Parameters.AddWithValue("@start", start.ToString("O"));
            cmd.Parameters.AddWithValue("@end", end.ToString("O"));
            using var reader = cmd.ExecuteReader();
            var list = new List<ConversationTurnRecord>();
            while (reader.Read())
            {
                list.Add(new ConversationTurnRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)));
            }
            return list;
        }
    }

    #endregion

    #region Summaries

    /// <summary>
    /// Saves a periodic summary digest.
    /// </summary>
    public long SaveSummary(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string summaryText,
        DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryText);

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO summaries (period_start, period_end, summary_text, created_at)
                VALUES (@period_start, @period_end, @summary_text, @created_at);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@period_start", periodStart.ToString("O"));
            cmd.Parameters.AddWithValue("@period_end", periodEnd.ToString("O"));
            cmd.Parameters.AddWithValue("@summary_text", summaryText.Trim());
            cmd.Parameters.AddWithValue("@created_at", (createdAt ?? DateTimeOffset.UtcNow).ToString("O"));

            object? idObj = cmd.ExecuteScalar();
            return idObj is long id ? id : 0;
        }
    }

    /// <summary>
    /// Retrieves periodic summaries ordered from newest to oldest.
    /// </summary>
    /// <param name="count">Maximum number of summaries to retrieve.</param>
    public IReadOnlyList<SummaryRecord> GetSummaries(int count = 100)
    {
        if (count <= 0) return Array.Empty<SummaryRecord>();

        lock (_lock)
        {
            ThrowIfDisposed();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, period_start, period_end, summary_text, created_at
                FROM summaries
                ORDER BY created_at DESC, id DESC
                LIMIT @count;
                """;
            cmd.Parameters.AddWithValue("@count", count);
            using var reader = cmd.ExecuteReader();
            var list = new List<SummaryRecord>();
            while (reader.Read())
            {
                list.Add(new SummaryRecord(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
            }
            return list;
        }
    }

    #endregion

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsConnection)
            {
                _connection.Dispose();
            }
        }
    }
}
