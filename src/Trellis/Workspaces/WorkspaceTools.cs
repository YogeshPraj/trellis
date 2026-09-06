using System.Text;
using Microsoft.Extensions.AI;

namespace Trellis.Workspaces;

/// <summary>
/// Builds the tools that let an agent use an <see cref="IWorkspace"/>.
/// </summary>
/// <remarks>
/// <para>
/// The workspace decides <em>where</em> a tool may reach; an
/// <see cref="Tools.IToolAuthorizer"/> decides <em>whether</em> a call happens at all. They
/// answer different questions and are worth using together — an allow-list of
/// <c>["read_file", "list_files"]</c> over a workspace gives an agent a corpus it can read and
/// nothing it can damage, and a read-only <see cref="WorkspaceOptions"/> makes the same
/// guarantee from the other side.
/// </para>
/// <para>
/// Failures come back to the model as text rather than as exceptions. A refused path is
/// information the agent can act on — it can pick a legal path and carry on — whereas throwing
/// would end a run over what is usually a typo.
/// </para>
/// </remarks>
public static class WorkspaceTools
{
    /// <summary>Creates read, write, list, exists, and delete tools bound to one workspace.</summary>
    public static IReadOnlyList<AITool> Create(IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return
        [
            AIFunctionFactory.Create(
                (string path, CancellationToken ct) => ReadAsync(workspace, path, ct),
                name: "read_file",
                description: "Reads a UTF-8 text file from the workspace. The path is relative to the workspace root."),

            AIFunctionFactory.Create(
                (string path, string content, CancellationToken ct) => WriteAsync(workspace, path, content, ct),
                name: "write_file",
                description: "Writes a UTF-8 text file to the workspace, replacing it if it exists. "
                    + "The path is relative to the workspace root; parent directories are created."),

            AIFunctionFactory.Create(
                (string? path, CancellationToken ct) => ListAsync(workspace, path, ct),
                name: "list_files",
                description: "Lists the files and directories in one workspace directory. "
                    + "Omit the path, or pass an empty string, for the workspace root."),

            AIFunctionFactory.Create(
                (string path, CancellationToken ct) => ExistsAsync(workspace, path, ct),
                name: "file_exists",
                description: "Reports whether a path exists in the workspace."),

            AIFunctionFactory.Create(
                (string path, CancellationToken ct) => DeleteAsync(workspace, path, ct),
                name: "delete_file",
                description: "Deletes a file, or an empty directory, from the workspace."),
        ];
    }

    private static async Task<string> ReadAsync(IWorkspace workspace, string path, CancellationToken ct)
    {
        try
        {
            return await workspace.ReadTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return $"There is no file '{path}' in the workspace.";
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ex.Message;
        }
    }

    private static async Task<string> WriteAsync(
        IWorkspace workspace, string path, string content, CancellationToken ct)
    {
        try
        {
            await workspace.WriteTextAsync(path, content, ct).ConfigureAwait(false);
            return $"Wrote {Encoding.UTF8.GetByteCount(content)} bytes to '{path}'.";
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ex.Message;
        }
    }

    private static async Task<string> ListAsync(IWorkspace workspace, string? path, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<WorkspaceEntry> entries =
                await workspace.ListAsync(path ?? string.Empty, ct).ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return "(empty)";
            }

            var text = new StringBuilder();
            foreach (WorkspaceEntry entry in entries)
            {
                text.AppendLine(entry.IsDirectory
                    ? $"{entry.Name}/"
                    : $"{entry.Name} ({entry.SizeBytes} bytes)");
            }
            return text.ToString().TrimEnd();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ex.Message;
        }
    }

    private static async Task<string> ExistsAsync(IWorkspace workspace, string path, CancellationToken ct)
    {
        try
        {
            return (await workspace.ExistsAsync(path, ct).ConfigureAwait(false)).ToString();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ex.Message;
        }
    }

    private static async Task<string> DeleteAsync(IWorkspace workspace, string path, CancellationToken ct)
    {
        try
        {
            await workspace.DeleteAsync(path, ct).ConfigureAwait(false);
            return $"Deleted '{path}'.";
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Refusals and quota limits are answers, not faults. Everything else — an unreadable disk,
    /// a permissions problem on the host — is a real failure and must not be flattened into a
    /// sentence the model will cheerfully work around.
    /// </summary>
    private static bool IsExpected(Exception ex) =>
        ex is WorkspacePathException or WorkspaceQuotaException;
}
