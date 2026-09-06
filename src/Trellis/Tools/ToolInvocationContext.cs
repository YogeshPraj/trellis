using Microsoft.Extensions.AI;

namespace Trellis.Tools;

/// <summary>The tool call an <see cref="IToolAuthorizer"/> is being asked to rule on.</summary>
/// <param name="ToolName">The tool the model asked for, as the model named it.</param>
/// <param name="Function">The function that would run.</param>
/// <param name="Arguments">
/// The arguments the model supplied. ⚠ These are model-controlled and frequently carry user
/// data — paths, queries, credentials pasted into a chat. Authorizers may inspect them freely,
/// but nothing in Trellis logs them, and neither should you without redaction.
/// </param>
public sealed record ToolInvocationContext(
    string ToolName,
    AIFunction Function,
    AIFunctionArguments Arguments);
