using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace XWolf.NanoWolf;

/// <summary>Executes the published calls and serves the catalog.</summary>
internal sealed class CallHandler(
    CallCatalog catalog,
    NanoWolfOptions options,
    IUserResolver users,
    IActionLog actionLog,
    IServiceProvider root,
    ILogger<CallHandler> logger)
{
    private readonly ConcurrentDictionary<Type, Lazy<object>> singletons = new();

    private void Prepare(HttpContext http)
    {
        http.Response.Headers[options.CatalogHeader] = catalog.Hash;
        http.Response.Headers.CacheControl = "no-store";
    }

    public async Task CatalogAsync(HttpContext http)
    {
        Prepare(http);
        var user = await users.ResolveAsync(http);
        await WriteJsonAsync(http, 200, catalog.Describe(user), null);
    }

    public async Task CallAsync(HttpContext http)
    {
        Prepare(http);
        var group = (string?)http.Request.RouteValues["group"] ?? "";
        var name = (string?)http.Request.RouteValues["method"] ?? "";

        var call = catalog.Find(group, name);
        if (call == null)
        {
            await WriteErrorAsync(http, new CallException(404, "not_found", "Unknown call"));
            return;
        }

        var isGet = HttpMethods.IsGet(http.Request.Method);
        if (isGet != (call.Verb == Verb.Get))
        {
            http.Response.Headers.Allow = call.Verb == Verb.Get ? "GET" : "POST";
            await WriteErrorAsync(http, new CallException(405, "method_not_allowed", $"Use {(call.Verb == Verb.Get ? "GET" : "POST")} for this call"));
            return;
        }

        CtxUser? user = null;
        Ctx ctx = new(null, options.TimeProvider.GetUtcNow(), http.RequestAborted) { Http = http };
        CallException? failure = null;
        object? result = null;
        try
        {
            if (!isGet)
            {
                if (!IsJson(http.Request.ContentType))
                    throw new CallException(400, "bad_request", "Content-Type must be application/json");
                if (!http.Request.Headers.ContainsKey(options.RequiredHeader))
                    throw new CallException(400, "bad_request", $"Missing header {options.RequiredHeader}");
            }

            user = await users.ResolveAsync(http);
            ctx = new Ctx(user, ctx.Now, http.RequestAborted) { Http = http };
            Authorize(call, user, http);

            var args = await CallBinder.BindAsync(call, http.Request, ctx, options);
            result = await InvokeAsync(call, args, http);
        }
        catch (CallException ex)
        {
            failure = ex;
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            failure = new CallException(499, "cancelled", "The request was cancelled");
        }
        catch (Exception ex)
        {
            var errorId = Guid.NewGuid().ToString("N")[..12];
            logger.LogError(ex, "Call {Call} failed (errorId {ErrorId})", call.Key, errorId);
            failure = new CallException(500, "internal", "Internal error", errorId: errorId);
        }

        if (!call.ReadOnly)
            await LogAsync(call, user, ctx, failure);

        if (failure == null)
        {
            try
            {
                await WriteResultAsync(http, call, result);
            }
            catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
            {
                // Writing the response failed (for example a response type that throws). Report it like any other failure.
                var errorId = Guid.NewGuid().ToString("N")[..12];
                logger.LogError(ex, "Writing the response of {Call} failed (errorId {ErrorId})", call.Key, errorId);
                if (http.Response.HasStarted)
                    http.Abort();
                else
                {
                    http.Response.Clear();
                    Prepare(http);
                    await WriteErrorAsync(http, new CallException(500, "internal", "Internal error", errorId: errorId));
                }
            }
        }
        else if (failure.Status != 499)
            await WriteErrorAsync(http, failure);
    }

    private static void Authorize(CallDescriptor call, CtxUser? user, HttpContext http)
    {
        if (call.Access == Access.Anonymous)
            return;
        if (user == null)
        {
            // The resolver can explain why there is no user (for example "session_replaced").
            var reason = http.Items[AuthItems.Reason] as string;
            throw reason == null ? CallException.Unauthenticated() : new CallException(401, reason, "Authentication required");
        }
        if (call.Access == Access.Admin && !user.IsAdmin)
            throw CallException.Forbidden();
    }

    private async Task<object?> InvokeAsync(CallDescriptor call, object?[] args, HttpContext http)
    {
        object? instance = null;
        if (!call.IsStatic)
            instance = call.IsSingleton
                ? singletons.GetOrAdd(call.DeclaringType, t => new Lazy<object>(() => ActivatorUtilities.CreateInstance(root, t))).Value
                : ActivatorUtilities.CreateInstance(http.RequestServices, call.DeclaringType);

        try
        {
            var raw = call.Invoker(instance, args);
            if (raw is Task task)
            {
                await task;
                raw = call.TaskResult?.Invoke(task);
            }
            return raw;
        }
        finally
        {
            // Per-call instances are disposed after the call; singletons live until the process ends.
            if (!call.IsSingleton)
            {
                if (instance is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (instance is IDisposable disposable)
                    disposable.Dispose();
            }
        }
    }

    private async Task LogAsync(CallDescriptor call, CtxUser? user, Ctx ctx, CallException? failure)
    {
        try
        {
            await actionLog.WriteAsync(
                new ActionLogEntry(ctx.Now, user?.Id, call.Key, failure == null, failure?.Code, new Dictionary<string, string>(ctx.Details)),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The action already happened: it is not undone because the log failed, but the failure must be visible.
            logger.LogError(ex, "The action log failed for call {Call}", call.Key);
        }
    }

    private static bool IsJson(string? contentType)
        => contentType != null && contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

    private async Task WriteResultAsync(HttpContext http, CallDescriptor call, object? result)
    {
        if (result is ICallResponse response)
        {
            http.Response.StatusCode = 200;
            await response.ExecuteAsync(http, http.RequestAborted);
            return;
        }

        var type = call.ResultType;
        if (result == null)
        {
            await WriteJsonAsync(http, 200, null, typeof(object));
            return;
        }
        if (type == null || type == typeof(object) || type.IsInterface || type.IsAbstract)
            type = result.GetType();
        await WriteJsonAsync(http, 200, result, type);
    }

    private Task WriteErrorAsync(HttpContext http, CallException ex)
    {
        var error = new Dictionary<string, object?> { ["code"] = ex.Code, ["message"] = ex.Message };
        if (ex.Fields is { Count: > 0 })
            error["fields"] = ex.Fields;
        if (ex.ErrorId != null)
            error["errorId"] = ex.ErrorId;
        return WriteJsonAsync(http, ex.Status, new { error }, null);
    }

    private async Task WriteJsonAsync(HttpContext http, int status, object? value, Type? type)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(http.Response.Body, value, type ?? value?.GetType() ?? typeof(object), options.Json, http.RequestAborted);
    }
}
