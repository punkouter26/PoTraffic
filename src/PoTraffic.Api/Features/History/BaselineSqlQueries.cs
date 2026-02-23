namespace PoTraffic.Api.Features.History;

/// <summary>
/// Shared raw-SQL fragments for per-route baseline aggregation queries.
/// DRY: extracted from GetBaselineQuery and GetOptimalDepartureQuery to prevent drift.
/// Parameters: @routeId (Guid), @dayOfWeek (string).
/// </summary>
internal static class BaselineSqlQueries
{
    /// <summary>
    /// Aggregates PollRecords by DayOfWeek × 5-minute TimeSlotBucket for a single route
    /// and day. Requires at least 3 distinct calendar days per bucket.
    /// STDEV cannot be expressed in LINQ — raw SQL is intentional per §6.2 of data-model.md.
    /// </summary>
    internal const string SlotAggregate = """
        SELECT
            DATENAME(dw, pr.PolledAt)                                       AS DayOfWeek,
            (DATEPART(hh, pr.PolledAt) * 60) + (DATEPART(mi, pr.PolledAt) / 5 * 5)
                                                                            AS TimeSlotBucket,
            AVG(CAST(pr.TravelDurationSeconds AS float))                    AS MeanDurationSeconds,
            STDEV(CAST(pr.TravelDurationSeconds AS float))                  AS StdDevDurationSeconds,
            COUNT(*)                                                        AS SessionCount
        FROM   dbo.PollRecords pr
        WHERE  pr.RouteId = @routeId
           AND pr.IsDeleted = 0
           AND pr.SessionId IS NOT NULL
           AND DATENAME(dw, pr.PolledAt) = @dayOfWeek
           AND pr.PolledAt >= DATEADD(day, -90, GETUTCDATE())
        GROUP BY
            DATENAME(dw, pr.PolledAt),
            (DATEPART(hh, pr.PolledAt) * 60) + (DATEPART(mi, pr.PolledAt) / 5 * 5)
        HAVING COUNT(DISTINCT CAST(CAST(pr.PolledAt AS date) AS nvarchar)) >= 3
        ORDER BY TimeSlotBucket
        """;
}
