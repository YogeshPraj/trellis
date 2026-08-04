using Microsoft.Extensions.AI;

namespace Trellis;

/// <summary>
/// Thrown when an agent run exhausts its self-healing budget (see
/// <see cref="OutputRetryOptions"/>) without producing an output that deserializes into
/// the result type and passes validation. Carries every failed attempt for diagnostics.
/// </summary>
public sealed class OutputValidationException : Exception
{
    internal OutputValidationException(Type targetType, IReadOnlyList<OutputFailure> failures, ChatResponse? lastResponse)
        : base(BuildMessage(targetType, failures))
    {
        TargetType = targetType;
        Failures = failures;
        LastResponse = lastResponse;
    }

    /// <summary>The result type the model failed to produce.</summary>
    public Type TargetType { get; }

    /// <summary>How many model calls were made before giving up.</summary>
    public int Attempts => Failures.Count;

    /// <summary>Every failed attempt, oldest first, with its raw response text and errors.</summary>
    public IReadOnlyList<OutputFailure> Failures { get; }

    /// <summary>The raw response of the final attempt, when one completed.</summary>
    public ChatResponse? LastResponse { get; }

    private static string BuildMessage(Type targetType, IReadOnlyList<OutputFailure> failures)
    {
        string errors = failures.Count > 0 ? string.Join("; ", failures[^1].Errors) : "none recorded";
        return $"The model did not produce a valid {targetType.Name} after {failures.Count} attempt(s). " +
               $"Last errors: {errors}";
    }
}
