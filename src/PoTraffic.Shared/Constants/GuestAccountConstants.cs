namespace PoTraffic.Shared.Constants;

/// <summary>
/// The shape of a GUEST account's email address (Rule 6 / Rule 13).
///
/// <para>
/// Lives in Shared because both sides depend on it: the API mints the address, and the client
/// detects a guest session from it to render "GUEST12345678 LOGGED IN". Held in one place —
/// and with the parse expressed as methods rather than restated as string surgery per caller —
/// so renaming the domain can't silently degrade the nav bar to a raw email address.
/// </para>
/// </summary>
public static class GuestAccountConstants
{
    /// <summary>Local-part prefix shared by all GUEST accounts.</summary>
    public const string EmailPrefix = "guest";

    /// <summary>Email domain suffix shared by all GUEST accounts.</summary>
    public const string EmailDomain = "@potraffic.dev";

    /// <summary>
    /// Number of random digits appended to the prefix.
    /// 8 digits = 100,000,000 unique GUEST identities (Rule 13).
    /// </summary>
    public const int SuffixLength = 8;

    /// <summary>Builds the address for a GUEST account with the given numeric suffix.</summary>
    public static string EmailFor(int suffix) => $"{EmailPrefix}{suffix}{EmailDomain}";

    /// <summary>True when <paramref name="email"/> is a minted GUEST address.</summary>
    public static bool IsGuestEmail(string? email)
        => email is not null
        && email.StartsWith(EmailPrefix, StringComparison.OrdinalIgnoreCase)
        && email.EndsWith(EmailDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>"guest12345678@potraffic.dev" → "GUEST12345678".</summary>
    public static string ToDisplayLabel(string email)
        => EmailPrefix.ToUpperInvariant() + email[EmailPrefix.Length..^EmailDomain.Length];
}
