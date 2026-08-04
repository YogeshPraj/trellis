using System.ComponentModel.DataAnnotations;

namespace Trellis;

/// <summary>
/// The default declarative validator: checks every property of the output against its
/// <see cref="System.ComponentModel.DataAnnotations"/> attributes ([Required], [Range],
/// [StringLength], [RegularExpression], ...).
/// </summary>
/// <remarks>
/// On positional records, target the generated property explicitly —
/// <c>record Booking([property: Range(0, 10_000)] decimal Price)</c> — otherwise the
/// attribute lands on the constructor parameter where the validator cannot see it.
/// Validation is one level deep (attributes on nested objects' own properties are not
/// walked); implement <see cref="IOutputValidator{TResult}"/> directly for deep or
/// cross-field rules.
/// </remarks>
public sealed class DataAnnotationsOutputValidator<TResult> : IOutputValidator<TResult>
{
    public ValueTask<OutputValidationResult> ValidateAsync(TResult output, CancellationToken cancellationToken = default)
    {
        if (output is null)
        {
            return new(OutputValidationResult.Failure("The output was null."));
        }

        List<ValidationResult> violations = [];
        bool valid = Validator.TryValidateObject(
            output, new ValidationContext(output), violations, validateAllProperties: true);

        return new(valid
            ? OutputValidationResult.Success
            : OutputValidationResult.Failure(violations.Select(v => v.ErrorMessage ?? "Validation failed.")));
    }
}
