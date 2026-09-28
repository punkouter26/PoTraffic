using FluentValidation;
using PoTraffic.API.Infrastructure.Storage;



using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.API.Features.Account;

public sealed record UpdateProfileCommand(UserId UserId, string Locale);

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    private static readonly HashSet<string> _validLocales = ["en-IE", "en-GB", "en-US", "de-DE", "fr-FR"];

    public UpdateProfileValidator()
    {
        RuleFor(c => c.Locale)
            .NotEmpty()
            .Must(l => _validLocales.Contains(l))
            .WithMessage("Locale must be one of: en-IE, en-GB, en-US, de-DE, fr-FR.");
    }
}

public sealed class UpdateProfileHandler
{
    private readonly TableStorageContext _db;

    public UpdateProfileHandler(TableStorageContext db) => _db = db;

    private static readonly UpdateProfileValidator Validator = new();

    public async Task<ProfileDto?> Handle(UpdateProfileCommand command, CancellationToken ct)
    {
        await Validator.ValidateAndThrowAsync(command, ct);

        User? user = _db.Users
            .FirstOrDefault(u => u.Id == command.UserId);

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
