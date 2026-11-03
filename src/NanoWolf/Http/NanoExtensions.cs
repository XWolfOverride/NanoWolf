using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace XWolf.NanoWolf;

public static class NanoWolfExtensions
{
    /// <summary>
    /// Discovers the published calls in the given assemblies, plus the ones of NanoWolf itself (authentication,
    /// administration, settings). Throws <see cref="CatalogException"/> if the catalog is invalid, so the
    /// application does not start.
    /// </summary>
    public static IServiceCollection AddNanoWolfCalls(this IServiceCollection services, IEnumerable<Assembly> assemblies, Action<NanoWolfOptions>? configure = null)
        => services.AddNanoWolfCalls(CallCatalog.FromAssemblies([.. assemblies, typeof(NanoWolfOptions).Assembly]), configure);

    /// <summary>Registers an already built catalog (for example, a selection of types).</summary>
    public static IServiceCollection AddNanoWolfCalls(this IServiceCollection services, CallCatalog catalog, Action<NanoWolfOptions>? configure = null)
    {
        services.AddNanoWolfOptions(configure);
        services.AddSingleton(catalog);
        services.AddSingleton<CallHandler>();
        return services;
    }

    /// <summary>Registers the infrastructure options. Can be called several times: every configure action is applied.</summary>
    public static IServiceCollection AddNanoWolfOptions(this IServiceCollection services, Action<NanoWolfOptions>? configure = null)
    {
        var options = services.AddOptions<NanoWolfOptions>();
        if (configure != null)
            options.Configure(configure);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<NanoWolfOptions>>().Value);
        return services;
    }

    /// <summary>
    /// Maps GET {prefix}/catalog and {prefix}/{group}/{method}. <see cref="IUserResolver"/> and <see cref="IActionLog"/>
    /// must be registered: they are requested here so a missing one fails at startup.
    /// </summary>
    public static IEndpointRouteBuilder MapNanoWolfCalls(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<NanoWolfOptions>();
        var handler = app.ServiceProvider.GetRequiredService<CallHandler>();
        var prefix = options.Prefix.TrimEnd('/');

        app.MapGet(prefix + "/catalog", handler.CatalogAsync);
        app.MapMethods(prefix + "/{group}/{method}", ["GET", "POST"], handler.CallAsync);
        return app;
    }
}
