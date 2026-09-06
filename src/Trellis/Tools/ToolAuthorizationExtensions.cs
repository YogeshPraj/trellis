using Microsoft.Extensions.AI;

namespace Trellis.Tools;

/// <summary>Puts an <see cref="IToolAuthorizer"/> in front of tools.</summary>
public static class ToolAuthorizationExtensions
{
    /// <summary>
    /// Returns the tool with <paramref name="authorizer"/> gating it.
    /// </summary>
    /// <remarks>
    /// Non-invocable tools — declarations, and provider-hosted tools like web search that the
    /// model runs server-side — pass through unchanged. There is no local call to intercept, so
    /// wrapping them would imply a protection that does not exist. Restrict those at the
    /// provider, and do not assume this call covered them.
    /// </remarks>
    public static AITool WithAuthorization(this AITool tool, IToolAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(authorizer);
        return tool is AIFunction function ? new AuthorizingAIFunction(function, authorizer) : tool;
    }

    /// <summary>Returns every tool with <paramref name="authorizer"/> gating it.</summary>
    public static IReadOnlyList<AITool> WithAuthorization(
        this IEnumerable<AITool> tools, IToolAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(authorizer);
        return [.. tools.Select(tool => tool.WithAuthorization(authorizer))];
    }
}
