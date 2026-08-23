namespace Trellis.Routing;

/// <summary>
/// Turns the model a caller asked for into the ordered list of concrete models that may
/// serve it (Strategy).
/// </summary>
/// <remarks>
/// Routing across endpoints answers "which deployment"; this answers "which model". They are
/// different questions: a request for <c>fast</c> might be served by <c>gpt-4o-mini</c> on one
/// deployment and <c>claude-haiku</c> on another, and a request for a specific model may still
/// need a dated snapshot as a fallback. The returned order is a preference order — the router
/// exhausts every healthy endpoint for the first entry before trying the second.
/// </remarks>
public interface IModelAliasResolver
{
    /// <summary>
    /// The concrete models that may serve <paramref name="requestedModel"/>, most preferred
    /// first. Return a single-element list containing the input to pass it through unchanged,
    /// and an empty list only when the request cannot be served at all.
    /// </summary>
    IReadOnlyList<string> Resolve(string? requestedModel);
}
