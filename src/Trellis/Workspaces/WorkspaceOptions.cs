namespace Trellis.Workspaces;

/// <summary>Limits a workspace enforces.</summary>
/// <remarks>
/// The defaults are deliberately small. A workspace is scratch space for one agent, not
/// storage; if a run legitimately needs hundreds of megabytes, that is worth having to say out
/// loud rather than discovering when a disk fills.
/// </remarks>
public sealed class WorkspaceOptions
{
    /// <summary>Largest single file, in bytes. Default 1 MiB.</summary>
    public long MaxFileBytes { get; set; } = 1024 * 1024;

    /// <summary>Largest total size of the workspace, in bytes. Default 64 MiB.</summary>
    public long MaxTotalBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Most files the workspace may hold. Default 1,000.</summary>
    public int MaxFileCount { get; set; } = 1_000;

    /// <summary>
    /// When true, every mutating operation is refused. For handing an agent a corpus to read
    /// without also handing it the ability to rewrite that corpus.
    /// </summary>
    public bool IsReadOnly { get; set; }
}
