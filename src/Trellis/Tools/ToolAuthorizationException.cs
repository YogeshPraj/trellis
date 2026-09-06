namespace Trellis.Tools;

/// <summary>
/// A tool call was refused by an <see cref="IToolAuthorizer"/> that chose to end the run
/// rather than let the model see the refusal.
/// </summary>
public sealed class ToolAuthorizationException(string toolName, string reason)
    : Exception($"Tool '{toolName}' was refused: {reason}")
{
    /// <summary>The tool that was refused.</summary>
    public string ToolName { get; } = toolName;

    /// <summary>Why it was refused.</summary>
    public string Reason { get; } = reason;
}
