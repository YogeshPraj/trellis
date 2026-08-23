using Microsoft.Extensions.AI;

namespace Trellis.Tests;

/// <summary>Cached-input discounts and long-context pricing tiers.</summary>
public class PricingTests
{
    private static StaticTokenCostModel Model(string id, ModelPrice price) =>
        new(new Dictionary<string, ModelPrice> { [id] = price });

    private static UsageDetails Usage(long input, long output, long? cached = null) =>
        new() { InputTokenCount = input, OutputTokenCount = output, CachedInputTokenCount = cached };

    [Fact]
    public void FlatPricing_ChargesInputAndOutputSeparately()
    {
        StaticTokenCostModel model = Model("m", new ModelPrice(3.00m, 15.00m));

        // 1M input at $3 + 1M output at $15.
        Assert.Equal(18.00m, model.EstimateCost("m", Usage(1_000_000, 1_000_000)));
    }

    [Fact]
    public void CachedTokens_AreDiscounted_AndNotDoubleCounted()
    {
        StaticTokenCostModel model = Model("m", new ModelPrice(3.00m, 15.00m)
        {
            CachedInputPerMillion = 0.30m,
        });

        // 1M input of which 800k cached: 200k at $3/M + 800k at $0.30/M = 0.60 + 0.24.
        Assert.Equal(0.84m, model.EstimateCost("m", Usage(1_000_000, 0, cached: 800_000)));
    }

    [Fact]
    public void CachedTokensWithoutARate_AreBilledAtFullInput()
    {
        StaticTokenCostModel model = Model("m", new ModelPrice(3.00m, 15.00m));

        // Over-estimating is the safe direction when a budget depends on the answer.
        Assert.Equal(3.00m, model.EstimateCost("m", Usage(1_000_000, 0, cached: 800_000)));
    }

    [Fact]
    public void CachedCountLargerThanInput_IsClamped()
    {
        StaticTokenCostModel model = Model("m", new ModelPrice(3.00m, 15.00m) { CachedInputPerMillion = 0m });

        // A provider reporting nonsense must not produce a negative charge.
        decimal? cost = model.EstimateCost("m", Usage(1_000, 0, cached: 999_999));

        Assert.Equal(0m, cost);
    }

    [Fact]
    public void ContextTier_AppliesToTheWholeRequest_NotJustTheExcess()
    {
        StaticTokenCostModel model = Model("gemini", new ModelPrice(1.25m, 10.00m)
        {
            ContextTiers = [new ContextPricingTier(200_000, 2.50m, 15.00m)],
        });

        // 1M input past the 200k threshold: the entire prompt is billed at $2.50/M, not just
        // the 800k above it. Marginal pricing would have said $1.25*0.2 + $2.50*0.8 = $2.25.
        Assert.Equal(2.50m, model.EstimateCost("gemini", Usage(1_000_000, 0)));
    }

    [Fact]
    public void BelowTheThreshold_TheBasePriceApplies()
    {
        StaticTokenCostModel model = Model("gemini", new ModelPrice(1.25m, 10.00m)
        {
            ContextTiers = [new ContextPricingTier(200_000, 2.50m, 15.00m)],
        });

        // 100k input is under the 200k threshold: base rate, $1.25/M.
        Assert.Equal(0.125m, model.EstimateCost("gemini", Usage(100_000, 0)));
    }

    [Fact]
    public void ExactlyAtTheThreshold_StaysOnTheLowerTier()
    {
        StaticTokenCostModel model = Model("m", new ModelPrice(1.00m, 1.00m)
        {
            ContextTiers = [new ContextPricingTier(200_000, 2.00m, 2.00m)],
        });

        // "Above 200k" means strictly above; 200k itself is still the cheap tier.
        Assert.Equal(0.20m, model.EstimateCost("m", Usage(200_000, 0)));
        Assert.Equal(0.400002m, model.EstimateCost("m", Usage(200_001, 0)));
    }

    [Fact]
    public void HighestMatchingTierWins_RegardlessOfDeclarationOrder()
    {
        StaticTokenCostModel model = Model("m", new ModelPrice(1.00m, 1.00m)
        {
            ContextTiers =
            [
                new ContextPricingTier(1_000_000, 4.00m, 4.00m),
                new ContextPricingTier(200_000, 2.00m, 2.00m),
            ],
        });

        Assert.Equal(2.00m, model.EstimateCost("m", Usage(1_000_000, 0)));
        Assert.Equal(8.000004m, model.EstimateCost("m", Usage(2_000_001, 0)));
    }

    [Fact]
    public void TierSelectionUsesInputSize_ButAlsoRepricesOutput()
    {
        StaticTokenCostModel model = Model("m", new ModelPrice(1.00m, 10.00m)
        {
            ContextTiers = [new ContextPricingTier(200_000, 2.00m, 20.00m)],
        });

        // A long prompt with a short completion still bills the completion at tier rates.
        decimal? cost = model.EstimateCost("m", Usage(1_000_000, 1_000));

        Assert.Equal(2.00m + 0.02m, cost);
    }

    [Fact]
    public void TiersAndCachingCombine()
    {
        StaticTokenCostModel model = Model("m", new ModelPrice(1.00m, 10.00m)
        {
            CachedInputPerMillion = 0.10m,
            ContextTiers = [new ContextPricingTier(200_000, 2.00m, 20.00m, CachedInputPerMillion: 0.20m)],
        });

        // 1M input, 900k cached, in the upper tier: 100k at $2/M + 900k at $0.20/M.
        Assert.Equal(0.20m + 0.18m, model.EstimateCost("m", Usage(1_000_000, 0, cached: 900_000)));
    }

    [Fact]
    public void UnknownModel_PricesAsNullNotZero()
    {
        StaticTokenCostModel model = Model("known", new ModelPrice(1m, 1m));

        Assert.Null(model.EstimateCost("mystery", Usage(1000, 1000)));
        Assert.Null(model.EstimateCost(null, Usage(1000, 1000)));
        Assert.NotNull(model.EstimateCost("KNOWN", Usage(1000, 1000)));
    }
}
