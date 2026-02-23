using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.Api.Features.Account;

public sealed record GetProfileQuery(Guid UserId) : IRequest<ProfileDto?>;

public sealed class GetProfileHandler : IRequestHandler<GetProfileQuery, ProfileDto?>
{
    private readonly PoTrafficDbContext _db;

    public GetProfileHandler(PoTrafficDbContext db) => _db = db;

    public async Task<ProfileDto?> Handle(GetProfileQuery query, CancellationToken ct)
    {
        User? user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == query.UserId, ct);

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
