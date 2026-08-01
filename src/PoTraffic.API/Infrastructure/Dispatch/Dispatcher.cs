using System.Collections.Concurrent;
using System.Reflection;
using FluentValidation;

namespace PoTraffic.API.Infrastructure.Dispatch;

/// <summary>Marker tying a request type to its response type.</summary>
public interface IRequest<TResponse>;

/// <summary>A feature-slice handler for <typeparamref name="TRequest"/>.</summary>
public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Dispatches a request to its single handler, running FluentValidation first.</summary>
public interface ISender
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-house replacement for the MediatR pipeline: resolves all
/// <see cref="IValidator{T}"/> instances for the concrete request type, throws
/// <see cref="ValidationException"/> on failures (mapped to 422 by
/// GlobalExceptionHandler), then invokes the request's handler.
/// </summary>
public sealed class Dispatcher(IServiceProvider services) : ISender
{
    private static readonly ConcurrentDictionary<Type, (Type HandlerType, MethodInfo Handle, Type ValidatorType)> _cache = new();

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        Type requestType = request.GetType();
        (Type handlerType, MethodInfo handle, Type validatorType) = _cache.GetOrAdd(requestType, static (rt) =>
        {
            Type responseType = rt.GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
                .GetGenericArguments()[0];
            Type ht = typeof(IRequestHandler<,>).MakeGenericType(rt, responseType);
            return (ht, ht.GetMethod(nameof(IRequestHandler<,>.Handle))!,
                typeof(IEnumerable<>).MakeGenericType(typeof(IValidator<>).MakeGenericType(rt)));
        });

        var validators = ((IEnumerable<IValidator>)services.GetRequiredService(validatorType)).ToArray();
        if (validators.Length > 0)
        {
            IValidationContext context = new ValidationContext<object>(request);
            FluentValidation.Results.ValidationResult[] results = await Task.WhenAll(
                validators.Select(v => v.ValidateAsync(context, cancellationToken)));
            List<FluentValidation.Results.ValidationFailure> failures =
                [.. results.SelectMany(r => r.Errors).Where(f => f is not null)];
            if (failures.Count > 0)
                throw new ValidationException(failures);
        }

        object handler = services.GetRequiredService(handlerType);
        return await (Task<TResponse>)handle.Invoke(handler, [request, cancellationToken])!;
    }
}

public static class DispatcherServiceExtensions
{
    /// <summary>Registers the dispatcher and every <see cref="IRequestHandler{TRequest,TResponse}"/> in the assembly.</summary>
    public static IServiceCollection AddDispatcher(this IServiceCollection services, Assembly assembly)
    {
        services.AddScoped<ISender, Dispatcher>();
        foreach (Type type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (Type iface in type.GetInterfaces().Where(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            {
                services.AddScoped(iface, type);
            }
        }
        return services;
    }
}
