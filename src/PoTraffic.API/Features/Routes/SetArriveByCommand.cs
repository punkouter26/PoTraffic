using FluentValidation;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.API.Features.Routes;

/// <summary>Sets (or clears, with null) the local time the user needs to arrive by.</summary>
public sealed record SetArriveByCommand(RouteId RouteId, UserId UserId, string? ArriveBy);

public sealed record SetArriveByRequest(string? ArriveBy);

public sealed class SetArriveByValidator : AbstractValidator<SetArriveByCommand>
{
    public SetArriveByValidator()
    {
        RuleFor(c => c.ArriveBy)
            .Must(t => t is null || TimeOnly.TryParseExact(t, "HH:mm", out _))
            .WithMessage("Arrive-by must be HH:mm.");
    }
}

public sealed class SetArriveByHandler(TableStorageContext db)
{
    private static readonly SetArriveByValidator Validator = new();

    public async Task<bool> Handle(SetArriveByCommand cmd, CancellationToken ct)
    {
        await Validator.ValidateAndThrowAsync(cmd, ct);

        EntityRoute? route = db.GetOwnedRoute(cmd.RouteId, cmd.UserId, excludeDeleted: true);
        if (route is null)
            return false;

        route.ArriveBy = cmd.ArriveBy is null ? null : TimeOnly.ParseExact(cmd.ArriveBy, "HH:mm");
        await db.SaveChangesAsync(ct);
        return true;
    }
}
