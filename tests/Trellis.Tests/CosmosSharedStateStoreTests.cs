using System.Net;
using Microsoft.Azure.Cosmos;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Trellis.Azure.Cosmos;
using Trellis.State;

namespace Trellis.Tests;

/// <summary>
/// Contract-level tests for the Cosmos provider: key/partition mapping, the semantics Trellis
/// depends on (absent → null, 412 → swap refused, TTL guard), and that the ETag conditional
/// write is actually issued. Cosmos's own storage behaviour is the SDK's responsibility, so
/// the container is substituted — the same approach used for the Redis provider.
/// </summary>
public class CosmosSharedStateStoreTests
{
    private static ItemResponse<T> Response<T>(T resource, string? etag = null)
    {
        ItemResponse<T> response = Substitute.For<ItemResponse<T>>();
        response.Resource.Returns(resource);
        response.ETag.Returns(etag);
        return response;
    }

    private static CosmosException NotFound() =>
        new("not found", HttpStatusCode.NotFound, 0, "activity", 1);

    private static CosmosException Status(HttpStatusCode code) =>
        new(code.ToString(), code, 0, "activity", 1);

    /// <summary>Captures the document the store writes, whatever its private shape.</summary>
    private static object? CapturedItem(Container container) =>
        container.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name is "UpsertItemAsync" or "CreateItemAsync" or "ReplaceItemAsync")
            .Select(c => c.GetArguments()[0])
            .LastOrDefault();

    private static string? PropertyValue(object? document, string propertyName) =>
        document?.GetType().GetProperty(propertyName)?.GetValue(document)?.ToString();

    [Fact]
    public async Task Get_MapsMissingItemToNull()
    {
        Container container = Substitute.For<Container>();
        container.ReadItemAsync<CosmosStateDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(NotFound());
        var store = new CosmosSharedStateStore(container);

        Assert.Null(await store.GetAsync("conversation:c1"));
    }

    [Fact]
    public async Task Set_WritesIntoThePrefixedPartition()
    {
        Container container = Substitute.For<Container>();
        var store = new CosmosSharedStateStore(container, keyPrefix: "trellis:");

        await store.SetAsync("conversation:c1", "the-payload");

        object? document = CapturedItem(container);
        Assert.Equal("trellis:conversation:c1", PropertyValue(document, "PartitionKey"));
        Assert.Equal("the-payload", PropertyValue(document, "Payload"));
        Assert.Equal("value", PropertyValue(document, "Id"));
    }

    [Fact]
    public async Task Set_WithTtl_StampsTheCosmosTtlProperty()
    {
        Container container = Substitute.For<Container>();
        var store = new CosmosSharedStateStore(container);

        await store.SetAsync("k", "v", TimeSpan.FromMinutes(5));

        Assert.Equal("300", PropertyValue(CapturedItem(container), "TimeToLiveSeconds"));
    }

    [Fact]
    public async Task Ttl_OnAContainerWithoutDefaultTtl_FailsLoudly()
    {
        Container container = Substitute.For<Container>();
        var store = new CosmosSharedStateStore(container, timeToLiveEnabled: false);

        // Cosmos silently ignores per-item TTL unless the container enables it; a store that
        // let that pass would be promising expiry it cannot deliver.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SetAsync("k", "v", TimeSpan.FromMinutes(5)).AsTask());
        Assert.Contains("DefaultTimeToLive", ex.Message);

        await store.SetAsync("k", "v");   // no TTL requested → fine
    }

    [Fact]
    public async Task Remove_TreatsAMissingItemAsSuccess()
    {
        Container container = Substitute.For<Container>();
        container.DeleteItemAsync<CosmosStateDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(NotFound());
        FeedIterator<CosmosIdProjection> empty = Substitute.For<FeedIterator<CosmosIdProjection>>();
        empty.HasMoreResults.Returns(false);
        container.GetItemQueryIterator<CosmosIdProjection>(Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .ReturnsForAnyArgs(empty);
        var store = new CosmosSharedStateStore(container);

        await store.RemoveAsync("k");   // must not throw
    }

    [Fact]
    public async Task CompareAndSwap_IssuesAConditionalWriteWithTheReadETag()
    {
        Container container = Substitute.For<Container>();
        container.ReadItemAsync<CosmosStateDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(_ => Task.FromResult(Response(new CosmosStateDocument { Payload = "current" }, etag: "etag-1")));
        var store = new CosmosSharedStateStore(container);

        bool swapped = await store.TrySetIfUnchangedAsync("k", "current", "next");

        Assert.True(swapped);
        ItemRequestOptions? options = container.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "ReplaceItemAsync")
            .Select(c => c.GetArguments()[3] as ItemRequestOptions)
            .LastOrDefault();
        Assert.Equal("etag-1", options?.IfMatchEtag);
    }

    [Fact]
    public async Task CompareAndSwap_ReturnsFalseWhenAnotherWriterWon()
    {
        Container container = Substitute.For<Container>();
        container.ReadItemAsync<CosmosStateDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(_ => Task.FromResult(Response(new CosmosStateDocument { Payload = "current" }, etag: "etag-1")));
        container.ReplaceItemAsync(Arg.Any<CosmosStateDocument>(), Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(Status(HttpStatusCode.PreconditionFailed));
        var store = new CosmosSharedStateStore(container);

        Assert.False(await store.TrySetIfUnchangedAsync("k", "current", "next"));
    }

    [Fact]
    public async Task CompareAndSwap_ReturnsFalseWhenTheStoredValueDiffers()
    {
        Container container = Substitute.For<Container>();
        container.ReadItemAsync<CosmosStateDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(_ => Task.FromResult(Response(new CosmosStateDocument { Payload = "something-else" }, etag: "etag-1")));
        var store = new CosmosSharedStateStore(container);

        Assert.False(await store.TrySetIfUnchangedAsync("k", "current", "next"));
    }

    [Fact]
    public async Task CompareAndSwap_ExpectingAbsent_CreatesAndLosesRacesGracefully()
    {
        Container container = Substitute.For<Container>();
        container.ReadItemAsync<CosmosStateDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(NotFound());
        var store = new CosmosSharedStateStore(container);

        Assert.True(await store.TrySetIfUnchangedAsync("k", expectedValue: null, "created"));

        // A concurrent creator between our read and our write must not be overwritten.
        container.CreateItemAsync(Arg.Any<CosmosStateDocument>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(Status(HttpStatusCode.Conflict));
        Assert.False(await store.TrySetIfUnchangedAsync("k", expectedValue: null, "created"));
    }

    [Fact]
    public async Task CompareAndSwap_ExpectingAbsent_RefusesWhenTheKeyExists()
    {
        Container container = Substitute.For<Container>();
        container.ReadItemAsync<CosmosStateDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(_ => Task.FromResult(Response(new CosmosStateDocument { Payload = "already here" }, etag: "e")));
        var store = new CosmosSharedStateStore(container);

        Assert.False(await store.TrySetIfUnchangedAsync("k", expectedValue: null, "new"));
    }

    [Fact]
    public async Task Increment_UsesServerSidePatch()
    {
        Container container = Substitute.For<Container>();
        container.PatchItemAsync<CosmosCounterDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<IReadOnlyList<PatchOperation>>(), Arg.Any<PatchItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(_ => Task.FromResult(Response(new CosmosCounterDocument { Counter = 7 })));
        var store = new CosmosSharedStateStore(container);

        Assert.Equal(7, await store.IncrementAsync("counter"));
    }

    [Fact]
    public async Task Increment_CreatesTheCounterOnFirstUse()
    {
        Container container = Substitute.For<Container>();
        container.PatchItemAsync<CosmosCounterDocument>(Arg.Any<string>(), Arg.Any<PartitionKey>(), Arg.Any<IReadOnlyList<PatchOperation>>(), Arg.Any<PatchItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(NotFound());
        container.CreateItemAsync(Arg.Any<CosmosCounterDocument>(), Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(_ => Task.FromResult(Response(new CosmosCounterDocument { Counter = 1 })));
        var store = new CosmosSharedStateStore(container);

        Assert.Equal(1, await store.IncrementAsync("counter"));
    }

    [Fact]
    public void ImplementsTheAtomicCapability_SoTheTieredStoreGetsRealCompareAndSwap()
    {
        Container container = Substitute.For<Container>();

        Assert.IsAssignableFrom<IAtomicSharedStateStore>(new CosmosSharedStateStore(container));
    }

}
