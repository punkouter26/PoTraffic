using FluentValidation;
using PoTraffic.Api.Infrastructure.Storage;

using MediatR;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;



using PoTraffic.Api.Infrastructure.Security;

using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.Api.Features.Auth;

public sealed record RegisterCommand(
    string Email,
    string Password,
    string Locale) : IRequest<RegisterResult>;

public sealed record RegisterResult(
    bool IsSuccess,
    AuthResponse? Response,
    string? ErrorCode);

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    private static readonly HashSet<string> _validLocales = ["en-IE", "en-GB", "en-US", "de-DE", "fr-FR"];

    private readonly TableStorageContext _db;

    public RegisterCommandValidator(TableStorageContext db)
    {
        _db = db;

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MustAsync(BeUnique).WithMessage("Email is already registered.");

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8)
            .WithMessage("Password must be at least 8 characters.");

        RuleFor(x => x.Locale)
            .NotEmpty()
            .Must(l => _validLocales.Contains(l))
            .WithMessage("Locale must be one of: en-IE, en-GB, en-US, de-DE, fr-FR.");
    }

    private async Task<bool> BeUnique(string email, CancellationToken ct)
        => !_db.Set<User>().Any(u => u.Email == email);
}

public sealed class RegisterCommandHandler : IRequestHandler<RegisterCommand, RegisterResult>
{
    private readonly TableStorageContext _db;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<RegisterCommandHandler> _logger;

    public RegisterCommandHandler(
        TableStorageContext db,
        JwtTokenService jwt,
        ILogger<RegisterCommandHandler> logger)
    {
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    public async Task<RegisterResult> Handle(RegisterCommand command, CancellationToken ct)
    {
        // FR-029: Administrator role is not self-registrable
        string hash = BCrypt.Net.BCrypt.HashPassword(command.Password);
        string verificationToken = Guid.NewGuid().ToString("N");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = command.Email,
            PasswordHash = hash,
            Locale = command.Locale,
            Role = "Commuter",
            IsEmailVerified = false,
            EmailVerificationToken = verificationToken,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Add(user);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (
            ex.Message.Contains("UX_Users_Email", StringComparison.OrdinalIgnoreCase) == true
            || ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Defense-in-depth: validator (BeUnique) already checks uniqueness.
            _logger.LogWarning(ex, "Duplicate email race condition for {Email}", command.Email);
            return new RegisterResult(false, null, "DUPLICATE_EMAIL");
        }

        // MVP: no SMTP — emit verification token to structured log
        _logger.LogInformation(
            "Email verification token for {Email}: {Token}",
            command.Email, verificationToken);

        (string accessToken, string refreshToken, DateTimeOffset expiresAt) = _jwt.GenerateTokens(user);
        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenExpiryDays);
        await _db.SaveChangesAsync(ct);

        return new RegisterResult(
            true,
            new AuthResponse(accessToken, refreshToken, expiresAt, user.Id, user.Role),
            null);
    }
}
