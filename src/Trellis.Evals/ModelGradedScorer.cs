using Microsoft.Extensions.AI;
using Trellis.Agents;

namespace Trellis.Evals;

/// <summary>
/// Grades an answer with a model, against the case's rubric.
/// </summary>
/// <remarks>
/// <para>
/// For everything a predicate cannot express: whether an answer is actually responsive,
/// whether a refusal was appropriate, whether a summary kept what mattered.
/// </para>
/// <para><b>Know what you are buying.</b> The judge is a model, so it has a model's variance —
/// run enough samples that you are reading a trend and not a coin flip. It costs money on every
/// case, and that cost is real but does not appear in the agent's own numbers; the report keeps
/// them separate for exactly that reason. It is also biased toward long, confident, well-formatted
/// answers, which is precisely the failure mode of an agent that has started padding. Where a
/// programmatic scorer can express the check, prefer it, and keep the judge for what is genuinely
/// a judgement.</para>
/// <para>
/// A grade is requested as a typed result, so the score arrives parsed and validated rather than
/// scraped out of prose — the same self-healing structured output the rest of Trellis uses.
/// </para>
/// </remarks>
public sealed class ModelGradedScorer<TResult> : IEvalScorer<TResult>
{
    private const string JudgeInstructions =
        """
        You grade an AI assistant's answer against a rubric. Be strict and literal: grade only
        what the rubric asks for, not whether you liked the answer. Length and confidence are
        not quality. Reply with a score from 0.0 to 1.0 and one sentence of justification.
        """;

    private readonly Agent<Grade> _judge;
    private readonly string? _defaultRubric;

    /// <param name="judgeClient">
    /// The model doing the grading. Worth making a different model from the one under test:
    /// models rate their own output generously.
    /// </param>
    /// <param name="defaultRubric">Used for cases that carry no rubric of their own.</param>
    /// <param name="name">Names this scorer in the report.</param>
    public ModelGradedScorer(
        IChatClient judgeClient, string? defaultRubric = null, string name = "model_graded")
    {
        ArgumentNullException.ThrowIfNull(judgeClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _judge = new Agent<Grade>(judgeClient, JudgeInstructions);
        _defaultRubric = defaultRubric;
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>The judge's verdict.</summary>
    /// <param name="Score">0.0 to 1.0.</param>
    /// <param name="Justification">One sentence.</param>
    public sealed record Grade(double Score, string Justification);

    /// <inheritdoc />
    public async ValueTask<EvalScore> ScoreAsync(
        EvalContext<TResult> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string rubric = context.Case.Rubric ?? _defaultRubric
            ?? throw new InvalidOperationException(
                $"Case '{context.Case.Id}' has no rubric and the scorer has no default, so there is " +
                "nothing to grade against.");

        string question = string.Join("\n", context.Case.Messages.Select(m => $"{m.Role}: {m.Text}"));

        AgentRunResult<Grade> graded = await _judge.RunAsync(
            $"""
             Conversation:
             {question}

             The assistant answered:
             {context.Text}

             Rubric:
             {rubric}
             """,
            cancellationToken).ConfigureAwait(false);

        // Clamped rather than trusted: a judge asked for 0..1 will occasionally return 5.
        return EvalScore.Partial(graded.Output.Score, graded.Output.Justification);
    }
}
