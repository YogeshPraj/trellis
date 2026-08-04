namespace Trellis;

/// <summary>
/// Validates a deserialized agent output beyond what JSON deserialization can check —
/// business rules, cross-field constraints, value ranges. When validation fails, the
/// errors are fed back to the model as a correction message and the run is retried
/// (see <see cref="OutputRetryOptions"/>), so validators drive self-healing, not just rejection.
/// </summary>
/// <remarks>
/// Implementations must be safe for concurrent use — one validator instance serves every
/// run of the agent it is attached to. A validator that throws is treated as a bug in the
/// validator (the exception propagates); to reject the output, return a failed
/// <see cref="OutputValidationResult"/> instead.
/// </remarks>
/// <typeparam name="TResult">The agent's output type.</typeparam>
public interface IOutputValidator<in TResult>
{
    /// <summary>Validates one candidate output. Return errors phrased for the model to act on.</summary>
    ValueTask<OutputValidationResult> ValidateAsync(TResult output, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of validating a candidate agent output.</summary>
public sealed class OutputValidationResult
{
    private OutputValidationResult(bool isValid, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Errors = errors;
    }

    /// <summary>A shared success result.</summary>
    public static OutputValidationResult Success { get; } = new(true, []);

    /// <summary>Whether the output passed validation.</summary>
    public bool IsValid { get; }

    /// <summary>The validation errors; empty when <see cref="IsValid"/> is true.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// Creates a failed result. Error messages are sent to the model verbatim, so phrase
    /// them as actionable corrections ("Price must be positive"), not internal diagnostics.
    /// </summary>
    public static OutputValidationResult Failure(params IEnumerable<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        string[] list = [.. errors];
        return new(false, list.Length > 0 ? list : ["The output failed validation."]);
    }
}
