namespace XWolf.NanoWolf;

/// <summary>Who can invoke a published call.</summary>
public enum Access
{
    /// <summary>Anyone, logged in or not.</summary>
    Anonymous,
    /// <summary>Any authenticated user (default).</summary>
    User,
    /// <summary>Authenticated administrators only.</summary>
    Admin,
}

/// <summary>HTTP verb used to invoke a call.</summary>
public enum Verb
{
    /// <summary>JSON body with named parameters. Accepts any supported parameter type.</summary>
    Post,
    /// <summary>Query string parameters. Requires <see cref="CallAttribute.ReadOnly"/> and simple parameter types.</summary>
    Get,
}

/// <summary>Publishes a method as a remote call. Methods without this attribute are never reachable.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CallAttribute : Attribute
{
    /// <summary>Group of the call. If null, the <see cref="CallGroupAttribute"/> of the class is used.</summary>
    public string? Group { get; set; }

    /// <summary>Name seen by the client. If null, the method name in camelCase (without "Async" suffix).</summary>
    public string? Name { get; set; }

    /// <summary>Required access level. Secure by default: <see cref="Access.User"/>.</summary>
    public Access Access { get; set; } = Access.User;

    /// <summary>True if the call does not modify data. Non read-only calls are logged automatically.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Verb used to invoke the call.</summary>
    public Verb Verb { get; set; } = Verb.Post;
}

/// <summary>Default group for the published methods of a class.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CallGroupAttribute(string group) : Attribute
{
    public string Group { get; } = group;
}

/// <summary>
/// Keeps a single instance of the class shared by all calls (instead of one instance per call).
/// Such instances are used concurrently: they must not keep request state (never the <see cref="Ctx"/>) in fields.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SingletonAttribute : Attribute
{
}
