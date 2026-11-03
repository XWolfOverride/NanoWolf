using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace XWolf.NanoWolf;

/// <summary>
/// Commands run from the console of the server, for what cannot depend on anybody being able to sign in:
/// the first administrator and the recovery of the access. Whoever can run them already controls the server.
/// </summary>
public static class NanoWolfCommands
{
    public const string Usage =
        "Commands:" + "\n" +
        "  invite <email> [--admin]   Creates an invitation link (the first administrator is created this way)." + "\n" +
        "  recovery <email>           Creates a single use code that opens an administrator session." + "\n" +
        "  reset <email>              Creates a link so an existing user chooses a (new) password.";

    /// <summary>
    /// Runs the command if the arguments are one of ours and returns true; otherwise returns false and the application
    /// should start normally. Call it from Program before running the web application.
    /// </summary>
    public static async Task<bool> TryRunAsync(string[] args, IServiceProvider services, TextWriter output, CancellationToken cancel = default)
    {
        if (args.Length == 0 || args[0] is not ("invite" or "recovery" or "reset"))
            return false;

        var email = EmailAddress.Normalize(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)));
        if (email == null)
        {
            await output.WriteLineAsync("An email is required." + "\n" + Usage);
            return true;
        }

        var options = services.GetRequiredService<NanoWolfOptions>();
        var tokens = services.GetRequiredService<TokenService>();
        var users = services.GetRequiredService<IUserStore>();
        var log = services.GetRequiredService<IActionLog>();
        var settings = services.GetRequiredService<SettingsService>();
        await settings.LoadAsync(cancel);
        var baseUrl = options.PublicUrl.TrimEnd('/');

        if (args[0] == "invite")
        {
            var isAdmin = args.Contains("--admin");
            if (await users.FindByEmailAsync(email, cancel) != null)
            {
                await output.WriteLineAsync($"{email} is already registered. Use 'recovery' or an administrator can create a password reset.");
                return true;
            }

            var hours = settings.Get(AuthSettings.InvitationHours);
            var (token, expires) = await tokens.CreateAsync(TokenKind.Join, "console", TimeSpan.FromHours(hours), email: email, isAdmin: isAdmin, cancel: cancel);
            await WriteLogAsync(log, options, "console.invite", new() { ["email"] = email, ["admin"] = isAdmin ? "true" : "false" }, cancel);
            await output.WriteLineAsync($"Invitation for {email}{(isAdmin ? " (administrator)" : "")}, valid until {expires:u}:");
            await output.WriteLineAsync($"{baseUrl}/#/join/{token}");
            return true;
        }

        if (args[0] == "reset")
        {
            var target = await users.FindByEmailAsync(email, cancel);
            if (target is not { Disabled: false })
            {
                await output.WriteLineAsync($"{email} is not an enabled user.");
                return true;
            }

            var resetHours = settings.Get(AuthSettings.InvitationHours);
            var (resetToken, resetUntil) = await tokens.CreateAsync(TokenKind.Reset, "console", TimeSpan.FromHours(resetHours), userId: target.Id, cancel: cancel);
            await WriteLogAsync(log, options, "console.reset", new() { ["user"] = target.Id }, cancel);
            await output.WriteLineAsync($"Link for {email} to choose a password, single use, valid until {resetUntil:u}:");
            await output.WriteLineAsync($"{baseUrl}/#/reset/{resetToken}");
            return true;
        }

        var user = await users.FindByEmailAsync(email, cancel);
        if (user is not { IsAdmin: true, Disabled: false })
        {
            await output.WriteLineAsync($"{email} is not an enabled administrator.");
            return true;
        }

        var minutes = settings.Get(AuthSettings.RecoveryMinutes);
        var (code, until) = await tokens.CreateAsync(TokenKind.Recovery, "console", TimeSpan.FromMinutes(minutes), userId: user.Id, cancel: cancel);
        await WriteLogAsync(log, options, "console.recovery", new() { ["user"] = user.Id }, cancel);
        await output.WriteLineAsync($"Recovery code for {email}, single use, valid until {until:u}:");
        await output.WriteLineAsync($"{baseUrl}/#/recover/{code}");
        return true;
    }

    private static Task WriteLogAsync(IActionLog log, NanoWolfOptions options, string call, Dictionary<string, string> details, CancellationToken cancel)
        => log.WriteAsync(new ActionLogEntry(options.TimeProvider.GetUtcNow(), null, call, true, null, details), cancel);
}

public static class NanoWolfAuthExtensions
{
    /// <summary>
    /// Registers authentication: settings, sign in methods, sessions, invitations and their services. The application
    /// must register the stores (<see cref="IUserStore"/>, <see cref="ISessionStore"/>, <see cref="ITokenStore"/>,
    /// <see cref="IThrottleStore"/>, <see cref="ISettingsStore"/>) and <see cref="IActionLog"/>.
    /// </summary>
    public static IServiceCollection AddNanoWolfAuth(this IServiceCollection services, Action<NanoWolfOptions>? configure = null)
    {
        services.AddNanoWolfOptions(configure);

        foreach (var definition in AuthSettings.All)
            services.AddSingleton(definition);
        services.AddSingleton<ISettingsRule, AdminCanSignInRule>();
        services.AddSingleton<ISettingsRule, PasswordLengthRule>();

        services.AddSingleton(new LoginMethod(LoginMethods.Password, AuthSettings.PasswordEnabled));
        services.AddSingleton<LoginMethods>();

        services.AddSingleton(NanoWolfPreferences.Accent);
        services.AddSingleton<PreferencesService>();

        services.AddSingleton<SettingsService>();
        services.AddHostedService<SettingsLoader>();
        services.AddSingleton<PasswordService>();
        services.AddSingleton<ThrottleService>();
        services.AddSingleton<TokenService>();
        services.AddSingleton<SessionService>();
        services.TryAddSingleton<IUserResolver, SessionUserResolver>();
        return services;
    }

    /// <summary>Declares an extra application level setting (for example, one of the application itself).</summary>
    public static IServiceCollection AddNanoWolfSetting(this IServiceCollection services, SettingDefinition definition)
        => services.AddSingleton(definition);
}
