using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.Admin;

namespace PoTraffic.Api.Features.Admin;

// Query pattern — read system configuration; sensitive values masked per FR-026
public sealed record GetSystemConfigurationQuery : IRequest<IReadOnlyList<SystemConfigDto>>;

public sealed class GetSystemConfigurationHandler : IRequestHandler<GetSystemConfigurationQuery, IReadOnlyList<SystemConfigDto>>
{
    private readonly PoTrafficDbContext _db;

    public GetSystemConfigurationHandler(PoTrafficDbContext db) => _db = db;

    public async Task<IReadOnlyList<SystemConfigDto>> Handle(GetSystemConfigurationQuery query, CancellationToken ct)
    {
        List<SystemConfiguration> configs =
            await _db.SystemConfigurations.AsNoTracking().OrderBy(c => c.Key).ToListAsync(ct);

        return configs.Select(c => new SystemConfigDto(
            Key: c.Key,
            Value: c.IsSensitive ? Mask(c.Value) : c.Value,
            Description: c.Description,
            IsSensitive: c.IsSensitive)).ToList();
    }

    /// <summary>
    /// FR-026: masks sensitive values as first-2-chars + "****" + last-2-chars.
    /// Values shorter than 4 characters are fully masked.
    /// </summary>
    internal static string Mask(string value)
    {
        if (value.Length <= 4) return "****";
        return value[..2] + "****" + value[^2..];
    }
}
