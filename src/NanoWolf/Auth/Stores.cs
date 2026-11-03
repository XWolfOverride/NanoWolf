namespace XWolf.NanoWolf;

/// <summary>A unique value (for example an email) already exists.</summary>
public sealed class DuplicateException(string message) : Exception(message);

// Storage contracts. Implementations: NanoWolf.Mongo (MongoDB) and NanoWolf.Testing (in memory, for tests).

public interface IUserStore
{
    Task<User?> FindByIdAsync(string id, CancellationToken cancel);
    Task<User?> FindByEmailAsync(string email, CancellationToken cancel);
    Task<IReadOnlyList<User>> ListAsync(CancellationToken cancel);

    /// <summary>Throws <see cref="DuplicateException"/> if the email is taken.</summary>
    Task CreateAsync(User user, CancellationToken cancel);

    /// <summary>Saves every field of the user except the preferences (see <see cref="SetPreferenceAsync"/>).</summary>
    Task UpdateAsync(User user, CancellationToken cancel);

    /// <summary>Sets one preference without touching anything else of the user. Does nothing if the user does not exist.</summary>
    Task SetPreferenceAsync(string userId, string key, string value, CancellationToken cancel);
}

public interface ISessionStore
{
    Task CreateAsync(SessionRecord session, CancellationToken cancel);
    Task<SessionRecord?> FindAsync(string id, CancellationToken cancel);
    Task UpdateAsync(SessionRecord session, CancellationToken cancel);

    /// <summary>Marks every active session of the user as replaced and makes them expire at <paramref name="expiresAt"/>.</summary>
    Task MarkReplacedAsync(string userId, DateTimeOffset when, DateTimeOffset expiresAt, CancellationToken cancel);

    Task DeleteAsync(string id, CancellationToken cancel);
    Task DeleteForUserAsync(string userId, CancellationToken cancel);
}

public interface ITokenStore
{
    Task CreateAsync(TokenRecord token, CancellationToken cancel);
    Task<TokenRecord?> FindAsync(string id, CancellationToken cancel);

    /// <summary>
    /// Atomically marks the token as used and returns it, only if it exists, has the given kind, was not used and
    /// has not expired. Returns null otherwise.
    /// </summary>
    Task<TokenRecord?> ConsumeAsync(string id, TokenKind kind, DateTimeOffset now, CancellationToken cancel);
}

public interface IThrottleStore
{
    Task<ThrottleRecord?> GetAsync(string key, CancellationToken cancel);
    Task SaveAsync(ThrottleRecord record, CancellationToken cancel);
    Task DeleteAsync(string key, CancellationToken cancel);
}

public interface ISettingsStore
{
    /// <summary>Stored settings: key and its value as a JSON literal.</summary>
    Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancel);

    Task SaveAsync(string key, string json, CancellationToken cancel);
}
