namespace Trellis.Workspaces;

/// <summary>
/// A path was refused: it resolved outside the workspace, or was malformed in a way that could
/// not be safely interpreted.
/// </summary>
/// <remarks>
/// The message carries the path as the caller wrote it, never the resolved absolute path.
/// Telling a model where its workspace sits on disk hands it the one fact it would need to aim
/// a better-informed attempt.
/// </remarks>
public sealed class WorkspacePathException(string attemptedPath, string reason)
    : Exception($"Path '{attemptedPath}' was refused: {reason}")
{
    /// <summary>The path as the caller supplied it.</summary>
    public string AttemptedPath { get; } = attemptedPath;

    /// <summary>Why it was refused.</summary>
    public string Reason { get; } = reason;
}
