using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace PoTraffic.Api.Features.Auth;

/// <summary>
/// Validates the Microsoft <c>id_token</c> returned by the authorization-code exchange (§4.3):
/// JWKS signature (from the <c>/common</c> OIDC metadata), audience (= our client id),
/// lifetime, and a <b>shape-based issuer validator</b> that pins the issuer to the token's
/// <c>tid</c> claim and an allow-list of tenant ids.
///
/// The <c>/common</c> endpoint is multi-tenant, so there is no single static issuer — the
/// issuer is templated <c>https://login.microsoftonline.com/{tid}/v2.0</c>. A naive
/// <c>ValidIssuer</c> would either reject everyone or accept any tenant; the shape validator
/// is the correct way to enforce <c>ValidateIssuer = true</c> against an allowed-tenant list.
/// Registered as a singleton so the <see cref="ConfigurationManager{T}"/> caches and
/// auto-refreshes signing keys instead of fetching JWKS on every sign-in.
/// </summary>
public sealed class MicrosoftIdTokenValidator
{
    private const string MetadataAddress =
        "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration";

    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configManager = new(
        MetadataAddress,
        new OpenIdConnectConfigurationRetriever());

    private readonly JsonWebTokenHandler _handler = new();

    public async Task<ExternalIdentity?> ValidateAsync(
        string idToken,
        string clientId,
        IReadOnlyCollection<string> allowedTenantIds,
        CancellationToken ct)
    {
        OpenIdConnectConfiguration config = await _configManager.GetConfigurationAsync(ct);

        TokenValidationParameters parameters = new()
        {
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateIssuer = true,
            IssuerValidator = (issuer, token, _) => ValidateIssuerShape(issuer, token, allowedTenantIds),
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
        };

        TokenValidationResult result = await _handler.ValidateTokenAsync(idToken, parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            return null;

        string? subject = jwt.Subject;
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        string email = Claim(jwt, "email") ?? Claim(jwt, "preferred_username") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
            return null;

        bool emailVerified = bool.TryParse(Claim(jwt, "email_verified"), out bool verified)
            ? verified
            : !string.IsNullOrWhiteSpace(Claim(jwt, "email"));

        return new ExternalIdentity(subject, email, emailVerified);
    }

    /// <summary>
    /// Shape-based issuer validator: the issuer MUST equal
    /// <c>https://login.microsoftonline.com/{tid}/v2.0</c> for the token's own <c>tid</c>,
    /// and that tenant MUST be permitted. An empty allow-list accepts any Microsoft tenant —
    /// work/school and personal (Outlook/Live, tenant <c>9188040d-…</c>) — matching the
    /// <c>AzureADandPersonalMicrosoftAccount</c> app audience. To restrict, list the exact
    /// tenant ids (add the personal-account tenant explicitly if personal MSAs are wanted).
    /// </summary>
    private static string ValidateIssuerShape(
        string issuer,
        SecurityToken token,
        IReadOnlyCollection<string> allowedTenantIds)
    {
        if (token is not JsonWebToken jwt || !jwt.TryGetClaim("tid", out Claim? tidClaim))
            throw new SecurityTokenInvalidIssuerException("id_token is missing the required 'tid' claim.");

        string tid = tidClaim.Value;
        string expected = $"https://login.microsoftonline.com/{tid}/v2.0";
        if (!string.Equals(issuer, expected, StringComparison.Ordinal))
            throw new SecurityTokenInvalidIssuerException(
                $"Issuer '{issuer}' does not match the token tenant '{tid}'.");

        if (allowedTenantIds.Count > 0 && !allowedTenantIds.Contains(tid, StringComparer.OrdinalIgnoreCase))
            throw new SecurityTokenInvalidIssuerException($"Tenant '{tid}' is not in the allowed list.");

        return issuer;
    }

    private static string? Claim(JsonWebToken jwt, string type)
        => jwt.TryGetClaim(type, out Claim? claim) ? claim.Value : null;
}
