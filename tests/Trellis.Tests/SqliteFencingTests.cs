using Microsoft.Data.Sqlite;
using Trellis.Checkpointing.Sqlite;
using Trellis.Graph;

namespace Trellis.Tests;

/// <summary>
/// Fencing where it actually has to hold: a durable store shared by more than one process.
/// The in-memory checkpointer can only ever be wrong within one process, so it cannot
/// demonstrate the case fencing exists for.
/// </summary>
public sealed class SqliteFencingTests : IDisposable
{
    private sealed record S(int N);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"trellis-fence-{Guid.NewGuid():N}.db");

    private SqliteCheckpointer<S> NewCheckpointer() => SqliteCheckpointer<S>.FromFile(_dbPath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        // WAL leaves -wal/-shm sidecars, and clearing the pool does not guarantee the OS handle
        // is released by the time we get here. Retry rather than fail on a cleanup race.
        foreach (string path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(25);
                }
            }
        }
    }

    [Fact]
    public async Task AStalledHolder_CannotWriteAfterBeingDisplaced()
    {
        // Two checkpointers over one database file — the same shape as two app instances.
        SqliteCheckpointer<S> displaced = NewCheckpointer();
        SqliteCheckpointer<S> current = NewCheckpointer();

        await displaced.SaveFencedAsync(new Checkpoint<S>("t", 1, "b", new S(1)), fencingToken: 4);
        await current.SaveFencedAsync(new Checkpoint<S>("t", 2, "c", new S(20)), fencingToken: 5);

        // The displaced instance wakes from its pause and tries to continue. Without the fence
        // this write lands and the workflow silently forks.
        RunLeaseLostException lost = await Assert.ThrowsAsync<RunLeaseLostException>(
            () => displaced.SaveFencedAsync(new Checkpoint<S>("t", 2, "x", new S(2)), fencingToken: 4));
        Assert.Equal(4, lost.FencingToken);

        Checkpoint<S>? latest = await current.LoadAsync("t");
        Assert.Equal(20, latest!.State.N);
    }

    [Fact]
    public async Task TheCurrentHolderKeepsWriting()
    {
        SqliteCheckpointer<S> checkpointer = NewCheckpointer();

        await checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 1, "b", new S(1)), fencingToken: 5);
        await checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 2, "c", new S(2)), fencingToken: 5);
        await checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 3, "d", new S(3)), fencingToken: 6);

        Assert.Equal(3, (await checkpointer.GetHistoryAsync("t")).Count);
        Assert.Equal(3, (await checkpointer.LoadAsync("t"))!.State.N);
    }

    [Fact]
    public async Task OtherThreadsAreUnaffected()
    {
        SqliteCheckpointer<S> checkpointer = NewCheckpointer();

        await checkpointer.SaveFencedAsync(new Checkpoint<S>("busy", 1, "b", new S(1)), fencingToken: 500);
        await checkpointer.SaveFencedAsync(new Checkpoint<S>("quiet", 1, "b", new S(1)), fencingToken: 1);

        Assert.Single(await checkpointer.GetHistoryAsync("quiet"));
    }

    [Fact]
    public async Task UnfencedWritesAreAlwaysAccepted()
    {
        SqliteCheckpointer<S> checkpointer = NewCheckpointer();

        await checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 1, "b", new S(1)), fencingToken: 900);
        await checkpointer.SaveAsync(new Checkpoint<S>("t", 2, "c", new S(2)));

        // Otherwise every graph running without a distributed lease would start failing the
        // moment one fenced write had ever been made against the same database.
        Assert.Equal(2, (await checkpointer.GetHistoryAsync("t")).Count);
    }

    [Fact]
    public async Task ADatabaseFromAnEarlierVersion_GainsItsFenceColumn()
    {
        // The exact schema shipped before fencing existed.
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await connection.OpenAsync();
            SqliteCommand create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE trellis_checkpoints (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    thread_id TEXT NOT NULL,
                    step INTEGER NOT NULL,
                    next_node TEXT NOT NULL,
                    state_json TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                INSERT INTO trellis_checkpoints (thread_id, step, next_node, state_json)
                VALUES ('t', 1, 'b', '{"N":1}');
                """;
            await create.ExecuteNonQueryAsync();
        }

        SqliteCheckpointer<S> checkpointer = NewCheckpointer();

        // Upgrading must not strand existing workflows...
        Checkpoint<S>? existing = await checkpointer.LoadAsync("t");
        Assert.Equal(1, existing!.State.N);

        // ...and pre-existing rows carry fence 0, so the first real holder is admitted rather
        // than fenced out by history it could not have taken part in.
        await checkpointer.SaveFencedAsync(new Checkpoint<S>("t", 2, "c", new S(2)), fencingToken: 1);
        Assert.Equal(2, (await checkpointer.LoadAsync("t"))!.State.N);
    }

    [Fact]
    public async Task AFencedGraphRun_PersistsThroughSqlite()
    {
        var store = new Trellis.State.InMemorySharedStateStore();
        var lease = new SharedStateRunLease(store, TimeSpan.FromSeconds(30));
        SqliteCheckpointer<S> checkpointer = NewCheckpointer();

        CompiledGraph<S> graph = new StateGraph<S>()
            .AddNode("step", s => s with { N = s.N + 1 })
            .SetEntryPoint("step")
            .Compile(checkpointer, lease);

        GraphResult<S> result = await graph.RunAsync(new S(0), new GraphRunOptions { ThreadId = "t" });

        Assert.Equal(1, result.FinalState.N);

        // End to end: a real lease minted a real token and a real database honoured it.
        Checkpoint<S>? saved = await checkpointer.LoadAsync("t");
        Assert.Equal(1, saved!.State.N);
    }
}
