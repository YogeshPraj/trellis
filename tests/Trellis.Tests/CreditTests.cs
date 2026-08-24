using Microsoft.Extensions.AI;

namespace Trellis.Tests;

/// <summary>The credit ledger and both admission models.</summary>
public class CreditTests
{
    private const long Million = 1_000_000;

    private static ICreditRateModel Rates(decimal input = 1m, decimal output = 1m) =>
        new TokenCostCreditRateModel(new StaticTokenCostModel(
            new Dictionary<string, ModelPrice> { ["m"] = new(input, output) }));

    private static UsageDetails Usage(long input, long output) =>
        new() { InputTokenCount = input, OutputTokenCount = output };

    private static CreditRequest Request(string subject, string id, long input = 0, long? maxOutput = null) =>
        new(subject, id, "m", input, maxOutput);

    // ---------- ledger ----------

    [Fact]
    public async Task BalanceIsTheSumOfEntries()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);

        await account.GrantAsync("u1", 10 * Million, "g1");
        await account.GrantAsync("u1", 5 * Million, "g2");
        await account.DeductAsync("u1", 3 * Million, "a1");

        Assert.Equal(12 * Million, await account.GetBalanceAsync("u1"));
    }

    [Fact]
    public async Task ARepeatedGrantIdCannotDoubleCredit()
    {
        var account = new CreditAccount(new InMemoryCreditLedger());

        Assert.True(await account.GrantAsync("u1", 10 * Million, "plan-2026-08"));
        Assert.False(await account.GrantAsync("u1", 10 * Million, "plan-2026-08"));

        // A retried or duplicated monthly top-up grants once.
        Assert.Equal(10 * Million, await account.GetBalanceAsync("u1"));
    }

    [Fact]
    public async Task BalancesAreIsolatedPerSubject()
    {
        var account = new CreditAccount(new InMemoryCreditLedger());
        await account.GrantAsync("u1", 10 * Million, "g1");

        Assert.Equal(0, await account.GetBalanceAsync("u2"));
    }

    [Fact]
    public async Task HistoryIsNewestFirst()
    {
        var account = new CreditAccount(new InMemoryCreditLedger());
        await account.GrantAsync("u1", Million, "g1", "first");
        await account.GrantAsync("u1", Million, "g2", "second");

        IReadOnlyList<CreditEntry> history = await account.GetHistoryAsync("u1");

        Assert.Equal("second", history[0].Reason);
    }

    // ---------- post-charge ----------

    [Fact]
    public async Task PostCharge_AdmitsOnPositiveBalance_ThenChargesActualUsage()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);
        await account.GrantAsync("u1", 10 * Million, "g1");
        var policy = new PostChargeCreditPolicy(ledger, Rates());

        CreditRequest request = Request("u1", "req1");
        CreditAdmission admission = await policy.AdmitAsync(request);
        Assert.True(admission.IsAllowed);
        Assert.Null(admission.ReservationId);   // nothing held

        await policy.SettleAsync(request, admission, Usage(1_000_000, 500_000));

        // 1M input + 0.5M output at 1.0/M each = 1.5 units = 1.5M micro-credits.
        Assert.Equal(10 * Million - 1_500_000, await account.GetBalanceAsync("u1"));
    }

    [Fact]
    public async Task PostCharge_RefusesWhenTheBalanceIsExhausted()
    {
        var ledger = new InMemoryCreditLedger();
        var policy = new PostChargeCreditPolicy(ledger, Rates());

        CreditAdmission admission = await policy.AdmitAsync(Request("broke", "req1"));

        Assert.False(admission.IsAllowed);
        Assert.Contains("minimum", admission.Reason);
    }

    [Fact]
    public async Task PostCharge_AllowsTheBalanceToGoNegative_ThenBlocksTheNextCall()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);
        await account.GrantAsync("u1", 1, "g1");        // a single micro-credit
        var policy = new PostChargeCreditPolicy(ledger, Rates());

        CreditRequest first = Request("u1", "req1");
        CreditAdmission admitted = await policy.AdmitAsync(first);
        Assert.True(admitted.IsAllowed);
        await policy.SettleAsync(first, admitted, Usage(1_000_000, 0));

        // The overshoot is the documented cost of post-charge...
        Assert.True(await account.GetBalanceAsync("u1") < 0);

        // ...and the next request is refused.
        Assert.False((await policy.AdmitAsync(Request("u1", "req2"))).IsAllowed);
    }

    [Fact]
    public async Task PostCharge_SettlingTwiceChargesOnce()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);
        await account.GrantAsync("u1", 10 * Million, "g1");
        var policy = new PostChargeCreditPolicy(ledger, Rates());

        CreditRequest request = Request("u1", "req1");
        CreditAdmission admission = await policy.AdmitAsync(request);
        await policy.SettleAsync(request, admission, Usage(Million, 0));
        await policy.SettleAsync(request, admission, Usage(Million, 0));   // retry

        Assert.Equal(9 * Million, await account.GetBalanceAsync("u1"));
    }

    [Fact]
    public async Task PostCharge_RefusesUnpricedModelsByDefault()
    {
        var ledger = new InMemoryCreditLedger();
        await new CreditAccount(ledger).GrantAsync("u1", 10 * Million, "g1");
        var policy = new PostChargeCreditPolicy(ledger, Rates());

        var request = new CreditRequest("u1", "req1", "unpriced-model", 0, null);
        await policy.SettleAsync(request, CreditAdmission.Allowed, Usage(Million, Million));

        // Serving an unpriced model free would be a silent revenue hole; nothing is charged
        // and nothing is recorded, so the gap is visible rather than absorbed.
        Assert.Equal(10 * Million, await new CreditAccount(ledger).GetBalanceAsync("u1"));
    }

    // ---------- prepaid ----------

    [Fact]
    public async Task Prepaid_HoldsTheWorstCaseThenReleasesTheDifference()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);
        await account.GrantAsync("u1", 10 * Million, "g1");
        var policy = new PrepaidCreditPolicy(ledger, Rates());

        CreditRequest request = Request("u1", "req1", input: 1_000_000, maxOutput: 1_000_000);
        CreditAdmission admission = await policy.AdmitAsync(request);

        // 1M input + 1M max output = 2 units held up front.
        Assert.True(admission.IsAllowed);
        Assert.Equal(2 * Million, admission.ReservedCredits);
        Assert.Equal(8 * Million, await account.GetBalanceAsync("u1"));

        await policy.SettleAsync(request, admission, Usage(1_000_000, 100_000));

        // Actual was 1.1 units; the other 0.9 comes back.
        Assert.Equal(10 * Million - 1_100_000, await account.GetBalanceAsync("u1"));
    }

    [Fact]
    public async Task Prepaid_CannotOverspendUnderConcurrency()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);
        await account.GrantAsync("u1", 3 * Million, "g1");   // room for one 2-unit request
        var policy = new PrepaidCreditPolicy(ledger, Rates());

        CreditAdmission first = await policy.AdmitAsync(Request("u1", "a", 1_000_000, 1_000_000));
        CreditAdmission second = await policy.AdmitAsync(Request("u1", "b", 1_000_000, 1_000_000));

        // This is the whole point: the second request sees the first one's hold.
        Assert.True(first.IsAllowed);
        Assert.False(second.IsAllowed);
    }

    [Fact]
    public async Task Prepaid_RefusesWithoutAnOutputCap()
    {
        var ledger = new InMemoryCreditLedger();
        await new CreditAccount(ledger).GrantAsync("u1", 100 * Million, "g1");
        var policy = new PrepaidCreditPolicy(ledger, Rates());

        CreditAdmission admission = await policy.AdmitAsync(Request("u1", "req1", 1000, maxOutput: null));

        Assert.False(admission.IsAllowed);
        Assert.Contains("output cap", admission.Reason);
    }

    [Fact]
    public async Task Prepaid_UsesTheConfiguredDefaultCapWhenTheRequestHasNone()
    {
        var ledger = new InMemoryCreditLedger();
        await new CreditAccount(ledger).GrantAsync("u1", 100 * Million, "g1");
        var policy = new PrepaidCreditPolicy(ledger, Rates(), defaultMaxOutputTokens: 4096);

        Assert.True((await policy.AdmitAsync(Request("u1", "req1", 1000, maxOutput: null))).IsAllowed);
    }

    [Fact]
    public async Task Prepaid_OvershootIsChargedNotRefunded()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);
        await account.GrantAsync("u1", 10 * Million, "g1");
        var policy = new PrepaidCreditPolicy(ledger, Rates());

        CreditRequest request = Request("u1", "req1", input: 100_000, maxOutput: 100_000);
        CreditAdmission admission = await policy.AdmitAsync(request);

        // The provider reported more than the cap: the ledger stays arithmetically true.
        await policy.SettleAsync(request, admission, Usage(1_000_000, 1_000_000));

        Assert.Equal(10 * Million - 2 * Million, await account.GetBalanceAsync("u1"));
    }

    [Fact]
    public async Task Prepaid_ExpiredHoldsAreReleased()
    {
        var time = new FakeTimeProvider();
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger, time);
        await account.GrantAsync("u1", 10 * Million, "g1");
        var policy = new PrepaidCreditPolicy(
            ledger, Rates(), reservationLifetime: TimeSpan.FromMinutes(10), timeProvider: time);

        // A request that is admitted and then dies without settling.
        await policy.AdmitAsync(Request("u1", "crashed", 1_000_000, 1_000_000));
        Assert.Equal(8 * Million, await account.GetBalanceAsync("u1"));

        Assert.Equal(0, await policy.ReleaseExpiredAsync("u1"));   // not yet expired
        time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(1, await policy.ReleaseExpiredAsync("u1"));
        Assert.Equal(10 * Million, await account.GetBalanceAsync("u1"));

        // Sweeping twice must not credit twice.
        Assert.Equal(0, await policy.ReleaseExpiredAsync("u1"));
        Assert.Equal(10 * Million, await account.GetBalanceAsync("u1"));
    }

    [Fact]
    public async Task Prepaid_ASettledHoldIsNotSweptLater()
    {
        var time = new FakeTimeProvider();
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger, time);
        await account.GrantAsync("u1", 10 * Million, "g1");
        var policy = new PrepaidCreditPolicy(
            ledger, Rates(), reservationLifetime: TimeSpan.FromMinutes(10), timeProvider: time);

        CreditRequest request = Request("u1", "req1", 1_000_000, 1_000_000);
        CreditAdmission admission = await policy.AdmitAsync(request);
        await policy.SettleAsync(request, admission, Usage(1_000_000, 1_000_000));

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(0, await policy.ReleaseExpiredAsync("u1"));
        Assert.Equal(8 * Million, await account.GetBalanceAsync("u1"));
    }

    // ---------- pipeline integration ----------

    [Fact]
    public async Task MeteringClient_ChargesARealCall()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);
        await account.GrantAsync("u1", 10 * Million, "g1");

        IChatClient client = new UsageClient(1_000_000, 0)
            .AsBuilder()
            .UseCredits(new PostChargeCreditPolicy(ledger, Rates()), _ => "u1")
            .Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" });

        Assert.Equal(9 * Million, await account.GetBalanceAsync("u1"));
    }

    [Fact]
    public async Task MeteringClient_RefusesWhenOutOfCredits()
    {
        var ledger = new InMemoryCreditLedger();
        IChatClient client = new UsageClient(1000, 0)
            .AsBuilder()
            .UseCredits(new PostChargeCreditPolicy(ledger, Rates()), _ => "broke")
            .Build();

        await Assert.ThrowsAsync<InsufficientCreditsException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" }));
    }

    [Fact]
    public async Task MeteringClient_ReleasesTheHoldWhenTheCallThrows()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);
        await account.GrantAsync("u1", 10 * Million, "g1");

        IChatClient client = new ThrowingClient()
            .AsBuilder()
            .UseCredits(new PrepaidCreditPolicy(ledger, Rates(), defaultMaxOutputTokens: 1_000_000), _ => "u1")
            .Build();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" }));

        // A failed call must not strand the hold.
        Assert.Equal(10 * Million, await account.GetBalanceAsync("u1"));
    }

    [Fact]
    public async Task MeteringClient_SettlesAnAbandonedStream()
    {
        var ledger = new InMemoryCreditLedger();
        var account = new CreditAccount(ledger);
        await account.GrantAsync("u1", 10 * Million, "g1");

        IChatClient client = new UsageClient(1_000_000, 0)
            .AsBuilder()
            .UseCredits(new PrepaidCreditPolicy(ledger, Rates(), defaultMaxOutputTokens: 1_000_000), _ => "u1")
            .Build();

        await using (IAsyncEnumerator<ChatResponseUpdate> stream = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" })
            .GetAsyncEnumerator())
        {
            await stream.MoveNextAsync();   // walk away mid-stream
        }

        // The hold is settled on disposal rather than stranded.
        Assert.True(await account.GetBalanceAsync("u1") > 8 * Million);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class UsageClient(long input, long output) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                ModelId = options?.ModelId,
                Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok") { ModelId = options?.ModelId };
            yield return new ChatResponseUpdate
            {
                ModelId = options?.ModelId,
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })],
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("provider down");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("provider down");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
