namespace Trellis.Workspaces;

/// <summary>
/// A bounded place an agent may read and write files.
/// </summary>
/// <remarks>
/// <para>
/// Paths are always relative to the workspace root and use <c>/</c> as the separator, whatever
/// the host uses. An implementation must refuse anything that resolves outside its root —
/// including by way of <c>..</c>, an absolute path, or a link.
/// </para>
/// <para>
/// The interface is async because a workspace need not be local: a container or a remote
/// sandbox implements the same contract, and code written against it does not change.
/// </para>
/// </remarks>
public interface IWorkspace
{
    /// <summary>Reads a text file.</summary>
    /// <exception cref="WorkspacePathException">The path escapes the workspace or is malformed.</exception>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes a text file, creating parent directories and replacing any existing content.</summary>
    /// <exception cref="WorkspacePathException">The path escapes the workspace or is malformed.</exception>
    /// <exception cref="WorkspaceQuotaException">The write would exceed a limit.</exception>
    ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>Lists one directory, non-recursively. Use <c>""</c> for the root.</summary>
    ValueTask<IReadOnlyList<WorkspaceEntry>> ListAsync(
        string path = "", CancellationToken cancellationToken = default);

    /// <summary>Whether a file or directory exists.</summary>
    ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file, or an empty directory. Missing paths are not an error.</summary>
    ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default);
}
