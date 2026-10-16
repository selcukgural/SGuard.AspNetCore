using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SGuard;
using SGuard.AspNetCore;

// In this namespace so that AddSGuardProblemDetails shows up next to AddProblemDetails without a using directive.
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers <see cref="SGuardExceptionHandler"/>.
/// </summary>
public static class SGuardServiceCollectionExtensions
{
    /// <summary>
    /// Returns guard failures as validation problem details responses: registers the problem details services and
    /// <see cref="SGuardExceptionHandler"/>. Call <c>app.UseExceptionHandler()</c> to enable the handler.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the handler, for example its status code.</param>
    /// <returns>The service collection, to chain calls.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddSGuardProblemDetails();
    ///
    /// var app = builder.Build();
    /// app.UseExceptionHandler();
    /// </code>
    /// </example>
    public static IServiceCollection AddSGuardProblemDetails(this IServiceCollection services,
                                                             Action<SGuardProblemDetailsOptions>? configure = null)
    {
        ThrowIf.Null(services);

        services.AddProblemDetails();

        // Not AddExceptionHandler, which adds the handler again on every call.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionHandler, SGuardExceptionHandler>());

        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services;
    }
}
