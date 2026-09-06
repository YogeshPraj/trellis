using Microsoft.Extensions.AI;

namespace Trellis.Agents.Teams;

/// <summary>Builds the <c>transfer_to_*</c> tools an agent uses to hand a conversation on.</summary>
/// <remarks>
/// Handoff is expressed as a tool call rather than as parsed text because a tool call is the
/// one thing models are already trained to emit precisely: it has a schema, the provider
/// validates it, and there is no prose to misread. It also means handoff targets appear in the
/// model's tool list, described, alongside everything else it can do.
/// </remarks>
internal static class HandoffTools
{
    public const string Prefix = "transfer_to_";

    /// <summary>One tool per agent this agent may transfer to.</summary>
    public static IReadOnlyList<AITool> For<TResult>(
        IEnumerable<IAgent<TResult>> targets, string currentAgentName)
    {
        List<AITool> tools = [];
        foreach (IAgent<TResult> target in targets)
        {
            // An agent that can transfer to itself will, eventually, and burn the handoff
            // budget doing it.
            if (string.Equals(target.Name, currentAgentName, StringComparison.Ordinal))
            {
                continue;
            }

            string targetName = target.Name;
            tools.Add(AIFunctionFactory.Create(
                (string? reason) =>
                {
                    HandoffSignal.Current?.Set(targetName, reason);

                    // Stop the tool loop here. Without this the model would be asked to
                    // continue the conversation it just said it was not the right agent for,
                    // at a round trip's cost, and the answer would come from the wrong agent.
                    if (FunctionInvokingChatClient.CurrentContext is { } invocation)
                    {
                        invocation.Terminate = true;
                    }
                    return $"Transferring to {targetName}.";
                },
                name: Prefix + targetName,
                description: $"Hand the conversation to the '{targetName}' agent, which handles: "
                    + $"{target.Description}. Use this when the request is better answered there. "
                    + "Give a short reason."));
        }
        return tools;
    }
}
