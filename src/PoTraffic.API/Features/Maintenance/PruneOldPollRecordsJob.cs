using PoTraffic.API.Infrastructure.Storage;

using Microsoft.Extensions.Logging;



namespace PoTraffic.API.Features.Maintenance;

/// <summary>
/// Hard-deletes PollRecords older than the cutoff (default: 90 days). The previous
/// implementation soft-deleted and nulled out <c>RawProviderResponse</c>, but the raw
/// payload was never read back — the soft-delete flag and the column became pure
/// storage overhead with no behavioural value. Hard-delete keeps the table small and
/// removes the IsDeleted filter from every read query.
/// </summary>
public sealed record PruneOldPollRecordsCommand;

public sealed class PruneOldPollRecordsCommandHandler
{
    private const int RetentionDays = 90;

    private readonly TableStorageContext _db;
    private readonly ILogger<PruneOldPollRecordsCommandHandler> _logger;

    public PruneOldPollRecordsCommandHandler(
        TableStorageContext db,
        ILogger<PruneOldPollRecordsCommandHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<int> Handle(PruneOldPollRecordsCommand command, CancellationToken ct)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

        List<PollRecord> oldRecords = _db.PollRecords
            .Where(p => p.PolledAt < cutoff)
            .ToList();

        if (oldRecords.Count == 0)
            return Task.FromResult(0);

        _db.RemoveRange(oldRecords);
        _logger.LogInformation(
            "PruneOldPollRecordsJob: hard-deleted {Count} PollRecords older than {Cutoff:o}",
            oldRecords.Count, cutoff);

        return Task.FromResult(oldRecords.Count);
    }
}

/// <summary>Entry point the background job scheduler resolves from a DI scope.</summary>
public sealed class PruneOldPollRecordsJob(PruneOldPollRecordsCommandHandler handler)
{
    public Task ExecuteAsync() => handler.Handle(new PruneOldPollRecordsCommand(), CancellationToken.None);
}