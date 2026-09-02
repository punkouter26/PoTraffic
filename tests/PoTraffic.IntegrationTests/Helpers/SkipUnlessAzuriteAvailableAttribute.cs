using Xunit;

namespace PoTraffic.IntegrationTests.Helpers;

/// <summary>
/// Conditional Fact — runs the test when Docker is reachable, otherwise skips it.
/// Azurite itself is created and cleaned up by Testcontainers inside the integration test host.
///
/// <para>
/// EVERY test method on a <c>BaseIntegrationTest</c> subclass must use this instead of
/// <c>[Fact]</c>: the base class's <c>InitializeAsync</c> starts the Azurite container, so a
/// plain <c>[Fact]</c> hard-fails with <c>DockerUnavailableException</c> on a machine without
/// Docker rather than skipping with the rest of the suite.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipUnlessAzuriteAvailableAttribute : FactAttribute
{
    public SkipUnlessAzuriteAvailableAttribute()
    {
        if (!DockerAvailability.IsAvailable)
            Skip = DockerAvailability.SkipReason;
    }
}
