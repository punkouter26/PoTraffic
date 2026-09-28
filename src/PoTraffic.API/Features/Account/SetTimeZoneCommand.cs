using FluentValidation;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.API.Infrastructure.Time;

namespace PoTraffic.API.Features.Account;

/// <summary>
/// Records the zone the user's browser reports. Sent on every app load, so it is a no-op
/// (no write) unless the zone actually changed.
/// </summary>
public sealed record SetTimeZoneCommand(UserId UserId, string TimeZoneId);

public sealed record SetTimeZoneRequest(string TimeZoneId);

public sealed class SetTimeZoneValidator : AbstractValidator<SetTimeZoneCommand>
{
    public SetTimeZoneValidator()
    {
        RuleFor(c => c.TimeZoneId)
            .NotEmpty()
            .MaximumLength(64)
            .Must(z => UserTime.TryFindZone(z) is not null)
            .WithMessage("Unknown time zone.");
    }
}

public sealed class SetTimeZoneHandler(TableStorageContext db)
{
    private static readonly SetTimeZoneValidator Validator = new();

    public async Task<bool> Handle(SetTimeZoneCommand command, CancellationToken ct)
    {
        await Validator.ValidateAndThrowAsync(command, ct);

        User? user = db.Users.FirstOrDefault(u => u.Id == command.UserId);
        if (user is null)
            return false;

        if (user.TimeZoneId != command.TimeZoneId)
        {
            user.TimeZoneId = command.TimeZoneId;
            await db.SaveChangesAsync(ct);
        }
        return true;
    }
}
