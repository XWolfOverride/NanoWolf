using System.Text.Json;

namespace XWolf.NanoWolf.Testing;

// In-memory stores. They exist only for the tests: the real ones use MongoDB. Like a real database, they hand out
// copies, so changing an object does not change the stored one until it is saved.

internal static class Copy
{
    public static T Of<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}

public sealed class MemoryUserStore : IUserStore
{
    private readonly object gate = new();
    private readonly List<User> users = [];

    public Task<User?> FindByIdAsync(string id, CancellationToken cancel)
    {
        lock (gate)
            return Task.FromResult(users.Where(u => u.Id == id).Select(Copy.Of).FirstOrDefault());
    }

    public Task<User?> FindByEmailAsync(string email, CancellationToken cancel)
    {
        lock (gate)
            return Task.FromResult(users.Where(u => u.Email == email).Select(Copy.Of).FirstOrDefault());
    }

    public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancel)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<User>>([.. users.Select(Copy.Of)]);
    }

    public Task CreateAsync(User user, CancellationToken cancel)
    {
        lock (gate)
        {
            if (users.Any(u => u.Email == user.Email))
                throw new DuplicateException(user.Email);
            users.Add(Copy.Of(user));
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User user, CancellationToken cancel)
    {
        lock (gate)
        {
            var index = users.FindIndex(u => u.Id == user.Id);
            if (index < 0)
                throw new InvalidOperationException("Unknown user");
            var preferences = users[index].Preferences; // not part of an update: see SetPreferenceAsync
            users[index] = Copy.Of(user);
            users[index].Preferences = preferences;
        }
        return Task.CompletedTask;
    }

    public Task SetPreferenceAsync(string userId, string key, string value, CancellationToken cancel)
    {
        lock (gate)
            users.FirstOrDefault(u => u.Id == userId)?.Preferences[key] = value;
        return Task.CompletedTask;
    }
}

public sealed class MemorySessionStore : ISessionStore
{
    private readonly object gate = new();
    private readonly List<SessionRecord> sessions = [];

    public IReadOnlyList<SessionRecord> All
    {
        get
        {
            lock (gate)
                return [.. sessions.Select(Copy.Of)];
        }
    }

    public Task CreateAsync(SessionRecord session, CancellationToken cancel)
    {
        lock (gate)
            sessions.Add(Copy.Of(session));
        return Task.CompletedTask;
    }

    public Task<SessionRecord?> FindAsync(string id, CancellationToken cancel)
    {
        lock (gate)
            return Task.FromResult(sessions.Where(s => s.Id == id).Select(Copy.Of).FirstOrDefault());
    }

    public Task UpdateAsync(SessionRecord session, CancellationToken cancel)
    {
        lock (gate)
        {
            var index = sessions.FindIndex(s => s.Id == session.Id);
            if (index >= 0)
                sessions[index] = Copy.Of(session);
        }
        return Task.CompletedTask;
    }

    public Task MarkReplacedAsync(string userId, DateTimeOffset when, DateTimeOffset expiresAt, CancellationToken cancel)
    {
        lock (gate)
            foreach (var session in sessions.Where(s => s.UserId == userId && s.ReplacedAt == null))
            {
                session.ReplacedAt = when;
                session.ExpiresAt = expiresAt;
            }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken cancel)
    {
        lock (gate)
            sessions.RemoveAll(s => s.Id == id);
        return Task.CompletedTask;
    }

    public Task DeleteForUserAsync(string userId, CancellationToken cancel)
    {
        lock (gate)
            sessions.RemoveAll(s => s.UserId == userId);
        return Task.CompletedTask;
    }
}

public sealed class MemoryTokenStore : ITokenStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, TokenRecord> tokens = [];

    public IReadOnlyList<TokenRecord> All
    {
        get
        {
            lock (gate)
                return [.. tokens.Values.Select(Copy.Of)];
        }
    }

    public Task CreateAsync(TokenRecord token, CancellationToken cancel)
    {
        lock (gate)
            tokens[token.Id] = Copy.Of(token);
        return Task.CompletedTask;
    }

    public Task<TokenRecord?> FindAsync(string id, CancellationToken cancel)
    {
        lock (gate)
            return Task.FromResult(tokens.TryGetValue(id, out var token) ? Copy.Of(token) : null);
    }

    public Task<TokenRecord?> ConsumeAsync(string id, TokenKind kind, DateTimeOffset now, CancellationToken cancel)
    {
        lock (gate)
        {
            if (!tokens.TryGetValue(id, out var token) || token.Kind != kind || token.UsedAt != null || token.ExpiresAt <= now)
                return Task.FromResult<TokenRecord?>(null);
            token.UsedAt = now;
            return Task.FromResult<TokenRecord?>(Copy.Of(token));
        }
    }
}

public sealed class MemoryThrottleStore : IThrottleStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, ThrottleRecord> records = [];

    public Task<ThrottleRecord?> GetAsync(string key, CancellationToken cancel)
    {
        lock (gate)
            return Task.FromResult(records.TryGetValue(key, out var record) ? Copy.Of(record) : null);
    }

    public Task SaveAsync(ThrottleRecord record, CancellationToken cancel)
    {
        lock (gate)
            records[record.Key] = Copy.Of(record);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancel)
    {
        lock (gate)
            records.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class MemorySettingsStore : ISettingsStore
{
    private readonly object gate = new();

    public Dictionary<string, string> Values { get; } = [];

    public Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancel)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Values));
    }

    public Task SaveAsync(string key, string json, CancellationToken cancel)
    {
        lock (gate)
            Values[key] = json;
        return Task.CompletedTask;
    }
}
