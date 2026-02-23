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

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? RefreshTokenExpiry { get; set; }

    public ICollection<EntityRoute> Routes { get; set; } = new List<EntityRoute>();
}
