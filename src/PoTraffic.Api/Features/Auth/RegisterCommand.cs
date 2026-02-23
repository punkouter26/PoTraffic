using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoTraffic.Api.Infrastructure.Data;

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
    private readonly PoTrafficDbContext _db;

    public RegisterCommandValidator(PoTrafficDbContext db)
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
            .MaximumLength(50);
    }

    private async Task<bool> BeUnique(string email, CancellationToken ct)
        => !await _db.Set<User>().AnyAsync(u => u.Email == email, ct);
}

public sealed class RegisterCommandHandler : IRequestHandler<RegisterCommand, RegisterResult>
{
    private readonly PoTrafficDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<RegisterCommandHandler> _logger;

    public RegisterCommandHandler(
        PoTrafficDbContext db,
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

        _db.Set<User>().Add(user);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException?.Message.Contains("UX_Users_Email", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("IX_Users_Email", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Defense-in-depth: validator (BeUnique) already checks uniqueness,
            // but a race condition can still cause a constraint violation.
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
