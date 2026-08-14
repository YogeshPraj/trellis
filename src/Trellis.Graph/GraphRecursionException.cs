using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

/// <summary>The graph ran more steps than <see cref="GraphRunOptions.MaxSteps"/> allows.</summary>
public sealed class GraphRecursionException(string message) : GraphExecutionException(message);
