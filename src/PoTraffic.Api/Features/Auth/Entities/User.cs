namespace PoTraffic.Api.Features.Auth.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Locale { get; set; } = string.Empty;
    public bool IsGdprDeleteRequested { get; set; }
    public bool IsEmailVerified { get; set; }
    public string? EmailVerificationToken { get; set; }

    /// <summary>"Commuter" or "Administrator"</summary>
    public string Role { get; set; } = "Commuter";

    /// <summary>
    /// Identity provider that minted the most recent session for this user.
    /// One of: "password", "guest", "google", "microsoft".
    /// Used by the production Microsoft-only auth policy (Rule 13).
    /// </summary>
    public string AuthProvider { get; set; } = "password";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? RefreshTokenExpiry { get; set; }

    public ICollection<EntityRoute> Routes { get; set; } = new List<EntityRoute>();
}
