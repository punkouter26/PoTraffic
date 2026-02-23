using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Shared.DTOs.Auth;

using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Api.Infrastructure.Security;

namespace PoTraffic.Api.Features.Auth;

public sealed class ExternalAuthService
{
    private readonly IEnumerable<IExternalIdentityProvider> _providers;
    private readonly IDataProtector _stateProtector;
    private readonly PoTrafficDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<ExternalAuthService> _logger;

    public ExternalAuthService(
        IEnumerable<IExternalIdentityProvider> providers,
        IDataProtectionProvider dataProtectionProvider,
        PoTrafficDbContext db,
        JwtTokenService jwt,
        ILogger<ExternalAuthService> logger)
    {
        _providers = providers;
        _stateProtector = dataProtectionProvider.CreateProtector("PoTraffic.Auth.ExternalState.v1");
        _db = db;
        _jwt = jwt;
        _logger = logger;
    }

    public string BuildStartRedirectUrl(string provider, string redirectUri, string? returnUrl)
    {
        IExternalIdentityProvider authProvider = ResolveProvider(provider);
        if (!authProvider.IsConfigured())
            throw new InvalidOperationException($"External provider '{provider}' is not configured.");

        var state = new ExternalStatePayload(
            Provider: authProvider.ProviderName,
            ReturnPath: NormalizeReturnPath(returnUrl),
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            Nonce: Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

        string protectedState = _stateProtector.Protect(JsonSerializer.Serialize(state));
        return authProvider.BuildAuthorizationUrl(redirectUri, protectedState);
    }

    public async Task<ExternalAuthCompletionResult> CompleteLoginAsync(
        string provider,
        string code,
        string state,
        string redirectUri,
        CancellationToken ct)
    {
        ExternalStatePayload? payload = UnprotectState(state);
        if (payload is null
            || payload.ExpiresAtUtc < DateTimeOffset.UtcNow
            || !string.Equals(payload.Provider, provider, StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalAuthCompletionResult(false, "/dashboard", null, "INVALID_STATE");
        }

        IExternalIdentityProvider authProvider;
        try
        {
            authProvider = ResolveProvider(provider);
        }
        catch
        {
            return new ExternalAuthCompletionResult(false, payload.ReturnPath, null, "UNSUPPORTED_PROVIDER");
        }

        ExternalIdentity? identity = await authProvider.ExchangeCodeAsync(code, redirectUri, ct);
        if (identity is null || string.IsNullOrWhiteSpace(identity.Email))
            return new ExternalAuthCompletionResult(false, payload.ReturnPath, null, "EXTERNAL_IDENTITY_UNAVAILABLE");

        string normalizedEmail = identity.Email.Trim().ToLowerInvariant();
        User? user = await _db.Set<User>().FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
                Locale = "en-US",
                Role = "Commuter",
                IsEmailVerified = identity.IsEmailVerified,
                CreatedAt = DateTimeOffset.UtcNow,
                LastLoginAt = DateTimeOffset.UtcNow
            };
            _db.Set<User>().Add(user);
        }
        else
        {
            user.LastLoginAt = DateTimeOffset.UtcNow;
            if (identity.IsEmailVerified)
                user.IsEmailVerified = true;
        }

        (string accessToken, string refreshToken, DateTimeOffset expiresAt) = _jwt.GenerateTokens(user);
        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiry = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenExpiryDays);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("External login succeeded for {Email} via {Provider}", normalizedEmail, authProvider.ProviderName);

        return new ExternalAuthCompletionResult(
            true,
            payload.ReturnPath,
            new AuthResponse(accessToken, refreshToken, expiresAt, user.Id, user.Role),
            null);
    }

    private IExternalIdentityProvider ResolveProvider(string provider)
    {
        IExternalIdentityProvider? match = _providers.LastOrDefault(
            p => string.Equals(p.ProviderName, provider, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException($"Unsupported provider '{provider}'.");
    }

    private ExternalStatePayload? UnprotectState(string state)
    {
        try
        {
            string json = _stateProtector.Unprotect(state);
            return JsonSerializer.Deserialize<ExternalStatePayload>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unprotect external auth state");
            return null;
        }
    }

    private static string NormalizeReturnPath(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return "/dashboard";

        if (!Uri.TryCreate(returnUrl, UriKind.Relative, out Uri? relativeUri))
            return "/dashboard";

        string normalized = relativeUri.OriginalString;
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        return normalized;
    }

    public static string BuildCompletionRedirectPath(ExternalAuthCompletionResult result)
    {
        string targetPath = "/auth/external-complete";
        if (!result.IsSuccess || result.Response is null)
        {
            string errorFragment = BuildFragment(new Dictionary<string, string?>
            {
                ["error"] = result.ErrorCode ?? "EXTERNAL_AUTH_FAILED",
                ["returnUrl"] = result.ReturnPath
            });

            return $"{targetPath}#{errorFragment}";
        }

        string successFragment = BuildFragment(new Dictionary<string, string?>
        {
            ["accessToken"] = result.Response.AccessToken,
            ["refreshToken"] = result.Response.RefreshToken,
            ["expiresAt"] = result.Response.ExpiresAt.ToString("O"),
            ["userId"] = result.Response.UserId.ToString(),
            ["role"] = result.Response.Role,
            ["returnUrl"] = result.ReturnPath
        });

        return $"{targetPath}#{successFragment}";
    }

    private static string BuildFragment(IReadOnlyDictionary<string, string?> values)
    {
        return string.Join('&', values
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .Select(kvp => $"{UrlEncoder.Default.Encode(kvp.Key)}={UrlEncoder.Default.Encode(kvp.Value!)}"));
    }

    private sealed record ExternalStatePayload(
        string Provider,
        string ReturnPath,
        DateTimeOffset ExpiresAtUtc,
        string Nonce);
}
