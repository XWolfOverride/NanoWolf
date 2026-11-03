using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace XWolf.NanoWolf.Tests;

/// <summary>The same contract of the stores, on MongoDB.</summary>
public sealed class MongoStoreContractTests : StoreContractTests
{
    protected override async Task<StoreSet> CreateAsync()
    {
        var run = await MongoTestRun.StartAsync();
        var services = run.Services;
        return new StoreSet(
            services.GetRequiredService<IUserStore>(),
            services.GetRequiredService<ISessionStore>(),
            services.GetRequiredService<ITokenStore>(),
            services.GetRequiredService<IThrottleStore>(),
            services.GetRequiredService<ISettingsStore>(),
            async () => await run.DisposeAsync());
    }

    [SkippableFact]
    public async Task The_indexes_that_protect_the_data_exist()
    {
        await using var run = await MongoTestRun.StartAsync();
        async Task<List<BsonDocument>> Indexes(string collection)
            => await (await run.Mongo.Collection<BsonDocument>(collection).Indexes.ListAsync()).ToListAsync();

        var users = await Indexes(MongoContext.Users);
        var sessions = await Indexes(MongoContext.Sessions);

        var email = users.Single(i => i["name"] == "email_unique");
        Assert.True(email["unique"].AsBoolean);
        Assert.Equal("email", email["key"].AsBsonDocument.Names.Single());
        Assert.Equal(0, sessions.Single(i => i["name"] == "expires")["expireAfterSeconds"].ToInt32());
        Assert.Contains(await Indexes(MongoContext.Tokens), i => i["name"] == "expires");
        Assert.Contains(await Indexes(MongoContext.Throttle), i => i["name"] == "expires");
    }

    [SkippableFact]
    public async Task The_log_is_written_without_losing_the_details()
    {
        await using var run = await MongoTestRun.StartAsync();
        var log = run.Services.GetRequiredService<IActionLog>();

        await log.WriteAsync(new ActionLogEntry(T0, "u1", "items.edit", false, "forbidden", new Dictionary<string, string> { ["item"] = "7" }), default);
        await log.WriteAsync(new ActionLogEntry(T0, null, "auth.login", true, null, new Dictionary<string, string>()), default);

        var documents = await run.Mongo.Collection<BsonDocument>(MongoContext.Log).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.Equal(2, documents.Count);
        var edit = documents.Single(d => d["call"] == "items.edit");
        Assert.Equal("u1", edit["userId"].AsString);
        Assert.False(edit["ok"].AsBoolean);
        Assert.Equal("forbidden", edit["errorCode"].AsString);
        Assert.Equal("7", edit["details"]["item"].AsString);
        Assert.Equal(T0.UtcDateTime, edit["time"].ToUniversalTime());
        Assert.True(documents.Single(d => d["call"] == "auth.login")["userId"].IsBsonNull);
    }

    [SkippableFact]
    public async Task A_run_only_touches_its_own_collections()
    {
        await using var first = await MongoTestRun.StartAsync();
        await using var second = await MongoTestRun.StartAsync();
        await first.Services.GetRequiredService<ISettingsStore>().SaveAsync("only.in.first", "1", default);

        Assert.Single(await first.Services.GetRequiredService<ISettingsStore>().LoadAsync(default));
        Assert.Empty(await second.Services.GetRequiredService<ISettingsStore>().LoadAsync(default));
        Assert.NotEqual(first.Mongo.CollectionPrefix, second.Mongo.CollectionPrefix);
        Assert.StartsWith(MongoContext.TestPrefix, first.Mongo.CollectionPrefix);
    }

    [Fact]
    public async Task Collections_without_the_test_prefix_are_never_dropped()
    {
        var mongo = new MongoContext(new MongoOptions { ConnectionString = "mongodb://localhost:1", Database = "anything", CollectionPrefix = "" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => mongo.DropTestCollectionsAsync());
    }
}
