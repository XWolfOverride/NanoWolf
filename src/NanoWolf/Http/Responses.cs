using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace XWolf.NanoWolf;

/// <summary>
/// A call result that writes its own HTTP response instead of being serialized as JSON (downloads, generated
/// images or documents...). Implement it to add new response types; no registration is needed.
/// </summary>
public interface ICallResponse
{
    /// <summary>Writes the response. The status is 200 and the catalog headers are already set.</summary>
    Task ExecuteAsync(HttpContext http, CancellationToken cancel);
}

/// <summary>
/// Tells the client how to treat the result of calls returning this type, as published in the catalog
/// (for example "binary" means the client gets a file/blob instead of JSON). Defaults to "response".
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class ResponseKindAttribute(string kind) : Attribute
{
    public string Kind { get; } = kind;
}

/// <summary>A binary response: a file to download, or an image/document generated on the fly.</summary>
[ResponseKind("binary")]
public sealed class DownloadResponse : ICallResponse
{
    private readonly byte[]? data;
    private readonly Stream? stream;

    public DownloadResponse(byte[] data, string contentType, string? fileName = null, bool inline = false)
        : this(contentType, fileName, inline)
        => this.data = data;

    /// <summary>The stream is copied to the response and disposed afterwards.</summary>
    public DownloadResponse(Stream stream, string contentType, string? fileName = null, bool inline = false)
        : this(contentType, fileName, inline)
        => this.stream = stream;

    private DownloadResponse(string contentType, string? fileName, bool inline)
    {
        // A malformed media type is a programming error: fail here, not halfway through the response.
        MediaTypeHeaderValue.Parse(contentType);
        ContentType = contentType;
        FileName = Sanitize(fileName);
        Inline = inline;
    }

    public string ContentType { get; }
    public string? FileName { get; }
    /// <summary>Shown in the browser instead of downloaded. Default false (attachment).</summary>
    public bool Inline { get; }

    public async Task ExecuteAsync(HttpContext http, CancellationToken cancel)
    {
        var response = http.Response;
        response.ContentType = ContentType;

        var disposition = new ContentDispositionHeaderValue(Inline ? "inline" : "attachment");
        if (FileName != null)
            disposition.SetHttpFileName(FileName);
        response.Headers.ContentDisposition = disposition.ToString();

        // Generated content must never be interpreted by the browser as part of the application.
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";

        if (data != null)
        {
            response.ContentLength = data.Length;
            await response.Body.WriteAsync(data, cancel);
            return;
        }

        await using (stream)
        {
            if (stream!.CanSeek)
                response.ContentLength = stream.Length - stream.Position;
            await stream.CopyToAsync(response.Body, cancel);
        }
    }

    /// <summary>Removes path parts and control characters from the suggested file name.</summary>
    private static string? Sanitize(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        var chars = Path.GetFileName(fileName.Replace('\\', '/'))
            .Select(c => char.IsControl(c) || c is '"' or '/' or '\\' or ':' ? '_' : c)
            .ToArray();
        var clean = new string(chars).Trim();
        return clean.Length == 0 ? null : clean;
    }
}
