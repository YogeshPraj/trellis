using Microsoft.Extensions.AI;
using Trellis.Diagnostics;

namespace Trellis.Tools;

/// <summary>
/// Wraps one tool so an <see cref="IToolAuthorizer"/> rules on it before it runs.
/// </summary>
/// <remarks>
/// <para>
/// The gate lives on the function rather than in the agent loop on purpose. Tool execution is
/// owned by <c>Microsoft.Extensions.AI</c>'s function-invoking client, not by Trellis, so a
/// check placed in our loop would be bypassed the moment anyone used the underlying client
/// directly. Wrapping the function means the tool is <em>itself</em> gated: the same protection
/// holds for hand-written tools, <c>[Tool]</c>-generated ones, MCP tools, and any future
/// source, however they are invoked.
/// </para>
/// <para><b>Why aborting does not throw inside an agent run</b></para>
/// <para>
/// The function-invoking client treats an exception from a tool as a recoverable error: it
/// feeds the failure back to the model and tries again, up to
/// <c>MaximumConsecutiveErrorsPerRequest</c> (3 by default). Throwing would therefore hand the
/// model several more attempts at the very thing it was just refused, and burn a model round
/// trip on each. Setting <c>Terminate</c> stops the loop immediately — but only if we return
/// normally, because throwing discards it. So inside a loop an abort terminates, and outside
/// one — where there is no loop to stop and the caller must be told — it throws. Either way the
/// tool body does not execute, which is the part that matters.
/// </para>
/// </remarks>
internal sealed class AuthorizingAIFunction(AIFunction inner, IToolAuthorizer authorizer)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var context = new ToolInvocationContext(Name, InnerFunction, arguments);

        ToolAuthorization verdict;
        try
        {
            verdict = await authorizer.AuthorizeAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up. Not a policy decision, and must not be reported as one.
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed. An authorizer that broke has granted nothing, and treating its
            // failure as permission would make every outage a privilege escalation.
            return Refuse(ToolAuthorizationDecision.Abort, $"the authorizer failed: {ex.Message}");
        }

        return verdict.IsAllowed
            ? await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false)
            : Refuse(verdict.Decision, verdict.Reason!);
    }

    private object? Refuse(ToolAuthorizationDecision decision, string reason)
    {
        AgentTelemetry.RecordToolAuthorization(Name, decision);

        if (decision == ToolAuthorizationDecision.Deny)
        {
            return $"Refused: {reason}. Do not retry this call; either proceed without this " +
                   "tool or tell the user what you are unable to do.";
        }

        if (FunctionInvokingChatClient.CurrentContext is { } invocation)
        {
            invocation.Terminate = true;
            return $"Refused: {reason}. This run is over.";
        }

        throw new ToolAuthorizationException(Name, reason);
    }
}
