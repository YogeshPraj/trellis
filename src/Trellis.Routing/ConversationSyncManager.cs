using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Trellis.Routing;

/// <summary>
/// Adapts a canonical client-side conversation to each endpoint's conversation model.
/// Endpoints with server-side state receive only the unsynced tail plus their provider
/// conversation id; everyone else gets the full history. Owns the per-(conversation,
/// endpoint) sync bookkeeping so the router doesn't have to.
/// </summary>
internal sealed class ConversationSyncManager
{
    private sealed record SyncEntry(string ProviderId, int SyncedCount)
    {
        public long TouchedAt { get; set; } = Environment.TickCount64;
    }

    // Bounded: least-recently-touched entries are evicted past this size, so a long-lived
    // process serving many conversations cannot leak sync bookkeeping. Evicting an entry
    // only costs one full-history resend on that conversation's next request.
    private const int MaxEntries = 1024;

    private readonly ConcurrentDictionary<string, SyncEntry> _sync = new();
    private readonly Lock _evictionLock = new();

    /// <summary>
    /// Shapes the outgoing request for an endpoint. Streaming calls always replay full
    /// history (sync bookkeeping for streams is not reliable) and invalidate the
    /// endpoint's sync so the next call re-establishes it.
    /// </summary>
    public (IList<ChatMessage> Messages, ChatOptions? Options) Prepare(
        ModelEndpoint endpoint,
        IList<ChatMessage> full,
        ChatOptions? options,
        bool streaming)
    {
        if (options?.ConversationId is not string logicalId)
        {
            return (full, options);
        }

        bool serverState = endpoint.Capabilities.Supports(ModelFeatures.ServerConversationState);
        string key = Key(logicalId, endpoint.Name);

        if (!serverState || streaming)
        {
            if (serverState)
            {
                _sync.TryRemove(key, out _);
            }
            // The logical id is ours, not the provider's — never leak it to a client.
            ChatOptions stripped = options.Clone();
            stripped.ConversationId = null;
            return (full, stripped);
        }

        if (_sync.TryGetValue(key, out SyncEntry? sync) && sync.SyncedCount <= full.Count)
        {
            sync.TouchedAt = Environment.TickCount64;
            ChatOptions delta = options.Clone();
            delta.ConversationId = sync.ProviderId;
            return ([.. full.Skip(sync.SyncedCount)], delta);
        }

        ChatOptions fresh = options.Clone();
        fresh.ConversationId = null;
        return (full, fresh);
    }

    /// <summary>Records what the endpoint's server now knows after a successful response.</summary>
    public void Record(ModelEndpoint endpoint, IList<ChatMessage> full, ChatOptions? options, ChatResponse response)
    {
        if (options?.ConversationId is not string logicalId
            || !endpoint.Capabilities.Supports(ModelFeatures.ServerConversationState)
            || response.ConversationId is not string providerId)
        {
            return;
        }
        // The server now knows the full input plus the messages it just generated.
        _sync[Key(logicalId, endpoint.Name)] = new SyncEntry(providerId, full.Count + response.Messages.Count);
        EvictIfOverCapacity();
    }

    private void EvictIfOverCapacity()
    {
        if (_sync.Count <= MaxEntries)
        {
            return;
        }
        lock (_evictionLock)
        {
            int excess = _sync.Count - MaxEntries + (MaxEntries / 10);
            if (excess <= 0)
            {
                return;
            }
            foreach (string key in _sync
                .OrderBy(kv => kv.Value.TouchedAt)
                .Take(excess)
                .Select(kv => kv.Key)
                .ToList())
            {
                _sync.TryRemove(key, out _);
            }
        }
    }

    private static string Key(string logicalId, string endpointName) => logicalId + "|" + endpointName;
}
