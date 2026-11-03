using System.Reflection;

namespace XWolf.NanoWolf;

/// <summary>Parameter of a published call.</summary>
public sealed class CallParam
{
    public required string Name { get; init; }
    public required TypeSpec Type { get; init; }
    public required bool Optional { get; init; }

    internal Type ClrType { get; init; } = default!;
    internal object? DefaultValue { get; init; }
    /// <summary>Position in the method signature.</summary>
    internal int Index { get; init; }
}

/// <summary>A published call: everything needed to publish it and to invoke it without reflection.</summary>
public sealed class CallDescriptor
{
    public required string Group { get; init; }
    public required string Name { get; init; }
    public string Key => $"{Group}.{Name}";
    public required Access Access { get; init; }
    public required bool ReadOnly { get; init; }
    public required Verb Verb { get; init; }
    public required IReadOnlyList<CallParam> Params { get; init; }
    public TypeSpec? Returns { get; init; }
    /// <summary>Where the call is declared, for messages.</summary>
    public required string Origin { get; init; }

    internal Type DeclaringType { get; init; } = default!;
    internal bool IsStatic { get; init; }
    internal bool IsSingleton { get; init; }
    internal int ParameterCount { get; init; }
    /// <summary>Index of the <see cref="Ctx"/> parameter, or -1.</summary>
    internal int CtxIndex { get; init; }
    internal Func<object?, object?[], object?> Invoker { get; init; } = default!;
    /// <summary>Extracts the result of a Task&lt;T&gt;, null for Task or synchronous methods.</summary>
    internal Func<Task, object?>? TaskResult { get; init; }
    /// <summary>Declared type of the result, used to serialize it.</summary>
    internal Type? ResultType { get; init; }
}
