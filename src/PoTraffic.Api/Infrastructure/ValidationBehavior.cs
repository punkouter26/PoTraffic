using FluentValidation;
using MediatR;

namespace PoTraffic.Api.Infrastructure;

/// <summary>
/// MediatR pipeline behaviour — invokes all registered FluentValidation
/// validators for the incoming request BEFORE the handler executes.
/// Throws <see cref="ValidationException"/> when one or more rules fail.
/// <para>
/// Pipeline Behavior pattern — cross-cutting validation concern is decoupled
/// from individual handlers and applied uniformly to every MediatR request.
/// </para>
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);

        FluentValidation.Results.ValidationResult[] results = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        List<FluentValidation.Results.ValidationFailure> failures = results
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count > 0)
            throw new ValidationException(failures);

        return await next();
    }
}
