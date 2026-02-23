using FluentValidation;
using MediatR;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

/// <summary>
/// Geocodes a single address via the chosen provider.
/// Used by the "Verify" button on the Create/Edit Route form before saving.
/// Does not persist any data.
/// </summary>
public sealed record VerifySingleAddressCommand(
    string Address,
    RouteProvider Provider) : IRequest<VerifySingleAddressResult>;

public sealed record VerifySingleAddressResult(
    bool IsValid,
    string? Coordinates,
    string? ErrorCode);

public sealed class VerifySingleAddressValidator : AbstractValidator<VerifySingleAddressCommand>
{
    public VerifySingleAddressValidator()
    {
        RuleFor(x => x.Address).NotEmpty().MaximumLength(ValidationConstants.AddressMaxLength);
        RuleFor(x => x.Provider).IsInEnum();
    }
}

public sealed class VerifySingleAddressCommandHandler
    : IRequestHandler<VerifySingleAddressCommand, VerifySingleAddressResult>
{
    private readonly ITrafficProviderFactory _providerFactory;

    public VerifySingleAddressCommandHandler(ITrafficProviderFactory providerFactory)
    {
        _providerFactory = providerFactory;
    }

    public async Task<VerifySingleAddressResult> Handle(
        VerifySingleAddressCommand cmd,
        CancellationToken ct)
    {
        // Factory pattern — see ITrafficProviderFactory
        ITrafficProvider provider = _providerFactory.GetProvider(cmd.Provider);

        string? coords = await provider.GeocodeAsync(cmd.Address, ct);

        return coords is null
            ? new VerifySingleAddressResult(false, null, "GEOCODE_FAILED")
            : new VerifySingleAddressResult(true, coords, null);
    }
}
