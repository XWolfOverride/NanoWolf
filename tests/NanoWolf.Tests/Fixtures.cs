using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace XWolf.NanoWolf.Tests;

public record Item(string Id, string Title);

public enum Mode { Light, Dark }

public class CreationCounter
{
    public int Created;
}

/// <summary>Valid calls used by the HTTP tests.</summary>
[CallGroup("items")]
public class ItemCalls
{
    [Call(ReadOnly = true, Verb = Verb.Get)]
    public Item Get(Ctx ctx, string id)
        => id == "missing" ? throw CallException.NotFound("No such item") : new Item(id, "Title of " + id);

    [Call]
    public Task<Item> EditAsync(Ctx ctx, string id, int version, string content, string? note = null)
    {
        ctx.Detail("item", id);
        if (version != 1)
            throw CallException.VersionConflict();
        return Task.FromResult(new Item(id, content));
    }

    [Call(Access = Access.Admin)]
    public string Secret() => "admin only";

    [Call(Access = Access.Anonymous, ReadOnly = true, Verb = Verb.Get)]
    public string Ping() => "pong";

    [Call]
    public void Nothing(Ctx ctx) { }

    [Call]
    public Item? Maybe() => null;

    [Call]
    public int Boom() => throw new InvalidOperationException("secret internal detail");

    [Call(ReadOnly = true, Verb = Verb.Get)]
    public int Sum(int[] values) => values.Sum();

    [Call]
    public string Theme(Mode mode) => mode.ToString();

    [Call(ReadOnly = true, Verb = Verb.Get)]
    public string Who(Ctx ctx) => ctx.RequireUser.Name;
}

public static class UtilCalls
{
    [Call(Group = "util", Access = Access.Anonymous, ReadOnly = true, Verb = Verb.Get)]
    public static string Echo(string text) => text;
}

[Singleton, CallGroup("counter")]
public class SharedCalls(CreationCounter counter)
{
    private int count;

    [Call(Access = Access.Anonymous)]
    public int Next()
    {
        counter.Created++;
        return Interlocked.Increment(ref count);
    }
}

[CallGroup("perCall")]
public class PerCallCalls
{
    private int count;

    public PerCallCalls(CreationCounter counter) => counter.Created++;

    [Call(Access = Access.Anonymous)]
    public int Next() => ++count;
}

public sealed class ThrowingLog : IActionLog
{
    public Task WriteAsync(ActionLogEntry entry, CancellationToken cancel) => throw new IOException("log unavailable");
}

/// <summary>Test user taken from the header "X-Test-User": "id|name|admin".</summary>
public sealed class HeaderUsers : IUserResolver
{
    public ValueTask<CtxUser?> ResolveAsync(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue("X-Test-User", out var value) || value.Count == 0)
            return ValueTask.FromResult<CtxUser?>(null);
        var parts = value[0]!.Split('|');
        return ValueTask.FromResult<CtxUser?>(new CtxUser(parts[0], parts.Length > 1 ? parts[1] : parts[0], parts.Length > 2 && parts[2] == "admin"));
    }
}

public sealed class TestApp : IAsyncDisposable
{
    public const string Alice = "u1|Alice";
    public const string Admin = "a1|Root|admin";

    private readonly WebApplication app;

    private TestApp(WebApplication app, HttpClient client, ListLog log, CreationCounter counter, CallCatalog catalog)
    {
        this.app = app;
        Client = client;
        Log = log;
        Counter = counter;
        Catalog = catalog;
    }

    public HttpClient Client { get; }
    public ListLog Log { get; }
    public CreationCounter Counter { get; }
    public CallCatalog Catalog { get; }

    public static async Task<TestApp> CreateAsync(Type[]? types = null, Action<IServiceCollection>? services = null, Action<NanoWolfOptions>? options = null)
    {
        types ??= [typeof(ItemCalls), typeof(UtilCalls), typeof(SharedCalls), typeof(PerCallCalls)];
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var log = new ListLog();
        var counter = new CreationCounter();
        var catalog = CallCatalog.FromTypes(types);
        builder.Services.AddSingleton<IActionLog>(log);
        builder.Services.AddSingleton<IUserResolver, HeaderUsers>();
        builder.Services.AddSingleton(counter);
        services?.Invoke(builder.Services);
        builder.Services.AddNanoWolfCalls(catalog, options);

        var app = builder.Build();
        app.MapNanoWolfCalls();
        await app.StartAsync();
        return new TestApp(app, app.GetTestClient(), log, counter, catalog);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? user = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (user != null)
            request.Headers.Add("X-Test-User", user);
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> PostAsync(string path, string json, string? user = null, bool header = true, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, contentType),
        };
        if (header)
            request.Headers.Add("X-NanoWolf", "1");
        if (user != null)
            request.Headers.Add("X-Test-User", user);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

[ResponseKind("text")]
public sealed class PlainTextResponse(string text) : ICallResponse
{
    public async Task ExecuteAsync(HttpContext http, CancellationToken cancel)
    {
        http.Response.ContentType = "text/plain; charset=utf-8";
        await http.Response.WriteAsync(text, cancel);
    }
}

public sealed class BrokenResponse : ICallResponse
{
    public Task ExecuteAsync(HttpContext http, CancellationToken cancel) => throw new InvalidOperationException("secret response detail");
}

public sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
{
    public static bool Disposed;

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

[CallGroup("files")]
public class FileCalls
{
    [Call(ReadOnly = true, Verb = Verb.Get)]
    public DownloadResponse Report(string name)
        => new([1, 2, 3, 4], "application/pdf", name);

    [Call]
    public async Task<DownloadResponse> RenderAsync(Ctx ctx)
    {
        ctx.Detail("kind", "render");
        await Task.Yield();
        return new DownloadResponse(new TrackedStream([9, 8, 7]), "image/png", "render.png", inline: true);
    }

    [Call(ReadOnly = true, Verb = Verb.Get)]
    public PlainTextResponse Text() => new("hello");

    [Call(ReadOnly = true, Verb = Verb.Get)]
    public object Either(bool file) => file ? new DownloadResponse([5], "application/octet-stream") : "plain json";

    [Call(ReadOnly = true, Verb = Verb.Get)]
    public BrokenResponse Broken() => new();
}

public static class BadResponseCalls
{
    [Call(Group = "bad")]
    public static int AsParameter(DownloadResponse response) => 1;
}

/// <summary>The authentication app of NanoWolf.Testing with the calls of these tests (<see cref="ItemCalls"/>) by default.</summary>
public static class TestAuth
{
    public static Task<AuthApp> CreateAsync(Action<MemorySettingsStore>? seedSettings = null, Action<IServiceCollection>? services = null, Action<NanoWolfOptions>? configure = null, Action<WebApplication>? pipeline = null, Type[]? calls = null)
        => AuthApp.CreateAsync(seedSettings, services, configure, pipeline, calls ?? [typeof(ItemCalls)]);
}
