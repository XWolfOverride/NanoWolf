using System.Net;
using System.Text.Json;

namespace XWolf.NanoWolf.Tests;

/// <summary>Invitations, passwords, administration, settings, recovery and the log.</summary>
public class AccountTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task<string> InviteAsync(Browser admin, string email, bool isAdmin = false)
    {
        var response = await admin.PostAsync("/rpc/admin/createInvitation", Json(new { email, isAdmin }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Browser.JsonAsync(response)).GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> AcceptAsync(Browser browser, string token, string password, string displayName = "Bob")
        => browser.PostAsync("/rpc/auth/acceptInvitation", Json(new { token, displayName, password }));

    private static async Task<(string Error, JsonElement Fields)> InvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var error = (await Browser.JsonAsync(response)).GetProperty("error");
        return (error.GetProperty("code").GetString()!, error.GetProperty("fields"));
    }

    // ---- invitations ----

    [Fact]
    public async Task An_invited_person_chooses_a_password_and_is_signed_in()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob@example.com");
        var guest = app.NewBrowser();

        var link = await Browser.JsonAsync(await guest.GetAsync("/rpc/auth/inspectLink?token=" + token));
        var accept = await AcceptAsync(guest, token, "a long password for Bob");
        var me = await Browser.JsonAsync(await guest.GetAsync("/rpc/auth/me"));

        Assert.Equal("join", link.GetProperty("kind").GetString());
        Assert.Equal("bob@example.com", link.GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal("bob@example.com", me.GetProperty("email").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await app.NewBrowser().LoginAsync("bob@example.com", "a long password for Bob")).StatusCode);
    }

    [Fact]
    public async Task An_invitation_can_create_an_administrator()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "second@example.com", isAdmin: true);
        var guest = app.NewBrowser();

        await AcceptAsync(guest, token, "a long password for the second admin");

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/rpc/admin/listUsers")).StatusCode);
    }

    [Fact]
    public async Task An_invitation_works_once()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob@example.com");

        var first = await AcceptAsync(app.NewBrowser(), token, "a long password for Bob");
        var second = await AcceptAsync(app.NewBrowser(), token, "another long password");
        var inspect = await app.NewBrowser().GetAsync("/rpc/auth/inspectLink?token=" + token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inspect.StatusCode);
    }

    [Fact]
    public async Task An_invitation_expires()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob@example.com");

        app.Time.Advance(TimeSpan.FromHours(73));
        var response = await AcceptAsync(app.NewBrowser(), token, "a long password for Bob");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_hash_of_the_invitation_is_stored()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();

        var token = await InviteAsync(admin, "bob@example.com");

        var record = Assert.Single(app.Tokens.All);
        Assert.NotEqual(token, record.Id);
        Assert.Equal(64, record.Id.Length);
    }

    [Fact]
    public async Task Invitations_are_bound_to_a_normalized_email()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "  Bob@Example.COM ");

        var link = await Browser.JsonAsync(await app.NewBrowser().GetAsync("/rpc/auth/inspectLink?token=" + token));

        Assert.Equal("bob@example.com", link.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Invitations_for_registered_or_invalid_emails_are_refused()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();

        var registered = await admin.PostAsync("/rpc/admin/createInvitation", Json(new { email = AuthApp.AdminEmail }));
        var invalid = await admin.PostAsync("/rpc/admin/createInvitation", Json(new { email = "not an email" }));

        Assert.Equal(HttpStatusCode.Conflict, registered.StatusCode);
        Assert.Equal("already_registered", await Browser.ErrorCodeAsync(registered));
        Assert.Equal("Not a valid email.", (await InvalidAsync(invalid)).Fields.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Only_administrators_can_invite()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var user = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var asUser = await user.PostAsync("/rpc/admin/createInvitation", Json(new { email = "bob@example.com" }));
        var anonymous = await app.NewBrowser().PostAsync("/rpc/admin/createInvitation", Json(new { email = "bob@example.com" }));

        Assert.Equal(HttpStatusCode.Forbidden, asUser.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Links_that_are_not_invitations_or_resets_cannot_be_inspected()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SignedInAdminAsync();
        var output = new StringWriter();
        await NanoWolfCommands.TryRunAsync(["recovery", AuthApp.AdminEmail], app.Services, output);
        var code = output.ToString().Split("/#/recover/")[1].Trim();

        var bogus = await app.NewBrowser().GetAsync("/rpc/auth/inspectLink?token=bogus");
        var recovery = await app.NewBrowser().GetAsync("/rpc/auth/inspectLink?token=" + code);

        Assert.Equal(HttpStatusCode.NotFound, bogus.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, recovery.StatusCode);
    }

    // ---- password policy ----

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public async Task The_password_length_is_between_10_and_256(int length, bool accepted)
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob@example.com");

        var response = await AcceptAsync(app.NewBrowser(), token, new string('x', length));

        Assert.Equal(accepted, response.IsSuccessStatusCode);
        if (!accepted)
            Assert.Contains("password", (await InvalidAsync(response)).Fields.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_refused_password_does_not_use_up_the_invitation()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob@example.com");
        var guest = app.NewBrowser();

        var tooShort = await AcceptAsync(guest, token, "short");
        var retry = await AcceptAsync(guest, token, "a long enough password");

        Assert.Equal((HttpStatusCode)422, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task The_password_cannot_be_the_email_and_the_name_is_required()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob.the.builder@example.com");

        var response = await AcceptAsync(app.NewBrowser(), token, "Bob.The.Builder@example.com", displayName: "   ");

        var (_, fields) = await InvalidAsync(response);
        Assert.Equal("The password cannot be the email.", fields.GetProperty("password").GetString());
        Assert.True(fields.TryGetProperty("displayName", out _));
    }

    [Fact]
    public async Task Spaces_are_part_of_the_password()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob@example.com");
        await AcceptAsync(app.NewBrowser(), token, "  spaced out phrase  ");

        var exact = await app.NewBrowser().LoginAsync("bob@example.com", "  spaced out phrase  ");
        var trimmed = await app.NewBrowser().LoginAsync("bob@example.com", "spaced out phrase");

        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, trimmed.StatusCode);
    }

    [Fact]
    public async Task The_same_text_typed_in_different_unicode_forms_is_the_same_password()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob@example.com");
        await AcceptAsync(app.NewBrowser(), token, "café con leche 123"); // é as one character

        var decomposed = await app.NewBrowser().LoginAsync("bob@example.com", "café con leche 123"); // e + accent

        Assert.Equal(HttpStatusCode.OK, decomposed.StatusCode);
    }

    // ---- resetting and changing passwords ----

    [Fact]
    public async Task An_administrator_gives_a_link_and_the_user_chooses_a_new_password()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var user = await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var oldSession = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var created = await admin.PostAsync("/rpc/admin/createPasswordReset", Json(new { userId = user.Id }));
        var token = (await Browser.JsonAsync(created)).GetProperty("token").GetString()!;
        var forgetful = app.NewBrowser();
        var link = await Browser.JsonAsync(await forgetful.GetAsync("/rpc/auth/inspectLink?token=" + token));
        var reset = await forgetful.PostAsync("/rpc/auth/resetPassword", Json(new { token, password = "my brand new password" }));

        Assert.Equal("reset", link.GetProperty("kind").GetString());
        Assert.Equal(AuthApp.UserEmail, link.GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await forgetful.GetAsync("/rpc/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/rpc/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewBrowser().LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.NewBrowser().LoginAsync(AuthApp.UserEmail, "my brand new password")).StatusCode);
    }

    [Fact]
    public async Task A_reset_link_works_once_and_not_for_disabled_users()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var user = await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var created = await admin.PostAsync("/rpc/admin/createPasswordReset", Json(new { userId = user.Id }));
        var token = (await Browser.JsonAsync(created)).GetProperty("token").GetString()!;

        var first = await app.NewBrowser().PostAsync("/rpc/auth/resetPassword", Json(new { token, password = "my brand new password" }));
        var second = await app.NewBrowser().PostAsync("/rpc/auth/resetPassword", Json(new { token, password = "yet another password" }));
        await admin.PostAsync("/rpc/admin/setDisabled", Json(new { userId = user.Id, disabled = true }));
        var forDisabled = await admin.PostAsync("/rpc/admin/createPasswordReset", Json(new { userId = user.Id }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal((HttpStatusCode)422, forDisabled.StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_closes_other_sessions()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var phone = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var laptop = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword); // phone is replaced

        var wrong = await laptop.PostAsync("/rpc/auth/changePassword", Json(new { current = "not it", password = "my brand new password" }));
        var changed = await laptop.PostAsync("/rpc/auth/changePassword", Json(new { current = AuthApp.UserPassword, password = "my brand new password" }));

        Assert.Equal("Incorrect password", (await InvalidAsync(wrong)).Fields.GetProperty("current").GetString());
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/rpc/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/rpc/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.NewBrowser().LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword)).StatusCode);
    }

    [Fact]
    public async Task Guessing_the_current_password_is_limited_too()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        for (var i = 0; i < 5; i++)
            await browser.PostAsync("/rpc/auth/changePassword", Json(new { current = "wrong " + i, password = "my brand new password" }));
        var response = await browser.PostAsync("/rpc/auth/changePassword", Json(new { current = AuthApp.UserPassword, password = "my brand new password" }));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // ---- administration ----

    [Fact]
    public async Task The_user_list_never_includes_secrets()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var response = await admin.GetAsync("/rpc/admin/listUsers");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains(AuthApp.UserEmail, text);
        Assert.DoesNotContain("AQAAAA", text); // the start of any PBKDF2 hash of the platform
        Assert.DoesNotContain("\"data\"", text);
    }

    [Fact]
    public async Task Administrators_cannot_disable_or_demote_themselves()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var id = (await app.Users.FindByEmailAsync(AuthApp.AdminEmail, default))!.Id;

        var disable = await admin.PostAsync("/rpc/admin/setDisabled", Json(new { userId = id, disabled = true }));
        var demote = await admin.PostAsync("/rpc/admin/setAdmin", Json(new { userId = id, isAdmin = false }));

        Assert.Equal((HttpStatusCode)422, disable.StatusCode);
        Assert.Equal((HttpStatusCode)422, demote.StatusCode);
    }

    [Fact]
    public async Task An_administrator_can_promote_a_user()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var user = await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var alice = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var before = await alice.GetAsync("/rpc/admin/listUsers");
        await admin.PostAsync("/rpc/admin/setAdmin", Json(new { userId = user.Id, isAdmin = true }));
        var after = await alice.GetAsync("/rpc/admin/listUsers");

        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    // ---- settings ----

    [Fact]
    public async Task Administrators_see_and_change_settings_and_the_change_applies_at_once()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob@example.com");

        var list = await Browser.JsonAsync(await admin.GetAsync("/rpc/settings/list"));
        var set = await admin.PostAsync("/rpc/settings/set", Json(new { key = "auth.password.minLength", value = 12 }));
        var eleven = await AcceptAsync(app.NewBrowser(), token, new string('x', 11));

        var minLength = list.EnumerateArray().Single(s => s.GetProperty("key").GetString() == "auth.password.minLength");
        Assert.Equal(10, minLength.GetProperty("value").GetInt32());
        Assert.Equal("int", minLength.GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal((HttpStatusCode)422, eleven.StatusCode);
        Assert.Equal("12", app.SettingsStore.Values["auth.password.minLength"]);
    }

    [Fact]
    public async Task Settings_are_validated()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();

        var outOfRange = await admin.PostAsync("/rpc/settings/set", Json(new { key = "auth.password.minLength", value = 3 }));
        var wrongType = await admin.PostAsync("/rpc/settings/set", Json(new { key = "auth.password.minLength", value = "twelve" }));
        var unknown = await admin.PostAsync("/rpc/settings/set", Json(new { key = "nope", value = 1 }));

        Assert.Equal("Must be between 8 and 64.", (await InvalidAsync(outOfRange)).Fields.GetProperty("value").GetString());
        Assert.Contains("whole number", (await InvalidAsync(wrongType)).Fields.GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(app.SettingsStore.Values);
    }

    [Fact]
    public async Task Only_administrators_touch_settings()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var alice = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var list = await alice.GetAsync("/rpc/settings/list");
        var set = await alice.PostAsync("/rpc/settings/set", Json(new { key = "auth.password.minLength", value = 12 }));

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, set.StatusCode);
    }

    [Fact]
    public async Task Stored_settings_that_are_invalid_or_unknown_are_ignored()
    {
        await using var app = await TestAuth.CreateAsync(seed =>
        {
            seed.Values["auth.password.minLength"] = "3";
            seed.Values["old.setting"] = "1";
            seed.Values["auth.lockout.threshold"] = "not json";
            seed.Values["auth.session.idleDays"] = "7";
        });
        var admin = await app.SignedInAdminAsync();

        var list = await Browser.JsonAsync(await admin.GetAsync("/rpc/settings/list"));

        int Value(string key) => list.EnumerateArray().Single(s => s.GetProperty("key").GetString() == key).GetProperty("value").GetInt32();
        Assert.Equal(10, Value("auth.password.minLength"));
        Assert.Equal(5, Value("auth.lockout.threshold"));
        Assert.Equal(7, Value("auth.session.idleDays"));
    }

    [Fact]
    public async Task No_change_can_leave_the_system_without_an_administrator_able_to_sign_in()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();

        var response = await admin.PostAsync("/rpc/settings/set", Json(new { key = "auth.password.enabled", value = false }));
        var methods = await Browser.JsonAsync(await app.NewBrowser().GetAsync("/rpc/auth/methods"));

        var message = (await Browser.JsonAsync(response)).GetProperty("error").GetProperty("message").GetString();
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Contains("no administrator", message);
        Assert.Equal(["password"], methods.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Changes_to_settings_are_logged_with_the_old_and_new_value()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();

        await admin.PostAsync("/rpc/settings/set", Json(new { key = "auth.lockout.threshold", value = 7 }));

        var entry = app.Log.Entries.Single(e => e.Call == "settings.set");
        Assert.True(entry.Ok);
        Assert.Equal("auth.lockout.threshold", entry.Details["setting"]);
        Assert.Equal("5", entry.Details["old"]);
        Assert.Equal("7", entry.Details["new"]);
    }

    // ---- console commands and recovery ----

    private static async Task<string> RunAsync(AuthApp app, params string[] args)
    {
        var output = new StringWriter();
        Assert.True(await NanoWolfCommands.TryRunAsync(args, app.Services, output));
        return output.ToString();
    }

    private static string TokenIn(string output, string route) => output.Split(route)[1].Split('\n')[0].Trim();

    [Fact]
    public async Task The_first_administrator_is_created_from_the_console()
    {
        await using var app = await TestAuth.CreateAsync();

        var output = await RunAsync(app, "invite", "Root@Example.com", "--admin");
        var token = TokenIn(output, "https://kno.example.com/#/join/");
        var guest = app.NewBrowser();
        var accept = await AcceptAsync(guest, token, "a long password for root", "Root");

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/rpc/admin/listUsers")).StatusCode);
        Assert.Contains(app.Log.Entries, e => e.Call == "console.invite" && e.Details["email"] == "root@example.com");
    }

    [Fact]
    public async Task Console_commands_explain_what_cannot_be_done()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        Assert.Contains("already registered", await RunAsync(app, "invite", AuthApp.UserEmail));
        Assert.Contains("not an enabled administrator", await RunAsync(app, "recovery", AuthApp.UserEmail));
        Assert.Contains("not an enabled administrator", await RunAsync(app, "recovery", "nobody@example.com"));
        Assert.Contains("An email is required", await RunAsync(app, "invite"));
        Assert.Empty(app.Tokens.All);
        Assert.False(await NanoWolfCommands.TryRunAsync(["serve"], app.Services, new StringWriter()));
        Assert.False(await NanoWolfCommands.TryRunAsync([], app.Services, new StringWriter()));
    }

    [Fact]
    public async Task A_recovery_code_opens_an_administrator_session_even_without_any_method()
    {
        await using var app = await TestAuth.CreateAsync(seed => seed.Values["auth.password.enabled"] = "false");
        await app.SeedUserAsync(AuthApp.AdminEmail, AuthApp.AdminPassword, admin: true, name: "Root");
        var code = TokenIn(await RunAsync(app, "recovery", AuthApp.AdminEmail), "/#/recover/");
        var browser = app.NewBrowser();

        var recover = await browser.PostAsync("/rpc/auth/recover", Json(new { code }));
        var fix = await browser.PostAsync("/rpc/settings/set", Json(new { key = "auth.password.enabled", value = true }));
        var methods = await Browser.JsonAsync(await app.NewBrowser().GetAsync("/rpc/auth/methods"));

        Assert.Equal(HttpStatusCode.OK, recover.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
        Assert.Equal(["password"], methods.EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(app.Sessions.All, s => s.Method == "recovery");
        Assert.Contains(app.Log.Entries, e => e.Call == "auth.recover" && e.Details["method"] == "recovery" && e.Ok);
    }

    [Fact]
    public async Task A_recovery_code_works_once_and_expires_quickly()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.AdminEmail, AuthApp.AdminPassword, admin: true);
        var used = TokenIn(await RunAsync(app, "recovery", AuthApp.AdminEmail), "/#/recover/");
        var late = TokenIn(await RunAsync(app, "recovery", AuthApp.AdminEmail), "/#/recover/");

        var first = await app.NewBrowser().PostAsync("/rpc/auth/recover", Json(new { code = used }));
        var second = await app.NewBrowser().PostAsync("/rpc/auth/recover", Json(new { code = used }));
        app.Time.Advance(TimeSpan.FromMinutes(16));
        var expired = await app.NewBrowser().PostAsync("/rpc/auth/recover", Json(new { code = late }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task Guessing_recovery_codes_gets_the_address_locked()
    {
        await using var app = await TestAuth.CreateAsync();
        var browser = app.NewBrowser();

        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/rpc/auth/recover", Json(new { code = "guess" + i }))).StatusCode);
        var response = await browser.PostAsync("/rpc/auth/recover", Json(new { code = "guess" }));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task A_recovery_code_of_a_user_who_lost_administrator_rights_is_useless()
    {
        await using var app = await TestAuth.CreateAsync();
        var root = await app.SeedUserAsync(AuthApp.AdminEmail, AuthApp.AdminPassword, admin: true);
        var code = TokenIn(await RunAsync(app, "recovery", AuthApp.AdminEmail), "/#/recover/");
        root.IsAdmin = false;
        await app.Users.UpdateAsync(root, default);

        var response = await app.NewBrowser().PostAsync("/rpc/auth/recover", Json(new { code }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_with_no_way_to_sign_in_gets_one_with_a_reset_link_from_the_console()
    {
        await using var app = await TestAuth.CreateAsync();
        // The state after the items of the old application were assigned to an owner: an administrator without a password.
        await app.Users.CreateAsync(new User { Id = "u-owner", Email = "owner@example.com", DisplayName = "Owner", IsAdmin = true, CreatedAt = app.Time.GetUtcNow() }, default);

        var token = TokenIn(await RunAsync(app, "reset", "Owner@Example.com"), "/#/reset/");
        var guest = app.NewBrowser();
        var link = await Browser.JsonAsync(await guest.GetAsync("/rpc/auth/inspectLink?token=" + token));
        var reset = await guest.PostAsync("/rpc/auth/resetPassword", Json(new { token, password = "my brand new password" }));

        Assert.Equal("reset", link.GetProperty("kind").GetString());
        Assert.Equal("owner@example.com", link.GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var login = await app.NewBrowser().LoginAsync("owner@example.com", "my brand new password");
        Assert.True((await Browser.JsonAsync(login)).GetProperty("isAdmin").GetBoolean());
        Assert.Contains(app.Log.Entries, e => e.Call == "console.reset" && e.Details["user"] == "u-owner");
    }

    [Fact]
    public async Task A_reset_link_from_the_console_needs_an_existing_enabled_user()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.Users.CreateAsync(new User { Id = "u-off", Email = "off@example.com", DisplayName = "Off", Disabled = true, CreatedAt = app.Time.GetUtcNow() }, default);

        Assert.Contains("not an enabled user", await RunAsync(app, "reset", "nobody@example.com"));
        Assert.Contains("not an enabled user", await RunAsync(app, "reset", "off@example.com"));
        Assert.Contains("An email is required", await RunAsync(app, "reset"));
        Assert.Empty(app.Tokens.All);
    }

    // ---- log ----

    [Fact]
    public async Task The_log_records_what_happened_but_never_secrets()
    {
        await using var app = await TestAuth.CreateAsync();
        var admin = await app.SignedInAdminAsync();
        var token = await InviteAsync(admin, "bob@example.com");
        var guest = app.NewBrowser();
        await AcceptAsync(guest, token, "a long password for Bob");
        await app.NewBrowser().LoginAsync("bob@example.com", "the wrong password");

        var entries = app.Log.Entries.ToList();
        var text = JsonSerializer.Serialize(entries);

        Assert.Contains(entries, e => e.Call == "admin.createInvitation" && e.Ok && e.Details["email"] == "bob@example.com");
        Assert.Contains(entries, e => e.Call == "auth.acceptInvitation" && e.Ok && e.Details.ContainsKey("user"));
        Assert.Contains(entries, e => e.Call == "auth.login" && !e.Ok && e.ErrorCode == "invalid_credentials");
        Assert.DoesNotContain("a long password for Bob", text);
        Assert.DoesNotContain("the wrong password", text);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(guest.Cookie!, text);
        Assert.DoesNotContain(AuthApp.AdminPassword, text);
    }
}
