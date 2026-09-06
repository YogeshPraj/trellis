using Microsoft.Extensions.AI;
using Trellis.Agents;

namespace Trellis.Evals;

/// <summary>What a scorer gets to look at.</summary>
/// <typeparam name="TResult">The agent's result type.</typeparam>
/// <param name="Case">The case that was run.</param>
/// <param name="Output">What the agent produced.</param>
/// <param name="Run">
/// The full run, for scorers that care how the answer was reached — how many self-healing
/// attempts it took, what it cost, which model answered.
/// </param>
public sealed record EvalContext<TResult>(
    EvalCase<TResult> Case,
    TResult Output,
    AgentRunResult<TResult> Run)
{
    /// <summary>The raw response text, for scorers that work on prose.</summary>
    public string Text => Run.Response.Text;

    /// <summary>Token usage, when the provider reported it.</summary>
    public UsageDetails? Usage => Run.Usage;
}
