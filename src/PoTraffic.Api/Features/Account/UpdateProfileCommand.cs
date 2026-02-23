using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.Api.Features.Account;

public sealed record UpdateProfileCommand(Guid UserId, string Locale) : IRequest<ProfileDto?>;

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    private static readonly HashSet<string> _validLocales = ["en-IE", "en-GB", "en-US", "de-DE", "fr-FR"];

    public UpdateProfileValidator()
    {
        RuleFor(c => c.Locale).NotEmpty().MaximumLength(10);
    }
}

public sealed class UpdateProfileHandler : IRequestHandler<UpdateProfileCommand, ProfileDto?>
{
    private readonly PoTrafficDbContext _db;

    public UpdateProfileHandler(PoTrafficDbContext db) => _db = db;

    public async Task<ProfileDto?> Handle(UpdateProfileCommand command, CancellationToken ct)
    {
        User? user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == command.UserId, ct);

        if (user is null) return null;

        user.Locale = command.Locale;
        await _db.SaveChangesAsync(ct);

        return new ProfileDto(
            UserId: user.Id,
            Email: user.Email,
            Locale: user.Locale,
            CreatedAt: user.CreatedAt,
            LastLoginAt: user.LastLoginAt,
            Role: user.Role);
    }
}
