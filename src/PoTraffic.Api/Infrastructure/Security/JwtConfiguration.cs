namespace PoTraffic.Api.Infrastructure.Security;

/// <summary>Strongly-typed binding for the <c>Jwt</c> configuration section.</summary>
public sealed class JwtConfiguration
{
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; }
    public int RefreshTokenExpiryDays { get; set; }
}
