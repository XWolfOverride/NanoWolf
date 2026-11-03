using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace XWolf.NanoWolf.Testing;

/// <summary>
/// A run of tests against MongoDB. Needs two environment variables, otherwise the tests are skipped:
/// NANOWOLF_MONGO (connection string) and NANOWOLF_MONGO_DB (a development or test database, its name has to say so).
/// Everything is created in collections with a "nwtest_..." prefix, so a run never touches other data, and only those
/// collections are dropped when it ends. Works with a user that can only use that one database.
/// </summary>
public sealed class MongoTestRun : IAsyncDisposable
{
    public const string ConnectionVariable = "NANOWOLF_MONGO";
    public const string DatabaseVariable = "NANOWOLF_MONGO_DB";

    private MongoTestRun(ServiceProvider services)
    {
        Services = services;
        Mongo = services.GetRequiredService<MongoContext>();
    }

    public ServiceProvider Services { get; }
    public MongoContext Mongo { get; }

    /// <summary>Starts a run. <paramref name="configure"/> adds the stores of the application, if it has any.</summary>
    public static async Task<MongoTestRun> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        var database = Environment.GetEnvironmentVariable(DatabaseVariable);
        Skip.If(string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(database),
            $"Set {ConnectionVariable} and {DatabaseVariable} to run the MongoDB tests.");

        if (!database!.Contains("dev", StringComparison.OrdinalIgnoreCase) && !database.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{DatabaseVariable} must name a development or test database (its name has to contain \"dev\" or \"test\"), not '{database}'.");

        var services = new ServiceCollection();
        services.AddNanoWolfMongo(o =>
        {
            o.ConnectionString = connection!;
            o.Database = database;
            o.CollectionPrefix = MongoContext.TestPrefix + Guid.NewGuid().ToString("N")[..10] + "_";
        });
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var run = new MongoTestRun(provider);
        try
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
                await hosted.StartAsync(default); // creates the indexes
        }
        catch
        {
            await run.DisposeAsync();
            throw;
        }
        return run;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Mongo.DropTestCollectionsAsync();
        }
        finally
        {
            await Services.DisposeAsync();
        }
    }
}

