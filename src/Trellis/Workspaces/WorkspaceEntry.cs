namespace Trellis.Workspaces;

/// <summary>One item in a workspace listing.</summary>
/// <param name="Name">The entry's name, without any directory part.</param>
/// <param name="IsDirectory">Whether it is a directory.</param>
/// <param name="SizeBytes">Size in bytes; zero for directories.</param>
public sealed record WorkspaceEntry(string Name, bool IsDirectory, long SizeBytes);
