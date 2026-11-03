using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace XWolf.NanoWolf;

/// <summary>Builds the argument list of a call from the request.</summary>
internal static class CallBinder
{
    public static async Task<object?[]> BindAsync(CallDescriptor call, HttpRequest request, Ctx ctx, NanoWolfOptions options)
    {
        var args = new object?[call.ParameterCount];
        if (call.CtxIndex >= 0)
            args[call.CtxIndex] = ctx;

        var fields = new Dictionary<string, string>();
        if (call.Verb == Verb.Get)
            BindQuery(call, request, args, fields);
        else
            await BindBodyAsync(call, request, args, fields, options, ctx.Cancel);

        if (fields.Count > 0)
            throw CallException.Invalid("Invalid parameters", fields);
        return args;
    }

    // ---- POST: JSON object with named parameters ----

    private static async Task BindBodyAsync(CallDescriptor call, HttpRequest request, object?[] args, Dictionary<string, string> fields, NanoWolfOptions options, CancellationToken cancel)
    {
        var body = await ReadBodyAsync(request, options.MaxBodyBytes, cancel);
        JsonDocument doc;
        try
        {
            doc = body.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new CallException(400, "bad_request", "The body is not valid JSON");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new CallException(400, "bad_request", "The body must be a JSON object with the named parameters");

            var known = call.Params.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!known.ContainsKey(prop.Name))
                    fields[prop.Name] = "unknown parameter";
                else if (!seen.Add(prop.Name))
                    fields[prop.Name] = "duplicated parameter";
            }

            foreach (var p in call.Params)
            {
                JsonElement value = default;
                var found = false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (string.Equals(prop.Name, p.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        found = true;
                        break;
                    }

                if (!found || value.ValueKind == JsonValueKind.Null)
                {
                    if (p.Optional)
                        args[p.Index] = p.DefaultValue;
                    else
                        fields[p.Name] = "required";
                    continue;
                }

                try
                {
                    args[p.Index] = value.Deserialize(p.ClrType, options.Json);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    fields[p.Name] = "invalid value";
                }
            }
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, int max, CancellationToken cancel)
    {
        if (request.ContentLength > max)
            throw new CallException(413, "too_large", "The request is too large");
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancel)) > 0)
        {
            if (ms.Length + read > max)
                throw new CallException(413, "too_large", "The request is too large");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    // ---- GET: simple parameters in the query string ----

    private static void BindQuery(CallDescriptor call, HttpRequest request, object?[] args, Dictionary<string, string> fields)
    {
        var known = call.Params.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.Query.Keys)
            if (!known.Contains(key))
                fields[key] = "unknown parameter";

        foreach (var p in call.Params)
        {
            var values = request.Query[p.Name];
            if (values.Count == 0 || (values.Count == 1 && string.IsNullOrEmpty(values[0]) && p.Type.Type != "string"))
            {
                if (p.Optional)
                    args[p.Index] = p.DefaultValue;
                else
                    fields[p.Name] = "required";
                continue;
            }

            try
            {
                var element = TypeSpecs.ListElement(p.ClrType);
                if (element != null)
                {
                    var list = Array.CreateInstance(element, values.Count);
                    for (var i = 0; i < values.Count; i++)
                        list.SetValue(Convert(values[i]!, element), i);
                    args[p.Index] = p.ClrType.IsArray ? list : Activator.CreateInstance(typeof(List<>).MakeGenericType(element), list);
                }
                else
                {
                    if (values.Count > 1)
                        throw new FormatException();
                    args[p.Index] = Convert(values[0]!, p.ClrType);
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
            {
                fields[p.Name] = "invalid value";
            }
        }
    }

    private static object? Convert(string text, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return text;
        if (type == typeof(bool)) return bool.Parse(text);
        if (type == typeof(byte)) return byte.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(short)) return short.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(int)) return int.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(long)) return long.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(float)) return float.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return double.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(decimal)) return decimal.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(Guid)) return Guid.Parse(text);
        if (type.IsEnum)
        {
            // Names only: numbers such as "99" would be accepted by Enum.Parse even if undefined.
            if (!Enum.GetNames(type).Contains(text, StringComparer.OrdinalIgnoreCase))
                throw new FormatException();
            return Enum.Parse(type, text, ignoreCase: true);
        }
        throw new InvalidCastException();
    }
}
