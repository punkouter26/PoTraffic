using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Storage;

namespace PoTraffic.Api.Features.Auth;

public sealed class ExternalAuthService(
    IEnumerable<IExternalIdentityProvider> providers,
    IDataProtectionProvider dataProtectionProvider,
    TableStorageContext db,
    IConfiguration configuration,
    ILogger<ExternalAuthService> logger)
{
    private readonly IDataProtector _stateProtector =
        dataProtectionProvider.CreateProtector("PoTraffic.Auth.ExternalState.v1");

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
        User? user = db.Users.FirstOrDefault(u => u.Email == normalizedEmail);

        // Admin email promotion — configured in Auth:AdminEmail (non-sensitive, stored in appsettings)
        string? adminEmail = configuration["Auth:AdminEmail"];
        bool isAdmin = !string.IsNullOrWhiteSpace(adminEmail)
            && string.Equals(normalizedEmail, adminEmail.Trim().ToLowerInvariant(), StringComparison.Ordinal);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                Locale = "en-US",
                Role = isAdmin ? "Administrator" : "Commuter",
                AuthProvider = authProvider.ProviderName.ToLowerInvariant(),
                IsEmailVerified = identity.IsEmailVerified,
                CreatedAt = DateTimeOffset.UtcNow,
                LastLoginAt = DateTimeOffset.UtcNow
            };
            db.Add(user);
        }
        else
        {
            user.LastLoginAt = DateTimeOffset.UtcNow;
            if (identity.IsEmailVerified)
                user.IsEmailVerified = true;
            // Ensure admin role is always set correctly — guards against DB records created before this rule
            if (isAdmin && user.Role != "Administrator")
                user.Role = "Administrator";
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("External login succeeded for {Email} via {Provider}", normalizedEmail, authProvider.ProviderName);

        return new ExternalAuthCompletionResult(true, payload.ReturnPath, user, null);
    }

    private IExternalIdentityProvider ResolveProvider(string provider)
    {
        IExternalIdentityProvider? match = providers.LastOrDefault(
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
            logger.LogWarning(ex, "Failed to unprotect external auth state");
            return null;
        }
    }

    private static string NormalizeReturnPath(string? returnUrl)
    {
        const string fallback = "/dashboard";

        if (string.IsNullOrWhiteSpace(returnUrl))
            return fallback;

        if (!Uri.TryCreate(returnUrl, UriKind.Relative, out Uri? relativeUri))
            return fallback;

        string normalized = relativeUri.OriginalString;

        // Must be a same-site absolute path. Reject protocol-relative ("//evil.com")
        // and backslash-tricked ("/\evil.com") forms the browser would treat as a
        // host — otherwise the post-login Redirect becomes an open redirect.
        if (!normalized.StartsWith('/')
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.StartsWith("/\\", StringComparison.Ordinal))
        {
            return fallback;
        }

        return normalized;
    }

    private sealed record ExternalStatePayload(
        string Provider,
        string ReturnPath,
        DateTimeOffset ExpiresAtUtc,
        string Nonce);
}
