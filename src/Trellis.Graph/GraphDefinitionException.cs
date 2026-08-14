using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

/// <summary>The graph was built incorrectly (bad edges, missing entry point, ...).</summary>
public sealed class GraphDefinitionException(string message) : Exception(message);
