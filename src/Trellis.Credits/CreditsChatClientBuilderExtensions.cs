using Microsoft.Extensions.AI;
using Trellis.Tokens;

namespace Trellis.Credits;

/// <summary>Adds credit metering to a chat client pipeline.</summary>
public static class CreditsChatClientBuilderExtensions
{
    /// <summary>
    /// Wraps the pipeline so every call is admitted against a credit balance and settled
    /// afterwards.
    /// </summary>
    /// <param name="builder">The pipeline being built.</param>
    /// <param name="policy">Post-charge or prepaid admission.</param>
    /// <param name="subjectSelector">Identifies who pays for a given request.</param>
    /// <param name="tokenCounter">
    /// Estimates prompt size for prepaid holds; defaults to the heuristic counter.
    /// </param>
    public static ChatClientBuilder UseCredits(
        this ChatClientBuilder builder,
        ICreditAdmissionPolicy policy,
        Func<ChatOptions?, string?> subjectSelector,
        ITokenCounter? tokenCounter = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new CreditMeteringChatClient(inner, policy, subjectSelector, tokenCounter));
    }
}
