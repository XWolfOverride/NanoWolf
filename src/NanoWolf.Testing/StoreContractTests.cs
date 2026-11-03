using Microsoft.Extensions.DependencyInjection;

namespace XWolf.NanoWolf.Testing;

public sealed class StoreSet(IUserStore users, ISessionStore sessions, ITokenStore tokens, IThrottleStore throttle, ISettingsStore settings, Func<Task> dispose) : IAsyncDisposable
{
    public IUserStore Users { get; } = users;
    public ISessionStore Sessions { get; } = sessions;
    public ITokenStore Tokens { get; } = tokens;
    public IThrottleStore Throttle { get; } = throttle;
    public ISettingsStore Settings { get; } = settings;

    public async ValueTask DisposeAsync() => await dispose();
}

/// <summary>
/// What every implementation of the stores must do. The same tests run on the in-memory stores and,
/// when NANOWOLF_MONGO holds a connection string, on MongoDB (in a throwaway database that is dropped afterwards).
/// </summary>
public abstract class StoreContractTests
{
    protected static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    protected abstract Task<StoreSet> CreateAsync();

    private static User NewUser(string email = "alice@example.com", bool admin = false) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Email = email,
        DisplayName = "Alice",
        IsAdmin = admin,
        CreatedAt = T0,
        Identities = [new Identity { Method = "password", Subject = email, Data = "hash-1" }],
    };

    private static SessionRecord NewSession(string userId, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        UserId = userId,
        Method = "password",
        CreatedAt = T0,
        LastSeenAt = T0,
        ExpiresAt = T0.AddDays(30),
        Ip = "10.0.0.1",
        UserAgent = "tests",
    };

    private static TokenRecord NewToken(TokenKind kind = TokenKind.Join, TimeSpan? lifetime = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = kind,
        Email = "bob@example.com",
        IsAdmin = true,
        CreatedBy = "console",
        CreatedAt = T0,
        ExpiresAt = T0 + (lifetime ?? TimeSpan.FromHours(72)),
    };

    // ---- users ----

    [SkippableFact]
    public async Task A_user_can_be_found_by_id_and_by_email_with_everything_it_had()
    {
        await using var stores = await CreateAsync();
        var user = NewUser(admin: true);
        user.Identities.Add(new Identity { Method = "google", Subject = "1234" });
        user.Disabled = true;

        await stores.Users.CreateAsync(user, default);
        var byId = await stores.Users.FindByIdAsync(user.Id, default);
        var byEmail = await stores.Users.FindByEmailAsync("alice@example.com", default);

        Assert.NotNull(byId);
        Assert.Equal(user.Id, byEmail!.Id);
        Assert.Equal("alice@example.com", byId.Email);
        Assert.Equal("Alice", byId.DisplayName);
        Assert.True(byId.IsAdmin);
        Assert.True(byId.Disabled);
        Assert.Equal(T0, byId.CreatedAt);
        Assert.Equal(["password", "google"], byId.Identities.Select(i => i.Method));
        Assert.Equal("hash-1", byId.Identities[0].Data);
        Assert.Null(byId.Identities[1].Data);
    }

    [SkippableFact]
    public async Task Missing_users_are_null()
    {
        await using var stores = await CreateAsync();

        Assert.Null(await stores.Users.FindByIdAsync("nope", default));
        Assert.Null(await stores.Users.FindByEmailAsync("nobody@example.com", default));
    }

    [SkippableFact]
    public async Task An_email_cannot_be_registered_twice()
    {
        await using var stores = await CreateAsync();
        await stores.Users.CreateAsync(NewUser(), default);

        await Assert.ThrowsAsync<DuplicateException>(() => stores.Users.CreateAsync(NewUser(), default));
        Assert.Single(await stores.Users.ListAsync(default));
    }

    [SkippableFact]
    public async Task Updating_a_user_replaces_it()
    {
        await using var stores = await CreateAsync();
        var user = NewUser();
        await stores.Users.CreateAsync(user, default);

        user.DisplayName = "Alicia";
        user.Identities[0].Data = "hash-2";
        await stores.Users.UpdateAsync(user, default);

        var found = (await stores.Users.FindByIdAsync(user.Id, default))!;
        Assert.Equal("Alicia", found.DisplayName);
        Assert.Equal("hash-2", found.Identities[0].Data);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stores.Users.UpdateAsync(NewUser("other@example.com"), default));
    }

    [SkippableFact]
    public async Task Changing_a_returned_user_changes_nothing_until_it_is_saved()
    {
        await using var stores = await CreateAsync();
        var user = NewUser();
        await stores.Users.CreateAsync(user, default);

        var found = (await stores.Users.FindByIdAsync(user.Id, default))!;
        found.IsAdmin = true;

        Assert.False((await stores.Users.FindByIdAsync(user.Id, default))!.IsAdmin);
    }

    [SkippableFact]
    public async Task All_users_can_be_listed()
    {
        await using var stores = await CreateAsync();
        await stores.Users.CreateAsync(NewUser("a@example.com"), default);
        await stores.Users.CreateAsync(NewUser("b@example.com"), default);

        Assert.Equal(["a@example.com", "b@example.com"], (await stores.Users.ListAsync(default)).Select(u => u.Email).Order());
    }

    // ---- preferences ----

    [SkippableFact]
    public async Task Preferences_are_set_one_by_one_and_read_back_with_the_user()
    {
        await using var stores = await CreateAsync();
        var user = NewUser();
        await stores.Users.CreateAsync(user, default);

        await stores.Users.SetPreferenceAsync(user.Id, "accent", "#62b8ff", default);
        await stores.Users.SetPreferenceAsync(user.Id, "listExcerpts", "false", default);
        await stores.Users.SetPreferenceAsync(user.Id, "accent", "#ff7eb6", default); // changes the value
        await stores.Users.SetPreferenceAsync("nobody", "accent", "#000000", default); // no user: nothing happens

        var found = (await stores.Users.FindByIdAsync(user.Id, default))!;
        Assert.Equal(2, found.Preferences.Count);
        Assert.Equal("#ff7eb6", found.Preferences["accent"]);
        Assert.Equal("false", found.Preferences["listExcerpts"]);
        Assert.Null(await stores.Users.FindByIdAsync("nobody", default));
    }

    [SkippableFact]
    public async Task Saving_a_user_loaded_earlier_never_undoes_a_preference_chosen_in_between()
    {
        await using var stores = await CreateAsync();
        var user = NewUser();
        await stores.Users.CreateAsync(user, default);
        var loadedEarlier = (await stores.Users.FindByIdAsync(user.Id, default))!; // no preferences yet

        await stores.Users.SetPreferenceAsync(user.Id, "accent", "#62b8ff", default);
        loadedEarlier.DisplayName = "Changed meanwhile";
        loadedEarlier.Identities[0].Data = "hash-2";
        await stores.Users.UpdateAsync(loadedEarlier, default);

        var found = (await stores.Users.FindByIdAsync(user.Id, default))!;
        Assert.Equal("Changed meanwhile", found.DisplayName);
        Assert.Equal("hash-2", found.Identities[0].Data);
        Assert.Equal("#62b8ff", found.Preferences["accent"]);
    }

    [SkippableFact]
    public async Task Preferences_chosen_at_the_same_time_as_other_changes_are_all_kept()
    {
        await using var stores = await CreateAsync();
        var user = NewUser();
        await stores.Users.CreateAsync(user, default);

        var preferences = Enumerable.Range(0, 10).Select(n => Task.Run(() => stores.Users.SetPreferenceAsync(user.Id, "k" + n, "v" + n, default)));
        var update = Task.Run(async () =>
        {
            var copy = (await stores.Users.FindByIdAsync(user.Id, default))!;
            copy.IsAdmin = true;
            await stores.Users.UpdateAsync(copy, default);
        });
        await Task.WhenAll(preferences.Append(update));

        var found = (await stores.Users.FindByIdAsync(user.Id, default))!;
        Assert.True(found.IsAdmin);
        Assert.Equal(10, found.Preferences.Count);
    }

    // ---- sessions ----

    [SkippableFact]
    public async Task A_session_can_be_stored_found_and_updated()
    {
        await using var stores = await CreateAsync();
        var session = NewSession("u1", "hash-of-token");
        await stores.Sessions.CreateAsync(session, default);

        var found = (await stores.Sessions.FindAsync("hash-of-token", default))!;
        found.LastSeenAt = T0.AddMinutes(10);
        found.ExpiresAt = T0.AddDays(40);
        await stores.Sessions.UpdateAsync(found, default);
        var updated = (await stores.Sessions.FindAsync("hash-of-token", default))!;

        Assert.Equal("u1", found.UserId);
        Assert.Equal("password", found.Method);
        Assert.Equal("10.0.0.1", found.Ip);
        Assert.Null(found.ReplacedAt);
        Assert.Equal(T0.AddMinutes(10), updated.LastSeenAt);
        Assert.Equal(T0.AddDays(40), updated.ExpiresAt);
        Assert.Null(await stores.Sessions.FindAsync("other", default));
    }

    [SkippableFact]
    public async Task Marking_replaced_only_touches_the_active_sessions_of_that_user()
    {
        await using var stores = await CreateAsync();
        await stores.Sessions.CreateAsync(NewSession("u1", "s1"), default);
        await stores.Sessions.CreateAsync(NewSession("u2", "s2"), default);

        await stores.Sessions.MarkReplacedAsync("u1", T0.AddHours(1), T0.AddDays(7), default);
        await stores.Sessions.MarkReplacedAsync("u1", T0.AddHours(2), T0.AddDays(9), default); // already replaced: unchanged

        var s1 = (await stores.Sessions.FindAsync("s1", default))!;
        var s2 = (await stores.Sessions.FindAsync("s2", default))!;
        Assert.Equal(T0.AddHours(1), s1.ReplacedAt);
        Assert.Equal(T0.AddDays(7), s1.ExpiresAt);
        Assert.Null(s2.ReplacedAt);
        Assert.Equal(T0.AddDays(30), s2.ExpiresAt);
    }

    [SkippableFact]
    public async Task Sessions_can_be_deleted_one_by_one_or_by_user()
    {
        await using var stores = await CreateAsync();
        await stores.Sessions.CreateAsync(NewSession("u1", "s1"), default);
        await stores.Sessions.CreateAsync(NewSession("u1", "s2"), default);
        await stores.Sessions.CreateAsync(NewSession("u2", "s3"), default);

        await stores.Sessions.DeleteAsync("s1", default);
        Assert.Null(await stores.Sessions.FindAsync("s1", default));
        Assert.NotNull(await stores.Sessions.FindAsync("s2", default));

        await stores.Sessions.DeleteForUserAsync("u1", default);
        Assert.Null(await stores.Sessions.FindAsync("s2", default));
        Assert.NotNull(await stores.Sessions.FindAsync("s3", default));
    }

    // ---- single use tokens ----

    [SkippableFact]
    public async Task A_token_can_be_found_and_consumed_once()
    {
        await using var stores = await CreateAsync();
        var token = NewToken();
        await stores.Tokens.CreateAsync(token, default);

        var found = (await stores.Tokens.FindAsync(token.Id, default))!;
        var first = await stores.Tokens.ConsumeAsync(token.Id, TokenKind.Join, T0.AddHours(1), default);
        var second = await stores.Tokens.ConsumeAsync(token.Id, TokenKind.Join, T0.AddHours(2), default);

        Assert.Equal(TokenKind.Join, found.Kind);
        Assert.Equal("bob@example.com", found.Email);
        Assert.True(found.IsAdmin);
        Assert.Null(found.UsedAt);
        Assert.Equal(T0.AddHours(1), first!.UsedAt);
        Assert.Null(second);
        Assert.Equal(T0.AddHours(1), (await stores.Tokens.FindAsync(token.Id, default))!.UsedAt);
    }

    [SkippableFact]
    public async Task The_wrong_kind_or_an_expired_token_is_not_consumed()
    {
        await using var stores = await CreateAsync();
        var token = NewToken(TokenKind.Reset, TimeSpan.FromHours(1));
        await stores.Tokens.CreateAsync(token, default);

        Assert.Null(await stores.Tokens.ConsumeAsync(token.Id, TokenKind.Join, T0, default));
        Assert.Null(await stores.Tokens.ConsumeAsync(token.Id, TokenKind.Reset, T0.AddHours(2), default));
        Assert.Null(await stores.Tokens.ConsumeAsync("unknown", TokenKind.Reset, T0, default));
        Assert.NotNull(await stores.Tokens.ConsumeAsync(token.Id, TokenKind.Reset, T0, default)); // still intact
    }

    [SkippableFact]
    public async Task Of_many_simultaneous_attempts_exactly_one_consumes_the_token()
    {
        await using var stores = await CreateAsync();
        var token = NewToken();
        await stores.Tokens.CreateAsync(token, default);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => stores.Tokens.ConsumeAsync(token.Id, TokenKind.Join, T0, default))));

        Assert.Equal(1, attempts.Count(a => a != null));
    }

    // ---- throttle and settings ----

    [SkippableFact]
    public async Task Failed_attempts_are_stored_replaced_and_deleted()
    {
        await using var stores = await CreateAsync();
        Assert.Null(await stores.Throttle.GetAsync("email:a@example.com", default));

        var record = new ThrottleRecord { Key = "email:a@example.com", Failures = 3, LastFailureAt = T0, ExpiresAt = T0.AddHours(1) };
        await stores.Throttle.SaveAsync(record, default);
        var first = (await stores.Throttle.GetAsync(record.Key, default))!;
        record.Failures = 5;
        record.LockedUntil = T0.AddSeconds(30);
        await stores.Throttle.SaveAsync(record, default);
        var second = (await stores.Throttle.GetAsync(record.Key, default))!;
        await stores.Throttle.DeleteAsync(record.Key, default);

        Assert.Equal(3, first.Failures);
        Assert.Null(first.LockedUntil);
        Assert.Equal(5, second.Failures);
        Assert.Equal(T0.AddSeconds(30), second.LockedUntil);
        Assert.Null(await stores.Throttle.GetAsync(record.Key, default));
    }

    [SkippableFact]
    public async Task Settings_are_stored_as_json_and_overwritten()
    {
        await using var stores = await CreateAsync();
        Assert.Empty(await stores.Settings.LoadAsync(default));

        await stores.Settings.SaveAsync("auth.password.minLength", "12", default);
        await stores.Settings.SaveAsync("auth.password.enabled", "true", default);
        await stores.Settings.SaveAsync("auth.password.minLength", "14", default);

        var loaded = await stores.Settings.LoadAsync(default);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("14", loaded["auth.password.minLength"]);
        Assert.Equal("true", loaded["auth.password.enabled"]);
    }
}
