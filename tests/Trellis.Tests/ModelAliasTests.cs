using Microsoft.Extensions.AI;
using Trellis.Routing;

namespace Trellis.Tests;

/// <summary>Model aliasing: one logical name to an ordered list of concrete models.</summary>
public class ModelAliasTests
{
    /// <summary>Records the model each call was actually asked for, and can be made to fail.</summary>
    private sealed class ModelRecordingClient(string name) : IChatClient
    {
        public List<string?> Requested { get; } = [];

        public bool Fail { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requested.Add(options?.ModelId);
            return Fail
                ? throw new HttpRequestException("429 Too Many Requests")
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, name)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static Task<ChatResponse> Ask(ModelRouter router, string? model) =>
        router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = model });

    [Fact]
    public async Task AliasResolvesToItsPreferredModel()
    {
        var mini = new ModelRecordingClient("mini");
        var router = new ModelRouter(
            [new ModelEndpoint("openai", mini) { Models = ["gpt-4o-mini"] }],
            new ModelRouterOptions
            {
                ModelAliases = new ModelAliasTable().Add("fast", "gpt-4o-mini"),
            });

        await Ask(router, "fast");

        // The endpoint is asked for the concrete model, never the alias.
        Assert.Equal("gpt-4o-mini", Assert.Single(mini.Requested));
    }

    [Fact]
    public async Task FallsToTheNextModelOnlyAfterTheFirstIsExhausted()
    {
        var openai = new ModelRecordingClient("openai") { Fail = true };
        var anthropic = new ModelRecordingClient("anthropic");
        var router = new ModelRouter(
            [
                new ModelEndpoint("openai", openai) { Models = ["gpt-4o-mini"] },
                new ModelEndpoint("anthropic", anthropic) { Models = ["claude-haiku"] },
            ],
            new ModelRouterOptions
            {
                ModelAliases = new ModelAliasTable().Add("fast", "gpt-4o-mini", "claude-haiku"),
            });

        ChatResponse response = await Ask(router, "fast");

        Assert.Equal("anthropic", response.Text);
        Assert.Equal("gpt-4o-mini", Assert.Single(openai.Requested));
        Assert.Equal("claude-haiku", Assert.Single(anthropic.Requested));
    }

    [Fact]
    public async Task EveryEndpointForThePreferredModelIsTriedBeforeTheFallback()
    {
        var primary = new ModelRecordingClient("primary") { Fail = true };
        var secondary = new ModelRecordingClient("secondary") { Fail = true };
        var fallback = new ModelRecordingClient("fallback");
        var router = new ModelRouter(
            [
                new ModelEndpoint("primary", primary) { Models = ["gpt-4o"] },
                new ModelEndpoint("secondary", secondary) { Models = ["gpt-4o"] },
                new ModelEndpoint("fallback", fallback) { Models = ["claude-sonnet"] },
            ],
            new ModelRouterOptions
            {
                ModelAliases = new ModelAliasTable().Add("smart", "gpt-4o", "claude-sonnet"),
            });

        ChatResponse response = await Ask(router, "smart");

        // A fallback model is a last resort, not something the load balancer may pick early.
        Assert.Single(primary.Requested);
        Assert.Single(secondary.Requested);
        Assert.Equal("fallback", response.Text);
    }

    [Fact]
    public async Task EndpointsThatDoNotServeTheModelAreSkipped()
    {
        var wrong = new ModelRecordingClient("wrong");
        var right = new ModelRecordingClient("right");
        var router = new ModelRouter(
            [
                new ModelEndpoint("wrong", wrong, priority: 0) { Models = ["llama-3"] },
                new ModelEndpoint("right", right, priority: 1) { Models = ["gpt-4o"] },
            ]);

        ChatResponse response = await Ask(router, "gpt-4o");

        // Priority 0 would normally win, but it cannot serve this model.
        Assert.Empty(wrong.Requested);
        Assert.Equal("right", response.Text);
    }

    [Fact]
    public async Task AnEndpointDeclaringNoModelsStillServesEverything()
    {
        var any = new ModelRecordingClient("any");
        var router = new ModelRouter([new ModelEndpoint("any", any)]);

        await Ask(router, "some-model-nobody-declared");

        // Backwards compatible: endpoints predating model declarations keep working.
        Assert.Equal("some-model-nobody-declared", Assert.Single(any.Requested));
    }

    [Fact]
    public async Task UnaliasedModelPassesThroughUnchanged()
    {
        var client = new ModelRecordingClient("c");
        var router = new ModelRouter(
            [new ModelEndpoint("c", client) { Models = ["gpt-4o"] }],
            new ModelRouterOptions { ModelAliases = new ModelAliasTable().Add("fast", "gpt-4o-mini") });

        await Ask(router, "gpt-4o");

        Assert.Equal("gpt-4o", Assert.Single(client.Requested));
    }

    [Fact]
    public async Task AModelNobodyServes_FailsUpFront()
    {
        var router = new ModelRouter([new ModelEndpoint("c", new ModelRecordingClient("c")) { Models = ["gpt-4o"] }]);

        await Assert.ThrowsAsync<NoCompatibleModelException>(() => Ask(router, "nonexistent"));
    }

    [Fact]
    public async Task CallerOptionsAreNotMutatedByModelResolution()
    {
        var client = new ModelRecordingClient("c");
        var router = new ModelRouter(
            [new ModelEndpoint("c", client) { Models = ["gpt-4o-mini"] }],
            new ModelRouterOptions { ModelAliases = new ModelAliasTable().Add("fast", "gpt-4o-mini") });

        var options = new ChatOptions { ModelId = "fast" };
        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        // The caller's object is reusable across requests; rewriting it in place would leak.
        Assert.Equal("fast", options.ModelId);
        Assert.Equal("gpt-4o-mini", Assert.Single(client.Requested));
    }

    [Fact]
    public async Task AliasingComposesWithCapabilityFiltering()
    {
        var noTools = new ModelRecordingClient("no-tools");
        var withTools = new ModelRecordingClient("with-tools");
        var router = new ModelRouter(
        [
            new ModelEndpoint("no-tools", noTools, priority: 0,
                new ModelCapabilities { Features = ModelFeatures.JsonResponseFormat }) { Models = ["gpt-4o"] },
            new ModelEndpoint("with-tools", withTools, priority: 1) { Models = ["gpt-4o"] },
        ]);

        await router.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions
            {
                ModelId = "gpt-4o",
                Tools = [AIFunctionFactory.Create(() => 1, name: "f")],
            });

        Assert.Empty(noTools.Requested);
        Assert.Single(withTools.Requested);
    }

    [Fact]
    public void CatalogueListsConcreteModelsAndAliases()
    {
        var router = new ModelRouter(
            [
                new ModelEndpoint("openai", new ModelRecordingClient("a")) { Models = ["gpt-4o", "gpt-4o-mini"] },
                new ModelEndpoint("anthropic", new ModelRecordingClient("b")) { Models = ["claude-haiku"] },
            ],
            new ModelRouterOptions
            {
                ModelAliases = new ModelAliasTable().Add("fast", "gpt-4o-mini", "claude-haiku"),
            });

        IReadOnlyList<ModelCatalogueEntry> catalogue = router.GetCatalogue();

        ModelCatalogueEntry mini = Assert.Single(catalogue, e => e.ModelId == "gpt-4o-mini");
        Assert.False(mini.IsAlias);
        Assert.Equal(["openai"], mini.ServedBy);

        ModelCatalogueEntry fast = Assert.Single(catalogue, e => e.ModelId == "fast");
        Assert.True(fast.IsAlias);
        Assert.Equal(["gpt-4o-mini", "claude-haiku"], fast.ResolvesTo);
        Assert.Equal(["openai", "anthropic"], fast.ServedBy);
    }

    [Fact]
    public void AliasTableRejectsAnEmptyTargetList()
    {
        Assert.Throws<ArgumentException>(() => new ModelAliasTable().Add("broken"));
    }

    [Fact]
    public void AliasLookupIsCaseInsensitive()
    {
        IModelAliasResolver aliases = new ModelAliasTable().Add("Fast", "gpt-4o-mini");

        Assert.Equal(["gpt-4o-mini"], aliases.Resolve("fast"));
        Assert.Equal(["unknown"], aliases.Resolve("unknown"));
    }
}
