using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XWolf.NanoWolf;

/// <summary>Type of a parameter or result as published in the catalog.</summary>
public sealed record TypeSpec
{
    /// <summary>string, bool, int, number, datetime, guid, enum, list, object or any.</summary>
    public string Type { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Values { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TypeSpec? Item { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FieldSpec>? Fields { get; init; }
}

public sealed record FieldSpec(string Name, TypeSpec Type, bool Optional);

internal enum TypeMode
{
    /// <summary>Parameters of POST calls: simple types, lists and plain objects.</summary>
    Body,
    /// <summary>Parameters of GET calls: simple types and lists of them.</summary>
    Query,
    /// <summary>Results: anything is accepted, unknown shapes are published as "any".</summary>
    Result,
}

internal static class TypeSpecs
{
    private static readonly HashSet<Type> ListDefinitions =
    [
        typeof(List<>), typeof(IList<>), typeof(IReadOnlyList<>), typeof(IEnumerable<>), typeof(ICollection<>), typeof(IReadOnlyCollection<>),
    ];

    public static TypeSpec? Describe(Type type, TypeMode mode, string where, List<string> errors)
        => Describe(type, mode, where, errors, []);

    private static TypeSpec? Describe(Type type, TypeMode mode, string where, List<string> errors, Stack<Type> path)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        // A raw JSON value (for example the value of a setting, whose type depends on the setting).
        if (type == typeof(JsonElement))
        {
            if (mode == TypeMode.Query)
            {
                errors.Add($"{where}: GET parameters must be simple types or lists of simple types (JsonElement).");
                return null;
            }
            return new TypeSpec { Type = "any" };
        }

        if (typeof(ICallResponse).IsAssignableFrom(type))
        {
            if (mode != TypeMode.Result)
            {
                errors.Add($"{where}: response types ({type.Name}) cannot be parameters.");
                return null;
            }
            return new TypeSpec { Type = ResponseKind(type) };
        }

        var simple = Simple(type);
        if (simple != null)
            return simple;

        var element = ListElement(type);
        if (element != null)
        {
            var item = Describe(element, mode, where, errors, path);
            if (item == null)
                return null;
            if (mode == TypeMode.Query && item.Type is "list" or "object" or "any")
            {
                errors.Add($"{where}: GET parameters must be simple types or lists of simple types ({type.Name}).");
                return null;
            }
            return new TypeSpec { Type = "list", Item = item };
        }

        if (mode == TypeMode.Query)
        {
            errors.Add($"{where}: GET parameters must be simple types or lists of simple types ({type.Name}).");
            return null;
        }

        if (type == typeof(object))
        {
            if (mode == TypeMode.Result)
                return new TypeSpec { Type = "any" };
            errors.Add($"{where}: 'object' is not a supported parameter type.");
            return null;
        }

        if (!IsPlainObject(type))
        {
            if (mode == TypeMode.Result)
                return new TypeSpec { Type = "any" };
            errors.Add($"{where}: unsupported type {type.FullName}.");
            return null;
        }

        if (path.Contains(type))
        {
            if (mode == TypeMode.Result)
                return new TypeSpec { Type = "any" };
            errors.Add($"{where}: recursive type {type.Name} is not supported.");
            return null;
        }

        path.Push(type);
        try
        {
            var nullability = new NullabilityInfoContext();
            var fields = new List<FieldSpec>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetMethod == null || prop.GetIndexParameters().Length > 0)
                    continue;
                var field = Describe(prop.PropertyType, mode, $"{where}.{prop.Name}", errors, path);
                if (field == null)
                    continue;
                var optional = Nullable.GetUnderlyingType(prop.PropertyType) != null
                    || (!prop.PropertyType.IsValueType && nullability.Create(prop).ReadState == NullabilityState.Nullable);
                fields.Add(new FieldSpec(CamelCase(prop.Name), field, optional));
            }
            return new TypeSpec { Type = "object", Fields = fields };
        }
        finally
        {
            path.Pop();
        }
    }

    private static TypeSpec? Simple(Type t)
    {
        if (t == typeof(string)) return new() { Type = "string" };
        if (t == typeof(bool)) return new() { Type = "bool" };
        if (t == typeof(byte) || t == typeof(short) || t == typeof(int) || t == typeof(long)) return new() { Type = "int" };
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return new() { Type = "number" };
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return new() { Type = "datetime" };
        if (t == typeof(Guid)) return new() { Type = "guid" };
        if (t.IsEnum) return new() { Type = "enum", Values = Enum.GetNames(t) };
        return null;
    }

    private static string ResponseKind(Type t)
        => t.GetCustomAttribute<ResponseKindAttribute>(false)?.Kind
           ?? t.GetInterfaces().Select(i => i.GetCustomAttribute<ResponseKindAttribute>(false)?.Kind).FirstOrDefault(k => k != null)
           ?? "response";

    internal static Type? ListElement(Type t)
    {
        if (t == typeof(string))
            return null;
        if (t.IsArray && t.GetArrayRank() == 1)
            return t.GetElementType();
        if (t.IsGenericType && ListDefinitions.Contains(t.GetGenericTypeDefinition()))
            return t.GetGenericArguments()[0];
        return null;
    }

    private static bool IsPlainObject(Type t)
    {
        if (t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition || t.IsPrimitive || t.IsEnum || t.IsPointer)
            return false;
        if (t.Namespace != null && t.Namespace.StartsWith("System", StringComparison.Ordinal))
            return false;
        return t.IsClass || t.IsValueType;
    }

    internal static string CamelCase(string name)
        => name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
