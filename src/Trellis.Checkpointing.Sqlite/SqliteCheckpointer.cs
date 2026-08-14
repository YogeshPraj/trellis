using System.Text.Json;
using Microsoft.Data.Sqlite;
using Trellis.Graph;
using Trellis.Graph.Checkpointing;

namespace Trellis.Checkpointing.Sqlite;

/// <summary>
/// Durable <see cref="ICheckpointer{TState}"/> backed by SQLite. State is serialized as JSON,
/// so <typeparamref name="TState"/> must round-trip through System.Text.Json.
/// The checkpoint table is created automatically on first use.
/// </summary>
public sealed class SqliteCheckpointer<TState> : ICheckpointer<TState>
{
    private const string TableName = "trellis_checkpoints";

    private readonly string _connectionString;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly int? _maxCheckpointsPerThread;
    private volatile bool _initialized;

    /// <param name="connectionString">A Microsoft.Data.Sqlite connection string, e.g. <c>Data Source=checkpoints.db</c>.</param>
    /// <param name="jsonOptions">Optional serializer options for the state payload.</param>
    /// <param name="maxCheckpointsPerThread">
    /// Retention: after each save, checkpoints beyond the newest N for that thread are
    /// pruned so the database doesn't grow forever. Default 100; null disables pruning.
    /// </param>
    public SqliteCheckpointer(
        string connectionString,
        JsonSerializerOptions? jsonOptions = null,
        int? maxCheckpointsPerThread = 100)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        if (maxCheckpointsPerThread is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCheckpointsPerThread));
        }
        _connectionString = connectionString;
        _jsonOptions = jsonOptions;
        _maxCheckpointsPerThread = maxCheckpointsPerThread;
    }

    /// <summary>Creates a checkpointer storing its database at <paramref name="filePath"/>.</summary>
    public static SqliteCheckpointer<TState> FromFile(
        string filePath,
        JsonSerializerOptions? jsonOptions = null,
        int? maxCheckpointsPerThread = 100)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        return new SqliteCheckpointer<TState>(
            new SqliteConnectionStringBuilder { DataSource = filePath }.ToString(),
            jsonOptions,
            maxCheckpointsPerThread);
    }

    public async Task SaveAsync(Checkpoint<TState> checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {TableName} (thread_id, step, next_node, state_json) VALUES ($thread, $step, $next, $state)";
        command.Parameters.AddWithValue("$thread", checkpoint.ThreadId);
        command.Parameters.AddWithValue("$step", checkpoint.Step);
        command.Parameters.AddWithValue("$next", checkpoint.NextNode);
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(checkpoint.State, _jsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (_maxCheckpointsPerThread is int keep)
        {
            SqliteCommand prune = connection.CreateCommand();
            prune.CommandText =
                $"""
                 DELETE FROM {TableName} WHERE thread_id = $thread AND id NOT IN
                     (SELECT id FROM {TableName} WHERE thread_id = $thread ORDER BY id DESC LIMIT $keep)
                 """;
            prune.Parameters.AddWithValue("$thread", checkpoint.ThreadId);
            prune.Parameters.AddWithValue("$keep", keep);
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Checkpoint<TState>?> LoadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT step, next_node, state_json FROM {TableName} WHERE thread_id = $thread ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$thread", threadId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return Read(threadId, reader);
    }

    public async Task<IReadOnlyList<Checkpoint<TState>>> GetHistoryAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT step, next_node, state_json FROM {TableName} WHERE thread_id = $thread ORDER BY id ASC";
        command.Parameters.AddWithValue("$thread", threadId);

        List<Checkpoint<TState>> history = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(Read(threadId, reader));
        }
        return history;
    }

    private Checkpoint<TState> Read(string threadId, SqliteDataReader reader)
    {
        TState state = JsonSerializer.Deserialize<TState>(reader.GetString(2), _jsonOptions)
            ?? throw new InvalidOperationException($"Checkpoint state for thread '{threadId}' deserialized to null.");
        return new Checkpoint<TState>(threadId, reader.GetInt32(0), reader.GetString(1), state);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // busy_timeout is per-connection: concurrent writers wait instead of failing
        // immediately with SQLITE_BUSY.
        SqliteCommand busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout = 5000;";
        await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (!_initialized)
        {
            SqliteCommand create = connection.CreateCommand();
            create.CommandText =
                $"""
                 PRAGMA journal_mode = WAL;
                 CREATE TABLE IF NOT EXISTS {TableName} (
                     id INTEGER PRIMARY KEY AUTOINCREMENT,
                     thread_id TEXT NOT NULL,
                     step INTEGER NOT NULL,
                     next_node TEXT NOT NULL,
                     state_json TEXT NOT NULL,
                     created_at TEXT NOT NULL DEFAULT (datetime('now'))
                 );
                 CREATE INDEX IF NOT EXISTS ix_{TableName}_thread ON {TableName} (thread_id, id);
                 """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }

        return connection;
    }
}
