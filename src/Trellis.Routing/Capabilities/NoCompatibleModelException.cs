namespace Trellis.Routing.Capabilities;

/// <summary>Thrown when no registered endpoint supports what the request needs.</summary>
public sealed class NoCompatibleModelException(string message) : Exception(message);
