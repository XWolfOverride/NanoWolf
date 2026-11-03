using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace XWolf.NanoWolf;

public static class NanoWolfMongoExtensions
{
    /// <summary>
    /// Registers the MongoDB stores of NanoWolf and the action log. Call it <b>before</b> <c>AddNanoWolfAuth</c>:
    /// the indexes are created when the application starts, before the settings are read.
    /// </summary>
    public static IServiceCollection AddNanoWolfMongo(this IServiceCollection services, Action<MongoOptions> configure)
    {
        var options = new MongoOptions();
        configure(options);

        services.AddSingleton(new MongoContext(options));
        services.AddSingleton<IUserStore, MongoUserStore>();
        services.AddSingleton<ISessionStore, MongoSessionStore>();
        services.AddSingleton<ITokenStore, MongoTokenStore>();
        services.AddSingleton<IThrottleStore, MongoThrottleStore>();
        services.AddSingleton<ISettingsStore, MongoSettingsStore>();
        services.AddSingleton<IActionLog, MongoActionLog>();
        services.AddHostedService<MongoSetup>();
        return services;
    }

    /// <summary>Creates the indexes now. Needed by programs that run a console command and exit without starting the host.</summary>
    public static Task EnsureNanoWolfIndexesAsync(this IServiceProvider services, CancellationToken cancel = default)
        => services.GetRequiredService<MongoContext>().EnsureIndexesAsync(cancel);
}

internal sealed class MongoSetup(MongoContext mongo) : IHostedService
{
    public Task StartAsync(CancellationToken cancel) => mongo.EnsureIndexesAsync(cancel);
    public Task StopAsync(CancellationToken cancel) => Task.CompletedTask;
}
