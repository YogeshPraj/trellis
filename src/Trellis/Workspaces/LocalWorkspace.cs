using System.Runtime.InteropServices;

namespace Trellis.Workspaces;

/// <summary>
/// A workspace rooted at a real directory on this machine.
/// </summary>
/// <remarks>
/// <para><b>What this does and does not promise</b></para>
/// <para>
/// This bounds an agent. It does not contain a hostile process. Every path is resolved — links
/// included — and refused if it lands outside the root, which stops a model that has been
/// talked into reading <c>../../.ssh/id_rsa</c> by a web page it just summarised. That is the
/// realistic threat, and it is the one this closes.
/// </para>
/// <para>
/// What it cannot close: the check and the open are two steps, so anything that can already
/// write to the workspace directory can swap a directory for a link in between and win the
/// race. Nor does it stop a tool from ignoring the workspace and calling
/// <see cref="File"/> directly — it constrains the tools built on it, not the process. Code
/// that runs genuinely untrusted output needs OS-level isolation: a container, a VM, a remote
/// sandbox. Those are separate implementations of <see cref="IWorkspace"/>, and calling this
/// one a sandbox would be claiming a guarantee it does not make.
/// </para>
/// <para><b>Multi-instance:</b> a local workspace is local. Two instances of an app do not
/// share one, so a conversation resumed on another instance will not find files written here.
/// A workspace that needs to outlive an instance belongs on shared storage behind a different
/// <see cref="IWorkspace"/>.</para>
/// <para><b>Concurrency:</b> nothing is locked. Two runs sharing a workspace can race on one
/// file. Give concurrent runs separate workspaces.</para>
/// </remarks>
public sealed class LocalWorkspace : IWorkspace
{
    /// <summary>Windows keeps these as device names in every directory; opening one is not a file operation.</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly string _root;
    private readonly WorkspaceOptions _options;

    /// <param name="rootPath">
    /// The directory to root at. Created if missing, and resolved once — so a root that is
    /// itself a link is handled by comparing against where it actually points.
    /// </param>
    /// <param name="options">Limits; defaults are used when null.</param>
    public LocalWorkspace(string rootPath, WorkspaceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _options = options ?? new WorkspaceOptions();

        Directory.CreateDirectory(rootPath);
        _root = RealPath(System.IO.Path.GetFullPath(rootPath))
            .TrimEnd(System.IO.Path.DirectorySeparatorChar);
    }

    /// <summary>Where this workspace lives. Host-absolute — do not show it to a model.</summary>
    public string RootPath => _root;

    public async ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        string full = Resolve(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"No file '{path}' in the workspace.", path);
        }

        var info = new FileInfo(full);
        if (info.Length > _options.MaxFileBytes)
        {
            throw new WorkspaceQuotaException("file size", info.Length, _options.MaxFileBytes);
        }

        return await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteTextAsync(
        string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ThrowIfReadOnly();

        string full = Resolve(path);
        long incoming = System.Text.Encoding.UTF8.GetByteCount(content);
        if (incoming > _options.MaxFileBytes)
        {
            throw new WorkspaceQuotaException("file size", incoming, _options.MaxFileBytes);
        }

        long existing = File.Exists(full) ? new FileInfo(full).Length : 0;
        (long totalBytes, int fileCount) = Measure();
        long projected = totalBytes - existing + incoming;
        if (projected > _options.MaxTotalBytes)
        {
            throw new WorkspaceQuotaException("total size", projected, _options.MaxTotalBytes);
        }
        if (existing == 0 && !File.Exists(full) && fileCount + 1 > _options.MaxFileCount)
        {
            throw new WorkspaceQuotaException("file count", fileCount + 1, _options.MaxFileCount);
        }

        string? parent = System.IO.Path.GetDirectoryName(full);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        // Write to a sibling and move into place, so a crash mid-write leaves the previous
        // content rather than a half-written file the next run would read as truth.
        string temporary = full + ".trellis-tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public ValueTask<IReadOnlyList<WorkspaceEntry>> ListAsync(
        string path = "", CancellationToken cancellationToken = default)
    {
        string full = string.IsNullOrEmpty(path) ? _root : Resolve(path);
        if (!Directory.Exists(full))
        {
            return ValueTask.FromResult<IReadOnlyList<WorkspaceEntry>>([]);
        }

        List<WorkspaceEntry> entries = [];
        foreach (string directory in Directory.EnumerateDirectories(full))
        {
            entries.Add(new WorkspaceEntry(System.IO.Path.GetFileName(directory), IsDirectory: true, 0));
        }
        foreach (string file in Directory.EnumerateFiles(full))
        {
            // A temp file from an interrupted write is an implementation detail, not content.
            if (file.EndsWith(".trellis-tmp", StringComparison.Ordinal))
            {
                continue;
            }
            entries.Add(new WorkspaceEntry(
                System.IO.Path.GetFileName(file), IsDirectory: false, new FileInfo(file).Length));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return ValueTask.FromResult<IReadOnlyList<WorkspaceEntry>>(entries);
    }

    public ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        string full = Resolve(path);
        return ValueTask.FromResult(File.Exists(full) || Directory.Exists(full));
    }

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfReadOnly();
        string full = Resolve(path);
        if (File.Exists(full))
        {
            File.Delete(full);
        }
        else if (Directory.Exists(full))
        {
            // Non-recursive on purpose: "delete this file" mistyped must not be able to mean
            // "delete this tree".
            Directory.Delete(full, recursive: false);
        }
        return ValueTask.CompletedTask;
    }

    private void ThrowIfReadOnly()
    {
        if (_options.IsReadOnly)
        {
            throw new WorkspacePathException("<workspace>", "this workspace is read-only");
        }
    }

    /// <summary>
    /// Turns a workspace-relative path into a host path that is proven to sit inside the root,
    /// or refuses it.
    /// </summary>
    internal string Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            throw new WorkspacePathException(path, "the path is empty");
        }
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            // A null byte can truncate the path inside a native call, so what gets opened is
            // not what was validated.
            throw new WorkspacePathException(path, "the path contains a null byte");
        }

        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || System.IO.Path.IsPathRooted(path) || HasDriveOrUncPrefix(normalized))
        {
            throw new WorkspacePathException(path, "workspace paths must be relative to the workspace root");
        }

        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                // Refused outright rather than normalized away: a path that has to climb out
                // of the workspace to be written is never a path this workspace should serve,
                // even when it would land back inside.
                throw new WorkspacePathException(path, "'..' is not allowed");
            }
            if (segment == ".")
            {
                continue;
            }
            if (IsReservedName(segment))
            {
                throw new WorkspacePathException(path, $"'{segment}' is a reserved device name");
            }
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                // Windows silently strips these, so the name checked is not the name opened.
                throw new WorkspacePathException(path, "path segments may not end with a space or a dot");
            }
            if (segment.Contains(':', StringComparison.Ordinal))
            {
                // An alternate data stream ("file.txt:hidden") is a different file.
                throw new WorkspacePathException(path, "':' is not allowed in a path segment");
            }
        }

        string combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, normalized));
        EnsureInsideRoot(path, combined);

        // The textual check above is not enough on its own: GetFullPath does not follow links,
        // so any directory along the way could point somewhere else entirely.
        WalkResolvingLinks(path, normalized.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return combined;
    }

    /// <summary>
    /// Re-walks the path one segment at a time from the root, jumping to the real target
    /// whenever a segment turns out to be a link, and checking containment after every step.
    /// </summary>
    /// <remarks>
    /// Resolving only the final component is not enough, and the difference is a real escape:
    /// for <c>escape/secret.txt</c> where <c>escape</c> is a junction, the leaf is an ordinary
    /// file that resolves to itself, so a leaf-only check reports the path as contained while
    /// the open reads straight through the junction. Only the segment-by-segment walk catches
    /// a link in the middle.
    /// </remarks>
    private void WalkResolvingLinks(string original, string[] segments)
    {
        string current = _root;
        foreach (string segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            current = System.IO.Path.Combine(current, segment);
            if (ResolveLinkTarget(current) is string target)
            {
                current = target;
            }

            // Checked at every step, so an escape is caught at the link itself rather than
            // depending on what happens to exist further down.
            EnsureInsideRoot(original, current);
        }
    }

    /// <summary>Where a path really points if it is a link (symlink or junction), else null.</summary>
    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo? target = Directory.Exists(path)
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                : File.Exists(path)
                    ? File.ResolveLinkTarget(path, returnFinalTarget: true)
                    : null;
            return target?.FullName;
        }
        catch (IOException)
        {
            // A broken or circular link resolves to nothing usable; leaving the path as-is
            // keeps the caller's containment check meaningful instead of throwing here.
            return null;
        }
    }

    private void EnsureInsideRoot(string original, string candidate)
    {
        string trimmed = candidate.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (trimmed.Equals(_root, comparison))
        {
            return;
        }
        // The separator matters: without it "/data/workspace-evil" passes a prefix test against
        // "/data/workspace".
        if (!trimmed.StartsWith(_root + System.IO.Path.DirectorySeparatorChar, comparison))
        {
            throw new WorkspacePathException(original, "it resolves outside the workspace");
        }
    }

    /// <summary>Resolves an existing path through any link to where it actually is.</summary>
    private static string RealPath(string path) =>
        ResolveLinkTarget(path) ?? System.IO.Path.GetFullPath(path);

    private static bool HasDriveOrUncPrefix(string normalized) =>
        normalized.StartsWith("//", StringComparison.Ordinal) ||
        (normalized.Length >= 2 && normalized[1] == ':');

    private static bool IsReservedName(string segment)
    {
        if (!OperatingSystem.IsWindows() && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }
        int dot = segment.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? segment : segment[..dot];
        return Array.Exists(ReservedNames, r => stem.Equals(r, StringComparison.OrdinalIgnoreCase));
    }

    private (long TotalBytes, int FileCount) Measure()
    {
        long bytes = 0;
        var count = 0;
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(file).Length;
            count++;
        }
        return (bytes, count);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the original failure is what the caller needs to see.
        }
    }
}
