using Microsoft.Extensions.AI;

namespace Trellis.Diagnostics;

/// <summary>Adds durable per-request usage records to a chat client pipeline.</summary>
public static class UsageRecordingChatClientBuilderExtensions
{
    /// <summary>
    /// Records what every request consumed — including the ones that failed or were
    /// abandoned mid-stream.
    /// </summary>
    /// <param name="builder">The pipeline being built.</param>
    /// <param name="sink">Where records are kept.</param>
    /// <param name="subjectSelector">Identifies who the request was for, when that is tracked.</param>
    /// <param name="costModel">
    /// Prices each record; defaults to <see cref="AgentTelemetry.CostModel"/>, so one price
    /// table serves telemetry, credits and audit alike.
    /// </param>
    public static ChatClientBuilder UseUsageRecording(
        this ChatClientBuilder builder,
        IUsageRecordSink sink,
        Func<ChatOptions?, string?>? subjectSelector = null,
        ITokenCostModel? costModel = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new UsageRecordingChatClient(inner, sink, subjectSelector, costModel));
    }
}
