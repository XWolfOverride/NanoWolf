using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace XWolf.NanoWolf;

/// <summary>Infrastructure configuration of the MongoDB connection. The connection string is a secret: keep it out of the repository.</summary>
public sealed class MongoOptions
{
    /// <summary>Connection string (for example from user secrets or an environment variable).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Database used by the application. Required: there is no default, so a mistake cannot touch another database.</summary>
    public string Database { get; set; } = "";

    public int ServerSelectionTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Added to the name of every collection. Empty in the application. The tests use a "nwtest_..." prefix so they run
    /// inside a development database and only ever touch (and drop) their own collections.
    /// </summary>
    public string CollectionPrefix { get; set; } = "";
}

/// <summary>The MongoDB client and database of the application, with the mapping of the NanoWolf types.</summary>
public sealed class MongoContext
{
    public const string Users = "users";
    public const string Sessions = "sessions";
    public const string Tokens = "tokens";
    public const string Throttle = "throttle";
    public const string Settings = "settings";
    public const string Log = "log";

    static MongoContext()
    {
        // Dates are stored as BSON dates (UTC, millisecond precision), not as the default array of ticks and offset.
        BsonSerializer.TryRegisterSerializer(new DateTimeOffsetSerializer(BsonType.DateTime));

        ConventionRegistry.Register("NanoWolf", new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new EnumRepresentationConvention(BsonType.String),
            new IgnoreExtraElementsConvention(true),
        }, type => type.Namespace == "XWolf.NanoWolf");

        // Types with a natural key other than "Id".
        if (!BsonClassMap.IsClassMapRegistered(typeof(ThrottleRecord)))
            BsonClassMap.RegisterClassMap<ThrottleRecord>(map =>
            {
                map.AutoMap();
                map.MapIdMember(r => r.Key);
            });
    }

    public MongoContext(MongoOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("Mongo:ConnectionString is not configured (use user secrets or an environment variable).");
        if (string.IsNullOrWhiteSpace(options.Database))
            throw new InvalidOperationException("Mongo:Database is not configured.");

        var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
        settings.ServerApi = new ServerApi(ServerApiVersion.V1);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(options.ServerSelectionTimeoutSeconds);
        Client = new MongoClient(settings);
        DatabaseName = options.Database;
        CollectionPrefix = options.CollectionPrefix ?? "";
        Database = Client.GetDatabase(options.Database);
    }

    public MongoClient Client { get; }
    public string DatabaseName { get; }
    public IMongoDatabase Database { get; }

    public string CollectionPrefix { get; }

    public IMongoCollection<T> Collection<T>(string name) => Database.GetCollection<T>(CollectionPrefix + name);

    /// <summary>Prefix of the collections the tests create. Only collections with it can be dropped by <see cref="DropTestCollectionsAsync"/>.</summary>
    public const string TestPrefix = "nwtest_";

    /// <summary>Drops the collections of a test run. Refuses to do anything unless the prefix is a test prefix.</summary>
    public async Task DropTestCollectionsAsync()
    {
        if (!CollectionPrefix.StartsWith(TestPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to drop collections that do not have the test prefix.");
        foreach (var name in await (await Database.ListCollectionNamesAsync()).ToListAsync())
            if (name.StartsWith(CollectionPrefix, StringComparison.Ordinal))
                await Database.DropCollectionAsync(name);
    }

    /// <summary>Creates the indexes the application depends on. Safe to call every time the application starts.</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancel)
    {
        // Unique email: the guarantee that a person cannot be registered twice.
        await Collection<User>(Users).Indexes.CreateOneAsync(new CreateIndexModel<User>(
            Builders<User>.IndexKeys.Ascending(u => u.Email), new CreateIndexOptions { Unique = true, Name = "email_unique" }), cancellationToken: cancel);

        // Expiration handled by the database itself: nothing to clean up by hand.
        await Collection<SessionRecord>(Sessions).Indexes.CreateManyAsync(
        [
            new CreateIndexModel<SessionRecord>(Builders<SessionRecord>.IndexKeys.Ascending(s => s.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "expires" }),
            new CreateIndexModel<SessionRecord>(Builders<SessionRecord>.IndexKeys.Ascending(s => s.UserId), new CreateIndexOptions { Name = "user" }),
        ], cancel);

        await Collection<TokenRecord>(Tokens).Indexes.CreateOneAsync(new CreateIndexModel<TokenRecord>(
            Builders<TokenRecord>.IndexKeys.Ascending(t => t.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "expires" }), cancellationToken: cancel);

        await Collection<ThrottleRecord>(Throttle).Indexes.CreateOneAsync(new CreateIndexModel<ThrottleRecord>(
            Builders<ThrottleRecord>.IndexKeys.Ascending(r => r.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "expires" }), cancellationToken: cancel);

        await Collection<BsonDocument>(Log).Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Descending("time"), new CreateIndexOptions { Name = "time" }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("call").Descending("time"), new CreateIndexOptions { Name = "call_time" }),
        ], cancel);
    }
}
