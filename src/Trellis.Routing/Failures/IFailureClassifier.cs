using System.Net;

namespace Trellis.Routing.Failures;

/// <summary>Turns provider exceptions into <see cref="FailureClassification"/>s (Strategy).</summary>
public interface IFailureClassifier
{
    FailureClassification Classify(Exception exception);
}
