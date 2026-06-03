using MediatR;
using PoTraffic.Api.Infrastructure.Storage;

using Microsoft.Extensions.Logging;



namespace PoTraffic.Api.Features.Maintenance;

public sealed record PruneOldPollRecordsCommand : IRequest<int>;

// Command pattern — encapsulates nightly batch mutation as a discrete MediatR command
public sealed class PruneOldPollRecordsCommandHandler
    : IRequestHandler<PruneOldPollRecordsCommand, int>
{
    private readonly TableStorageContext _db;
    private readonly ILogger<PruneOldPollRecordsCommandHandler> _logger;

    public PruneOldPollRecordsCommandHandler(
        TableStorageContext db,
        ILogger<PruneOldPollRecordsCommandHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> Handle(PruneOldPollRecordsCommand command, CancellationToken ct)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-90);

        // IgnoreQueryFilters to bypass global soft-delete filter (FR-020)
        List<PollRecord> oldRecords = _db.PollRecords
            .Where(p => !p.IsDeleted && p.PolledAt < cutoff)
            .ToList();

        if (oldRecords.Count == 0)
            return 0;

        foreach (PollRecord record in oldRecords)
        {
            record.IsDeleted = true;
            record.RawProviderResponse = null;  // free storage; no longer needed post-pruning
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "PruneOldPollRecordsJob: soft-deleted {Count} PollRecords older than {Cutoff}",
            oldRecords.Count, cutoff);

        return oldRecords.Count;
    }
}

/// <summary>
/// Thin Hangfire dispatch wrapper invoked by the recurring job scheduler.
/// Hangfire resolves this via HangfireJobActivator (DI scope).
/// </summary>
public sealed class PruneOldPollRecordsJob
{
    private readonly ISender _sender;

    public PruneOldPollRecordsJob(ISender sender)
    {
        _sender = sender;
    }

    public async Task ExecuteAsync()
    {
        await _sender.Send(new PruneOldPollRecordsCommand());
    }
}
