using Microsoft.Extensions.AI;

namespace Trellis.Tests;

/// <summary>
/// The gate on tool execution. The property that matters in every one of these is not what
/// the caller is told — it is whether the tool body actually ran.
/// </summary>
public class ToolAuthorizationTests
{
    /// <summary>A tool that records whether it was reached.</summary>
    private sealed class SpyTool
    {
        public int Calls { get; private set; }

        public AIFunction Function => AIFunctionFactory.Create(
            (string target) => { Calls++; return $"deleted {target}"; },
            name: "delete_everything");
    }

    private static AIFunctionArguments Args(string target) =>
        new(new Dictionary<string, object?> { ["target"] = target });

    [Fact]
    public async Task AnAllowedCall_Runs()
    {
        var spy = new SpyTool();
        var gated = (AIFunction)spy.Function.WithAuthorization(AllowAllToolAuthorizer.Instance);

        object? result = await gated.InvokeAsync(Args("prod"));

        Assert.Equal(1, spy.Calls);
        Assert.Contains("deleted prod", result?.ToString());
    }

    [Fact]
    public async Task ADeniedCall_NeverReachesTheTool()
    {
        var spy = new SpyTool();
        var gated = (AIFunction)spy.Function.WithAuthorization(
            DelegateToolAuthorizer.FromPredicate(_ => ToolAuthorization.Deny("production is off limits")));

        object? result = await gated.InvokeAsync(Args("prod"));

        // The only assertion that matters: the body did not execute.
        Assert.Equal(0, spy.Calls);
        Assert.Contains("production is off limits", result?.ToString());
    }

    [Fact]
    public async Task ADeniedCall_ReturnsAResultRatherThanThrowing()
    {
        var gated = (AIFunction)new SpyTool().Function.WithAuthorization(
            DelegateToolAuthorizer.FromPredicate(_ => ToolAuthorization.Deny("nope")));

        // Denial is reported to the model as an ordinary tool result, so the agent can explain
        // itself or take another route instead of the run dying.
        object? result = await gated.InvokeAsync(Args("prod"));

        Assert.NotNull(result);
    }

    [Fact]
    public async Task AnAbortedCall_ThrowsAndNeverReachesTheTool()
    {
        var spy = new SpyTool();
        var gated = (AIFunction)spy.Function.WithAuthorization(
            DelegateToolAuthorizer.FromPredicate(_ => ToolAuthorization.Abort("hard stop")));

        // Invoked directly, with no function-invoking loop to terminate, so the caller is
        // told the only way left: an exception.
        ToolAuthorizationException ex = await Assert.ThrowsAsync<ToolAuthorizationException>(
            () => gated.InvokeAsync(Args("prod")).AsTask());

        Assert.Equal(0, spy.Calls);
        Assert.Equal("delete_everything", ex.ToolName);
        Assert.Equal("hard stop", ex.Reason);
    }

    [Fact]
    public async Task AnAuthorizerThatThrows_FailsClosed()
    {
        var spy = new SpyTool();
        var gated = (AIFunction)spy.Function.WithAuthorization(
            new DelegateToolAuthorizer((_, _) => throw new InvalidOperationException("policy service down")));

        // A policy engine being unreachable is not evidence of permission. Failing open here
        // would turn every outage into a privilege escalation.
        await Assert.ThrowsAsync<ToolAuthorizationException>(() => gated.InvokeAsync(Args("prod")).AsTask());

        Assert.Equal(0, spy.Calls);
    }

    [Fact]
    public async Task Cancellation_IsNotMistakenForAPolicyFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var gated = (AIFunction)new SpyTool().Function.WithAuthorization(
            new DelegateToolAuthorizer((_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult(ToolAuthorization.Allow());
            }));

        // Reporting a cancelled run as a refused tool would send callers chasing a policy
        // problem that does not exist.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gated.InvokeAsync(Args("prod"), cts.Token).AsTask());
    }

    [Fact]
    public async Task TheAuthorizerSeesTheToolNameAndArguments()
    {
        ToolInvocationContext? seen = null;
        var gated = (AIFunction)new SpyTool().Function.WithAuthorization(
            DelegateToolAuthorizer.FromPredicate(context =>
            {
                seen = context;
                return ToolAuthorization.Allow();
            }));

        await gated.InvokeAsync(Args("staging"));

        Assert.Equal("delete_everything", seen!.ToolName);
        Assert.Equal("staging", seen.Arguments["target"]);
    }

    [Fact]
    public async Task ArgumentsCanDecideTheVerdict()
    {
        var spy = new SpyTool();
        var gated = (AIFunction)spy.Function.WithAuthorization(
            DelegateToolAuthorizer.FromPredicate(context =>
                context.Arguments.TryGetValue("target", out object? t) && t as string == "prod"
                    ? ToolAuthorization.Deny("not production")
                    : ToolAuthorization.Allow()));

        await gated.InvokeAsync(Args("prod"));
        Assert.Equal(0, spy.Calls);

        await gated.InvokeAsync(Args("staging"));
        Assert.Equal(1, spy.Calls);
    }

    [Fact]
    public void ANonInvocableTool_PassesThroughUnchanged()
    {
        // Provider-hosted tools run server-side, so there is no local call to intercept.
        // Wrapping one would imply a protection that does not exist.
        AITool hosted = new HostedWebSearchTool();

        AITool gated = hosted.WithAuthorization(
            DelegateToolAuthorizer.FromPredicate(_ => ToolAuthorization.Abort("no")));

        Assert.Same(hosted, gated);
    }

    [Fact]
    public async Task TheWrapperKeepsTheToolsIdentity()
    {
        AIFunction original = new SpyTool().Function;
        var gated = (AIFunction)original.WithAuthorization(AllowAllToolAuthorizer.Instance);

        // The model addresses tools by name and picks them by description; changing either
        // would silently alter behaviour just by turning authorization on.
        Assert.Equal(original.Name, gated.Name);
        Assert.Equal(original.Description, gated.Description);
        await Task.CompletedTask;
    }
}

/// <summary>The built-in policies, and how they compose.</summary>
public class ToolAuthorizerPolicyTests
{
    private static ToolInvocationContext Context(string name) =>
        new(name, AIFunctionFactory.Create(() => "ok", name: name), new AIFunctionArguments());

    [Fact]
    public async Task AllowList_PermitsOnlyWhatItNames()
    {
        var authorizer = new AllowListToolAuthorizer(["read_file"]);

        Assert.True((await authorizer.AuthorizeAsync(Context("read_file"))).IsAllowed);
        Assert.False((await authorizer.AuthorizeAsync(Context("write_file"))).IsAllowed);
    }

    [Fact]
    public async Task AllowList_DeniesByDefault_RatherThanAborting()
    {
        var authorizer = new AllowListToolAuthorizer(["read_file"]);

        ToolAuthorization verdict = await authorizer.AuthorizeAsync(Context("rm_rf"));

        Assert.Equal(ToolAuthorizationDecision.Deny, verdict.Decision);
    }

    [Fact]
    public async Task AllowList_CanBeToldToAbortInstead()
    {
        var authorizer = new AllowListToolAuthorizer(["read_file"], abortOnRefusal: true);

        ToolAuthorization verdict = await authorizer.AuthorizeAsync(Context("rm_rf"));

        Assert.Equal(ToolAuthorizationDecision.Abort, verdict.Decision);
    }

    [Fact]
    public async Task AllowList_IsCaseSensitive()
    {
        var authorizer = new AllowListToolAuthorizer(["read_file"]);

        // Tool names are identifiers. Matching loosely would let "READ_FILE" through a list
        // that never named it.
        Assert.False((await authorizer.AuthorizeAsync(Context("READ_FILE"))).IsAllowed);
    }

    [Fact]
    public async Task Composite_RequiresUnanimity()
    {
        var composite = new CompositeToolAuthorizer(
            AllowAllToolAuthorizer.Instance,
            DelegateToolAuthorizer.FromPredicate(_ => ToolAuthorization.Deny("second says no")));

        ToolAuthorization verdict = await composite.AuthorizeAsync(Context("anything"));

        // "Any one may allow" would let a permissive policy cancel a restrictive one, which is
        // how an allow-list stops meaning anything once a second policy sits beside it.
        Assert.False(verdict.IsAllowed);
        Assert.Equal("second says no", verdict.Reason);
    }

    [Fact]
    public async Task Composite_StopsAtTheFirstRefusal()
    {
        var consulted = 0;
        var composite = new CompositeToolAuthorizer(
            DelegateToolAuthorizer.FromPredicate(_ => ToolAuthorization.Deny("no")),
            DelegateToolAuthorizer.FromPredicate(_ => { consulted++; return ToolAuthorization.Allow(); }));

        await composite.AuthorizeAsync(Context("anything"));

        Assert.Equal(0, consulted);
    }

    [Fact]
    public async Task Composite_AllowsWhenEveryPolicyAgrees()
    {
        var composite = new CompositeToolAuthorizer(
            new AllowListToolAuthorizer(["read_file"]),
            DelegateToolAuthorizer.FromPredicate(_ => ToolAuthorization.Allow()));

        Assert.True((await composite.AuthorizeAsync(Context("read_file"))).IsAllowed);
    }

    [Fact]
    public async Task AnEmptyComposite_Allows()
    {
        // Vacuous truth, and the only consistent answer: composing zero restrictions cannot
        // itself be a restriction.
        Assert.True((await new CompositeToolAuthorizer().AuthorizeAsync(Context("x"))).IsAllowed);
    }

    [Fact]
    public void ARefusalNeedsAReason()
    {
        // An unexplained refusal reaches the model as an empty instruction and teaches it
        // nothing about what to do instead.
        Assert.Throws<ArgumentException>(() => ToolAuthorization.Deny("  "));
        Assert.Throws<ArgumentException>(() => ToolAuthorization.Abort(""));
    }
}

/// <summary>
/// The gate as wired into <see cref="Agent{TResult}"/>, driven through a real function-invoking
/// loop. The unit tests prove the wrapper; these prove the agent actually puts it on.
/// </summary>
public class AgentToolAuthorizationTests
{
    /// <summary>Asks for one tool call, then answers with text once it has the result.</summary>
    private sealed class ToolCallingClient(string toolName) : IChatClient
    {
        private int _turn;

        public int Turns => _turn;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return Interlocked.Increment(ref _turn) == 1
                ? new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", toolName, new Dictionary<string, object?> { ["target"] = "prod" })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "all done"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static (AIFunction Tool, Func<int> Calls) SpyTool()
    {
        var calls = 0;
        AIFunction tool = AIFunctionFactory.Create(
            (string target) => { Interlocked.Increment(ref calls); return $"deleted {target}"; },
            name: "delete_everything");
        return (tool, () => calls);
    }

    [Fact]
    public async Task WithoutAnAuthorizer_TheToolRuns()
    {
        (AIFunction tool, Func<int> calls) = SpyTool();
        var agent = new Agent<string>(
            new ToolCallingClient("delete_everything"), tools: [tool]);

        await agent.RunAsync("clean up");

        // The existing default: no gate unless you ask for one. Documented, not accidental.
        Assert.Equal(1, calls());
    }

    [Fact]
    public async Task ADeniedTool_NeverRuns_AndTheAgentStillAnswers()
    {
        (AIFunction tool, Func<int> calls) = SpyTool();
        var agent = new Agent<string>(
            new ToolCallingClient("delete_everything"),
            tools: [tool],
            toolAuthorizer: new AllowListToolAuthorizer(["read_file"]));

        AgentRunResult<string> result = await agent.RunAsync("clean up");

        Assert.Equal(0, calls());
        Assert.Equal("all done", result.Output);
    }

    [Fact]
    public async Task AnAbortingAuthorizer_StopsTheLoopWithoutAnotherModelCall()
    {
        (AIFunction tool, Func<int> calls) = SpyTool();
        var client = new ToolCallingClient("delete_everything");
        var agent = new Agent<string>(
            client,
            tools: [tool],
            toolAuthorizer: new AllowListToolAuthorizer(["read_file"], abortOnRefusal: true));

        await agent.RunAsync("clean up");

        // Abort's guarantee is that the loop stops dead: the tool never ran, and the model was
        // never asked again. Denial by contrast lets the conversation continue (2 turns).
        Assert.Equal(0, calls());
        Assert.Equal(1, client.Turns);
    }

    [Fact]
    public async Task ADeniedToolLetsTheConversationContinue()
    {
        (AIFunction tool, Func<int> calls) = SpyTool();
        var client = new ToolCallingClient("delete_everything");
        var agent = new Agent<string>(
            client,
            tools: [tool],
            toolAuthorizer: new AllowListToolAuthorizer(["read_file"]));

        await agent.RunAsync("clean up");

        // The contrast with Abort above: same refusal, but the model gets to respond to it.
        Assert.Equal(0, calls());
        Assert.Equal(2, client.Turns);
    }

    [Fact]
    public async Task PerRunTools_AreGatedToo()
    {
        (AIFunction tool, Func<int> calls) = SpyTool();
        var agent = new Agent<string, string>(
            new ToolCallingClient("delete_everything"),
            tools: _ => [tool],
            toolAuthorizer: new AllowListToolAuthorizer([]));

        await agent.RunAsync("deps", "clean up");

        // Tools built per run from injected dependencies must not be a way around the gate.
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task AnAuthorizerThatThrowsMidRun_FailsClosedAndStopsTheRun()
    {
        (AIFunction tool, Func<int> calls) = SpyTool();
        var client = new ToolCallingClient("delete_everything");
        var agent = new Agent<string>(
            client,
            tools: [tool],
            toolAuthorizer: new DelegateToolAuthorizer(
                (_, _) => throw new InvalidOperationException("policy service down")));

        await agent.RunAsync("clean up");

        // A broken policy engine must not become a way to run the tool, and must not leave the
        // model looping against an authorizer that will keep throwing.
        Assert.Equal(0, calls());
        Assert.Equal(1, client.Turns);
    }
}
