namespace XWolf.NanoWolf;

/// <summary>Error with a stable code that is sent to the client.</summary>
public sealed class CallException(int status, string code, string message, IReadOnlyDictionary<string, string>? fields = null, string? errorId = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, string>? Fields { get; } = fields;
    public string? ErrorId { get; } = errorId;

    public static CallException Unauthenticated(string message = "Authentication required") => new(401, "unauthenticated", message);
    public static CallException Forbidden(string message = "Not allowed") => new(403, "forbidden", message);
    /// <summary>Also used for existing objects the user has no access to, to not reveal their existence.</summary>
    public static CallException NotFound(string message = "Not found") => new(404, "not_found", message);
    public static CallException Invalid(string message, IReadOnlyDictionary<string, string>? fields = null) => new(422, "invalid", message, fields);
    public static CallException VersionConflict(string message = "The object was modified by someone else") => new(409, "version_conflict", message);
}
