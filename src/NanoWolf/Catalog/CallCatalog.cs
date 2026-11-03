using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace XWolf.NanoWolf;

/// <summary>The startup configuration is invalid. Lists every problem found, not only the first.</summary>
public sealed class CatalogException(IReadOnlyList<string> errors)
    : Exception("Invalid call catalog:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Every published call, discovered by reflection once at startup and immutable afterwards.
/// If anything is wrong building it throws <see cref="CatalogException"/> and the application must not start.
/// </summary>
public sealed partial class CallCatalog
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "constructor", "prototype", "__proto__", "tostring", "valueof", "hasownproperty", "tojson",
    };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex NamePattern();

    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, CallDescriptor> byPath;

    private CallCatalog(List<CallDescriptor> calls)
    {
        Calls = calls;
        byPath = calls.ToDictionary(c => c.Group + "/" + c.Name, StringComparer.Ordinal);
        Hash = ComputeHash(calls);
    }

    public IReadOnlyList<CallDescriptor> Calls { get; }

    /// <summary>Hash of the whole catalog (independent of the user). Changes when the published contract changes.</summary>
    public string Hash { get; }

    public CallDescriptor? Find(string group, string name)
        => byPath.GetValueOrDefault(group + "/" + name);

    public static CallCatalog FromAssemblies(params Assembly[] assemblies)
        => FromTypes(assemblies.SelectMany(SafeTypes));

    public static CallCatalog FromTypes(IEnumerable<Type> types)
    {
        var errors = new List<string>();
        var calls = new List<CallDescriptor>();
        foreach (var type in types)
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<CallAttribute>(false);
                if (attr == null)
                    continue;
                var call = Build(type, method, attr, errors);
                if (call != null)
                    calls.Add(call);
            }

        foreach (var clash in calls.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Call '{clash.Key}' is declared more than once: {string.Join(" and ", clash.Select(c => c.Origin))} (overloads and names that differ only in case also clash).");

        if (errors.Count > 0)
            throw new CatalogException(errors);

        calls.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return new CallCatalog(calls);
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    private static CallDescriptor? Build(Type type, MethodInfo method, CallAttribute attr, List<string> errors)
    {
        var origin = $"{type.FullName}.{method.Name}";
        var before = errors.Count;

        if (!method.IsPublic)
            errors.Add($"{origin}: published methods must be public.");
        if (method.IsGenericMethodDefinition)
            errors.Add($"{origin}: generic methods cannot be published.");
        if (!method.IsStatic && (type.IsAbstract || type.IsGenericTypeDefinition))
            errors.Add($"{origin}: instance methods must belong to a concrete, non generic class.");

        var group = attr.Group ?? type.GetCustomAttribute<CallGroupAttribute>(false)?.Group;
        if (string.IsNullOrWhiteSpace(group))
            errors.Add($"{origin}: no group (set Group in [Call] or [CallGroup] on the class).");

        var name = attr.Name;
        if (name == null)
        {
            name = method.Name;
            if (name.Length > 5 && name.EndsWith("Async", StringComparison.Ordinal))
                name = name[..^5];
            name = TypeSpecs.CamelCase(name);
        }

        foreach (var (what, value) in new[] { ("group", group), ("name", name) })
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (!NamePattern().IsMatch(value))
                errors.Add($"{origin}: invalid {what} '{value}' (letters and digits, starting with a letter).");
            else if (Reserved.Contains(value))
                errors.Add($"{origin}: '{value}' is a reserved word in JavaScript and cannot be used as {what}.");
        }

        if (attr.Verb == Verb.Get && !attr.ReadOnly)
            errors.Add($"{origin}: Get calls must be ReadOnly.");

        // Return type
        var returnType = method.ReturnType;
        Type? resultType = null;
        Func<Task, object?>? taskResult = null;
        if (returnType == typeof(Task))
        {
            // No result.
        }
        else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            resultType = returnType.GetGenericArguments()[0];
            taskResult = CompileTaskResult(returnType);
        }
        else if (returnType == typeof(ValueTask) || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            errors.Add($"{origin}: ValueTask is not supported, use Task.");
        else if (returnType != typeof(void))
            resultType = returnType;

        TypeSpec? returns = null;
        if (resultType != null)
            returns = TypeSpecs.Describe(resultType, TypeMode.Result, $"{origin} (result)", errors);

        // Parameters
        var ps = method.GetParameters();
        var list = new List<CallParam>();
        var ctxIndex = -1;
        var nullability = new NullabilityInfoContext();
        foreach (var p in ps)
        {
            if (p.ParameterType == typeof(Ctx))
            {
                if (ctxIndex >= 0)
                    errors.Add($"{origin}: more than one Ctx parameter.");
                ctxIndex = p.Position;
                continue;
            }
            if (p.ParameterType == typeof(CancellationToken))
            {
                errors.Add($"{origin}: do not declare a CancellationToken, use Ctx.Cancel.");
                continue;
            }
            if (p.ParameterType.IsByRef || p.IsOut)
            {
                errors.Add($"{origin}: ref/out parameter '{p.Name}' is not supported.");
                continue;
            }

            var spec = TypeSpecs.Describe(p.ParameterType, attr.Verb == Verb.Get ? TypeMode.Query : TypeMode.Body, $"{origin}({p.Name})", errors);
            if (spec == null)
                continue;

            var nullableValue = Nullable.GetUnderlyingType(p.ParameterType) != null;
            var nullableRef = !p.ParameterType.IsValueType && nullability.Create(p).WriteState == NullabilityState.Nullable;
            var optional = p.HasDefaultValue || nullableValue || nullableRef;
            object? def = null;
            if (p.HasDefaultValue && p.DefaultValue is not DBNull and not null)
            {
                // Reflection can hand an enum default as its number: the compiled call needs the enum itself.
                var target = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                def = target.IsEnum && !target.IsInstanceOfType(p.DefaultValue) ? Enum.ToObject(target, p.DefaultValue) : p.DefaultValue;
            }
            else if (p.ParameterType.IsValueType && !nullableValue)
                def = Activator.CreateInstance(p.ParameterType);

            list.Add(new CallParam
            {
                Name = p.Name!,
                Type = spec,
                Optional = optional,
                ClrType = p.ParameterType,
                DefaultValue = def,
                Index = p.Position,
            });
        }

        if (errors.Count > before)
            return null;

        return new CallDescriptor
        {
            Group = group!,
            Name = name,
            Access = attr.Access,
            ReadOnly = attr.ReadOnly,
            Verb = attr.Verb,
            Params = list,
            Returns = returns,
            Origin = origin,
            DeclaringType = type,
            IsStatic = method.IsStatic,
            IsSingleton = type.GetCustomAttribute<SingletonAttribute>(false) != null,
            ParameterCount = ps.Length,
            CtxIndex = ctxIndex,
            Invoker = CompileInvoker(method),
            TaskResult = taskResult,
            ResultType = resultType,
        };
    }

    private static Func<object?, object?[], object?> CompileInvoker(MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var args = Expression.Parameter(typeof(object?[]), "args");
        var arguments = method.GetParameters()
            .Select(p => (Expression)Expression.Convert(Expression.ArrayIndex(args, Expression.Constant(p.Position)), p.ParameterType))
            .ToList();
        Expression call = method.IsStatic
            ? Expression.Call(method, arguments)
            : Expression.Call(Expression.Convert(instance, method.DeclaringType!), method, arguments);
        Expression body = method.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));
        return Expression.Lambda<Func<object?, object?[], object?>>(body, instance, args).Compile();
    }

    private static Func<Task, object?> CompileTaskResult(Type taskType)
    {
        var task = Expression.Parameter(typeof(Task), "task");
        var result = Expression.Property(Expression.Convert(task, taskType), "Result");
        return Expression.Lambda<Func<Task, object?>>(Expression.Convert(result, typeof(object)), task).Compile();
    }

    /// <summary>Projection of the calls the given user can see (the real check is always done when calling).</summary>
    internal object Describe(CtxUser? user)
    {
        var groups = new SortedDictionary<string, SortedDictionary<string, object>>(StringComparer.Ordinal);
        foreach (var call in Calls.Where(c => Visible(c, user)))
        {
            if (!groups.TryGetValue(call.Group, out var calls))
                groups[call.Group] = calls = new(StringComparer.Ordinal);
            calls[call.Name] = Describe(call);
        }
        return new { hash = Hash, groups };
    }

    private static bool Visible(CallDescriptor call, CtxUser? user) => call.Access switch
    {
        Access.Anonymous => true,
        Access.User => user != null,
        _ => user is { IsAdmin: true },
    };

    private static object Describe(CallDescriptor call) => new
    {
        verb = call.Verb.ToString().ToLowerInvariant(),
        access = call.Access.ToString().ToLowerInvariant(),
        readOnly = call.ReadOnly,
        @params = call.Params.Select(p => new { name = p.Name, type = p.Type, optional = p.Optional }).ToList(),
        returns = call.Returns,
    };

    private static string ComputeHash(List<CallDescriptor> calls)
    {
        var full = new
        {
            calls = calls.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new { key = c.Key, d = Describe(c) }).ToList(),
        };
        var json = JsonSerializer.Serialize(full, HashJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16].ToLowerInvariant();
    }
}
