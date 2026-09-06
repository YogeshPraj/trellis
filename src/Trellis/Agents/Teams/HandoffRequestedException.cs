namespace Trellis.Agents.Teams;

/// <summary>
/// Signals that a run transferred control instead of producing a result.
/// </summary>
/// <remarks>
/// Internal, and control flow rather than failure: the run genuinely has no
/// <c>TResult</c> to return, and raising that as far as the team loop is what stops the
/// self-healing validator from treating a deliberate handoff as a malformed answer and paying
/// for two correction round trips before anyone notices.
/// </remarks>
internal sealed class HandoffRequestedException(string target, string? reason) : Exception(
    $"The agent transferred the conversation to '{target}'.")
{
    public string Target { get; } = target;

    public string? Reason { get; } = reason;
}
