namespace Trellis.Graph.Leasing;

/// <summary>
/// The run no longer holds its lease, so it stopped rather than write state it does not own.
/// </summary>
/// <remarks>
/// This is an expected condition in a multi-instance deployment, not a bug: some other
/// instance now owns the thread and is carrying the workflow forward. The usual correct
/// response is to do nothing. Retrying immediately will simply fail to acquire.
/// </remarks>
public sealed class RunLeaseLostException(string threadId, long fencingToken, Exception? innerException = null)
    : GraphExecutionException(
        $"The run lease for thread '{threadId}' (fencing token {fencingToken}) was lost; another instance " +
        "has taken over this thread. Progress up to the last accepted checkpoint is intact.",
        innerException)
{
    /// <summary>The graph thread whose lease was lost.</summary>
    public string ThreadId { get; } = threadId;

    /// <summary>The token this run held when it discovered the loss.</summary>
    public long FencingToken { get; } = fencingToken;
}
