using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;


namespace PoTraffic.Api.Infrastructure.Security;

/// <summary>
/// Generates and validates JWT access tokens and opaque refresh tokens.
/// </summary>
public sealed class JwtTokenService
{
    private readonly JwtConfiguration _config;

    public JwtTokenService(IOptions<JwtConfiguration> config)
    {
        _config = config.Value;
    }

    public int RefreshTokenExpiryDays => _config.RefreshTokenExpiryDays;

    /// <summary>
    /// Creates a signed JWT access token and a random refresh token for the given user.
    /// </summary>
    public (string accessToken, string refreshToken, DateTimeOffset expiresAt) GenerateTokens(User user)
    {
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddMinutes(_config.ExpiryMinutes);

        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(_config.Key));
        SigningCredentials creds = new(key, SecurityAlgorithms.HmacSha256);

        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("role", user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        ];

        JwtSecurityToken token = new(
            issuer: _config.Issuer,
            audience: _config.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds);

        string accessToken = new JwtSecurityTokenHandler().WriteToken(token);
        string refreshToken = GenerateRefreshToken();

        return (accessToken, refreshToken, expiresAt);
    }

    private static string GenerateRefreshToken()
    {
        Span<byte> bytes = stackalloc byte[64];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
