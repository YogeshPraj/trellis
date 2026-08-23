namespace Trellis.Routing;

/// <summary>
/// A fixed table of model aliases: one logical name to an ordered list of concrete models.
/// Anything not in the table passes through unchanged, so adding a table never breaks
/// requests that already worked.
/// </summary>
/// <example>
/// <code>
/// var aliases = new ModelAliasTable
/// {
///     ["fast"]     = ["gpt-4o-mini", "claude-haiku-4"],   // second is the fallback
///     ["reasoning"] = ["o3", "claude-opus-4"],
///     ["gpt-4o"]   = ["gpt-4o", "gpt-4o-2024-08-06"],     // pin a snapshot as backup
/// };
/// </code>
/// </example>
public sealed class ModelAliasTable : IModelAliasResolver, IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>
{
    private readonly Dictionary<string, IReadOnlyList<string>> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Defines the ordered concrete models an alias resolves to.</summary>
    public IReadOnlyList<string> this[string alias]
    {
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(alias);
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count == 0)
            {
                throw new ArgumentException($"Alias '{alias}' must resolve to at least one model.", nameof(value));
            }
            _aliases[alias] = [.. value];
        }
    }

    /// <summary>Alias names defined in this table.</summary>
    public IReadOnlyCollection<string> Aliases => _aliases.Keys;

    /// <summary>Adds an alias; chainable for object-initializer-free construction.</summary>
    public ModelAliasTable Add(string alias, params string[] models)
    {
        this[alias] = models;
        return this;
    }

    public IReadOnlyList<string> Resolve(string? requestedModel)
    {
        if (requestedModel is null)
        {
            // No model named: every endpoint is a candidate under whatever it defaults to.
            return [];
        }
        return _aliases.TryGetValue(requestedModel, out IReadOnlyList<string>? models)
            ? models
            : [requestedModel];
    }

    public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator() => _aliases.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
