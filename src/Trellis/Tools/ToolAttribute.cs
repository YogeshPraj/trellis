namespace Trellis.Tools;

/// <summary>
/// Marks a method as an agent tool. The Trellis source generator emits a
/// <c>CreateTools()</c> method on the containing class (which must be <c>partial</c>)
/// that turns every marked method into an <see cref="Microsoft.Extensions.AI.AIFunction"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ToolAttribute : Attribute
{
    /// <summary>Tool name shown to the model. Defaults to the snake_case method name.</summary>
    public string? Name { get; set; }

    /// <summary>Description shown to the model to decide when to call the tool.</summary>
    public string? Description { get; set; }
}
