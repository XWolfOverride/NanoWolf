using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace XWolf.NanoWolf;

/// <summary>
/// A personal choice a user can make (accent color, whether to show excerpts...). Declared in code, like the application
/// settings, so nothing else can be stored and every value is checked: the key must be known and the value must be valid.
/// Values are plain text; the definition knows how to check and tidy its own.
/// </summary>
public sealed partial class PreferenceDefinition
{
    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,31}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    private readonly Func<string, string> normalize;

    /// <param name="normalize">Returns the value tidied (for example in lower case) or throws <see cref="FormatException"/> with a message.</param>
    public PreferenceDefinition(string key, string defaultValue, string description, Func<string, string> normalize)
    {
        if (!KeyPattern().IsMatch(key))
            throw new ArgumentException($"Preference key '{key}' must start with a lower case letter and have only letters and digits.");
        Key = key;
        Description = description;
        this.normalize = normalize;
        Default = Normalize(defaultValue);
    }

    public string Key { get; }
    public string Default { get; }
    public string Description { get; }

    /// <summary>The value tidied, or <see cref="FormatException"/> if it is not valid.</summary>
    public string Normalize(string? value) => normalize(value ?? "");

    /// <summary>A color as #rrggbb (stored in lower case).</summary>
    public static PreferenceDefinition Color(string key, string defaultValue, string description)
        => new(key, defaultValue, description, v => ColorPattern().IsMatch(v) ? v.ToLowerInvariant() : throw new FormatException("A color as #rrggbb."));

    public static PreferenceDefinition Bool(string key, bool defaultValue, string description)
        => new(key, defaultValue ? "true" : "false", description, v => v.Trim().ToLowerInvariant() switch
        {
            "true" => "true",
            "false" => "false",
            _ => throw new FormatException("true or false."),
        });

    /// <summary>One of the allowed values (stored with the capitalization given here).</summary>
    public static PreferenceDefinition Choice(string key, string defaultValue, string description, params string[] allowed)
        => new(key, defaultValue, description, v => allowed.FirstOrDefault(a => a.Equals(v.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException("One of: " + string.Join(", ", allowed) + "."));
}

/// <summary>Preferences every NanoWolf application has.</summary>
public static class NanoWolfPreferences
{
    /// <summary>The accent color of the interface. The client derives the rest of the palette from it.</summary>
    public static readonly PreferenceDefinition Accent = PreferenceDefinition.Color("accent", "#ffd95a", "Accent color of the interface.");
}

/// <summary>Reads and writes the preferences of the current user.</summary>
public sealed class PreferencesService
{
    private readonly IUserStore users;
    private readonly Dictionary<string, PreferenceDefinition> definitions = [];

    public PreferencesService(IUserStore users, IEnumerable<PreferenceDefinition> definitions)
    {
        this.users = users;
        foreach (var definition in definitions)
            if (!this.definitions.TryAdd(definition.Key, definition))
                throw new InvalidOperationException($"Preference '{definition.Key}' is declared more than once.");
    }

    /// <summary>Every declared preference with its value: the user's own, or the default. Stored values that are not valid or not declared are ignored.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(Ctx ctx)
    {
        var user = await users.FindByIdAsync(ctx.RequireUser.Id, ctx.Cancel) ?? throw CallException.Unauthenticated();
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in definitions.Values)
        {
            var value = definition.Default;
            if (user.Preferences.TryGetValue(definition.Key, out var stored))
            {
                try
                {
                    value = definition.Normalize(stored);
                }
                catch (FormatException)
                {
                    // A stored value that is not valid any more (the rules changed): the default is used.
                }
            }
            result[definition.Key] = value;
        }
        return result;
    }

    public async Task SetAsync(Ctx ctx, string key, string value)
    {
        var user = ctx.RequireUser;
        if (!definitions.TryGetValue(key ?? "", out var definition))
            throw CallException.Invalid("Invalid data", new Dictionary<string, string> { ["key"] = "Unknown preference." });

        string normalized;
        try
        {
            normalized = definition.Normalize(value);
        }
        catch (FormatException ex)
        {
            throw CallException.Invalid("Invalid data", new Dictionary<string, string> { ["value"] = ex.Message });
        }

        await users.SetPreferenceAsync(user.Id, definition.Key, normalized, ctx.Cancel);
        ctx.Detail("preference", definition.Key);
    }
}

/// <summary>The calls of the current user about their own account. Published as group "account".</summary>
[CallGroup("account")]
public sealed class AccountCalls(PreferencesService preferences)
{
    /// <summary>All the preferences of the user, with defaults where they have not chosen.</summary>
    [Call(ReadOnly = true, Verb = Verb.Get)]
    public Task<IReadOnlyDictionary<string, string>> Preferences(Ctx ctx) => preferences.GetAsync(ctx);

    [Call]
    public Task SetPreference(Ctx ctx, string key, string value) => preferences.SetAsync(ctx, key, value);
}

public static class NanoWolfPreferenceExtensions
{
    /// <summary>Declares a preference of the application (the ones of NanoWolf, such as the accent color, are already declared).</summary>
    public static IServiceCollection AddNanoWolfPreference(this IServiceCollection services, PreferenceDefinition definition)
        => services.AddSingleton(definition);
}
