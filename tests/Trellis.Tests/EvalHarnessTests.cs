using Microsoft.Extensions.AI;
using Trellis.Agents.Teams;
using Trellis.Evals;

namespace Trellis.Tests;

/// <summary>
/// The eval harness. The properties that matter are not "does it compute a mean" — they are
/// that an outage is not reported as a regression, and that noise is not reported as a signal.
/// </summary>
public class EvalHarnessTests
{
    /// <summary>An agent that answers from a script, cycling if the suite runs more samples.</summary>
    private sealed class StubAgent(params string[] answers) : IAgent<string>
    {
        private int _call = -1;

        public int Calls => _call + 1;

        public string Name => "stub";

        public string Description => "answers from a script";

        public Task<AgentTurn<string>> TakeTurnAsync(
            IEnumerable<ChatMessage> messages,
            IReadOnlyList<AITool>? additionalTools = null,
            CancellationToken cancellationToken = default)
        {
            string answer = answers[Interlocked.Increment(ref _call) % answers.Length];
            return Task.FromResult(AgentTurn<string>.Answered(
                new AgentRunResult<string>(answer, new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)))));
        }
    }

    /// <summary>An agent that always fails, standing in for a provider outage.</summary>
    private sealed class BrokenAgent : IAgent<string>
    {
        public string Name => "broken";

        public string Description => "always fails";

        public Task<AgentTurn<string>> TakeTurnAsync(
            IEnumerable<ChatMessage> messages,
            IReadOnlyList<AITool>? additionalTools = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentTurn<string>>(new HttpRequestException("503 service unavailable"));
    }

    private static EvalSuite<string> Suite(
        IEnumerable<EvalCase<string>> cases, EvalOptions? options = null) =>
        new("suite", cases, [PredicateScorer<string>.ExactMatch()], options);

    [Fact]
    public async Task AMatchingAnswerPasses()
    {
        EvalSuite<string> suite = Suite([new EvalCase<string>("c1", "hi") { Expected = "hello" }]);

        EvalReport report = await suite.RunAsync(new StubAgent("hello"));

        Assert.Equal(1, report.PassedCount);
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(1.0, report.Mean);
    }

    [Fact]
    public async Task AWrongAnswerFails_WithAnExplanation()
    {
        EvalSuite<string> suite = Suite([new EvalCase<string>("c1", "hi") { Expected = "hello" }]);

        EvalReport report = await suite.RunAsync(new StubAgent("goodbye"));

        Assert.Equal(1, report.FailedCount);
        Assert.Contains("expected 'hello'", report.Cases[0].Scores[0].Explanations[0]);
    }

    [Fact]
    public async Task AnOutageIsErrored_NotScoredZero()
    {
        EvalSuite<string> suite = Suite([new EvalCase<string>("c1", "hi") { Expected = "hello" }]);

        EvalReport report = await suite.RunAsync(new BrokenAgent());

        // The distinction the whole report rests on: a 503 is not evidence the agent got worse.
        // Averaging it in as zero would file an incident as a quality regression.
        Assert.Equal(1, report.ErroredCount);
        Assert.Contains("503", report.Cases[0].Error);
        Assert.Equal(0, report.Mean);
        Assert.Equal(0, report.FailedCount);
    }

    [Fact]
    public async Task ErroredCasesDoNotDragDownTheMean()
    {
        var suite = new EvalSuite<string>(
            "suite",
            [new EvalCase<string>("ok", "hi") { Expected = "hello" }],
            [PredicateScorer<string>.ExactMatch()]);

        EvalReport good = await suite.RunAsync(new StubAgent("hello"));
        EvalReport broken = await suite.RunAsync(new BrokenAgent());

        Assert.Equal(1.0, good.Mean);
        // Mean is over cases that actually ran, so it is 0 here because nothing scored — not
        // because a zero was averaged in.
        Assert.Equal(0, broken.Mean);
        Assert.Equal(1, broken.ErroredCount);
    }

    [Fact]
    public async Task EverySampleRuns_AndTheSpreadIsReported()
    {
        // Alternating answers: half right, half wrong. Exactly the shape of a flaky agent.
        var agent = new StubAgent("hello", "goodbye");
        EvalSuite<string> suite = Suite(
            [new EvalCase<string>("c1", "hi") { Expected = "hello" }],
            new EvalOptions { Samples = 4, PassThreshold = 0.9 });

        EvalReport report = await suite.RunAsync(agent);

        Assert.Equal(4, agent.Calls);
        EvalScoreSummary score = report.Cases[0].Scores[0];
        Assert.Equal(0.5, score.Mean);
        // The spread is what tells a reader this number is not stable.
        Assert.Equal(1.0, score.Spread);
        Assert.False(report.Cases[0].Passed);
    }

    [Fact]
    public async Task CasesAreOrderedById_SoTwoReportsDiffCleanly()
    {
        EvalSuite<string> suite = Suite(
        [
            new EvalCase<string>("zebra", "hi") { Expected = "hello" },
            new EvalCase<string>("alpha", "hi") { Expected = "hello" },
            new EvalCase<string>("middle", "hi") { Expected = "hello" },
        ]);

        EvalReport report = await suite.RunAsync(new StubAgent("hello"));

        Assert.Equal(["alpha", "middle", "zebra"], report.Cases.Select(c => c.CaseId));
    }

    [Fact]
    public void DuplicateCaseIds_AreRefused()
    {
        // Baselines are keyed on the id, so a duplicate would silently stop being measured.
        Assert.Throws<ArgumentException>(() => Suite(
        [
            new EvalCase<string>("same", "a") { Expected = "x" },
            new EvalCase<string>("same", "b") { Expected = "y" },
        ]));
    }

    [Fact]
    public void ASuiteWithNoScorers_IsRefused()
    {
        // It could never fail, so it could never help.
        Assert.Throws<ArgumentException>(() => new EvalSuite<string>(
            "suite", [new EvalCase<string>("c1", "hi")], []));
    }

    [Fact]
    public async Task ATopLevelHandoffIsErrored_NotScored()
    {
        EvalSuite<string> suite = Suite([new EvalCase<string>("c1", "hi") { Expected = "hello" }]);

        EvalReport report = await suite.RunAsync(new HandingOffAgent());

        // Nothing answered, so scoring the non-answer would blame the answer for what is
        // actually a routing problem.
        Assert.Equal(1, report.ErroredCount);
        Assert.Contains("handed off", report.Cases[0].Error);
    }

    private sealed class HandingOffAgent : IAgent<string>
    {
        public string Name => "router";

        public string Description => "always defers";

        public Task<AgentTurn<string>> TakeTurnAsync(
            IEnumerable<ChatMessage> messages,
            IReadOnlyList<AITool>? additionalTools = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentTurn<string>.HandedOff("someone_else", "not mine"));
    }

    [Fact]
    public async Task AReportRoundTripsThroughJson()
    {
        EvalSuite<string> suite = Suite([new EvalCase<string>("c1", "hi") { Expected = "hello" }]);
        EvalReport report = await suite.RunAsync(new StubAgent("hello"));

        EvalReport restored = EvalReport.FromJson(report.ToJson());

        // A baseline that cannot be stored and read back is not a baseline.
        Assert.Equal(report.SuiteName, restored.SuiteName);
        Assert.Equal(report.Mean, restored.Mean);
        Assert.Equal(report.Cases[0].CaseId, restored.Cases[0].CaseId);
        Assert.Equal(report.Cases[0].Scores[0].Spread, restored.Cases[0].Scores[0].Spread);
    }

    [Fact]
    public async Task ScorersAllRun_AndTheCaseMeanCombinesThem()
    {
        var suite = new EvalSuite<string>(
            "suite",
            [new EvalCase<string>("c1", "hi") { Expected = "hello" }],
            [
                PredicateScorer<string>.ExactMatch(),
                PredicateScorer<string>.Contains("nowhere-in-the-answer"),
            ]);

        EvalReport report = await suite.RunAsync(new StubAgent("hello"));

        Assert.Equal(2, report.Cases[0].Scores.Count);
        Assert.Equal(0.5, report.Cases[0].Mean);
    }
}

/// <summary>Baseline comparison, and specifically its refusal to call noise a regression.</summary>
public class EvalComparisonTests
{
    private static EvalReport Report(params (string Id, double Mean, double Min, double Max)[] cases) =>
        new()
        {
            SuiteName = "suite",
            CompletedAt = DateTimeOffset.UnixEpoch,
            Samples = 3,
            Cases =
            [
                .. cases.Select(c => new EvalCaseResult
                {
                    CaseId = c.Id,
                    Scores = [new EvalScoreSummary("s", c.Mean, c.Min, c.Max, [])],
                    Mean = c.Mean,
                    Passed = c.Mean >= 1.0,
                    Samples = 3,
                    AverageDuration = TimeSpan.Zero,
                }),
            ],
        };

    [Fact]
    public void ADropLargerThanTheBaselineSpread_IsARegression()
    {
        EvalReport baseline = Report(("c1", 1.0, 1.0, 1.0));
        EvalReport candidate = Report(("c1", 0.4, 0.4, 0.4));

        EvalComparison comparison = EvalComparison.Against(baseline, candidate);

        EvalCaseDelta delta = Assert.Single(comparison.Regressions);
        Assert.Equal("c1", delta.CaseId);
        Assert.Equal(-0.6, delta.Change, 3);
    }

    [Fact]
    public void ADropSmallerThanTheBaselineSpread_IsNotARegression()
    {
        // The baseline itself swung between 0.6 and 1.0, so a mean of 0.7 tells you nothing.
        EvalReport baseline = Report(("c1", 0.8, 0.6, 1.0));
        EvalReport candidate = Report(("c1", 0.7, 0.7, 0.7));

        EvalComparison comparison = EvalComparison.Against(baseline, candidate);

        // Calling this a regression is how teams end up chasing changes they never made.
        Assert.Empty(comparison.Regressions);
        Assert.True(comparison.Deltas[0].IsWithinNoise);
    }

    [Fact]
    public void AGainLargerThanTheSpread_IsAnImprovement()
    {
        EvalReport baseline = Report(("c1", 0.2, 0.2, 0.2));
        EvalReport candidate = Report(("c1", 0.9, 0.9, 0.9));

        EvalComparison comparison = EvalComparison.Against(baseline, candidate);

        Assert.Single(comparison.Improvements);
        Assert.Empty(comparison.Regressions);
    }

    [Fact]
    public void NewAndMissingCasesAreNamed_NotSilentlyScored()
    {
        EvalReport baseline = Report(("kept", 1.0, 1.0, 1.0), ("dropped", 1.0, 1.0, 1.0));
        EvalReport candidate = Report(("kept", 1.0, 1.0, 1.0), ("added", 1.0, 1.0, 1.0));

        EvalComparison comparison = EvalComparison.Against(baseline, candidate);

        // A case that vanished is a change to the suite, not a change in quality, and reporting
        // it as either a pass or a regression would hide that someone deleted coverage.
        Assert.Equal(["added"], comparison.NewCases);
        Assert.Equal(["dropped"], comparison.MissingCases);
        Assert.Empty(comparison.Regressions);
    }

    [Fact]
    public void TheOverallMeanDeltaIsReported()
    {
        EvalReport baseline = Report(("a", 1.0, 1.0, 1.0), ("b", 1.0, 1.0, 1.0));
        EvalReport candidate = Report(("a", 1.0, 1.0, 1.0), ("b", 0.0, 0.0, 0.0));

        EvalComparison comparison = EvalComparison.Against(baseline, candidate);

        Assert.Equal(-0.5, comparison.MeanDelta, 3);
    }

    [Fact]
    public void ASingleSampleBaselineHasNoTolerance_SoEveryMoveLooksReal()
    {
        // Documented consequence rather than a hidden one: with one sample the spread is zero,
        // so the comparison cannot tell a real change from a different roll of the dice. This
        // is the argument for raising Samples before trusting a verdict.
        EvalReport baseline = Report(("c1", 1.0, 1.0, 1.0));
        EvalReport candidate = Report(("c1", 0.99, 0.99, 0.99));

        EvalComparison comparison = EvalComparison.Against(baseline, candidate);

        Assert.Single(comparison.Regressions);
    }
}

/// <summary>
/// The harness against real Trellis agents and a real model-graded scorer, rather than stubs —
/// the pieces have to actually compose, not just each work alone.
/// </summary>
public class EvalIntegrationTests
{
    [Fact]
    public async Task ASuiteRunsAgainstARealAgent()
    {
        var agent = new Agent<string>(new FakeChatClient("Paris"));
        var suite = new EvalSuite<string>(
            "geography",
            [new EvalCase<string>("capital", "Capital of France?") { Expected = "Paris" }],
            [PredicateScorer<string>.ExactMatch(), PredicateScorer<string>.Contains("Paris")]);

        EvalReport report = await suite.RunAsync(agent);

        Assert.Equal(1, report.PassedCount);
        Assert.Equal(1.0, report.Mean);
    }

    [Fact]
    public async Task ASuiteRunsAgainstATeam()
    {
        var triage = new Agent<string>(new FakeChatClient("handled")) { Name = "triage" };
        var team = new AgentTeam<string>(triage, [triage]);

        var suite = new EvalSuite<string>(
            "routing",
            [new EvalCase<string>("c1", "hi") { Expected = "handled" }],
            [PredicateScorer<string>.ExactMatch()]);

        // A team is an IAgent, so the same suite measures a single agent and a whole team.
        EvalReport report = await suite.RunAsync(team);

        Assert.Equal(1, report.PassedCount);
    }

    [Fact]
    public async Task TheModelGradedScorerParsesAJudgesVerdict()
    {
        var judge = new FakeChatClient("""{"Score":0.75,"Justification":"mostly right, missed the caveat"}""");
        var scorer = new ModelGradedScorer<string>(judge);

        var suite = new EvalSuite<string>(
            "graded",
            [new EvalCase<string>("c1", "explain recursion") { Rubric = "must mention a base case" }],
            [scorer]);

        EvalReport report = await suite.RunAsync(new Agent<string>(new FakeChatClient("a function calling itself")));

        // The grade arrives parsed and validated through Trellis's own structured output rather
        // than scraped out of prose.
        Assert.Equal(0.75, report.Cases[0].Mean, 3);
        Assert.Contains("missed the caveat", report.Cases[0].Scores[0].Explanations[0]);
    }

    [Fact]
    public async Task AJudgeReturningAnOutOfRangeScore_IsClamped()
    {
        var judge = new FakeChatClient("""{"Score":5.0,"Justification":"great"}""");
        var suite = new EvalSuite<string>(
            "graded",
            [new EvalCase<string>("c1", "hi") { Rubric = "anything" }],
            [new ModelGradedScorer<string>(judge)]);

        EvalReport report = await suite.RunAsync(new Agent<string>(new FakeChatClient("hi")));

        // A judge asked for 0..1 will occasionally return 5, and one such case must not be able
        // to drag a whole suite's mean above everything else.
        Assert.Equal(1.0, report.Cases[0].Mean);
    }

    [Fact]
    public async Task ACaseWithNoRubricAndNoDefault_IsErroredNotGuessed()
    {
        var suite = new EvalSuite<string>(
            "graded",
            [new EvalCase<string>("c1", "hi")],
            [new ModelGradedScorer<string>(new FakeChatClient("{}"))]);

        EvalReport report = await suite.RunAsync(new Agent<string>(new FakeChatClient("hi")));

        Assert.Equal(1, report.ErroredCount);
        Assert.Contains("no rubric", report.Cases[0].Error);
    }

    [Fact]
    public async Task CostIsReportedWhenAPriceListIsSupplied()
    {
        var costModel = new StaticTokenCostModel(
            new Dictionary<string, ModelPrice> { ["m"] = new(3.00m, 15.00m) });

        var suite = new EvalSuite<string>(
            "priced",
            [new EvalCase<string>("c1", "hi") { Expected = "ok" }],
            [PredicateScorer<string>.ExactMatch()],
            costModel: costModel);

        EvalReport report = await suite.RunAsync(new UsageAgent());

        // Cost per case is what makes "cheaper model, is it worse?" answerable in one run.
        Assert.NotNull(report.EstimatedCost);
        Assert.Equal(2_000, report.TotalTokens);
    }

    [Fact]
    public async Task CostIsNullRatherThanZero_WhenNothingPricedIt()
    {
        var suite = new EvalSuite<string>(
            "unpriced",
            [new EvalCase<string>("c1", "hi") { Expected = "ok" }],
            [PredicateScorer<string>.ExactMatch()]);

        EvalReport report = await suite.RunAsync(new UsageAgent());

        // Zero would read as free. Null reads as unknown, which is the truth.
        Assert.Null(report.EstimatedCost);
        Assert.NotNull(report.TotalTokens);
    }

    /// <summary>Answers "ok" and reports usage, so cost accounting has something to price.</summary>
    private sealed class UsageAgent : IAgent<string>
    {
        public string Name => "usage";

        public string Description => "reports usage";

        public Task<AgentTurn<string>> TakeTurnAsync(
            IEnumerable<ChatMessage> messages,
            IReadOnlyList<AITool>? additionalTools = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                ModelId = "m",
                Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 1000 },
            };
            return Task.FromResult(AgentTurn<string>.Answered(new AgentRunResult<string>("ok", response)));
        }
    }
}
