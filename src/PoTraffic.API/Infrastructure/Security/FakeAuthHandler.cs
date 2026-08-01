using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace PoTraffic.API.Infrastructure.Security;

/// <summary>
/// Header-driven authentication for Development and Testing (Rule 3 — Dev/Test Auth).
/// A request carrying <c>X-Fake-User</c> is authenticated as that user, with the roles
/// listed in <c>X-Fake-Roles</c>. It lets integration, API-E2E and UI-E2E tests assume
/// any identity in a single header instead of driving a full OAuth round-trip.
///
/// <para>
/// ⚠️ Guardrail: constructing this handler — or registering it — in the Production
/// environment throws <see cref="InvalidOperationException"/>. The check is duplicated
/// deliberately: <see cref="SecurityExtensions"/> refuses to wire the scheme up, and the
/// handler itself refuses to run, so no future refactor of the registration path can
/// quietly expose header-spoofed identities in Production.
/// </para>
/// </summary>
public sealed class FakeAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Name of the authentication scheme this handler serves.</summary>
    public const string SchemeName = "Fake";

    /// <summary>Identifies the user: either a <see cref="UserId"/> GUID or an email address.</summary>
    public const string UserHeader = "X-Fake-User";

    /// <summary>Comma-separated role list. Defaults to <see cref="DefaultRole"/> when absent.</summary>
    public const string RolesHeader = "X-Fake-Roles";

    /// <summary>Role assigned when <see cref="RolesHeader"/> is absent or blank.</summary>
    public const string DefaultRole = "Commuter";

    /// <summary>Value of the <c>auth_provider</c> claim, so downstream policies can spot fake sessions.</summary>
    public const string AuthProvider = "fake";

    public FakeAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IWebHostEnvironment environment)
        : base(options, logger, encoder)
    {
        GuardNotProduction(environment);
    }

    /// <summary>
    /// Throws when <paramref name="environment"/> is Production. Shared with
    /// <see cref="SecurityExtensions"/> so registration and construction fail identically.
    /// </summary>
    public static void GuardNotProduction(IWebHostEnvironment environment)
    {
        if (environment.IsProduction())
            throw new InvalidOperationException(
                $"{nameof(FakeAuthHandler)} must never be active in Production — it authenticates " +
                $"callers from the '{UserHeader}' request header, which any client can set. " +
                "Microsoft OAuth is the only sign-in path in Production.");
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out StringValues user) || string.IsNullOrWhiteSpace(user))
            return Task.FromResult(AuthenticateResult.NoResult());

        string identifier = user.ToString().Trim();
        (UserId userId, string email) = ResolveIdentity(identifier);

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Email, email),
            new("email", email),
            new("auth_provider", AuthProvider),
        ];

        foreach (string role in ParseRoles(Request.Headers[RolesHeader].ToString()))
            claims.Add(new Claim(ClaimTypes.Role, role));

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    /// <summary>
    /// A GUID header value is used verbatim as the <see cref="UserId"/>; anything else is
    /// treated as an email and mapped to a deterministic id, so repeated requests for
    /// <c>alice@example.com</c> resolve to the same user across a test run.
    /// </summary>
    private static (UserId Id, string Email) ResolveIdentity(string identifier)
        => UserId.TryParse(identifier, null, out UserId parsed)
            ? (parsed, $"{parsed.Value:N}@fake.potraffic.dev")
            : (UserId.From(DeterministicGuid(identifier)), identifier);

    /// <summary>Stable id for a non-GUID identifier. Not a security primitive — just a fixed mapping.</summary>
    private static Guid DeterministicGuid(string value)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())).AsSpan(0, 16));

    private static IEnumerable<string> ParseRoles(string header)
    {
        string[] roles = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return roles.Length == 0 ? [DefaultRole] : roles;
    }
}
