using Trellis.Conversations;
using Trellis.State;

namespace Trellis.Conversations.Storage;

/// <summary>What to do when the authoritative tier itself is unreachable.</summary>
public enum AuthorityUnavailableBehavior
{
    /// <summary>
    /// Fail the save (default). Nothing is written anywhere, so no tier can accumulate turns
    /// the durable copy will never see — the conversation cannot fork.
    /// </summary>
    Fail,

    /// <summary>
    /// Promote the highest-ranked healthy tier and keep serving. Turns survive the outage,
    /// but they exist only in a non-durable tier until the authority returns, and if that
    /// tier is also lost they are gone. Choose this only when continuing matters more than
    /// durability for the length of an outage.
    /// </summary>
    PromoteHealthiest,
}
