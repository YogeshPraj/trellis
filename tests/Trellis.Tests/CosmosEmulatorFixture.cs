using Microsoft.Azure.Cosmos;

namespace Trellis.Tests;

/// <summary>
/// Connects to a local Azure Cosmos DB emulator, creating the containers the providers expect.
/// Everything here no-ops when the emulator is unreachable, so the suite stays green on a
/// machine — or a CI runner — without one.
/// </summary>
/// <remarks>
/// The substituted-container tests prove Trellis's own logic. These prove the parts only a
/// real server can: that a transactional batch is atomic, that a duplicate id really does
/// return 409 (which is the whole concurrency mechanism), that <c>ORDER BY c.version DESC</c>
/// works inside a partition, and that ETag preconditions behave as assumed.
/// </remarks>
public static class CosmosEmulatorFixture
{
    /// <summary>The emulator's well-known endpoint and key — published by Microsoft, not a secret.</summary>
    private const string Endpoint = "https://localhost:8081";

    private const string Key =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const string DatabaseId = "trellis-tests";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Database? _database;
    private static bool _probed;
    private static bool _available;

    /// <summary>A client configured for the emulator: gateway mode, and its self-signed cert accepted.</summary>
    public static CosmosClient CreateClient() =>
        new(Endpoint, Key, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            RequestTimeout = TimeSpan.FromSeconds(20),
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            }),
        });

    /// <summary>
    /// Whether an emulator answered. Probed once per run; a machine without one simply skips.
    /// </summary>
    public static async Task<bool> IsAvailableAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_probed)
            {
                return _available;
            }
            _probed = true;
            try
            {
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                CosmosClient client = CreateClient();
                DatabaseResponse response = await client
                    .CreateDatabaseIfNotExistsAsync(DatabaseId, cancellationToken: probe.Token);
                _database = response.Database;
                _available = true;
            }
            catch
            {
                _available = false;
            }
            return _available;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// A container created for one test, partitioned as the provider requires and dropped by
    /// the returned scope so tests never see each other's documents.
    /// </summary>
    public static async Task<CosmosContainerScope> CreateContainerAsync(
        string partitionKeyPath, bool enableTimeToLive = true)
    {
        if (!await IsAvailableAsync() || _database is null)
        {
            throw new InvalidOperationException("The emulator is not available; check IsAvailableAsync first.");
        }

        var properties = new ContainerProperties("c" + Guid.NewGuid().ToString("N"), partitionKeyPath);
        if (enableTimeToLive)
        {
            // -1 enables per-item TTL without expiring anything by default.
            properties.DefaultTimeToLive = -1;
        }

        ContainerResponse response = await _database.CreateContainerIfNotExistsAsync(properties, throughput: 400);
        return new CosmosContainerScope(response.Container);
    }
}

/// <summary>A container that deletes itself when the test finishes.</summary>
public sealed class CosmosContainerScope(Container container) : IAsyncDisposable
{
    public Container Container { get; } = container;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Container.DeleteContainerAsync();
        }
        catch (CosmosException)
        {
            // A leftover test container costs nothing; failing teardown would mask the result.
        }
    }
}
