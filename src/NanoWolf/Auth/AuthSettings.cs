namespace XWolf.NanoWolf;

/// <summary>Application level settings of authentication.</summary>
public static class AuthSettings
{
    public static readonly SettingKey<bool> PasswordEnabled = new("auth.password.enabled", true,
        "Allow signing in with email and password.");

    public static readonly SettingKey<int> PasswordMinLength = new("auth.password.minLength", 10,
        "Minimum password length.", v => v is < 8 or > 64 ? "Must be between 8 and 64." : null);

    public static readonly SettingKey<int> PasswordMaxLength = new("auth.password.maxLength", 256,
        "Maximum password length (long phrases and generated passwords are welcome).", v => v is < 64 or > 1024 ? "Must be between 64 and 1024." : null);

    public static readonly SettingKey<int> LockoutThreshold = new("auth.lockout.threshold", 5,
        "Failed attempts for one email before it is temporarily locked.", v => v is < 1 or > 20 ? "Must be between 1 and 20." : null);

    public static readonly SettingKey<int> LockoutIpThreshold = new("auth.lockout.ipThreshold", 20,
        "Failed attempts from one address before it is temporarily locked.", v => v is < 5 or > 200 ? "Must be between 5 and 200." : null);

    public static readonly SettingKey<bool> SingleSession = new("auth.session.single", true,
        "A new sign in closes the previous session of the same user.");

    public static readonly SettingKey<int> SessionIdleDays = new("auth.session.idleDays", 30,
        "Days without use after which a session expires.", v => v is < 1 or > 365 ? "Must be between 1 and 365." : null);

    public static readonly SettingKey<int> SessionMaxDays = new("auth.session.maxDays", 90,
        "Maximum duration of a session, however often it is used.", v => v is < 1 or > 730 ? "Must be between 1 and 730." : null);

    public static readonly SettingKey<int> InvitationHours = new("auth.invitation.hours", 72,
        "Hours an invitation or a password reset link stays valid.", v => v is < 1 or > 720 ? "Must be between 1 and 720." : null);

    public static readonly SettingKey<int> RecoveryMinutes = new("auth.recovery.minutes", 15,
        "Minutes a recovery code generated from the console stays valid.", v => v is < 1 or > 120 ? "Must be between 1 and 120." : null);

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        PasswordEnabled, PasswordMinLength, PasswordMaxLength, LockoutThreshold, LockoutIpThreshold,
        SingleSession, SessionIdleDays, SessionMaxDays, InvitationHours, RecoveryMinutes,
    ];
}

/// <summary>A way of signing in. A method is active when its setting says so.</summary>
public sealed record LoginMethod(string Id, SettingKey<bool> Enabled);

public sealed class LoginMethods(IEnumerable<LoginMethod> all)
{
    public const string Password = "password";

    public IReadOnlyList<LoginMethod> All { get; } = [.. all];

    public ISet<string> EnabledIds(SettingsSnapshot snapshot)
        => All.Where(m => snapshot.Get(m.Enabled)).Select(m => m.Id).ToHashSet();
}

/// <summary>Shared by the rule of the settings and by the administration calls.</summary>
internal static class AdminAccess
{
    /// <summary>True if some enabled administrator has an identity in one of the active methods.</summary>
    public static bool AnyAdminCanSignIn(IEnumerable<User> users, ISet<string> enabledMethods)
        => users.Any(u => u.IsAdmin && !u.Disabled && u.Identities.Any(i => enabledMethods.Contains(i.Method)));

    public const string NoAdminMessage = "After this change no administrator would be able to sign in.";
}

/// <summary>A change cannot leave the application without any administrator able to sign in.</summary>
internal sealed class AdminCanSignInRule(IUserStore users, LoginMethods methods) : ISettingsRule
{
    public async Task<string?> CheckAsync(SettingsSnapshot proposed, CancellationToken cancel)
        => AdminAccess.AnyAdminCanSignIn(await users.ListAsync(cancel), methods.EnabledIds(proposed))
            ? null
            : AdminAccess.NoAdminMessage + " Enable a method that an administrator uses, or keep the current one.";
}

internal sealed class PasswordLengthRule : ISettingsRule
{
    public Task<string?> CheckAsync(SettingsSnapshot proposed, CancellationToken cancel)
        => Task.FromResult(proposed.Get(AuthSettings.PasswordMinLength) > proposed.Get(AuthSettings.PasswordMaxLength)
            ? "The minimum password length cannot be greater than the maximum."
            : null);
}
