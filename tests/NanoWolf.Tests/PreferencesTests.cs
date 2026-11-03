using System.Net;
using System.Text.Json;

namespace XWolf.NanoWolf.Tests;

/// <summary>Personal choices such as the accent color: validated, per user, and never able to store anything else.</summary>
public class PreferencesTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task<Dictionary<string, string>> PreferencesOfAsync(Browser browser)
    {
        var response = await browser.GetAsync("/rpc/account/preferences");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Browser.JsonAsync(response)).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    private static Task<HttpResponseMessage> SetAsync(Browser browser, string key, string value)
        => browser.PostAsync("/rpc/account/setPreference", Json(new { key, value }));

    [Fact]
    public async Task A_new_user_has_the_default_accent()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var preferences = await PreferencesOfAsync(browser);

        Assert.Equal("#ffd95a", preferences["accent"]);
    }

    [Fact]
    public async Task A_chosen_accent_is_tidied_stored_and_given_back()
    {
        await using var app = await TestAuth.CreateAsync();
        var user = await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var response = await SetAsync(browser, "accent", "#62B8FF");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("#62b8ff", (await PreferencesOfAsync(browser))["accent"]);
        Assert.Equal("#62b8ff", (await app.Users.FindByIdAsync(user.Id, default))!.Preferences["accent"]);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("ffd95a")]
    [InlineData("#gggggg")]
    [InlineData("#ffd95a ")]
    [InlineData("#ffd95aff")]
    [InlineData("#ffd95a;background:url(//evil)")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task Only_a_color_as_rrggbb_is_accepted(string value)
    {
        await using var app = await TestAuth.CreateAsync();
        var user = await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var response = await SetAsync(browser, "accent", value);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True((await Browser.JsonAsync(response)).GetProperty("error").GetProperty("fields").TryGetProperty("value", out _));
        Assert.Empty((await app.Users.FindByIdAsync(user.Id, default))!.Preferences);
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("Accent")]
    [InlineData("a.b")]
    [InlineData("$set")]
    [InlineData("__proto__")]
    [InlineData("")]
    public async Task Only_declared_preferences_can_be_stored(string key)
    {
        await using var app = await TestAuth.CreateAsync();
        var user = await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var response = await SetAsync(browser, key, "#62b8ff");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.True((await Browser.JsonAsync(response)).GetProperty("error").GetProperty("fields").TryGetProperty("key", out _));
        Assert.Empty((await app.Users.FindByIdAsync(user.Id, default))!.Preferences);
    }

    [Fact]
    public async Task Everybody_has_their_own_preferences()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        await app.SeedUserAsync("bob@example.com", "bobs long password");
        var alice = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var bob = await app.SignedInAsync("bob@example.com", "bobs long password");

        await SetAsync(alice, "accent", "#ff7eb6");

        Assert.Equal("#ff7eb6", (await PreferencesOfAsync(alice))["accent"]);
        Assert.Equal("#ffd95a", (await PreferencesOfAsync(bob))["accent"]);
    }

    [Fact]
    public async Task Preferences_need_a_signed_in_user()
    {
        await using var app = await TestAuth.CreateAsync();
        var anonymous = app.NewBrowser();

        var read = await anonymous.GetAsync("/rpc/account/preferences");
        var write = await SetAsync(anonymous, "accent", "#62b8ff");

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Fact]
    public async Task A_stored_value_that_is_not_valid_or_not_declared_never_reaches_the_client()
    {
        await using var app = await TestAuth.CreateAsync();
        var user = await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        await app.Users.SetPreferenceAsync(user.Id, "accent", "url(//evil)", default); // for example from an older version of the rules
        await app.Users.SetPreferenceAsync(user.Id, "legacy", "<script>", default);
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var preferences = await PreferencesOfAsync(browser);

        Assert.Equal("#ffd95a", preferences["accent"]);
        Assert.False(preferences.ContainsKey("legacy"));
    }

    [Fact]
    public async Task Changing_a_preference_does_not_touch_the_rest_of_the_account()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword, admin: true, name: "Alice");
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        await SetAsync(browser, "accent", "#7bd88f");
        var me = await Browser.JsonAsync(await browser.GetAsync("/rpc/auth/me"));
        var login = await app.NewBrowser().LoginAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("Alice", me.GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task The_log_says_which_preference_changed_but_not_the_value()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        await SetAsync(browser, "accent", "#62b8ff");
        await SetAsync(browser, "accent", "not a color");

        var entries = app.Log.Entries.Where(e => e.Call == "account.setPreference").ToList();
        Assert.Equal([true, false], entries.Select(e => e.Ok));
        Assert.Equal("accent", entries[0].Details["preference"]);
        Assert.DoesNotContain("62b8ff", JsonSerializer.Serialize(entries));
        Assert.DoesNotContain("not a color", JsonSerializer.Serialize(entries));
    }

    [Fact]
    public async Task The_catalog_publishes_the_calls()
    {
        await using var app = await TestAuth.CreateAsync();
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var browser = await app.SignedInAsync(AuthApp.UserEmail, AuthApp.UserPassword);

        var catalog = await Browser.JsonAsync(await browser.GetAsync("/rpc/catalog"));

        var account = catalog.GetProperty("groups").GetProperty("account");
        Assert.Equal("get", account.GetProperty("preferences").GetProperty("verb").GetString());
        Assert.Equal("post", account.GetProperty("setPreference").GetProperty("verb").GetString());
    }

    // ---- the definitions themselves ----

    [Fact]
    public void Definitions_check_and_tidy_their_values()
    {
        var color = PreferenceDefinition.Color("tone", "#AABBCC", "d");
        var flag = PreferenceDefinition.Bool("flag", true, "d");
        var choice = PreferenceDefinition.Choice("view", "All", "d", "All", "Mine", "Shared");

        Assert.Equal("#aabbcc", color.Default);
        Assert.Equal("true", flag.Default);
        Assert.Equal(["true", "false"], [flag.Normalize(" TRUE "), flag.Normalize("False")]);
        Assert.Throws<FormatException>(() => flag.Normalize("yes"));
        Assert.Throws<FormatException>(() => flag.Normalize("1"));
        Assert.Equal("Mine", choice.Normalize("mine"));
        Assert.Throws<FormatException>(() => choice.Normalize("everything"));
        Assert.Throws<FormatException>(() => choice.Normalize(null));
    }

    [Theory]
    [InlineData("Accent")]
    [InlineData("1abc")]
    [InlineData("a.b")]
    [InlineData("a-b")]
    [InlineData("")]
    [InlineData("averyveryveryverylongpreferencename12345")]
    public void Keys_are_restricted_so_they_can_never_be_a_database_path_or_an_operator(string key)
        => Assert.Throws<ArgumentException>(() => PreferenceDefinition.Bool(key, true, "d"));

    [Fact]
    public void A_default_that_is_not_valid_is_a_mistake_found_at_startup()
        => Assert.Throws<FormatException>(() => PreferenceDefinition.Color("tone", "yellow", "d"));
}
