using FluentValidation;
using PoTraffic.Api.Infrastructure.Storage;

using MediatR;


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
    private readonly TableStorageContext _db;

    public UpdateSystemConfigurationHandler(TableStorageContext db) => _db = db;

    public Task<SystemConfigDto?> Handle(UpdateSystemConfigurationCommand command, CancellationToken ct)
    {
        SystemConfiguration? config =
            _db.SystemConfigurations.FirstOrDefault(c => c.Key == command.Key);

        if (config is null) return Task.FromResult<SystemConfigDto?>(null);

        config.Value = command.Value;
        await _db.SaveChangesAsync(ct);

        return Task.FromResult<SystemConfigDto?>(new SystemConfigDto(
            Key: config.Key,
            Value: config.IsSensitive ? GetSystemConfigurationHandler.Mask(config.Value) : config.Value,
            Description: config.Description,
            IsSensitive: config.IsSensitive));
    }
}
