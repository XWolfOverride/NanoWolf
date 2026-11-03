using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace XWolf.NanoWolf.Testing;

/// <summary>A browser: it keeps the session cookie between requests.</summary>
public sealed class Browser(HttpClient client)
{
    public const string CookieName = "__Host-nanowolf";

    public string? Cookie { get; set; }

    /// <summary>Extra headers sent with every request (for example to pretend to come from a proxy).</summary>
    public Dictionary<string, string> Headers { get; } = [];

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<HttpResponseMessage> PostAsync(string path, string json = "{}") => SendAsync(HttpMethod.Post, path, json);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json)
    {
        var request = new HttpRequestMessage(method, path);
        if (json != null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            request.Headers.Add("X-NanoWolf", "1");
        }
        foreach (var (name, value) in Headers)
            request.Headers.Add(name, value);
        if (Cookie != null)
            request.Headers.Add("Cookie", $"{CookieName}={Cookie}");

        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            foreach (var cookie in cookies.Where(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal)))
            {
                var value = cookie[(CookieName.Length + 1)..].Split(';')[0];
                Cookie = value.Length == 0 ? null : value;
            }
        return response;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
        => (await JsonAsync(response)).GetProperty("error").GetProperty("code").GetString()!;

    public Task<HttpResponseMessage> LoginAsync(string email, string password)
        => PostAsync("/rpc/auth/login", JsonSerializer.Serialize(new { email, password }));
}

/// <summary>The authentication system running on in-memory stores and a clock the tests control.</summary>
public sealed class AuthApp : IAsyncDisposable
{
    public const string AdminEmail = "root@example.com";
    public const string AdminPassword = "correct horse battery staple";
    public const string UserEmail = "alice@example.com";
    public const string UserPassword = "a long password for Alice";

    private readonly WebApplication app;
    private readonly HttpClient client;

    private AuthApp(WebApplication app)
    {
        this.app = app;
        client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
    }

    public IServiceProvider Services => app.Services;
    public FakeTimeProvider Time => Services.GetRequiredService<FakeTimeProvider>();
    public MemoryUserStore Users => Services.GetRequiredService<MemoryUserStore>();
    public MemorySessionStore Sessions => Services.GetRequiredService<MemorySessionStore>();
    public MemoryTokenStore Tokens => Services.GetRequiredService<MemoryTokenStore>();
    public MemorySettingsStore SettingsStore => Services.GetRequiredService<MemorySettingsStore>();
    public ListLog Log => Services.GetRequiredService<ListLog>();

    public static async Task<AuthApp> CreateAsync(Action<MemorySettingsStore>? seedSettings = null, Action<IServiceCollection>? services = null, Action<NanoWolfOptions>? configure = null, Action<WebApplication>? pipeline = null, Type[]? calls = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        var settings = new MemorySettingsStore();
        seedSettings?.Invoke(settings);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var users = new MemoryUserStore();
        var sessions = new MemorySessionStore();
        var tokens = new MemoryTokenStore();
        var log = new ListLog();
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(users);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(log);
        builder.Services.AddSingleton<IUserStore>(users);
        builder.Services.AddSingleton<ISessionStore>(sessions);
        builder.Services.AddSingleton<ITokenStore>(tokens);
        builder.Services.AddSingleton<IThrottleStore, MemoryThrottleStore>();
        builder.Services.AddSingleton<ISettingsStore>(settings);
        builder.Services.AddSingleton<IActionLog>(log);
        services?.Invoke(builder.Services);

        void Configure(NanoWolfOptions o)
        {
            o.TimeProvider = time;
            o.PasswordIterations = 1000; // fast tests; the default is much higher
            o.PublicUrl = "https://kno.example.com";
            configure?.Invoke(o);
        }
        builder.Services.AddNanoWolfAuth(Configure);
        builder.Services.AddNanoWolfCalls(CallCatalog.FromTypes([typeof(AuthCalls), typeof(AdminCalls), typeof(SettingsCalls), typeof(AccountCalls), .. calls ?? []]), Configure);

        var app = builder.Build();
        pipeline?.Invoke(app);
        app.MapNanoWolfCalls();
        await app.StartAsync();
        return new AuthApp(app);
    }

    public Browser NewBrowser() => new(client);

    /// <summary>Creates a user directly in the store, as if it had been invited and had accepted.</summary>
    public async Task<User> SeedUserAsync(string email, string password, bool admin = false, string name = "Someone")
    {
        var passwords = Services.GetRequiredService<PasswordService>();
        var user = new User
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = email,
            DisplayName = name,
            IsAdmin = admin,
            CreatedAt = Time.GetUtcNow(),
            Identities = [new Identity { Method = LoginMethods.Password, Subject = email, Data = passwords.Hash(password) }],
        };
        await Users.CreateAsync(user, CancellationToken.None);
        return user;
    }

    public async Task<Browser> SignedInAsync(string email, string password)
    {
        var browser = NewBrowser();
        var response = await browser.LoginAsync(email, password);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return browser;
    }

    /// <summary>An administrator, already signed in.</summary>
    public async Task<Browser> SignedInAdminAsync()
    {
        await SeedUserAsync(AdminEmail, AdminPassword, admin: true, name: "Root");
        return await SignedInAsync(AdminEmail, AdminPassword);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
