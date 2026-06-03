using Xunit.Sdk;

namespace PoTraffic.E2ETests.Helpers;

/// <summary>
/// Runtime skip helper for xUnit v2 (which lacks <c>Assert.Skip</c>).
/// Throw this from a test body to record the test outcome as <em>Skipped</em>
/// (not Passed) in the TRX, so a test whose prerequisite is missing
/// (e.g. real traffic provider) does not falsely inflate coverage counts.
///
/// Inherits from <see cref="XunitException"/> with a sentinel type name
/// that xUnit's runner recognises and reports as <c>Skipped</c>.
/// </summary>
public sealed class SkippableException : XunitException
{
    public SkippableException(string reason) : base(reason) { }
}
