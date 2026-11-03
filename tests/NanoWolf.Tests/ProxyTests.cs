using System.Net;
using Microsoft.AspNetCore.Builder;

namespace XWolf.NanoWolf.Tests;

/// <summary>Living behind a reverse proxy: the real address of the client, and the public base address.</summary>
public class ProxyTests
{
    /// <summary>The test server has no network: the address of the connection is taken from a header.</summary>
    private static void Pipeline(WebApplication app)
    {
        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Remote", out var remote))
                context.Connection.RemoteIpAddress = IPAddress.Parse(remote.ToString());
            return next();
        });
        app.UseNanoWolfBase();
    }

    private static Task<AuthApp> AppAsync(Action<NanoWolfOptions>? configure = null)
        => TestAuth.CreateAsync(configure: configure, pipeline: Pipeline);

    private static Browser From(AuthApp app, string remote, string? forwardedFor = null)
    {
        var browser = app.NewBrowser();
        browser.Headers["X-Test-Remote"] = remote;
        if (forwardedFor != null)
        {
            browser.Headers["X-Forwarded-For"] = forwardedFor;
            browser.Headers["X-Forwarded-Proto"] = "https";
        }
        return browser;
    }

    private static string LastIp(AuthApp app) => app.Log.Entries.Last(e => e.Call == "auth.login").Details["ip"];

    [Fact]
    public async Task A_trusted_proxy_tells_the_real_address_of_the_client()
    {
        await using var app = await AppAsync(o => o.TrustedProxies = ["10.0.0.0/8"]);

        await From(app, "10.1.2.3", "203.0.113.9").LoginAsync("nobody@example.com", "wrong");

        Assert.Equal("203.0.113.9", LastIp(app));
    }

    [Fact]
    public async Task A_single_trusted_address_works_too()
    {
        await using var app = await AppAsync(o => o.TrustedProxies = ["192.168.1.10"]);

        await From(app, "192.168.1.10", "203.0.113.9").LoginAsync("nobody@example.com", "wrong");

        Assert.Equal("203.0.113.9", LastIp(app));
    }

    [Fact]
    public async Task Without_configuration_only_the_machine_itself_is_trusted()
    {
        await using var app = await AppAsync();

        await From(app, "127.0.0.1", "203.0.113.9").LoginAsync("nobody@example.com", "wrong");
        Assert.Equal("203.0.113.9", LastIp(app));

        await From(app, "198.51.100.7", "203.0.113.50").LoginAsync("nobody@example.com", "wrong");
        Assert.Equal("198.51.100.7", LastIp(app));
    }

    [Fact]
    public async Task Forwarded_headers_from_anybody_else_are_ignored()
    {
        await using var app = await AppAsync(o => o.TrustedProxies = ["10.0.0.0/8"]);

        await From(app, "198.51.100.7", "203.0.113.9").LoginAsync("nobody@example.com", "wrong");

        Assert.Equal("198.51.100.7", LastIp(app));
    }

    [Fact]
    public async Task Pretending_to_be_other_addresses_does_not_escape_the_lock()
    {
        await using var app = await AppAsync(o => o.TrustedProxies = ["10.0.0.0/8"]);
        var attacker = app.NewBrowser();
        attacker.Headers["X-Test-Remote"] = "198.51.100.7"; // not a proxy

        for (var i = 0; i < 20; i++)
        {
            attacker.Headers["X-Forwarded-For"] = $"203.0.113.{i}"; // a different "client" each time
            await attacker.LoginAsync($"guess{i}@example.com", "wrong");
        }
        attacker.Headers["X-Forwarded-For"] = "203.0.113.250";
        var response = await attacker.LoginAsync("another@example.com", "wrong");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Each_real_client_behind_the_proxy_is_limited_on_its_own()
    {
        await using var app = await AppAsync(o => o.TrustedProxies = ["10.0.0.0/8"]);
        var noisy = From(app, "10.1.2.3", "203.0.113.9");
        for (var i = 0; i < 20; i++)
            await noisy.LoginAsync($"guess{i}@example.com", "wrong");

        var noisyAgain = await noisy.LoginAsync("another@example.com", "wrong");
        var neighbour = await From(app, "10.1.2.3", "203.0.113.77").LoginAsync("another@example.com", "wrong");

        Assert.Equal(HttpStatusCode.TooManyRequests, noisyAgain.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, neighbour.StatusCode);
    }

    [Fact]
    public async Task The_path_of_the_public_address_is_the_base_path_with_or_without_the_proxy_stripping_it()
    {
        await using var app = await AppAsync(o => o.PublicUrl = "https://example.com/kno/");
        var browser = app.NewBrowser();

        var stripped = await browser.GetAsync("/rpc/auth/methods");
        var kept = await browser.GetAsync("/kno/rpc/auth/methods");

        Assert.Equal(HttpStatusCode.OK, stripped.StatusCode);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    [Fact]
    public async Task A_session_works_under_a_base_path()
    {
        await using var app = await AppAsync(o => o.PublicUrl = "https://example.com/kno");
        await app.SeedUserAsync(AuthApp.UserEmail, AuthApp.UserPassword);
        var browser = app.NewBrowser();

        var login = await browser.PostAsync("/kno/rpc/auth/login", System.Text.Json.JsonSerializer.Serialize(new { email = AuthApp.UserEmail, password = AuthApp.UserPassword }));
        var me = await browser.GetAsync("/kno/rpc/auth/me");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains("path=/;", login.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant()); // the __Host- prefix requires "/"
    }

    [Fact]
    public async Task A_wrong_configuration_stops_the_application_when_it_starts()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => AppAsync(o => o.PublicUrl = "not a url"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AppAsync(o => o.PublicUrl = "ftp://example.com"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AppAsync(o => o.TrustedProxies = ["10.0.0.0/8", "somewhere"]));
        Assert.Contains("somewhere", ex.Message);
    }
}
