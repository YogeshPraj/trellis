namespace Trellis.Routing;

/// <summary>One model the router can serve, and what stands behind it.</summary>
/// <param name="ModelId">The model or alias name a caller would ask for.</param>
/// <param name="IsAlias">Whether this is an alias rather than a concrete model.</param>
/// <param name="ServedBy">Names of the endpoints that can serve it.</param>
/// <param name="ResolvesTo">For an alias, the concrete models it prefers, in order; empty otherwise.</param>
public sealed record ModelCatalogueEntry(
    string ModelId,
    bool IsAlias,
    IReadOnlyList<string> ServedBy,
    IReadOnlyList<string> ResolvesTo);
