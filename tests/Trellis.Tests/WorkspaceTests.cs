using Microsoft.Extensions.AI;
using Trellis.Workspaces;

namespace Trellis.Tests;

/// <summary>
/// Containment. Every test here is the same question asked a different way: can a path that
/// looks legal reach a file outside the workspace?
/// </summary>
public sealed class WorkspaceContainmentTests : IDisposable
{
    private readonly string _rootPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"trellis-ws-{Guid.NewGuid():N}");

    private readonly string _outsidePath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"trellis-outside-{Guid.NewGuid():N}");

    private readonly LocalWorkspace _workspace;

    public WorkspaceContainmentTests()
    {
        _workspace = new LocalWorkspace(_rootPath);
        Directory.CreateDirectory(_outsidePath);
        File.WriteAllText(System.IO.Path.Combine(_outsidePath, "secret.txt"), "the secret");
    }

    public void Dispose()
    {
        foreach (string path in new[] { _rootPath, _outsidePath, _rootPath + "-link" })
        {
            WorkspaceTestCleanup.DeleteTree(path);
        }
    }


    /// <summary>
    /// Creates a directory link, preferring whatever the platform allows without privilege.
    /// Windows symlinks need admin or developer mode, but a junction does not — and a junction
    /// is the same escape, so declining to test it because the fancier mechanism is unavailable
    /// would leave the most important case unverified.
    /// </summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        mklink!.WaitForExit();
        return mklink.ExitCode == 0 && Directory.Exists(link);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("subdir/../../escape.txt")]
    [InlineData("./../../escape.txt")]
    [InlineData("..")]
    [InlineData("a/b/c/../../../../out.txt")]
    public async Task ParentTraversal_IsRefused(string path)
    {
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.ReadTextAsync(path).AsTask());
    }

    [Fact]
    public async Task TraversalThatWouldLandBackInside_IsStillRefused()
    {
        await _workspace.WriteTextAsync("a/b.txt", "hi");

        // "a/../a/b.txt" resolves inside the workspace, so a containment check alone would
        // pass it. Refusing '..' outright means there is no path arithmetic to get wrong.
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.ReadTextAsync("a/../a/b.txt").AsTask());
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("//server/share/file.txt")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("C:/Windows/System32/config/SAM")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task AbsoluteAndUncPaths_AreRefused(string path)
    {
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.ReadTextAsync(path).AsTask());
    }

    [Fact]
    public async Task BackslashesAreTreatedAsSeparators_NotAsFilenameCharacters()
    {
        // On a non-Windows host a backslash is a legal filename character, so "..\..\x" would
        // be one long innocent-looking name rather than a traversal. Normalizing first means
        // the same input is refused on every platform.
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.ReadTextAsync("..\\..\\secret.txt").AsTask());
    }

    [Fact]
    public async Task ANullByte_IsRefused()
    {
        // A null byte can truncate a path inside a native call, so what is opened stops being
        // what was validated.
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.ReadTextAsync("safe.txt\0/../../etc/passwd").AsTask());
    }

    [Fact]
    public async Task AlternateDataStreams_AreRefused()
    {
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.WriteTextAsync("notes.txt:hidden", "x").AsTask());
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("logs/COM1")]
    [InlineData("aux.log")]
    public async Task WindowsDeviceNames_AreRefusedOnWindows(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // These are devices in every directory. Opening one is not a file operation at all.
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.WriteTextAsync(path, "x").AsTask());
    }

    [Theory]
    [InlineData("trailing ")]
    [InlineData("trailing.")]
    public async Task TrailingSpaceOrDot_IsRefused(string path)
    {
        // Windows strips these silently, so the name validated is not the name opened.
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.WriteTextAsync(path, "x").AsTask());
    }

    [Fact]
    public async Task ASiblingDirectoryWithTheRootAsAPrefix_IsNotInside()
    {
        // "/tmp/ws-evil" starts with "/tmp/ws", so a plain prefix comparison would admit it.
        string sibling = _rootPath + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var neighbour = new LocalWorkspace(sibling);
            await neighbour.WriteTextAsync("leak.txt", "data");

            await Assert.ThrowsAsync<WorkspacePathException>(
                () => _workspace.ReadTextAsync("../" + System.IO.Path.GetFileName(sibling) + "/leak.txt").AsTask());
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public async Task ASymlinkPointingOutside_IsRefused()
    {
        string link = System.IO.Path.Combine(_rootPath, "escape");
        Assert.True(TryCreateDirectoryLink(link, _outsidePath), "could not create a directory link to test with");

        // Sanity: the link really does reach outside, so a failure below is containment
        // working rather than the link never having been an escape.
        Assert.True(File.Exists(System.IO.Path.Combine(link, "secret.txt")));

        // This is the case textual normalization cannot catch: every segment is innocent, and
        // GetFullPath does not follow links. Only resolving the real target catches it.
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.ReadTextAsync("escape/secret.txt").AsTask());
    }

    [Fact]
    public async Task WritingThroughASymlinkedDirectory_IsRefused()
    {
        string link = System.IO.Path.Combine(_rootPath, "out");
        Assert.True(TryCreateDirectoryLink(link, _outsidePath), "could not create a directory link to test with");

        // The target does not exist yet, so containment has to be judged from the nearest
        // existing ancestor — which is the link.
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => _workspace.WriteTextAsync("out/planted.txt", "payload").AsTask());

        Assert.False(File.Exists(System.IO.Path.Combine(_outsidePath, "planted.txt")));
    }

    [Fact]
    public async Task ARootThatIsItselfALink_StillWorks()
    {
        string link = _rootPath + "-link";
        Assert.True(TryCreateDirectoryLink(link, _rootPath), "could not create a directory link to test with");

        try
        {
            // Resolving the root once at construction is what makes this work: otherwise every
            // legitimate path under it would resolve "outside" its own textual root.
            var viaLink = new LocalWorkspace(link);
            await viaLink.WriteTextAsync("ok.txt", "fine");

            Assert.Equal("fine", await viaLink.ReadTextAsync("ok.txt"));
        }
        finally
        {
            WorkspaceTestCleanup.DeleteTree(link);
        }
    }

    [Fact]
    public async Task LegitimatePathsStillWork()
    {
        await _workspace.WriteTextAsync("notes.txt", "hello");
        await _workspace.WriteTextAsync("deep/nested/file.txt", "world");

        Assert.Equal("hello", await _workspace.ReadTextAsync("notes.txt"));
        Assert.Equal("world", await _workspace.ReadTextAsync("deep/nested/file.txt"));
        Assert.Equal("world", await _workspace.ReadTextAsync("./deep/nested/file.txt"));
        Assert.True(await _workspace.ExistsAsync("deep/nested"));
    }
}

/// <summary>Limits, read-only mode, and the ordinary file operations.</summary>
public sealed class WorkspaceBehaviourTests : IDisposable
{
    private readonly string _rootPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"trellis-ws-{Guid.NewGuid():N}");

    public void Dispose()
    {
        WorkspaceTestCleanup.DeleteTree(_rootPath);
    }

    private LocalWorkspace New(WorkspaceOptions? options = null) => new(_rootPath, options);

    [Fact]
    public async Task AFileTooLarge_IsRefused()
    {
        LocalWorkspace workspace = New(new WorkspaceOptions { MaxFileBytes = 10 });

        await Assert.ThrowsAsync<WorkspaceQuotaException>(
            () => workspace.WriteTextAsync("big.txt", new string('x', 11)).AsTask());

        Assert.False(await workspace.ExistsAsync("big.txt"));
    }

    [Fact]
    public async Task TotalSize_IsCapped()
    {
        LocalWorkspace workspace = New(new WorkspaceOptions { MaxFileBytes = 100, MaxTotalBytes = 20 });

        await workspace.WriteTextAsync("a.txt", new string('x', 15));

        // An agent looping on write is the realistic way a disk fills.
        await Assert.ThrowsAsync<WorkspaceQuotaException>(
            () => workspace.WriteTextAsync("b.txt", new string('x', 15)).AsTask());
    }

    [Fact]
    public async Task OverwritingDoesNotDoubleCountAgainstTheTotal()
    {
        LocalWorkspace workspace = New(new WorkspaceOptions { MaxFileBytes = 100, MaxTotalBytes = 20 });

        await workspace.WriteTextAsync("a.txt", new string('x', 15));

        // Replacing content frees what it replaces; counting the old bytes too would wedge a
        // workspace that is nowhere near full.
        await workspace.WriteTextAsync("a.txt", new string('y', 18));

        Assert.Equal(new string('y', 18), await workspace.ReadTextAsync("a.txt"));
    }

    [Fact]
    public async Task FileCount_IsCapped()
    {
        LocalWorkspace workspace = New(new WorkspaceOptions { MaxFileCount = 2 });

        await workspace.WriteTextAsync("1.txt", "a");
        await workspace.WriteTextAsync("2.txt", "b");

        await Assert.ThrowsAsync<WorkspaceQuotaException>(
            () => workspace.WriteTextAsync("3.txt", "c").AsTask());
    }

    [Fact]
    public async Task ReadOnly_RefusesEveryMutation()
    {
        LocalWorkspace writable = New();
        await writable.WriteTextAsync("corpus.txt", "reference material");

        LocalWorkspace readOnly = New(new WorkspaceOptions { IsReadOnly = true });

        Assert.Equal("reference material", await readOnly.ReadTextAsync("corpus.txt"));
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => readOnly.WriteTextAsync("corpus.txt", "rewritten").AsTask());
        await Assert.ThrowsAsync<WorkspacePathException>(
            () => readOnly.DeleteAsync("corpus.txt").AsTask());
    }

    [Fact]
    public async Task ListingIsSortedAndMarksDirectories()
    {
        LocalWorkspace workspace = New();
        await workspace.WriteTextAsync("b.txt", "22");
        await workspace.WriteTextAsync("a/inner.txt", "x");

        IReadOnlyList<WorkspaceEntry> entries = await workspace.ListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal("a", entries[0].Name);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("b.txt", entries[1].Name);
        Assert.Equal(2, entries[1].SizeBytes);
    }

    [Fact]
    public async Task DeletingANonEmptyDirectory_IsRefused()
    {
        LocalWorkspace workspace = New();
        await workspace.WriteTextAsync("tree/file.txt", "x");

        // Non-recursive on purpose: a mistyped "delete this file" must not be able to mean
        // "delete this tree".
        await Assert.ThrowsAnyAsync<IOException>(() => workspace.DeleteAsync("tree").AsTask());
        Assert.True(await workspace.ExistsAsync("tree/file.txt"));
    }

    [Fact]
    public async Task DeletingSomethingAbsent_IsNotAnError()
    {
        LocalWorkspace workspace = New();
        await workspace.DeleteAsync("never-existed.txt");
    }

    [Fact]
    public async Task ReadingAMissingFile_ReportsItAsMissing()
    {
        LocalWorkspace workspace = New();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => workspace.ReadTextAsync("nope.txt").AsTask());
    }

    [Fact]
    public async Task AFailedWriteLeavesNoTemporaryFileBehind()
    {
        LocalWorkspace workspace = New(new WorkspaceOptions { MaxFileBytes = 5 });
        await workspace.WriteTextAsync("ok.txt", "abc");

        await Assert.ThrowsAsync<WorkspaceQuotaException>(
            () => workspace.WriteTextAsync("ok.txt", "way too long").AsTask());

        IReadOnlyList<WorkspaceEntry> entries = await workspace.ListAsync();
        Assert.DoesNotContain(entries, e => e.Name.Contains("tmp", StringComparison.Ordinal));
        Assert.Equal("abc", await workspace.ReadTextAsync("ok.txt"));
    }
}

/// <summary>The tools an agent actually sees.</summary>
public sealed class WorkspaceToolTests : IDisposable
{
    private readonly string _rootPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"trellis-ws-{Guid.NewGuid():N}");

    private readonly LocalWorkspace _workspace;

    public WorkspaceToolTests() => _workspace = new LocalWorkspace(_rootPath);

    public void Dispose()
    {
        WorkspaceTestCleanup.DeleteTree(_rootPath);
    }

    private AIFunction Tool(string name) =>
        (AIFunction)WorkspaceTools.Create(_workspace).First(t => ((AIFunction)t).Name == name);

    [Fact]
    public void TheExpectedToolsAreOffered()
    {
        IReadOnlyList<string> names = [.. WorkspaceTools.Create(_workspace).Select(t => ((AIFunction)t).Name)];

        Assert.Equal(["read_file", "write_file", "list_files", "file_exists", "delete_file"], names);
    }

    [Fact]
    public async Task WriteThenReadRoundTrips()
    {
        await Tool("write_file").InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["path"] = "notes.txt", ["content"] = "remember this" }));

        object? read = await Tool("read_file").InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["path"] = "notes.txt" }));

        Assert.Contains("remember this", read?.ToString());
    }

    [Fact]
    public async Task ARefusedPathComesBackAsText_NotAnException()
    {
        object? result = await Tool("read_file").InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["path"] = "../../../etc/passwd" }));

        // The agent can read this, pick a legal path, and carry on. Throwing would end a run
        // over what is usually a typo.
        Assert.Contains("refused", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARefusalDoesNotLeakTheHostPath()
    {
        object? result = await Tool("read_file").InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["path"] = "../secret.txt" }));

        // Telling a model where its workspace lives hands it the one fact it needs to aim a
        // better-informed attempt.
        Assert.DoesNotContain(_rootPath, result?.ToString());
    }

    [Fact]
    public async Task ListingAnEmptyWorkspace_SaysSo()
    {
        object? result = await Tool("list_files").InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["path"] = "" }));

        Assert.Equal("(empty)", result?.ToString());
    }

    [Fact]
    public async Task WorkspaceToolsComposeWithAuthorization()
    {
        IReadOnlyList<AITool> gated = WorkspaceTools
            .Create(_workspace)
            .WithAuthorization(new AllowListToolAuthorizer(["read_file", "list_files"]));

        var write = (AIFunction)gated.First(t => ((AIFunction)t).Name == "write_file");
        await write.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["path"] = "blocked.txt", ["content"] = "x" }));

        // Two independent controls answering different questions: the workspace bounds where a
        // tool may reach, the authorizer decides whether it runs at all.
        Assert.False(await _workspace.ExistsAsync("blocked.txt"));
    }
}

/// <summary>Temp-directory cleanup that copes with the links these tests create.</summary>
internal static class WorkspaceTestCleanup
{
    /// <summary>
    /// Removes a tree, unlinking any reparse points first. A recursive delete trips over a
    /// junction on Windows, and a cleanup failure would otherwise be reported as the test
    /// failing — which is how a passing containment check came to look like a bug.
    /// </summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                if (new DirectoryInfo(directory).LinkTarget is not null)
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            if (new DirectoryInfo(path).LinkTarget is not null)
            {
                Directory.Delete(path);
                return;
            }
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory says nothing about the code under test.
        }
    }
}
