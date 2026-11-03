using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Text.Json;

namespace XWolf.NanoWolf.Tests;

public class CallTests
{
    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ErrorCode(HttpResponseMessage response)
        => (await JsonOf(response)).GetProperty("error").GetProperty("code").GetString()!;

    // ---- basic calls ----

    [Fact]
    public async Task Get_call_returns_the_result_in_camel_case()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.GetAsync("/rpc/items/get?id=7", TestApp.Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonOf(response);
        Assert.Equal("7", json.GetProperty("id").GetString());
        Assert.Equal("Title of 7", json.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Post_call_binds_named_parameters_and_awaits_tasks()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.PostAsync("/rpc/items/edit", """{"id":"7","version":1,"content":"hello"}""", TestApp.Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", (await JsonOf(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Parameter_names_are_case_insensitive()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.PostAsync("/rpc/items/edit", """{"ID":"7","Version":1,"Content":"x"}""", TestApp.Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Void_and_null_results_are_json_null()
    {
        await using var app = await TestApp.CreateAsync();

        var nothing = await app.PostAsync("/rpc/items/nothing", "{}", TestApp.Alice);
        var maybe = await app.PostAsync("/rpc/items/maybe", "{}", TestApp.Alice);

        Assert.Equal("null", await nothing.Content.ReadAsStringAsync());
        Assert.Equal("null", await maybe.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_empty_body_is_an_empty_parameter_object()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.PostAsync("/rpc/items/nothing", "", TestApp.Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Static_methods_are_called_without_an_instance()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.GetAsync("/rpc/util/echo?text=hi");

        Assert.Equal("hi", (await JsonOf(response)).GetString());
    }

    [Fact]
    public async Task Ctx_carries_the_user_and_is_not_sent_by_the_client()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.GetAsync("/rpc/items/who", TestApp.Alice);

        Assert.Equal("Alice", (await JsonOf(response)).GetString());
        Assert.Empty(app.Catalog.Find("items", "who")!.Params);
    }

    // ---- instances ----

    [Fact]
    public async Task Singleton_classes_keep_one_instance()
    {
        await using var app = await TestApp.CreateAsync();

        var first = await JsonOf(await app.PostAsync("/rpc/counter/next", "{}"));
        var second = await JsonOf(await app.PostAsync("/rpc/counter/next", "{}"));

        Assert.Equal(1, first.GetInt32());
        Assert.Equal(2, second.GetInt32());
    }

    [Fact]
    public async Task Instance_classes_are_created_for_every_call()
    {
        await using var app = await TestApp.CreateAsync();

        var first = await JsonOf(await app.PostAsync("/rpc/perCall/next", "{}"));
        var second = await JsonOf(await app.PostAsync("/rpc/perCall/next", "{}"));

        Assert.Equal(1, first.GetInt32());
        Assert.Equal(1, second.GetInt32()); // a new instance: its counter starts again
        Assert.Equal(2, app.Counter.Created);
    }

    // ---- access ----

    [Fact]
    public async Task Calls_require_a_user_by_default()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.GetAsync("/rpc/items/get?id=1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await ErrorCode(response));
    }

    [Fact]
    public async Task Admin_calls_reject_normal_users_and_accept_admins()
    {
        await using var app = await TestApp.CreateAsync();

        var user = await app.PostAsync("/rpc/items/secret", "{}", TestApp.Alice);
        var admin = await app.PostAsync("/rpc/items/secret", "{}", TestApp.Admin);
        var anonymous = await app.PostAsync("/rpc/items/secret", "{}");

        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Anonymous_calls_work_without_user()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.GetAsync("/rpc/items/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- request validation ----

    [Fact]
    public async Task Post_without_the_custom_header_is_rejected()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.PostAsync("/rpc/items/nothing", "{}", TestApp.Alice, header: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_request", await ErrorCode(response));
    }

    [Fact]
    public async Task Post_with_another_content_type_is_rejected()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.PostAsync("/rpc/items/nothing", "a=b", TestApp.Alice, contentType: "application/x-www-form-urlencoded");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_unknown_and_invalid_parameters_are_reported_by_field()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.PostAsync("/rpc/items/edit", """{"id":"7","content":"x","extra":1,"note":5}""", TestApp.Alice);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var error = (await JsonOf(response)).GetProperty("error");
        Assert.Equal("invalid", error.GetProperty("code").GetString());
        var fields = error.GetProperty("fields");
        Assert.Equal("required", fields.GetProperty("version").GetString());
        Assert.Equal("unknown parameter", fields.GetProperty("extra").GetString());
        Assert.Equal("invalid value", fields.GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_bad_request()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.PostAsync("/rpc/items/nothing", "{not json", TestApp.Alice);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Large_bodies_are_rejected()
    {
        await using var app = await TestApp.CreateAsync(options: o => o.MaxBodyBytes = 100);

        var response = await app.PostAsync("/rpc/items/edit", $$"""{"id":"7","version":1,"content":"{{new string('x', 500)}}"}""", TestApp.Alice);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Enums_are_accepted_by_name_only()
    {
        await using var app = await TestApp.CreateAsync();

        var byName = await app.PostAsync("/rpc/items/theme", """{"mode":"Dark"}""", TestApp.Alice);
        var byNumber = await app.PostAsync("/rpc/items/theme", """{"mode":1}""", TestApp.Alice);

        Assert.Equal("Dark", (await JsonOf(byName)).GetString());
        Assert.Equal((HttpStatusCode)422, byNumber.StatusCode);
    }

    [Fact]
    public async Task Query_parameters_support_lists_and_are_validated()
    {
        await using var app = await TestApp.CreateAsync();

        var sum = await app.GetAsync("/rpc/items/sum?values=1&values=2&values=3", TestApp.Alice);
        var bad = await app.GetAsync("/rpc/items/sum?values=1&values=x", TestApp.Alice);
        var unknown = await app.GetAsync("/rpc/items/sum?values=1&other=2", TestApp.Alice);
        var missing = await app.GetAsync("/rpc/items/sum", TestApp.Alice);

        Assert.Equal(6, (await JsonOf(sum)).GetInt32());
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        Assert.Equal((HttpStatusCode)422, unknown.StatusCode);
        Assert.Equal((HttpStatusCode)422, missing.StatusCode);
    }

    [Fact]
    public async Task Unknown_calls_and_wrong_verbs_are_rejected()
    {
        await using var app = await TestApp.CreateAsync();

        var unknown = await app.GetAsync("/rpc/items/nope", TestApp.Alice);
        var wrongVerb = await app.PostAsync("/rpc/items/get", "{}", TestApp.Alice);
        var wrongVerb2 = await app.GetAsync("/rpc/items/edit", TestApp.Alice);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongVerb.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongVerb2.StatusCode);
    }

    // ---- errors ----

    [Fact]
    public async Task Call_errors_keep_their_status_and_code()
    {
        await using var app = await TestApp.CreateAsync();

        var notFound = await app.GetAsync("/rpc/items/get?id=missing", TestApp.Alice);
        var conflict = await app.PostAsync("/rpc/items/edit", """{"id":"7","version":9,"content":"x"}""", TestApp.Alice);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("not_found", await ErrorCode(notFound));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("version_conflict", await ErrorCode(conflict));
    }

    [Fact]
    public async Task Unexpected_errors_do_not_leak_details()
    {
        await using var app = await TestApp.CreateAsync();

        var response = await app.PostAsync("/rpc/items/boom", "{}", TestApp.Alice);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret internal detail", text);
        Assert.DoesNotContain("InvalidOperationException", text);
        Assert.Contains("errorId", text);
    }

    // ---- headers ----

    [Fact]
    public async Task Responses_carry_the_catalog_hash_and_are_not_cacheable()
    {
        await using var app = await TestApp.CreateAsync();

        var ok = await app.GetAsync("/rpc/items/ping");
        var error = await app.GetAsync("/rpc/items/nope");

        foreach (var response in new[] { ok, error })
        {
            Assert.Equal(app.Catalog.Hash, response.Headers.GetValues("X-NanoWolf-Catalog").Single());
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
    }

    // ---- catalog endpoint ----

    private static async Task<HashSet<string>> VisibleCalls(TestApp app, string? user)
    {
        var json = await JsonOf(await app.GetAsync("/rpc/catalog", user));
        var result = new HashSet<string>();
        foreach (var group in json.GetProperty("groups").EnumerateObject())
            foreach (var call in group.Value.EnumerateObject())
                result.Add(group.Name + "." + call.Name);
        return result;
    }

    [Fact]
    public async Task The_catalog_only_lists_what_the_user_can_call()
    {
        await using var app = await TestApp.CreateAsync();

        var anonymous = await VisibleCalls(app, null);
        var user = await VisibleCalls(app, TestApp.Alice);
        var admin = await VisibleCalls(app, TestApp.Admin);

        Assert.Contains("items.ping", anonymous);
        Assert.DoesNotContain("items.get", anonymous);
        Assert.Contains("items.get", user);
        Assert.DoesNotContain("items.secret", user);
        Assert.Contains("items.secret", admin);
    }

    [Fact]
    public async Task The_catalog_describes_parameters()
    {
        await using var app = await TestApp.CreateAsync();

        var json = await JsonOf(await app.GetAsync("/rpc/catalog", TestApp.Alice));
        var edit = json.GetProperty("groups").GetProperty("items").GetProperty("edit");

        Assert.Equal("post", edit.GetProperty("verb").GetString());
        var parameters = edit.GetProperty("params").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        Assert.Equal(["id", "version", "content", "note"], parameters.Keys);
        Assert.Equal("int", parameters["version"].GetProperty("type").GetProperty("type").GetString());
        Assert.False(parameters["id"].GetProperty("optional").GetBoolean());
        Assert.True(parameters["note"].GetProperty("optional").GetBoolean());
    }

    [Fact]
    public async Task The_hash_in_the_catalog_does_not_depend_on_the_user()
    {
        await using var app = await TestApp.CreateAsync();

        var anonymous = (await JsonOf(await app.GetAsync("/rpc/catalog"))).GetProperty("hash").GetString();
        var admin = (await JsonOf(await app.GetAsync("/rpc/catalog", TestApp.Admin))).GetProperty("hash").GetString();

        Assert.Equal(anonymous, admin);
        Assert.Equal(app.Catalog.Hash, admin);
    }

    // ---- action log ----

    [Fact]
    public async Task Only_calls_that_modify_data_are_logged()
    {
        await using var app = await TestApp.CreateAsync();

        await app.GetAsync("/rpc/items/get?id=1", TestApp.Alice);
        await app.PostAsync("/rpc/items/edit", """{"id":"7","version":1,"content":"x"}""", TestApp.Alice);

        var entry = Assert.Single(app.Log.Entries);
        Assert.Equal("items.edit", entry.Call);
        Assert.Equal("u1", entry.UserId);
        Assert.True(entry.Ok);
        Assert.Equal("7", entry.Details["item"]);
    }

    [Fact]
    public async Task Failures_are_logged_with_their_code_including_denied_access()
    {
        await using var app = await TestApp.CreateAsync();

        await app.PostAsync("/rpc/items/edit", """{"id":"7","version":9,"content":"x"}""", TestApp.Alice);
        await app.PostAsync("/rpc/items/secret", "{}", TestApp.Alice);
        await app.PostAsync("/rpc/items/secret", "{}");

        var entries = app.Log.Entries.ToList();
        Assert.Equal(["version_conflict", "forbidden", "unauthenticated"], entries.Select(e => e.ErrorCode));
        Assert.All(entries, e => Assert.False(e.Ok));
        Assert.Null(entries[2].UserId);
    }

    [Fact]
    public async Task The_log_never_contains_parameters_or_results()
    {
        await using var app = await TestApp.CreateAsync();

        await app.PostAsync("/rpc/items/edit", """{"id":"7","version":1,"content":"TOP-SECRET-CONTENT"}""", TestApp.Alice);

        var text = JsonSerializer.Serialize(app.Log.Entries);
        Assert.DoesNotContain("TOP-SECRET-CONTENT", text);
    }

    [Fact]
    public async Task A_failing_log_does_not_break_the_call()
    {
        await using var app = await TestApp.CreateAsync(services: s =>
        {
            s.RemoveAll<IActionLog>();
            s.AddSingleton<IActionLog, ThrowingLog>();
        });

        var response = await app.PostAsync("/rpc/items/nothing", "{}", TestApp.Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
