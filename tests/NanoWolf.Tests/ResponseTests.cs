using System.Net;
using System.Text.Json;

namespace XWolf.NanoWolf.Tests;

public class ResponseTests
{
    private static Task<TestApp> App() => TestApp.CreateAsync([typeof(FileCalls)]);

    [Fact]
    public async Task A_download_response_writes_bytes_and_headers()
    {
        await using var app = await App();

        var response = await app.GetAsync("/rpc/files/report?name=monthly.pdf", TestApp.Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("monthly.pdf", response.Content.Headers.ContentDisposition.FileName);
        Assert.Equal(4, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Downloads_are_sandboxed_and_not_sniffed()
    {
        await using var app = await App();

        var response = await app.GetAsync("/rpc/files/report?name=a.pdf", TestApp.Alice);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(app.Catalog.Hash, response.Headers.GetValues("X-NanoWolf-Catalog").Single());
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\temp\evil.exe", "evil.exe")]
    [InlineData("a\"b.pdf", "a_b.pdf")]
    public async Task File_names_cannot_contain_paths_or_quotes(string requested, string expected)
    {
        await using var app = await App();

        var response = await app.GetAsync("/rpc/files/report?name=" + Uri.EscapeDataString(requested), TestApp.Alice);

        Assert.Equal(expected, response.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Non_ascii_file_names_are_encoded()
    {
        await using var app = await App();

        var response = await app.GetAsync("/rpc/files/report?name=" + Uri.EscapeDataString("informe año.pdf"), TestApp.Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("informe año.pdf", response.Content.Headers.ContentDisposition!.FileNameStar);
    }

    [Fact]
    public async Task Streams_are_sent_inline_when_asked_and_always_disposed()
    {
        await using var app = await App();
        TrackedStream.Disposed = false;

        var response = await app.PostAsync("/rpc/files/render", "{}", TestApp.Alice);

        Assert.Equal([9, 8, 7], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(TrackedStream.Disposed);
    }

    [Fact]
    public async Task Responses_of_calls_that_modify_data_are_logged_like_any_other()
    {
        await using var app = await App();

        await app.PostAsync("/rpc/files/render", "{}", TestApp.Alice);

        var entry = Assert.Single(app.Log.Entries);
        Assert.Equal("files.render", entry.Call);
        Assert.Equal("render", entry.Details["kind"]);
    }

    [Fact]
    public async Task New_response_types_need_no_registration()
    {
        await using var app = await App();

        var response = await app.GetAsync("/rpc/files/text", TestApp.Alice);

        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_call_returning_object_can_answer_with_a_response_or_with_json()
    {
        await using var app = await App();

        var file = await app.GetAsync("/rpc/files/either?file=true", TestApp.Alice);
        var json = await app.GetAsync("/rpc/files/either?file=false", TestApp.Alice);

        Assert.Equal([5], await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"plain json\"", await json.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_response_that_fails_becomes_a_generic_error()
    {
        await using var app = await App();

        var response = await app.GetAsync("/rpc/files/broken", TestApp.Alice);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret response detail", text);
        Assert.Contains("errorId", text);
    }

    [Fact]
    public async Task The_catalog_tells_the_client_what_kind_of_result_to_expect()
    {
        await using var app = await App();

        var json = JsonDocument.Parse(await (await app.GetAsync("/rpc/catalog", TestApp.Alice)).Content.ReadAsStringAsync()).RootElement;
        var files = json.GetProperty("groups").GetProperty("files");

        Assert.Equal("binary", files.GetProperty("report").GetProperty("returns").GetProperty("type").GetString());
        Assert.Equal("binary", files.GetProperty("render").GetProperty("returns").GetProperty("type").GetString());
        Assert.Equal("text", files.GetProperty("text").GetProperty("returns").GetProperty("type").GetString());
        Assert.Equal("any", files.GetProperty("either").GetProperty("returns").GetProperty("type").GetString());
    }

    [Fact]
    public void Response_types_cannot_be_parameters()
    {
        var ex = Assert.Throws<CatalogException>(() => CallCatalog.FromTypes([typeof(BadResponseCalls)]));

        Assert.Contains(ex.Errors, e => e.Contains("cannot be parameters"));
    }

    [Fact]
    public void Download_responses_reject_malformed_content_types()
    {
        Assert.ThrowsAny<Exception>(() => new DownloadResponse([1], "not a media type"));
    }
}
