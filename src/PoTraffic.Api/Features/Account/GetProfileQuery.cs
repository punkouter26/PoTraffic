using PoTraffic.Api.Infrastructure.Storage;


using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.Api.Features.Account;

public sealed record GetProfileQuery(Guid UserId) : IRequest<ProfileDto?>;

public sealed class GetProfileHandler : IRequestHandler<GetProfileQuery, ProfileDto?>
{
    private readonly TableStorageContext _db;

    public GetProfileHandler(TableStorageContext db) => _db = db;

    public async Task<ProfileDto?> Handle(GetProfileQuery query, CancellationToken ct)
    {
        User? user = _db.Users
            
            .FirstOrDefault(u => u.Id == query.UserId);

        if (user is null) return null;

        return new ProfileDto(
            UserId: user.Id,
            Email: user.Email,
            Locale: user.Locale,
            CreatedAt: user.CreatedAt,
            LastLoginAt: user.LastLoginAt,
            Role: user.Role);
    }
}
