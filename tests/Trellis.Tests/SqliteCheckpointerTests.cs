using Trellis.Checkpointing.Sqlite;
using Trellis.Graph;

namespace Trellis.Tests;

public sealed class SqliteCheckpointerTests : IDisposable
{
    private sealed record CounterState(int Count);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"trellis-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsLatestCheckpoint()
    {
        var checkpointer = SqliteCheckpointer<CounterState>.FromFile(_dbPath);

        await checkpointer.SaveAsync(new Checkpoint<CounterState>("t1", 1, "b", new CounterState(1)));
        await checkpointer.SaveAsync(new Checkpoint<CounterState>("t1", 2, StateGraph.End, new CounterState(2)));
        await checkpointer.SaveAsync(new Checkpoint<CounterState>("other", 1, "x", new CounterState(99)));

        Checkpoint<CounterState>? latest = await checkpointer.LoadAsync("t1");

        Assert.NotNull(latest);
        Assert.Equal(2, latest.Step);
        Assert.Equal(StateGraph.End, latest.NextNode);
        Assert.Equal(2, latest.State.Count);
    }

    [Fact]
    public async Task Load_UnknownThread_ReturnsNull()
    {
        var checkpointer = SqliteCheckpointer<CounterState>.FromFile(_dbPath);

        Assert.Null(await checkpointer.LoadAsync("missing"));
    }

    [Fact]
    public async Task History_IsOrderedOldestFirst()
    {
        var checkpointer = SqliteCheckpointer<CounterState>.FromFile(_dbPath);
        await checkpointer.SaveAsync(new Checkpoint<CounterState>("t1", 1, "b", new CounterState(1)));
        await checkpointer.SaveAsync(new Checkpoint<CounterState>("t1", 2, "c", new CounterState(2)));

        IReadOnlyList<Checkpoint<CounterState>> history = await checkpointer.GetHistoryAsync("t1");

        Assert.Equal([1, 2], history.Select(c => c.Step));
    }

    [Fact]
    public async Task GraphRun_PersistsAndResumesAcrossCheckpointerInstances()
    {
        var options = new GraphRunOptions { ThreadId = "wf-1", InterruptBefore = ["send"] };

        StateGraph<CounterState> Build() => new StateGraph<CounterState>()
            .AddNode("prepare", s => s with { Count = s.Count + 1 })
            .AddNode("send", s => s with { Count = s.Count + 10 })
            .AddEdge("prepare", "send")
            .SetEntryPoint("prepare");

        // First "process": runs until the interrupt, persisting to the db file.
        GraphResult<CounterState> paused = await Build()
            .Compile(SqliteCheckpointer<CounterState>.FromFile(_dbPath))
            .RunAsync(new CounterState(0), options);
        Assert.Equal(GraphRunStatus.Interrupted, paused.Status);

        // Second "process": brand-new checkpointer instance over the same file resumes and finishes.
        GraphResult<CounterState> finished = await Build()
            .Compile(SqliteCheckpointer<CounterState>.FromFile(_dbPath))
            .RunAsync(new CounterState(0), options);

        Assert.Equal(GraphRunStatus.Completed, finished.Status);
        Assert.Equal(11, finished.FinalState.Count);
    }
}
