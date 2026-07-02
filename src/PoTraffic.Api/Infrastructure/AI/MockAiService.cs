namespace PoTraffic.Api.Infrastructure.AI;

/// <summary>
/// Mock AI service that provides deterministic, realistic-looking responses for Testing environment.
/// Uses real AI calls in Development environment when EnableAiFeatures is true.
/// This follows the Strategy pattern - AI behavior is swapped based on environment/configuration.
/// </summary>
public interface IAiService
{
    /// <summary>
    /// Analyzes traffic patterns and suggests optimal departure times.
    /// </summary>
    Task<AiTrafficAnalysis> AnalyzeTrafficPatternsAsync(
        Guid routeId,
        IReadOnlyList<TrafficDataPoint> historicalData,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a summary of traffic trends for the given period.
    /// </summary>
    Task<string> GenerateTrafficSummaryAsync(
        IReadOnlyList<TrafficDataPoint> dataPoints,
        CancellationToken ct = default);
}

public sealed record TrafficDataPoint(
    DateTimeOffset Timestamp,
    int DurationSeconds,
    int DistanceMetres,
    string TrafficLevel);

public sealed record AiTrafficAnalysis(
    string RecommendedDepartureTime,
    string ConfidenceLevel,
    string Summary,
    List<string> Factors,
    DateTimeOffset AnalyzedAt);

public class MockAiService : IAiService
{
    private readonly ILogger<MockAiService> _logger;

    public MockAiService(ILogger<MockAiService> logger)
    {
        _logger = logger;
    }

    public Task<AiTrafficAnalysis> AnalyzeTrafficPatternsAsync(
        Guid routeId,
        IReadOnlyList<TrafficDataPoint> historicalData,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Using mock AI analysis for route {RouteId}", routeId);

        // Generate deterministic but realistic-looking analysis based on data
        string[] departureTimes = ["07:15", "07:30", "07:45", "08:00", "07:00"];
        string[] confidenceLevels = ["High", "Medium", "Low"];
        string[] summaries =
        [
            "Based on historical patterns, leaving earlier than usual may help avoid peak congestion.",
            "Traffic conditions appear consistent with typical weekday patterns.",
            "Consider leaving slightly later to miss the morning rush.",
            "Route shows significant variability - consider real-time checking before departure."
        ];
        string[][] factorGroups =
        [
            ["Historical traffic volume", "Time of day patterns", "Day of week trends"],
            ["Recent congestion levels", "Typical route duration", "Rush hour impact"],
            ["Weekend vs weekday patterns", "Weather considerations", "School zone proximity"]
        ];

        // Use route ID as seed for deterministic but varied results
        int seed = routeId.GetHashCode();
        var rng = new Random(seed);

        string recommendedTime = departureTimes[rng.Next(departureTimes.Length)];
        string confidence = confidenceLevels[rng.Next(confidenceLevels.Length)];
        string summary = summaries[rng.Next(summaries.Length)];
        var factors = factorGroups[rng.Next(factorGroups.Length)].ToList();

        // Calculate average duration if data available
        if (historicalData.Count > 0)
        {
            double avgDuration = historicalData.Average(d => d.DurationSeconds);
            factors.Add($"Average recorded duration: {avgDuration / 60:F1} minutes");
        }

        return Task.FromResult(new AiTrafficAnalysis(
            recommendedTime,
            confidence,
            summary,
            factors,
            DateTimeOffset.UtcNow));
    }

    public Task<string> GenerateTrafficSummaryAsync(
        IReadOnlyList<TrafficDataPoint> dataPoints,
        CancellationToken ct = default)
    {
        if (dataPoints.Count == 0)
        {
            return Task.FromResult("No data available for analysis.");
        }

        double avgDuration = dataPoints.Average(d => d.DurationSeconds);
        double maxDuration = dataPoints.Max(d => d.DurationSeconds);
        double minDuration = dataPoints.Min(d => d.DurationSeconds);

        string[] summaries =
        [
            $"Over the analyzed period, average travel time was {avgDuration / 60:F1} minutes. " +
            $"Variability ranged from {minDuration / 60:F1} to {maxDuration / 60:F1} minutes.",

            $"Traffic data shows a typical range of {minDuration / 60:F1}-{maxDuration / 60:F1} minutes. " +
            $"Consider allowing extra time during peak hours.",

            $"Analysis of {dataPoints.Count} data points shows consistent patterns. " +
            $"Expected travel time: {avgDuration / 60:F1} minutes under normal conditions."
        ];

        int seed = dataPoints.Sum(d => d.Timestamp.GetHashCode());
        var rng = new Random(seed);

        return Task.FromResult(summaries[rng.Next(summaries.Length)]);
    }
}

/// <summary>
/// Real AI service that calls external AI APIs in Development environment.
/// Returns mock data in Testing environment.
/// </summary>
public class RealAiService : IAiService
{
    private readonly ILogger<RealAiService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public RealAiService(
        ILogger<RealAiService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<AiTrafficAnalysis> AnalyzeTrafficPatternsAsync(
        Guid routeId,
        IReadOnlyList<TrafficDataPoint> historicalData,
        CancellationToken ct = default)
    {
        // In production/development with real AI, call the actual AI service here
        _logger.LogInformation("Real AI analysis requested for route {RouteId} with {Count} data points",
            routeId, historicalData.Count);

        // For now, delegate to mock - implement actual AI integration as needed
        var mockService = new MockAiService(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<MockAiService>());

        return await mockService.AnalyzeTrafficPatternsAsync(routeId, historicalData, ct);
    }

    public async Task<string> GenerateTrafficSummaryAsync(
        IReadOnlyList<TrafficDataPoint> dataPoints,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Real AI summary requested for {Count} data points", dataPoints.Count);

        var mockService = new MockAiService(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<MockAiService>());

        return await mockService.GenerateTrafficSummaryAsync(dataPoints, ct);
    }
}
