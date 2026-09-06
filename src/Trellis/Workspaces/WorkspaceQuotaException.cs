namespace Trellis.Workspaces;

/// <summary>
/// An operation was refused because it would exceed a workspace limit.
/// </summary>
/// <remarks>
/// Agents fail in loops. An unbounded workspace turns one confused run into a full disk, which
/// takes down everything else on the machine rather than just that agent.
/// </remarks>
public sealed class WorkspaceQuotaException(string limit, long attempted, long allowed)
    : Exception($"Refused: this would exceed the workspace {limit} limit ({attempted} > {allowed}).")
{
    /// <summary>Which limit was hit.</summary>
    public string Limit { get; } = limit;

    /// <summary>What the operation would have reached.</summary>
    public long Attempted { get; } = attempted;

    /// <summary>What is permitted.</summary>
    public long Allowed { get; } = allowed;
}
