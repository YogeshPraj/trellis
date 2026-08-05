using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Trellis;

/// <summary>Shared run pipeline for all agent flavors, including the self-healing loop.</summary>
internal static class AgentRunner
{
    /// <summary>A candidate output plus the reasons it was rejected (empty when accepted).</summary>
    internal readonly record struct Materialized<TResult>(TResult? Value, IReadOnlyList<string> Errors);

    public static List<ChatMessage> BuildPayload(string? instructions, IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> all = [];
        if (!string.IsNullOrEmpty(instructions))
        {
            all.Add(new ChatMessage(ChatRole.System, instructions));
        }
        all.AddRange(messages);
        return all;
    }

    public static async Task<AgentRunResult<TResult>> RunAsync<TResult>(
        IChatClient client,
        string? instructions,
        ChatOptions? options,
        IEnumerable<ChatMessage> messages,
        IOutputValidator<TResult>? validator,
        OutputRetryOptions? retryOptions,
        CancellationToken cancellationToken)
    {
        using Activity? activity = AgentTelemetry.StartRun(typeof(TResult), options, streaming: false);
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            AgentRunResult<TResult> result = await RunCoreAsync(
                client, instructions, options, messages, validator, retryOptions, cancellationToken)
                .ConfigureAwait(false);
            AgentTelemetry.RecordSuccess(
                activity, result.Response, result.Attempts, Stopwatch.GetElapsedTime(startedAt));
            return result;
        }
        catch (Exception ex)
        {
            AgentTelemetry.RecordFailure(activity, ex, Stopwatch.GetElapsedTime(startedAt));
            throw;
        }
    }

    private static async Task<AgentRunResult<TResult>> RunCoreAsync<TResult>(
        IChatClient client,
        string? instructions,
        ChatOptions? options,
        IEnumerable<ChatMessage> messages,
        IOutputValidator<TResult>? validator,
        OutputRetryOptions? retryOptions,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> all = BuildPayload(instructions, messages);

        bool plainText = typeof(TResult) == typeof(string);
        if (plainText && validator is null)
        {
            // Nothing can fail validation on this path; skip the loop entirely.
            ChatResponse plain = await client
                .GetResponseAsync(all, options, cancellationToken)
                .ConfigureAwait(false);
            return new AgentRunResult<TResult>((TResult)(object)plain.Text, plain);
        }

        OutputRetryOptions retry = retryOptions ?? OutputRetryOptions.Default;
        int maxAttempts = retry.MaxRetries + 1;
        List<OutputFailure> failures = [];
        ChatResponse? lastResponse = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ChatResponse response = plainText
                ? await client.GetResponseAsync(all, options, cancellationToken).ConfigureAwait(false)
                : await client.GetResponseAsync<TResult>(all, options, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            Materialized<TResult> materialized = await MaterializeAsync(response, validator, cancellationToken)
                .ConfigureAwait(false);

            if (materialized.Errors.Count == 0)
            {
                return new AgentRunResult<TResult>(materialized.Value!, response, attempt);
            }

            var failure = new OutputFailure(attempt, materialized.Errors, response.Text);
            failures.Add(failure);
            lastResponse = response;
            AgentTelemetry.RecordRejection(response.ModelId, materialized.Errors[0]);

            if (attempt < maxAttempts)
            {
                // The model's failed attempt (including any tool call/result chain) plus the
                // correction go into THIS run's payload only — a Conversation absorbs just
                // the final accepted response, so retries never pollute canonical history.
                all.AddRange(response.Messages);
                all.Add(new ChatMessage(retry.FeedbackRole, FormatFeedback(retry, failure)));
            }
        }

        throw new OutputValidationException(typeof(TResult), failures, lastResponse);
    }

    /// <summary>
    /// Builds a streaming run. <paramref name="prepare"/> runs lazily on first enumeration
    /// (so creating a stream nobody iterates has no side effects), and
    /// <paramref name="onCompleted"/> fires only after a fully-enumerated, valid response.
    /// </summary>
    public static AgentStream<TResult> Stream<TResult>(
        IChatClient client,
        Func<CancellationToken, ValueTask<(List<ChatMessage> Payload, ChatOptions? Options)>> prepare,
        IOutputValidator<TResult>? validator,
        Func<AgentRunResult<TResult>, ValueTask>? onCompleted = null,
        ChatOptions? optionsHint = null) =>
        new(
            ct => StreamCoreAsync(client, prepare, ct),
            async (response, ct) =>
            {
                Materialized<TResult> materialized = await MaterializeAsync(response, validator, ct)
                    .ConfigureAwait(false);
                if (materialized.Errors.Count > 0)
                {
                    // No retry: tokens already handed to the caller cannot be retracted.
                    throw new OutputValidationException(
                        typeof(TResult), [new OutputFailure(1, materialized.Errors, response.Text)], response);
                }

                var result = new AgentRunResult<TResult>(materialized.Value!, response);
                if (onCompleted is not null)
                {
                    await onCompleted(result).ConfigureAwait(false);
                }
                return result;
            },
            optionsHint);

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamCoreAsync(
        IChatClient client,
        Func<CancellationToken, ValueTask<(List<ChatMessage> Payload, ChatOptions? Options)>> prepare,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        (List<ChatMessage> payload, ChatOptions? options) = await prepare(cancellationToken).ConfigureAwait(false);
        await foreach (ChatResponseUpdate update in client
            .GetStreamingResponseAsync(payload, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Asks for JSON matching <typeparamref name="TResult"/>'s schema. The buffered path gets
    /// this from <c>GetResponseAsync&lt;T&gt;</c>; streaming has no such overload, so the
    /// response format is set here instead.
    /// </summary>
    /// <remarks>
    /// The schema is used as-is, so its root is an object only when
    /// <typeparamref name="TResult"/> is one. Providers that require an object root
    /// (OpenAI's strict JSON schema mode) reject a bare <c>int</c> or array result type —
    /// wrap primitives in a record for streaming runs.
    /// </remarks>
    public static ChatOptions? WithStructuredOutputFormat<TResult>(ChatOptions? options)
    {
        if (typeof(TResult) == typeof(string))
        {
            return options;
        }
        ChatOptions shaped = options?.Clone() ?? new ChatOptions();
        shaped.ResponseFormat ??= ChatResponseFormat.ForJsonSchema(
            AIJsonUtilities.CreateJsonSchema(typeof(TResult)),
            typeof(TResult).Name);
        return shaped;
    }

    /// <summary>
    /// Turns a raw response into a typed output: deserializes (when
    /// <typeparamref name="TResult"/> is not <see cref="string"/>) and runs the validator.
    /// Shared by the buffered and streaming paths so both reject identically.
    /// </summary>
    public static async ValueTask<Materialized<TResult>> MaterializeAsync<TResult>(
        ChatResponse response,
        IOutputValidator<TResult>? validator,
        CancellationToken cancellationToken)
    {
        TResult? value;
        if (typeof(TResult) == typeof(string))
        {
            value = (TResult)(object)response.Text;
        }
        else
        {
            // A buffered typed run already produced ChatResponse<TResult>; an assembled
            // stream has not, so deserialize the aggregated response here.
            ChatResponse<TResult> typed = response as ChatResponse<TResult>
                ?? new ChatResponse<TResult>(response, AIJsonUtilities.DefaultOptions);
            try
            {
                value = typed.Result;
                if (value is null)
                {
                    return new(default, [$"The response deserialized to null instead of a {typeof(TResult).Name} value."]);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                string detail = ex.InnerException is { } inner ? $"{ex.Message} {inner.Message}" : ex.Message;
                return new(default, [$"The response could not be parsed as {typeof(TResult).Name}: {detail}"]);
            }
        }

        if (validator is not null)
        {
            OutputValidationResult verdict = await validator
                .ValidateAsync(value, cancellationToken)
                .ConfigureAwait(false);
            if (!verdict.IsValid)
            {
                return new(value, verdict.Errors);
            }
        }
        return new(value, []);
    }

    public static string FormatFeedback(OutputRetryOptions retry, OutputFailure failure) =>
        retry.FeedbackFormatter?.Invoke(failure) ?? BuildFeedback(failure);

    private static string BuildFeedback(OutputFailure failure)
    {
        var sb = new StringBuilder("Your previous response was rejected:\n");
        foreach (string error in failure.Errors)
        {
            sb.Append("- ").Append(error).Append('\n');
        }
        sb.Append("Correct these problems and answer again. ")
          .Append("Respond with only the corrected answer in the requested format — no apologies, no commentary.");
        return sb.ToString();
    }
}
