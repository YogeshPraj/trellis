using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Trellis.Agents;
using Trellis.Agents.Teams;
using Trellis.Diagnostics;

namespace Trellis.Evals;

/// <summary>
/// A named set of cases and the scorers that judge them, runnable against any agent.
/// </summary>
/// <remarks>
/// <para>
/// The target is an <see cref="IAgent{TResult}"/>, so the same suite runs against a single
/// agent, a differently-prompted one, one on a cheaper model, or a whole team — which is the
/// point. "Did switching models make this worse" is a question you answer by running one suite
/// twice, not by writing a second suite.
/// </para>
/// <para>
/// A suite reports; it does not decide. Whether a 2% drop matters is a judgement about the
/// product, so <see cref="EvalComparison"/> hands back the numbers and the observed noise, and
/// leaves the verdict to the caller or their CI.
/// </para>
/// </remarks>
public sealed class EvalSuite<TResult>
{
    private readonly IReadOnlyList<EvalCase<TResult>> _cases;
    private readonly IReadOnlyList<IEvalScorer<TResult>> _scorers;
    private readonly EvalOptions _options;
    private readonly ITokenCostModel? _costModel;

    /// <param name="name">Names the suite in reports.</param>
    /// <param name="cases">The cases. Ids must be unique — a baseline is keyed on them.</param>
    /// <param name="scorers">Every scorer runs on every case.</param>
    /// <param name="options">Sampling, concurrency, threshold.</param>
    /// <param name="costModel">Prices the runs. Null leaves cost unreported rather than zero.</param>
    public EvalSuite(
        string name,
        IEnumerable<EvalCase<TResult>> cases,
        IEnumerable<IEvalScorer<TResult>> scorers,
        EvalOptions? options = null,
        ITokenCostModel? costModel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(scorers);

        Name = name;
        _cases = [.. cases];
        _scorers = [.. scorers];
        _options = options ?? new EvalOptions();
        _costModel = costModel;

        if (_scorers.Count == 0)
        {
            throw new ArgumentException("A suite with no scorers cannot fail, so it cannot help.", nameof(scorers));
        }

        string[] duplicates = [.. _cases.GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key)];
        if (duplicates.Length > 0)
        {
            // Baselines are keyed on the id, so duplicates would silently overwrite each other
            // and a case would quietly stop being measured.
            throw new ArgumentException(
                $"Duplicate case ids: {string.Join(", ", duplicates)}. Ids key the baseline, so they must be unique.",
                nameof(cases));
        }
        if (_options.Samples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.Samples, "Samples must be at least 1.");
        }
        if (_options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxConcurrency, "MaxConcurrency must be at least 1.");
        }
    }

    /// <summary>The suite's name.</summary>
    public string Name { get; }

    /// <summary>Runs every case against <paramref name="agent"/>.</summary>
    public async Task<EvalReport> RunAsync(IAgent<TResult> agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        ConcurrentDictionary<string, EvalCaseResult> results = new(StringComparer.Ordinal);

        await Parallel.ForEachAsync(
            _cases,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxConcurrency,
                CancellationToken = cancellationToken,
            },
            async (evalCase, ct) =>
            {
                EvalCaseResult result = await RunCaseAsync(agent, evalCase, ct).ConfigureAwait(false);
                results[evalCase.Id] = result;
            }).ConfigureAwait(false);

        return new EvalReport
        {
            SuiteName = Name,
            CompletedAt = DateTimeOffset.UtcNow,
            Samples = _options.Samples,
            // Ordered so two reports diff cleanly; concurrency makes completion order meaningless.
            Cases = [.. results.Values.OrderBy(c => c.CaseId, StringComparer.Ordinal)],
        };
    }

    private async Task<EvalCaseResult> RunCaseAsync(
        IAgent<TResult> agent, EvalCase<TResult> evalCase, CancellationToken cancellationToken)
    {
        var perScorer = new Dictionary<string, List<EvalScore>>(StringComparer.Ordinal);
        var elapsed = TimeSpan.Zero;
        long tokens = 0;
        var sawTokens = false;
        decimal cost = 0m;
        var sawCost = false;

        try
        {
            for (var sample = 0; sample < _options.Samples; sample++)
            {
                long startedAt = Stopwatch.GetTimestamp();
                AgentTurn<TResult> turn = await agent
                    .TakeTurnAsync(evalCase.Messages, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                elapsed += Stopwatch.GetElapsedTime(startedAt);

                if (turn.IsHandoff)
                {
                    // A top-level handoff means nothing answered, so there is nothing to score.
                    // Reporting it as a low score would blame the answer for a routing problem.
                    throw new InvalidOperationException(
                        $"The agent handed off to '{turn.HandoffTarget}' instead of answering. " +
                        "Evaluate the team rather than one of its members.");
                }

                AgentRunResult<TResult> run = turn.Result!;
                if (run.Usage is { } usage)
                {
                    sawTokens = true;
                    tokens += (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0);

                    if (_costModel?.EstimateCost(run.Response.ModelId, usage) is { } sampleCost)
                    {
                        sawCost = true;
                        cost += sampleCost;
                    }
                }

                var context = new EvalContext<TResult>(evalCase, run.Output, run);
                foreach (IEvalScorer<TResult> scorer in _scorers)
                {
                    EvalScore score = await scorer.ScoreAsync(context, cancellationToken).ConfigureAwait(false);
                    if (!perScorer.TryGetValue(scorer.Name, out List<EvalScore>? scores))
                    {
                        perScorer[scorer.Name] = scores = [];
                    }
                    scores.Add(score);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Errored, not zero. A provider outage or a broken judge is not evidence that the
            // agent got worse, and averaging it in as zero would report an incident as a
            // regression.
            return new EvalCaseResult
            {
                CaseId = evalCase.Id,
                Scores = [],
                Mean = 0,
                Passed = false,
                Samples = _options.Samples,
                AverageDuration = TimeSpan.Zero,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                Tags = evalCase.Tags,
            };
        }

        List<EvalScoreSummary> summaries =
        [
            .. perScorer.Select(pair => new EvalScoreSummary(
                pair.Key,
                pair.Value.Average(s => s.Value),
                pair.Value.Min(s => s.Value),
                pair.Value.Max(s => s.Value),
                [.. pair.Value.Select(s => s.Explanation ?? string.Empty)]))
                .OrderBy(s => s.Scorer, StringComparer.Ordinal),
        ];

        double mean = summaries.Average(s => s.Mean);
        return new EvalCaseResult
        {
            CaseId = evalCase.Id,
            Scores = summaries,
            Mean = mean,
            Passed = mean >= _options.PassThreshold,
            Samples = _options.Samples,
            AverageDuration = elapsed / _options.Samples,
            TotalTokens = sawTokens ? tokens : null,
            EstimatedCost = sawCost ? cost : null,
            Tags = evalCase.Tags,
        };
    }
}
