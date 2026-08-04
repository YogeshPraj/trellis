using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Trellis;

/// <summary>Shared run pipeline for all agent flavors, including the self-healing loop.</summary>
internal static class AgentRunner
{
    public static async Task<AgentRunResult<TResult>> RunAsync<TResult>(
        IChatClient client,
        string? instructions,
        ChatOptions? options,
        IEnumerable<ChatMessage> messages,
        IOutputValidator<TResult>? validator,
        OutputRetryOptions? retryOptions,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> all = [];
        if (!string.IsNullOrEmpty(instructions))
        {
            all.Add(new ChatMessage(ChatRole.System, instructions));
        }
        all.AddRange(messages);

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
            ChatResponse response;
            TResult? value = default;
            List<string> errors = [];

            if (plainText)
            {
                response = await client.GetResponseAsync(all, options, cancellationToken).ConfigureAwait(false);
                value = (TResult)(object)response.Text;
            }
            else
            {
                ChatResponse<TResult> typed = await client
                    .GetResponseAsync<TResult>(all, options, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                response = typed;
                try
                {
                    value = typed.Result;
                    if (value is null)
                    {
                        errors.Add($"The response deserialized to null instead of a {typeof(TResult).Name} value.");
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    string detail = ex.InnerException is { } inner ? $"{ex.Message} {inner.Message}" : ex.Message;
                    errors.Add($"The response could not be parsed as {typeof(TResult).Name}: {detail}");
                }
            }

            if (errors.Count == 0 && validator is not null)
            {
                OutputValidationResult verdict = await validator
                    .ValidateAsync(value!, cancellationToken)
                    .ConfigureAwait(false);
                if (!verdict.IsValid)
                {
                    errors.AddRange(verdict.Errors);
                }
            }

            if (errors.Count == 0)
            {
                return new AgentRunResult<TResult>(value!, response, attempt);
            }

            var failure = new OutputFailure(attempt, errors, response.Text);
            failures.Add(failure);
            lastResponse = response;

            if (attempt < maxAttempts)
            {
                // The model's failed attempt (including any tool call/result chain) plus the
                // correction go into THIS run's payload only — a Conversation absorbs just
                // the final accepted response, so retries never pollute canonical history.
                all.AddRange(response.Messages);
                all.Add(new ChatMessage(
                    retry.FeedbackRole,
                    retry.FeedbackFormatter?.Invoke(failure) ?? BuildFeedback(failure)));
            }
        }

        throw new OutputValidationException(typeof(TResult), failures, lastResponse);
    }

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
