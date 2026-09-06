namespace Trellis.Agents.Teams;

/// <summary>
/// Carries a handoff from the tool the model called up to the loop that has to act on it.
/// </summary>
/// <remarks>
/// <para>
/// The tool runs deep inside Microsoft.Extensions.AI's function-invoking client, several frames
/// below anything Trellis controls, so there is no return value to thread the decision through.
/// An async-local is how that client publishes its own invocation context, and it is right here
/// for the same reason: one turn is one async flow, so concurrent turns never cross.
/// </para>
/// <para>
/// What it cannot be is a plain <c>AsyncLocal&lt;string&gt;</c> assigned by the tool. An
/// async-local flows <em>downward</em> into deeper contexts; a value assigned in a nested one is
/// invisible to the caller that started it, so the handoff would be set and then silently lost.
/// A mutable holder published <em>before</em> the turn fixes that: the reference flows down, and
/// the tool mutates the object the caller is still holding.
/// </para>
/// </remarks>
internal static class HandoffSignal
{
    private static readonly AsyncLocal<Scope?> Slot = new();

    /// <summary>The holder one turn writes its handoff into.</summary>
    internal sealed class Scope
    {
        public string? Target { get; private set; }

        public string? Reason { get; private set; }

        public bool WasRequested => Target is not null;

        /// <summary>Records a transfer. The first one wins: later calls in the same turn are noise.</summary>
        public void Set(string target, string? reason)
        {
            if (Target is null)
            {
                Target = target;
                Reason = reason;
            }
        }
    }

    /// <summary>Opens a scope for one turn, replacing anything left from a previous one.</summary>
    public static Scope Begin()
    {
        var scope = new Scope();
        Slot.Value = scope;
        return scope;
    }

    /// <summary>Closes the current scope so nothing outside a turn observes it.</summary>
    public static void End() => Slot.Value = null;

    /// <summary>The scope for the turn in flight, if any.</summary>
    public static Scope? Current => Slot.Value;
}
