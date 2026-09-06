namespace Trellis.Tools;

/// <summary>
/// Requires every authorizer to permit the call. The first refusal wins and the rest are not
/// consulted, so ordering policies cheapest-first keeps the hot path short.
/// </summary>
/// <remarks>
/// Unanimity is the only composition safe to default to. "Any one may allow" lets a permissive
/// policy quietly cancel a restrictive one, which is how an allow-list stops meaning anything
/// the moment a second policy is added beside it.
/// </remarks>
public sealed class CompositeToolAuthorizer : IToolAuthorizer
{
    private readonly IToolAuthorizer[] _authorizers;

    public CompositeToolAuthorizer(params IToolAuthorizer[] authorizers)
        : this((IEnumerable<IToolAuthorizer>)authorizers)
    {
    }

    public CompositeToolAuthorizer(IEnumerable<IToolAuthorizer> authorizers)
    {
        ArgumentNullException.ThrowIfNull(authorizers);
        _authorizers = [.. authorizers];
        if (Array.Exists(_authorizers, a => a is null))
        {
            throw new ArgumentException("An authorizer in the composite was null.", nameof(authorizers));
        }
    }

    public async ValueTask<ToolAuthorization> AuthorizeAsync(
        ToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (IToolAuthorizer authorizer in _authorizers)
        {
            ToolAuthorization verdict = await authorizer
                .AuthorizeAsync(context, cancellationToken).ConfigureAwait(false);
            if (!verdict.IsAllowed)
            {
                return verdict;
            }
        }
        return ToolAuthorization.Allow();
    }
}
