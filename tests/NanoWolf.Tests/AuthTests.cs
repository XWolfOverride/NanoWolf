using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XWolf.NanoWolf.Tests;

/// <summary>Signing in with a password, the lock after failures and the sessions.</summary>
public class AuthTests
{
    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<AuthApp> AppWithAliceAsync()
    {
        var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword, name: "Alice");
        return app;
    }

    // ---- signing in ----

    [Fact]
    public async Task The_sign_in_screen_gets_the_active_methods()
    {
        await using var app = await TestAuth.CreateAsync();

        var json = await Browser.JsonAsync(await app.NewBrowser().GetAsync("/rpc/auth/methods"));

        Assert.Equal(["password"], json.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_disabled_method_cannot_be_used_and_is_not_offered()
    {
        await using var app = await TestAuth.CreateAsync(seed => seed.Values["auth.password.enabled"] = "false");
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var browser = app.NewBrowser();

        var methods = await Browser.JsonAsync(await browser.GetAsync("/rpc/auth/methods"));
        var login = await browser.LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        Assert.Equal(0, methods.GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("method_disabled", await Browser.ErrorCodeAsync(login));
    }

    [Fact]
    public async Task Signing_in_returns_the_user_and_opens_a_session()
    {
        await using var app = await AppWithAliceAsync();
        var browser = app.NewBrowser();

        var login = await browser.LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var me = await browser.GetAsync("/rpc/auth/me");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var user = await Browser.JsonAsync(me);
        Assert.Equal(AuthApp.UserEmail, user.GetProperty("email").GetString());
        Assert.Equal("Alice", user.GetProperty("displayName").GetString());
        Assert.False(user.GetProperty("isAdmin").GetBoolean());
        Assert.DoesNotContain("password", await me.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_email_is_case_and_space_insensitive()
    {
        await using var app = await AppWithAliceAsync();

        var response = await app.NewBrowser().LoginAsync("  ALICE@Example.com ", AuthApp.UserPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_session_cookie_is_protected()
    {
        await using var app = await AppWithAliceAsync();

        var response = await app.NewBrowser().LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();

        Assert.StartsWith("__host-nanowolf=", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.Contains("expires=", cookie);
    }

    [Fact]
    public async Task Only_the_hash_of_the_session_token_is_stored()
    {
        await using var app = await AppWithAliceAsync();
        var browser = app.NewBrowser();

        await browser.LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var session = Assert.Single(app.Sessions.All);
        Assert.NotEqual(browser.Cookie, session.Id);
        Assert.Equal(Sha256(browser.Cookie!), session.Id);
        Assert.True(browser.Cookie!.Length >= 43); // 256 bits
        Assert.Equal("password", session.Method);
    }

    [Fact]
    public async Task A_made_up_cookie_is_not_a_session()
    {
        await using var app = await AppWithAliceAsync();
        var browser = app.NewBrowser();
        browser.Cookie = "not-a-real-token";

        var response = await browser.GetAsync("/rpc/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await Browser.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_are_indistinguishable()
    {
        await using var app = await AppWithAliceAsync();
        var browser = app.NewBrowser();

        var wrongPassword = await browser.LoginAsync(AuthApp.UserEmail, "definitely not the password");
        var unknownEmail = await browser.LoginAsync("nobody@example.com", "definitely not the password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownEmail.Content.ReadAsStringAsync());
        Assert.Null(browser.Cookie);
    }

    [Fact]
    public async Task A_disabled_user_cannot_sign_in_and_looks_like_a_wrong_password()
    {
        await using var app = await AppWithAliceAsync();
        var user = (await app.Users.FindByEmailAsync(AuthApp.UserEmail, default))!;
        user.Disabled = true;
        await app.Users.UpdateAsync(user, default);

        var response = await app.NewBrowser().LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        Assert.Equal("invalid_credentials", await Browser.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_outdated_hash_is_upgraded_at_the_next_sign_in()
    {
        await using var app = await TestAuth.CreateAsync();
        var weak = new PasswordService(app.Services.GetRequiredService<SettingsService>(), new NanoWolfOptions { PasswordIterations = 300 });
        var user = await app.SeedUserAsync(AuthApp.UserEmail, "placeholder placeholder");
        user.Identities[0].Data = weak.Hash(AuthApp.UserPassword);
        await app.Users.UpdateAsync(user, default);
        var before = user.Identities[0].Data;

        var first = await app.NewBrowser().LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var after = (await app.Users.FindByEmailAsync(AuthApp.UserEmail, default))!.Identities[0].Data;
        var second = await app.NewBrowser().LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotEqual(before, after);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    // ---- lock after failures ----

    [Fact]
    public async Task Repeated_failures_lock_the_email_even_for_the_right_password()
    {
        await using var app = await AppWithAliceAsync();
        var browser = app.NewBrowser();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync(AuthApp.UserEmail, "wrong " + i)).StatusCode);
        var locked = await browser.LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        var error = (await Browser.JsonAsync(locked)).GetProperty("error");
        Assert.Equal("too_many_attempts", error.GetProperty("code").GetString());
        Assert.True(int.Parse(error.GetProperty("fields").GetProperty("retryAfter").GetString()!) > 0);
    }

    [Fact]
    public async Task The_lock_ends_and_grows_with_more_failures()
    {
        await using var app = await AppWithAliceAsync();
        var browser = app.NewBrowser();
        for (var i = 0; i < 5; i++)
            await browser.LoginAsync(AuthApp.UserEmail, "wrong " + i);

        app.Time.Advance(TimeSpan.FromSeconds(31));
        var afterWait = await browser.LoginAsync(AuthApp.UserEmail, "wrong again"); // 6th failure: the lock doubles
        app.Time.Advance(TimeSpan.FromSeconds(31));
        var stillLocked = await browser.LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        app.Time.Advance(TimeSpan.FromSeconds(31));
        var free = await browser.LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, afterWait.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, stillLocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);
    }

    [Fact]
    public async Task Unknown_emails_get_locked_like_real_ones()
    {
        await using var app = await AppWithAliceAsync();
        var browser = app.NewBrowser();

        for (var i = 0; i < 5; i++)
            await browser.LoginAsync("nobody@example.com", "wrong " + i);
        var response = await browser.LoginAsync("nobody@example.com", "wrong again");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task A_successful_sign_in_clears_the_failures()
    {
        await using var app = await AppWithAliceAsync();
        var browser = app.NewBrowser();

        for (var i = 0; i < 4; i++)
            await browser.LoginAsync(AuthApp.UserEmail, "wrong " + i);
        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword)).StatusCode);
        for (var i = 0; i < 4; i++)
            await browser.LoginAsync(AuthApp.UserEmail, "wrong " + i);

        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword)).StatusCode);
    }

    [Fact]
    public async Task One_address_trying_many_emails_gets_locked()
    {
        await using var app = await AppWithAliceAsync();
        var browser = app.NewBrowser();

        for (var i = 0; i < 20; i++)
            await browser.LoginAsync($"guess{i}@example.com", "wrong");
        var response = await browser.LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Very_long_passwords_are_rejected_without_hashing_them()
    {
        await using var app = await AppWithAliceAsync();

        var response = await app.NewBrowser().LoginAsync(AuthApp.UserEmail, new string('x', 100_000));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- sessions ----

    [Fact]
    public async Task Signing_in_somewhere_else_closes_the_other_session_and_says_so()
    {
        await using var app = await AppWithAliceAsync();
        var laptop = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var phone = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var oldSession = await laptop.GetAsync("/rpc/auth/me");
        var newSession = await phone.GetAsync("/rpc/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        Assert.Equal("session_replaced", await Browser.ErrorCodeAsync(oldSession));
        Assert.Equal(HttpStatusCode.OK, newSession.StatusCode);
    }

    [Fact]
    public async Task The_replaced_session_is_forgotten_after_a_week()
    {
        await using var app = await AppWithAliceAsync();
        var laptop = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        app.Time.Advance(TimeSpan.FromDays(8));
        var response = await laptop.GetAsync("/rpc/auth/me");

        Assert.Equal("unauthenticated", await Browser.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Several_sessions_can_coexist_if_the_setting_allows_it()
    {
        await using var app = await TestAuth.CreateAsync(seed => seed.Values["auth.session.single"] = "false");
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var laptop = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var phone = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/rpc/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/rpc/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Signing_out_removes_the_session_and_the_cookie()
    {
        await using var app = await AppWithAliceAsync();
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var logout = await browser.PostAsync("/rpc/auth/logout");

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Empty(app.Sessions.All);
        Assert.Null(browser.Cookie);
    }

    [Fact]
    public async Task A_session_expires_after_days_without_use()
    {
        await using var app = await AppWithAliceAsync();
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        app.Time.Advance(TimeSpan.FromDays(29));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/rpc/auth/me")).StatusCode);
        app.Time.Advance(TimeSpan.FromDays(31));

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/rpc/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Using_a_session_extends_it_but_never_beyond_the_absolute_maximum()
    {
        await using var app = await AppWithAliceAsync();
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        for (var day = 20; day <= 80; day += 20)
        {
            app.Time.Advance(TimeSpan.FromDays(20));
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/rpc/auth/me")).StatusCode);
        }
        app.Time.Advance(TimeSpan.FromDays(20)); // day 100, used 20 days ago, but the maximum is 90

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/rpc/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_closes_their_sessions()
    {
        await using var app = await AppWithAliceAsync();
        var admin = await app.SignedInAdminAsync();
        var alice = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var id = (await app.Users.FindByEmailAsync(AuthApp.UserEmail, default))!.Id;

        var response = await admin.PostAsync("/rpc/admin/setDisabled", JsonSerializer.Serialize(new { userId = id, disabled = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/rpc/auth/me")).StatusCode);
        Assert.DoesNotContain(app.Sessions.All, s => s.UserId == id);
    }

    [Fact]
    public async Task A_user_disabled_behind_the_back_of_the_session_is_still_refused()
    {
        await using var app = await AppWithAliceAsync();
        var alice = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var user = (await app.Users.FindByEmailAsync(AuthApp.UserEmail, default))!;
        user.Disabled = true;
        await app.Users.UpdateAsync(user, default);

        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/rpc/auth/me")).StatusCode);
    }

    [Fact]
    public async Task The_session_works_for_any_protected_call()
    {
        await using var app = await AppWithAliceAsync();
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var response = await browser.GetAsync("/rpc/items/who");

        Assert.Equal("Alice", (await Browser.JsonAsync(response)).GetString());
    }
}
