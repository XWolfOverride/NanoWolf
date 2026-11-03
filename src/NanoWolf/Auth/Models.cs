namespace XWolf.NanoWolf;

/// <summary>Keys used to pass authentication information along the request.</summary>
internal static class AuthItems
{
    /// <summary>Why there is no user (for example "session_replaced"). Set by the user resolver.</summary>
    public const string Reason = "nanowolf.auth.reason";

    /// <summary>Id (hash) of the session of the current request.</summary>
    public const string SessionKey = "nanowolf.auth.session";
}

/// <summary>A way of proving who a user is: for a password, the subject is the email and the data the hash.</summary>
public sealed class Identity
{
    public string Method { get; set; } = "";
    public string Subject { get; set; } = "";
    public string? Data { get; set; }
}

public sealed class User
{
    public string Id { get; set; } = "";

    /// <summary>Normalized (trimmed, lower case) and unique.</summary>
    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool Disabled { get; set; }
    public List<Identity> Identities { get; set; } = [];

    /// <summary>Personal choices (accent color, view...). Only keys declared as <see cref="PreferenceDefinition"/> are ever written.</summary>
    public Dictionary<string, string> Preferences { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SessionRecord
{
    /// <summary>SHA-256 of the token the browser holds. The token itself is never stored.</summary>
    public string Id { get; set; } = "";

    public string UserId { get; set; } = "";
    public string Method { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>Set when a login elsewhere took over: it is kept for a while to explain what happened.</summary>
    public DateTimeOffset? ReplacedAt { get; set; }
}

public enum TokenKind
{
    /// <summary>Creates a new user with the email of the token.</summary>
    Join,
    /// <summary>Sets a new password for an existing user.</summary>
    Reset,
    /// <summary>Opens an administrator session when other ways of access are not available.</summary>
    Recovery,
}

/// <summary>A single use secret link. Only the hash of the token is stored.</summary>
public sealed class TokenRecord
{
    public string Id { get; set; } = "";
    public TokenKind Kind { get; set; }
    public string? Email { get; set; }
    public string? UserId { get; set; }
    public bool IsAdmin { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>Failed access attempts of a key ("email:..." or "ip:...") kept apart from users, so unknown emails behave alike.</summary>
public sealed class ThrottleRecord
{
    public string Key { get; set; } = "";
    public int Failures { get; set; }
    public DateTimeOffset LastFailureAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>When the record can be forgotten (the database removes it automatically).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>What the client gets to know about the current user.</summary>
public sealed record UserInfo(string Id, string Email, string DisplayName, bool IsAdmin);
