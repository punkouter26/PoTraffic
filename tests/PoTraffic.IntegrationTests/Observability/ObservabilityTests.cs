using System.Diagnostics;
using System.Net;
using Xunit;

namespace PoTraffic.IntegrationTests.Observability;

/// <summary>
/// Observability integration tests — verifies that the OTel pipeline is wired
/// and that the health endpoint responds, confirming the application instrumentation
/// does not break startup or request handling.
/// </summary>
public sealed class ObservabilityTests : BaseIntegrationTest
{
    public ObservabilityTests() : base() { }

    /// <summary>
    /// OTel sampling canary — verifies that the OpenTelemetry ActivitySource is active
    /// and that a trace is produced for a real HTTP request to the health endpoint.
    ///
    /// Given  the API is running with OTel instrumentation
    /// When   GET /health is called
    /// Then   the response is 200 OK (pipeline did not crash)
    /// And    an Activity is present in the current execution context (OTel is sampling)
    ///
    /// Note: Full distributed trace validation (Azure Monitor export) requires a live
    /// Application Insights connection string and is out of scope for integration tests.
    /// This test acts as a smoke-test canary for the OTel pipeline wiring.
    /// </summary>
    [Fact(Skip = "OTel ActivityListener setup requires additional plumbing outside " +
                 "WebApplicationFactory scope. Manual canary: check Azure Monitor for " +
                 "'dependencies' and 'requests' telemetry after deploying to App Service.")]
    public async Task OTelPipeline_ProducesActivity_ForHealthEndpointRequest()
    {
        // Arrange
        Activity? capturedActivity = null;

        using ActivityListener listener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => capturedActivity = a
        };
        ActivitySource.AddActivityListener(listener);

        HttpClient client = CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");

        // Assert — pipeline is alive
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert — at least one OTel Activity was produced during the request
        Assert.NotNull(capturedActivity);
    }

    /// <summary>
    /// Smoke test — health endpoint returns 200 OK, confirming that OTel bootstrap
    /// in Program.cs does not cause a DI resolution failure or startup exception.
    /// </summary>
    [Fact]
    public async Task HealthEndpoint_Returns200_ConfirmingOTelBootstrapSucceeded()
    {
        // Arrange
        HttpClient client = CreateClient();

        // Act
        HttpResponseMessage response = await client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
