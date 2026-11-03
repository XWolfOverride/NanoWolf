namespace XWolf.NanoWolf;

/// <summary>Calls to sign in and out, and to accept invitations. Published as group "auth".</summary>
[CallGroup("auth")]
public sealed class AuthCalls(
    IUserStore users,
    SettingsService settings,
    PasswordService passwords,
    SessionService sessions,
    ThrottleService throttle,
    TokenService tokens,
    LoginMethods methods)
{
    private const string BadCredentials = "Incorrect email or password";
    private const int MaxPasswordInput = 1024;

    /// <summary>Methods the sign in screen can offer.</summary>
    [Call(Access = Access.Anonymous, ReadOnly = true, Verb = Verb.Get)]
    public string[] Methods()
        => [.. methods.All.Where(m => settings.Get(m.Enabled)).Select(m => m.Id)];

    [Call(Access = Access.User, ReadOnly = true, Verb = Verb.Get)]
    public async Task<UserInfo> Me(Ctx ctx)
        => Info(await CurrentUserAsync(ctx));

    [Call(Access = Access.Anonymous)]
    public async Task<UserInfo> Login(Ctx ctx, string email, string password)
    {
        ctx.Detail("method", LoginMethods.Password);
        ctx.Detail("ip", ctx.Ip ?? "");
        RequirePasswordMethod();

        var normalized = EmailAddress.Normalize(email) ?? Truncate((email ?? "").Trim().ToLowerInvariant());
        var emailKey = ThrottleService.EmailKey(normalized);
        var ipKey = ThrottleService.IpKey(ctx.Ip);

        var wait = await throttle.LockedForAsync(ctx.Cancel, emailKey, ipKey);
        if (wait != null)
            throw TooManyAttempts(wait.Value);

        var user = EmailAddress.Normalize(email) is { } valid ? await users.FindByEmailAsync(valid, ctx.Cancel) : null;
        var identity = user?.Identities.FirstOrDefault(i => i.Method == LoginMethods.Password);

        var verified = false;
        var needsRehash = false;
        if (user == null || identity?.Data == null || user.Disabled || password.Length > MaxPasswordInput)
            passwords.SpendTime(password.Length > MaxPasswordInput ? "" : password);
        else
            verified = passwords.Verify(identity.Data, password, out needsRehash);

        if (!verified)
        {
            await throttle.FailEmailAsync(normalized, ctx.Cancel);
            await throttle.FailIpAsync(ctx.Ip, ctx.Cancel);
            throw new CallException(401, "invalid_credentials", BadCredentials);
        }

        if (needsRehash)
        {
            identity!.Data = passwords.Hash(password);
            await users.UpdateAsync(user!, ctx.Cancel);
        }

        await throttle.ResetAsync(emailKey, ctx.Cancel);
        await sessions.StartAsync(user!, LoginMethods.Password, ctx);
        ctx.Detail("user", user!.Id);
        return Info(user);
    }

    [Call(Access = Access.User)]
    public async Task Logout(Ctx ctx) => await sessions.EndAsync(ctx);

    /// <summary>What a link is for, so the client shows the right screen. Unknown, used or expired: not found.</summary>
    [Call(Access = Access.Anonymous, ReadOnly = true, Verb = Verb.Get)]
    public async Task<LinkInfo> InspectLink(Ctx ctx, string token)
    {
        var record = await tokens.PeekAsync(token, ctx.Cancel, TokenKind.Join, TokenKind.Reset);
        if (record == null)
            throw CallException.NotFound("The link is not valid or has expired");
        var email = record.Email;
        if (record.Kind == TokenKind.Reset)
            email = (await users.FindByIdAsync(record.UserId ?? "", ctx.Cancel))?.Email;
        return new LinkInfo(record.Kind == TokenKind.Join ? "join" : "reset", email ?? "");
    }

    /// <summary>Creates the user of an invitation with a password and signs them in.</summary>
    [Call(Access = Access.Anonymous)]
    public async Task<UserInfo> AcceptInvitation(Ctx ctx, string token, string displayName, string password)
    {
        RequirePasswordMethod();
        ctx.Detail("ip", ctx.Ip ?? "");

        var invitation = await tokens.PeekAsync(token, ctx.Cancel, TokenKind.Join);
        if (invitation == null)
            throw CallException.NotFound("The link is not valid or has expired");

        var name = (displayName ?? "").Trim();
        var fields = new Dictionary<string, string>();
        if (name.Length is 0 or > 80)
            fields["displayName"] = "Between 1 and 80 characters.";
        if (passwords.Validate(password, invitation.Email!) is { } problem)
            fields["password"] = problem;
        if (fields.Count > 0)
            throw CallException.Invalid("Invalid data", fields);

        // The link is consumed before creating the user, so it can never be used twice.
        if (await tokens.ConsumeAsync(token, TokenKind.Join, ctx.Cancel) == null)
            throw CallException.NotFound("The link is not valid or has expired");

        var user = new User
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = invitation.Email!,
            DisplayName = name,
            IsAdmin = invitation.IsAdmin,
            CreatedAt = ctx.Now,
            Identities = [new Identity { Method = LoginMethods.Password, Subject = invitation.Email!, Data = passwords.Hash(password) }],
        };
        try
        {
            await users.CreateAsync(user, ctx.Cancel);
        }
        catch (DuplicateException)
        {
            throw new CallException(409, "already_registered", "That email is already registered");
        }

        await sessions.StartAsync(user, LoginMethods.Password, ctx);
        ctx.Detail("user", user.Id);
        return Info(user);
    }

    /// <summary>Sets a new password with a reset link, closes every other session and signs the user in.</summary>
    [Call(Access = Access.Anonymous)]
    public async Task<UserInfo> ResetPassword(Ctx ctx, string token, string password)
    {
        RequirePasswordMethod();
        ctx.Detail("ip", ctx.Ip ?? "");

        var reset = await tokens.PeekAsync(token, ctx.Cancel, TokenKind.Reset);
        var user = reset == null ? null : await users.FindByIdAsync(reset.UserId ?? "", ctx.Cancel);
        if (user == null || user.Disabled)
            throw CallException.NotFound("The link is not valid or has expired");

        if (passwords.Validate(password, user.Email) is { } problem)
            throw CallException.Invalid("Invalid data", new Dictionary<string, string> { ["password"] = problem });

        if (await tokens.ConsumeAsync(token, TokenKind.Reset, ctx.Cancel) == null)
            throw CallException.NotFound("The link is not valid or has expired");

        SetPassword(user, password);
        await users.UpdateAsync(user, ctx.Cancel);
        await throttle.ResetAsync(ThrottleService.EmailKey(user.Email), ctx.Cancel);
        await sessions.EndAllAsync(user.Id, ctx.Cancel);
        await sessions.StartAsync(user, LoginMethods.Password, ctx);
        ctx.Detail("user", user.Id);
        return Info(user);
    }

    /// <summary>Changes the password of the current user and closes every other session.</summary>
    [Call(Access = Access.User)]
    public async Task ChangePassword(Ctx ctx, string current, string password)
    {
        RequirePasswordMethod();
        var user = await CurrentUserAsync(ctx);
        var emailKey = ThrottleService.EmailKey(user.Email);

        var wait = await throttle.LockedForAsync(ctx.Cancel, emailKey);
        if (wait != null)
            throw TooManyAttempts(wait.Value);

        var identity = user.Identities.FirstOrDefault(i => i.Method == LoginMethods.Password);
        if (identity?.Data == null || current.Length > MaxPasswordInput || !passwords.Verify(identity.Data, current, out _))
        {
            await throttle.FailEmailAsync(user.Email, ctx.Cancel);
            throw CallException.Invalid("Invalid data", new Dictionary<string, string> { ["current"] = "Incorrect password" });
        }
        if (passwords.Validate(password, user.Email) is { } problem)
            throw CallException.Invalid("Invalid data", new Dictionary<string, string> { ["password"] = problem });

        SetPassword(user, password);
        await users.UpdateAsync(user, ctx.Cancel);
        await throttle.ResetAsync(emailKey, ctx.Cancel);
        await sessions.EndAllAsync(user.Id, ctx.Cancel);
        await sessions.StartAsync(user, LoginMethods.Password, ctx);
    }

    /// <summary>
    /// Opens an administrator session with a recovery code generated from the console. Works even if no sign in
    /// method is enabled; that is what it is for.
    /// </summary>
    [Call(Access = Access.Anonymous)]
    public async Task<UserInfo> Recover(Ctx ctx, string code)
    {
        ctx.Detail("method", "recovery");
        ctx.Detail("ip", ctx.Ip ?? "");

        var ipKey = ThrottleService.IpKey(ctx.Ip);
        var wait = await throttle.LockedForAsync(ctx.Cancel, ipKey);
        if (wait != null)
            throw TooManyAttempts(wait.Value);

        var record = await tokens.ConsumeAsync(code, TokenKind.Recovery, ctx.Cancel);
        var user = record == null ? null : await users.FindByIdAsync(record.UserId ?? "", ctx.Cancel);
        if (user == null || user.Disabled || !user.IsAdmin)
        {
            await throttle.FailIpAsync(ctx.Ip, ctx.Cancel);
            throw new CallException(401, "invalid_credentials", "The code is not valid or has expired");
        }

        await sessions.StartAsync(user, "recovery", ctx);
        ctx.Detail("user", user.Id);
        return Info(user);
    }

    // ---- helpers ----

    private void RequirePasswordMethod()
    {
        if (!settings.Get(AuthSettings.PasswordEnabled))
            throw new CallException(403, "method_disabled", "Signing in with a password is not enabled");
    }

    private void SetPassword(User user, string password)
    {
        var hash = passwords.Hash(password);
        var identity = user.Identities.FirstOrDefault(i => i.Method == LoginMethods.Password);
        if (identity == null)
            user.Identities.Add(new Identity { Method = LoginMethods.Password, Subject = user.Email, Data = hash });
        else
            identity.Data = hash;
    }

    private async Task<User> CurrentUserAsync(Ctx ctx)
        => await users.FindByIdAsync(ctx.RequireUser.Id, ctx.Cancel) ?? throw CallException.Unauthenticated();

    private static UserInfo Info(User user) => new(user.Id, user.Email, user.DisplayName, user.IsAdmin);

    private static string Truncate(string value) => value.Length > 100 ? value[..100] : value;

    private static CallException TooManyAttempts(TimeSpan wait)
    {
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        return new CallException(429, "too_many_attempts", "Too many attempts, try again later",
            new Dictionary<string, string> { ["retryAfter"] = seconds.ToString() });
    }
}

/// <summary>What a single use link is for.</summary>
public sealed record LinkInfo(string Kind, string Email);
