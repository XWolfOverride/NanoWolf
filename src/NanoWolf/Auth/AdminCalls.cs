using System.Text.Json;

namespace XWolf.NanoWolf;

public sealed record AdminUserInfo(string Id, string Email, string DisplayName, bool IsAdmin, bool Disabled, string[] Methods, DateTimeOffset CreatedAt);

public sealed record LinkCreated(string Token, DateTimeOffset ExpiresAt);

/// <summary>Account administration. Published as group "admin". Administrators manage accounts, never content.</summary>
[CallGroup("admin")]
public sealed class AdminCalls(
    IUserStore users,
    SettingsService settings,
    SessionService sessions,
    TokenService tokens,
    LoginMethods methods)
{
    [Call(Access = Access.Admin, ReadOnly = true, Verb = Verb.Get)]
    public async Task<AdminUserInfo[]> ListUsers(Ctx ctx)
        => [.. (await users.ListAsync(ctx.Cancel))
            .OrderBy(u => u.Email, StringComparer.Ordinal)
            .Select(u => new AdminUserInfo(u.Id, u.Email, u.DisplayName, u.IsAdmin, u.Disabled, [.. u.Identities.Select(i => i.Method)], u.CreatedAt))];

    /// <summary>Creates an invitation for an email. The administrator delivers the link; nothing is sent.</summary>
    [Call(Access = Access.Admin)]
    public async Task<LinkCreated> CreateInvitation(Ctx ctx, string email, bool isAdmin = false)
    {
        var normalized = EmailAddress.Normalize(email)
            ?? throw CallException.Invalid("Invalid data", new Dictionary<string, string> { ["email"] = "Not a valid email." });
        if (await users.FindByEmailAsync(normalized, ctx.Cancel) != null)
            throw new CallException(409, "already_registered", "That email is already registered");

        var hours = settings.Get(AuthSettings.InvitationHours);
        var (token, expires) = await tokens.CreateAsync(TokenKind.Join, ctx.RequireUser.Id, TimeSpan.FromHours(hours),
            email: normalized, isAdmin: isAdmin, cancel: ctx.Cancel);
        ctx.Detail("email", normalized);
        ctx.Detail("admin", isAdmin ? "true" : "false");
        return new LinkCreated(token, expires);
    }

    /// <summary>Creates a link so the user chooses a new password. Administrators never see or set a password.</summary>
    [Call(Access = Access.Admin)]
    public async Task<LinkCreated> CreatePasswordReset(Ctx ctx, string userId)
    {
        var user = await users.FindByIdAsync(userId, ctx.Cancel) ?? throw CallException.NotFound("Unknown user");
        if (user.Disabled)
            throw CallException.Invalid("The user is disabled");

        var hours = settings.Get(AuthSettings.InvitationHours);
        var (token, expires) = await tokens.CreateAsync(TokenKind.Reset, ctx.RequireUser.Id, TimeSpan.FromHours(hours),
            userId: user.Id, cancel: ctx.Cancel);
        ctx.Detail("user", user.Id);
        return new LinkCreated(token, expires);
    }

    [Call(Access = Access.Admin)]
    public async Task SetDisabled(Ctx ctx, string userId, bool disabled)
    {
        var user = await users.FindByIdAsync(userId, ctx.Cancel) ?? throw CallException.NotFound("Unknown user");
        if (user.Id == ctx.RequireUser.Id && disabled)
            throw CallException.Invalid("You cannot disable yourself");

        user.Disabled = disabled;
        await EnsureAnAdminCanSignInAsync(user, ctx.Cancel);
        await users.UpdateAsync(user, ctx.Cancel);
        if (disabled)
            await sessions.EndAllAsync(user.Id, ctx.Cancel);
        ctx.Detail("user", user.Id);
        ctx.Detail("disabled", disabled ? "true" : "false");
    }

    [Call(Access = Access.Admin)]
    public async Task SetAdmin(Ctx ctx, string userId, bool isAdmin)
    {
        var user = await users.FindByIdAsync(userId, ctx.Cancel) ?? throw CallException.NotFound("Unknown user");
        if (user.Id == ctx.RequireUser.Id && !isAdmin)
            throw CallException.Invalid("You cannot remove your own administrator permission");

        user.IsAdmin = isAdmin;
        await EnsureAnAdminCanSignInAsync(user, ctx.Cancel);
        await users.UpdateAsync(user, ctx.Cancel);
        ctx.Detail("user", user.Id);
        ctx.Detail("admin", isAdmin ? "true" : "false");
    }

    /// <summary>
    /// A change cannot leave the system without an administrator able to sign in. If it already had none (for example
    /// an administrator working from a recovery session), only changes that make it worse are refused.
    /// </summary>
    private async Task EnsureAnAdminCanSignInAsync(User changed, CancellationToken cancel)
    {
        var current = await users.ListAsync(cancel);
        var after = current.Where(u => u.Id != changed.Id).Append(changed);
        var enabled = methods.All.Where(m => settings.Get(m.Enabled)).Select(m => m.Id).ToHashSet();
        if (AdminAccess.AnyAdminCanSignIn(current, enabled) && !AdminAccess.AnyAdminCanSignIn(after, enabled))
            throw CallException.Invalid(AdminAccess.NoAdminMessage);
    }
}

/// <summary>Application level settings. Published as group "settings".</summary>
[CallGroup("settings")]
public sealed class SettingsCalls(SettingsService settings)
{
    [Call(Access = Access.Admin, ReadOnly = true, Verb = Verb.Get)]
    public IReadOnlyList<SettingInfo> List() => settings.Describe();

    [Call(Access = Access.Admin)]
    public Task Set(Ctx ctx, string key, JsonElement value) => settings.SetAsync(ctx, key, value);
}
