using Trellis.Graph.Checkpointing;
using Trellis.Graph.Diagnostics;
using Trellis.Graph.Resilience;

namespace Trellis.Graph;

/// <summary>The graph failed while executing.</summary>
public class GraphExecutionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
