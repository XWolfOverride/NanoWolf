using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace XWolf.NanoWolf;

/// <summary>Declaration of an application level setting (stored in the database, editable from the admin panel).</summary>
public abstract class SettingDefinition
{
    internal SettingDefinition(string key, Type type, object defaultValue, string description)
    {
        if (type != typeof(bool) && type != typeof(int) && type != typeof(string))
            throw new ArgumentException($"Setting '{key}': only bool, int and string settings are supported.");
        Key = key;
        Type = type;
        Default = defaultValue;
        Description = description;
    }

    public string Key { get; }
    public Type Type { get; }
    public object Default { get; }
    public string Description { get; }

    /// <summary>Takes effect only after restarting the application.</summary>
    public bool RestartRequired { get; init; }

    /// <summary>Never shown in the panel nor written to the log.</summary>
    public bool Secret { get; init; }

    internal abstract string? Validate(object value);
}

/// <summary>A typed setting. The optional validator returns an error message, or null if the value is fine.</summary>
public sealed class SettingKey<T>(string key, T defaultValue, string description, Func<T, string?>? validate = null)
    : SettingDefinition(key, typeof(T), defaultValue!, description)
{
    public T DefaultValue { get; } = defaultValue;

    internal override string? Validate(object value) => validate?.Invoke((T)value);
}

/// <summary>The settings as they would be after a change; passed to the rules that check the whole set.</summary>
public sealed class SettingsSnapshot(IReadOnlyDictionary<string, object> values)
{
    public T Get<T>(SettingKey<T> key)
        => values.TryGetValue(key.Key, out var value) ? (T)value : key.DefaultValue;
}

/// <summary>A rule about the whole set of settings, checked before accepting a change. Returns an error message or null.</summary>
public interface ISettingsRule
{
    Task<string?> CheckAsync(SettingsSnapshot proposed, CancellationToken cancel);
}

public sealed record SettingInfo(string Key, string Type, object? Value, object Default, string Description, bool RestartRequired, bool Secret);

/// <summary>Holds the current value of every setting in memory (single instance server) and validates changes.</summary>
public sealed class SettingsService
{
    private readonly ISettingsStore store;
    private readonly Dictionary<string, SettingDefinition> definitions;
    private readonly IReadOnlyList<ISettingsRule> rules;
    private readonly ILogger<SettingsService> logger;
    private readonly SemaphoreSlim writing = new(1, 1);
    private volatile IReadOnlyDictionary<string, object> values = new Dictionary<string, object>();

    public SettingsService(ISettingsStore store, IEnumerable<SettingDefinition> definitions, IEnumerable<ISettingsRule> rules, ILogger<SettingsService> logger)
    {
        this.store = store;
        this.rules = [.. rules];
        this.logger = logger;
        this.definitions = [];
        foreach (var definition in definitions)
            if (!this.definitions.TryAdd(definition.Key, definition))
                throw new InvalidOperationException($"Setting '{definition.Key}' is declared more than once.");
    }

    /// <summary>Reads the stored values. Unknown or invalid stored values are ignored (the default is used) and reported.</summary>
    public async Task LoadAsync(CancellationToken cancel)
    {
        var loaded = new Dictionary<string, object>();
        foreach (var (key, json) in await store.LoadAsync(cancel))
        {
            if (!definitions.TryGetValue(key, out var definition))
            {
                logger.LogWarning("Stored setting '{Key}' is not declared and is ignored.", key);
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var value = Parse(definition, doc.RootElement);
                var error = definition.Validate(value);
                if (error != null)
                    throw new FormatException(error);
                loaded[key] = value;
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                logger.LogWarning("Stored setting '{Key}' is invalid ({Reason}) and is ignored.", key, ex.Message);
            }
        }
        values = loaded;
    }

    public T Get<T>(SettingKey<T> key)
        => values.TryGetValue(key.Key, out var value) ? (T)value : key.DefaultValue;

    /// <summary>Changes a setting. Administrators only. Nothing is saved if the value or the resulting set is not valid.</summary>
    public async Task SetAsync(Ctx ctx, string key, JsonElement json)
    {
        if (!ctx.RequireUser.IsAdmin)
            throw CallException.Forbidden();
        if (!definitions.TryGetValue(key, out var definition))
            throw CallException.NotFound("Unknown setting");

        object value;
        try
        {
            value = Parse(definition, json);
        }
        catch (FormatException ex)
        {
            throw CallException.Invalid("Invalid value", new Dictionary<string, string> { ["value"] = ex.Message });
        }
        var error = definition.Validate(value);
        if (error != null)
            throw CallException.Invalid("Invalid value", new Dictionary<string, string> { ["value"] = error });

        await writing.WaitAsync(ctx.Cancel);
        try
        {
            var proposed = new Dictionary<string, object>(values) { [key] = value };
            var snapshot = new SettingsSnapshot(proposed);
            foreach (var rule in rules)
            {
                var message = await rule.CheckAsync(snapshot, ctx.Cancel);
                if (message != null)
                    throw CallException.Invalid(message);
            }

            await store.SaveAsync(key, JsonSerializer.Serialize(value), ctx.Cancel);
            var previous = values.TryGetValue(key, out var old) ? old : definition.Default;
            values = proposed;

            ctx.Detail("setting", key);
            if (!definition.Secret)
            {
                ctx.Detail("old", previous.ToString() ?? "");
                ctx.Detail("new", value.ToString() ?? "");
            }
        }
        finally
        {
            writing.Release();
        }
    }

    public IReadOnlyList<SettingInfo> Describe()
        => [.. definitions.Values
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new SettingInfo(
                d.Key,
                d.Type == typeof(bool) ? "bool" : d.Type == typeof(int) ? "int" : "string",
                d.Secret ? null : values.GetValueOrDefault(d.Key, d.Default),
                d.Secret ? "" : d.Default,
                d.Description,
                d.RestartRequired,
                d.Secret))];

    private static object Parse(SettingDefinition definition, JsonElement json)
    {
        if (definition.Type == typeof(bool) && json.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return json.GetBoolean();
        if (definition.Type == typeof(int) && json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var number))
            return number;
        if (definition.Type == typeof(string) && json.ValueKind == JsonValueKind.String)
            return json.GetString()!;
        throw new FormatException($"expected a {(definition.Type == typeof(bool) ? "boolean" : definition.Type == typeof(int) ? "whole number" : "text")}");
    }
}

/// <summary>Loads the settings when the application starts.</summary>
internal sealed class SettingsLoader(SettingsService settings) : IHostedService
{
    public Task StartAsync(CancellationToken cancel) => settings.LoadAsync(cancel);
    public Task StopAsync(CancellationToken cancel) => Task.CompletedTask;
}
