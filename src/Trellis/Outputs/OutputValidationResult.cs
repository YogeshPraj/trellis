namespace Trellis.Outputs;

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
