using System.Text.Json.Serialization;

namespace PoTraffic.API.Features.Auth;

public sealed class User
{
    public UserId Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Locale { get; set; } = string.Empty;

    /// <summary>
    /// IANA zone the browser reports (e.g. <c>America/Los_Angeles</c>), synced on every app
    /// load. Per-slot statistics bucket in this zone; null until the first sync, when the
    /// zone is guessed from <see cref="Locale"/> (see <c>UserTime.ZoneFor</c>).
    /// </summary>
    public string? TimeZoneId { get; set; }
    public bool IsGdprDeleteRequested { get; set; }
    public bool IsEmailVerified { get; set; }

    /// <summary>"Commuter" or "Administrator"</summary>
    public string Role { get; set; } = "Commuter";

    /// <summary>
    /// Identity provider that minted the most recent session for this user.
    /// One of: "guest", "microsoft". Used by the production Microsoft-only
    /// auth policy (Rule 13).
    /// </summary>
    public string AuthProvider { get; set; } = "microsoft";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    [JsonIgnore]
    public ICollection<EntityRoute> Routes { get; set; } = new List<EntityRoute>();
}
