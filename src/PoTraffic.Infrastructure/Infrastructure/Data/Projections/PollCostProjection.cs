namespace PoTraffic.Api.Infrastructure.Data.Projections;

/// <summary>
/// Keyless projection used by poll-cost raw-SQL aggregation.
/// Provider is stored as int to match the database column type.
/// </summary>
public sealed class PollCostProjection
{
    public int ProviderInt { get; set; }
    public int PollCount { get; set; }
}
