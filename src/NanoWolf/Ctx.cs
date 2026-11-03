using Microsoft.AspNetCore.Http;

namespace XWolf.NanoWolf;

/// <summary>Identity of the user executing a call.</summary>
public sealed record CtxUser(string Id, string Name, bool IsAdmin);

/// <summary>
/// Context of one call. It is passed explicitly as a parameter, there is no ambient/global access to it.
/// </summary>
public sealed class Ctx(CtxUser? user, DateTimeOffset now, CancellationToken cancel)
{
    private readonly Dictionary<string, string> details = [];

    /// <summary>The underlying request. Internal: calls only see what <see cref="Ctx"/> exposes.</summary>
    internal HttpContext? Http { get; init; }

    /// <summary>Address of the client (behind a reverse proxy, configure forwarded headers to get the real one).</summary>
    public string? Ip => Http?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent
    {
        get
        {
            var value = Http?.Request.Headers.UserAgent.ToString();
            return string.IsNullOrEmpty(value) ? null : value.Length > 200 ? value[..200] : value;
        }
    }

    /// <summary>Authenticated user. Never null in calls with <see cref="Access.User"/> or <see cref="Access.Admin"/>.</summary>
    public CtxUser? User { get; } = user;

    /// <summary>Time of the call.</summary>
    public DateTimeOffset Now { get; } = now;

    /// <summary>Cancelled when the client aborts the request.</summary>
    public CancellationToken Cancel { get; } = cancel;

    /// <summary>The user, or an unauthenticated error if there is none.</summary>
    public CtxUser RequireUser => User ?? throw CallException.Unauthenticated();

    /// <summary>Details (ids, never content or secrets) added to the automatic log entry of this call.</summary>
    public IReadOnlyDictionary<string, string> Details => details;

    /// <summary>Adds a detail to the log entry of this call.</summary>
    public void Detail(string key, string value) => details[key] = value;
}

/// <summary>Entry written for every call that is not read-only. Parameters and results are never included.</summary>
public sealed record ActionLogEntry(
    DateTimeOffset Time,
    string? UserId,
    string Call,
    bool Ok,
    string? ErrorCode,
    IReadOnlyDictionary<string, string> Details);

/// <summary>Destination of the automatic action log.</summary>
public interface IActionLog
{
    Task WriteAsync(ActionLogEntry entry, CancellationToken cancel);
}

/// <summary>Identifies the user of a request (session cookie, etc). Returns null if there is none or it is disabled.</summary>
public interface IUserResolver
{
    ValueTask<CtxUser?> ResolveAsync(HttpContext http);
}
