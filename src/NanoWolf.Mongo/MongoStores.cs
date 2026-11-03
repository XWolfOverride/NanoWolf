using MongoDB.Bson;
using MongoDB.Driver;

namespace XWolf.NanoWolf;

internal sealed class MongoUserStore(MongoContext mongo) : IUserStore
{
    private IMongoCollection<User> Users => mongo.Collection<User>(MongoContext.Users);

    public async Task<User?> FindByIdAsync(string id, CancellationToken cancel)
        => await Users.Find(u => u.Id == id).FirstOrDefaultAsync(cancel);

    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancel)
        => await Users.Find(u => u.Email == email).FirstOrDefaultAsync(cancel);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancel)
        => await Users.Find(FilterDefinition<User>.Empty).ToListAsync(cancel);

    public async Task CreateAsync(User user, CancellationToken cancel)
    {
        try
        {
            await Users.InsertOneAsync(user, cancellationToken: cancel);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateException(user.Email);
        }
    }

    public async Task UpdateAsync(User user, CancellationToken cancel)
    {
        // Every field except the preferences, which have their own operation: a user loaded a moment ago and saved now
        // (a password rehash, a disabled account...) must not undo a preference chosen in between.
        var result = await Users.UpdateOneAsync(
            u => u.Id == user.Id,
            Builders<User>.Update
                .Set(u => u.Email, user.Email).Set(u => u.DisplayName, user.DisplayName).Set(u => u.IsAdmin, user.IsAdmin)
                .Set(u => u.Disabled, user.Disabled).Set(u => u.Identities, user.Identities).Set(u => u.CreatedAt, user.CreatedAt),
            cancellationToken: cancel);
        if (result.MatchedCount == 0)
            throw new InvalidOperationException("Unknown user");
    }

    public Task SetPreferenceAsync(string userId, string key, string value, CancellationToken cancel)
        => Users.UpdateOneAsync(u => u.Id == userId, Builders<User>.Update.Set("preferences." + key, value), cancellationToken: cancel);
}

internal sealed class MongoSessionStore(MongoContext mongo) : ISessionStore
{
    private IMongoCollection<SessionRecord> Sessions => mongo.Collection<SessionRecord>(MongoContext.Sessions);

    public Task CreateAsync(SessionRecord session, CancellationToken cancel)
        => Sessions.InsertOneAsync(session, cancellationToken: cancel);

    public async Task<SessionRecord?> FindAsync(string id, CancellationToken cancel)
        => await Sessions.Find(s => s.Id == id).FirstOrDefaultAsync(cancel);

    public Task UpdateAsync(SessionRecord session, CancellationToken cancel)
        => Sessions.ReplaceOneAsync(s => s.Id == session.Id, session, cancellationToken: cancel);

    public Task MarkReplacedAsync(string userId, DateTimeOffset when, DateTimeOffset expiresAt, CancellationToken cancel)
        => Sessions.UpdateManyAsync(
            s => s.UserId == userId && s.ReplacedAt == null,
            Builders<SessionRecord>.Update.Set(s => s.ReplacedAt, when).Set(s => s.ExpiresAt, expiresAt),
            cancellationToken: cancel);

    public Task DeleteAsync(string id, CancellationToken cancel)
        => Sessions.DeleteOneAsync(s => s.Id == id, cancel);

    public Task DeleteForUserAsync(string userId, CancellationToken cancel)
        => Sessions.DeleteManyAsync(s => s.UserId == userId, cancel);
}

internal sealed class MongoTokenStore(MongoContext mongo) : ITokenStore
{
    private IMongoCollection<TokenRecord> Tokens => mongo.Collection<TokenRecord>(MongoContext.Tokens);

    public Task CreateAsync(TokenRecord token, CancellationToken cancel)
        => Tokens.InsertOneAsync(token, cancellationToken: cancel);

    public async Task<TokenRecord?> FindAsync(string id, CancellationToken cancel)
        => await Tokens.Find(t => t.Id == id).FirstOrDefaultAsync(cancel);

    /// <summary>One atomic operation in the database: of several simultaneous attempts, exactly one wins.</summary>
    public async Task<TokenRecord?> ConsumeAsync(string id, TokenKind kind, DateTimeOffset now, CancellationToken cancel)
    {
        var filter = Builders<TokenRecord>.Filter.And(
            Builders<TokenRecord>.Filter.Eq(t => t.Id, id),
            Builders<TokenRecord>.Filter.Eq(t => t.Kind, kind),
            Builders<TokenRecord>.Filter.Eq(t => t.UsedAt, null),
            Builders<TokenRecord>.Filter.Gt(t => t.ExpiresAt, now));
        return await Tokens.FindOneAndUpdateAsync(
            filter,
            Builders<TokenRecord>.Update.Set(t => t.UsedAt, now),
            new FindOneAndUpdateOptions<TokenRecord> { ReturnDocument = ReturnDocument.After },
            cancel);
    }
}

internal sealed class MongoThrottleStore(MongoContext mongo) : IThrottleStore
{
    private IMongoCollection<ThrottleRecord> Records => mongo.Collection<ThrottleRecord>(MongoContext.Throttle);

    public async Task<ThrottleRecord?> GetAsync(string key, CancellationToken cancel)
        => await Records.Find(r => r.Key == key).FirstOrDefaultAsync(cancel);

    public Task SaveAsync(ThrottleRecord record, CancellationToken cancel)
        => Records.ReplaceOneAsync(r => r.Key == record.Key, record, new ReplaceOptions { IsUpsert = true }, cancel);

    public Task DeleteAsync(string key, CancellationToken cancel)
        => Records.DeleteOneAsync(r => r.Key == key, cancel);
}

internal sealed class MongoSettingsStore(MongoContext mongo) : ISettingsStore
{
    private IMongoCollection<BsonDocument> Settings => mongo.Collection<BsonDocument>(MongoContext.Settings);

    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancel)
    {
        var documents = await Settings.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(cancel);
        return documents.ToDictionary(d => d["_id"].AsString, d => d["value"].AsString);
    }

    public Task SaveAsync(string key, string json, CancellationToken cancel)
        => Settings.ReplaceOneAsync(
            new BsonDocument("_id", key),
            new BsonDocument { ["_id"] = key, ["value"] = json },
            new ReplaceOptions { IsUpsert = true },
            cancel);
}

/// <summary>Writes the automatic action log to the "log" collection.</summary>
internal sealed class MongoActionLog(MongoContext mongo) : IActionLog
{
    public Task WriteAsync(ActionLogEntry entry, CancellationToken cancel)
    {
        var details = new BsonDocument();
        foreach (var (key, value) in entry.Details)
            details[key] = value;

        var document = new BsonDocument
        {
            ["time"] = entry.Time.UtcDateTime,
            ["userId"] = entry.UserId is null ? BsonNull.Value : entry.UserId,
            ["call"] = entry.Call,
            ["ok"] = entry.Ok,
            ["errorCode"] = entry.ErrorCode is null ? BsonNull.Value : entry.ErrorCode,
            ["details"] = details,
        };
        return mongo.Collection<BsonDocument>(MongoContext.Log).InsertOneAsync(document, cancellationToken: cancel);
    }
}
