using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace XWolf.NanoWolf;

/// <summary>Random secrets that are given to people and stored only as a hash.</summary>
internal static class Secrets
{
    /// <summary>256 random bits as URL safe text.</summary>
    public static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// What is stored instead of the token. The token is random and long, so a fast hash is enough:
    /// there is nothing to guess and nothing to brute force.
    /// </summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

internal static class EmailAddress
{
    /// <summary>Trimmed, lower case email, or null if it does not look like one. No deliverability check: no mail is sent.</summary>
    public static string? Normalize(string? email)
    {
        if (email == null)
            return null;
        var value = email.Trim().ToLowerInvariant();
        if (value.Length is 0 or > 254 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return null;
        var at = value.IndexOf('@');
        if (at < 1 || at != value.LastIndexOf('@') || at > 64)
            return null;
        var domain = value[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') ? value : null;
    }
}

/// <summary>Password policy and hashing (PBKDF2 through the platform hasher).</summary>
public sealed class PasswordService
{
    private readonly SettingsService settings;
    private readonly PasswordHasher<User> hasher;
    private readonly string dummyHash;

    public PasswordService(SettingsService settings, NanoWolfOptions options)
    {
        this.settings = settings;
        hasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = options.PasswordIterations }));
        dummyHash = Hash("a password nobody uses " + Guid.NewGuid());
    }

    /// <summary>NFKC, so the same password typed on different keyboards or systems matches. Spaces are kept.</summary>
    public static string Normalize(string password) => password.Normalize(NormalizationForm.FormKC);

    /// <summary>Returns the reason why the password is not acceptable, or null.</summary>
    public string? Validate(string password, string email)
    {
        var normalized = Normalize(password);
        var min = settings.Get(AuthSettings.PasswordMinLength);
        var max = settings.Get(AuthSettings.PasswordMaxLength);
        if (normalized.Length < min)
            return $"The password must have at least {min} characters.";
        if (normalized.Length > max)
            return $"The password must have at most {max} characters.";
        if (string.Equals(normalized, email, StringComparison.OrdinalIgnoreCase))
            return "The password cannot be the email.";
        return null;
    }

    public string Hash(string password) => hasher.HashPassword(new User(), Normalize(password));

    /// <summary>Checks a password. <paramref name="needsRehash"/> is true if the hash uses outdated parameters.</summary>
    public bool Verify(string hash, string password, out bool needsRehash)
    {
        var result = hasher.VerifyHashedPassword(new User(), hash, Normalize(password));
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }

    /// <summary>Spends the same time as a real check, for emails that do not exist.</summary>
    public void SpendTime(string password) => hasher.VerifyHashedPassword(new User(), dummyHash, Normalize(password));
}

/// <summary>Temporary lock after repeated failures, by email and by address.</summary>
public sealed class ThrottleService(IThrottleStore store, SettingsService settings, NanoWolfOptions options)
{
    private static readonly TimeSpan FirstLock = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Forget = TimeSpan.FromHours(1);

    public static string EmailKey(string normalizedEmail) => "email:" + normalizedEmail;
    public static string IpKey(string? ip) => "ip:" + (ip ?? "unknown");

    /// <summary>How long the caller must wait, or null if no key is locked.</summary>
    public async Task<TimeSpan?> LockedForAsync(CancellationToken cancel, params string[] keys)
    {
        var now = options.TimeProvider.GetUtcNow();
        TimeSpan? longest = null;
        foreach (var key in keys)
        {
            var record = await store.GetAsync(key, cancel);
            if (record?.LockedUntil > now)
            {
                var wait = record.LockedUntil.Value - now;
                if (longest == null || wait > longest)
                    longest = wait;
            }
        }
        return longest;
    }

    public async Task FailAsync(string key, int threshold, CancellationToken cancel)
    {
        var now = options.TimeProvider.GetUtcNow();
        var record = await store.GetAsync(key, cancel) ?? new ThrottleRecord { Key = key };
        if (now - record.LastFailureAt > Forget)
            record.Failures = 0;
        record.Failures++;
        record.LastFailureAt = now;
        if (record.Failures >= threshold)
        {
            var factor = Math.Pow(2, Math.Min(record.Failures - threshold, 10));
            var wait = TimeSpan.FromTicks((long)(FirstLock.Ticks * factor));
            record.LockedUntil = now + (wait > MaxLock ? MaxLock : wait);
        }
        record.ExpiresAt = (record.LockedUntil ?? now) > now + Forget ? record.LockedUntil!.Value : now + Forget;
        await store.SaveAsync(record, cancel);
    }

    public Task FailEmailAsync(string normalizedEmail, CancellationToken cancel)
        => FailAsync(EmailKey(normalizedEmail), settings.Get(AuthSettings.LockoutThreshold), cancel);

    public Task FailIpAsync(string? ip, CancellationToken cancel)
        => FailAsync(IpKey(ip), settings.Get(AuthSettings.LockoutIpThreshold), cancel);

    public Task ResetAsync(string key, CancellationToken cancel) => store.DeleteAsync(key, cancel);
}

/// <summary>Single use links: invitations, password resets and recovery codes.</summary>
public sealed class TokenService(ITokenStore store, NanoWolfOptions options)
{
    /// <summary>Creates a token and returns the secret (only now: afterwards just its hash exists).</summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateAsync(
        TokenKind kind, string createdBy, TimeSpan lifetime, string? email = null, string? userId = null, bool isAdmin = false, CancellationToken cancel = default)
    {
        var now = options.TimeProvider.GetUtcNow();
        var token = Secrets.NewToken();
        var record = new TokenRecord
        {
            Id = Secrets.Hash(token),
            Kind = kind,
            Email = email,
            UserId = userId,
            IsAdmin = isAdmin,
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
        await store.CreateAsync(record, cancel);
        return (token, record.ExpiresAt);
    }

    /// <summary>The token if it is valid (right kind, not used, not expired), without using it.</summary>
    public async Task<TokenRecord?> PeekAsync(string token, CancellationToken cancel, params TokenKind[] kinds)
    {
        var record = await store.FindAsync(Secrets.Hash(token), cancel);
        var now = options.TimeProvider.GetUtcNow();
        return record != null && record.UsedAt == null && record.ExpiresAt > now && kinds.Contains(record.Kind) ? record : null;
    }

    public Task<TokenRecord?> ConsumeAsync(string token, TokenKind kind, CancellationToken cancel)
        => store.ConsumeAsync(Secrets.Hash(token), kind, options.TimeProvider.GetUtcNow(), cancel);
}

/// <summary>Sessions stored in the database. The browser holds a random token; only its hash is stored.</summary>
public sealed class SessionService(ISessionStore sessions, IUserStore users, SettingsService settings, NanoWolfOptions options)
{
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReplacedKept = TimeSpan.FromDays(7);

    /// <summary>Opens a session for the user and sets the cookie. Closes the previous one if there is a single session per user.</summary>
    public async Task StartAsync(User user, string method, Ctx ctx)
    {
        var now = options.TimeProvider.GetUtcNow();
        if (settings.Get(AuthSettings.SingleSession))
            await sessions.MarkReplacedAsync(user.Id, now, now + ReplacedKept, ctx.Cancel);

        var token = Secrets.NewToken();
        var record = new SessionRecord
        {
            Id = Secrets.Hash(token),
            UserId = user.Id,
            Method = method,
            CreatedAt = now,
            LastSeenAt = now,
            Ip = ctx.Ip,
            UserAgent = ctx.UserAgent,
        };
        record.ExpiresAt = ExpiryFor(record);
        await sessions.CreateAsync(record, ctx.Cancel);
        SetCookie(ctx.Http, token, record.ExpiresAt);
    }

    /// <summary>Closes the session of this request.</summary>
    public async Task EndAsync(Ctx ctx)
    {
        if (ctx.Http?.Items[AuthItems.SessionKey] is string key)
            await sessions.DeleteAsync(key, ctx.Cancel);
        ClearCookie(ctx.Http);
    }

    public Task EndAllAsync(string userId, CancellationToken cancel) => sessions.DeleteForUserAsync(userId, cancel);

    /// <summary>The user of the request, or null. Explains why not through <see cref="AuthItems.Reason"/>.</summary>
    internal async ValueTask<CtxUser?> ResolveAsync(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(options.CookieName, out var token) || string.IsNullOrEmpty(token))
            return null;

        var cancel = http.RequestAborted;
        var session = await sessions.FindAsync(Secrets.Hash(token), cancel);
        var now = options.TimeProvider.GetUtcNow();
        if (session == null || session.ExpiresAt <= now)
            return null;
        if (session.ReplacedAt != null)
        {
            http.Items[AuthItems.Reason] = "session_replaced";
            return null;
        }

        var user = await users.FindByIdAsync(session.UserId, cancel);
        if (user == null || user.Disabled)
        {
            await sessions.DeleteAsync(session.Id, cancel);
            return null;
        }

        if (now - session.LastSeenAt > TouchEvery)
        {
            session.LastSeenAt = now;
            session.ExpiresAt = ExpiryFor(session);
            await sessions.UpdateAsync(session, cancel);
            SetCookie(http, token, session.ExpiresAt);
        }

        http.Items[AuthItems.SessionKey] = session.Id;
        return new CtxUser(user.Id, user.DisplayName, user.IsAdmin);
    }

    /// <summary>Idle limit renewed on use, never beyond the absolute maximum.</summary>
    private DateTimeOffset ExpiryFor(SessionRecord session)
    {
        var idle = session.LastSeenAt.AddDays(settings.Get(AuthSettings.SessionIdleDays));
        var max = session.CreatedAt.AddDays(settings.Get(AuthSettings.SessionMaxDays));
        return idle < max ? idle : max;
    }

    private void SetCookie(HttpContext? http, string token, DateTimeOffset expires)
        => http?.Response.Cookies.Append(options.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RequireSecureCookie,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expires,
            IsEssential = true,
        });

    private void ClearCookie(HttpContext? http)
        => http?.Response.Cookies.Delete(options.CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RequireSecureCookie,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
}

/// <summary>Identifies the user of a request from the session cookie.</summary>
internal sealed class SessionUserResolver(SessionService sessions) : IUserResolver
{
    public ValueTask<CtxUser?> ResolveAsync(HttpContext http) => sessions.ResolveAsync(http);
}
