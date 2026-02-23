using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.Admin;

namespace PoTraffic.Api.Features.Admin;

// Command pattern — update a single configuration entry
public sealed record UpdateSystemConfigurationCommand(string Key, string Value) : IRequest<SystemConfigDto?>;

public sealed class UpdateSystemConfigurationValidator : AbstractValidator<UpdateSystemConfigurationCommand>
{
    public UpdateSystemConfigurationValidator()
    {
        RuleFor(c => c.Key).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Value).NotNull().MaximumLength(500);
    }
}

public sealed class UpdateSystemConfigurationHandler : IRequestHandler<UpdateSystemConfigurationCommand, SystemConfigDto?>
{
    private readonly PoTrafficDbContext _db;

    public UpdateSystemConfigurationHandler(PoTrafficDbContext db) => _db = db;

    public async Task<SystemConfigDto?> Handle(UpdateSystemConfigurationCommand command, CancellationToken ct)
    {
        SystemConfiguration? config =
            await _db.SystemConfigurations.FirstOrDefaultAsync(c => c.Key == command.Key, ct);

        if (config is null) return null;

        config.Value = command.Value;
        await _db.SaveChangesAsync(ct);

        return new SystemConfigDto(
            Key: config.Key,
            Value: config.IsSensitive ? GetSystemConfigurationHandler.Mask(config.Value) : config.Value,
            Description: config.Description,
            IsSensitive: config.IsSensitive);
    }
}
