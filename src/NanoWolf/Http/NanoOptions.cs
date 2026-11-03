using System.Text.Json;
using System.Text.Json.Serialization;

namespace XWolf.NanoWolf;

/// <summary>
/// Infrastructure level configuration (appsettings, environment, secrets). Applied when the application starts.
/// Application level settings, editable from the admin panel, are declared with <see cref="SettingKey{T}"/>.
/// </summary>
public sealed class NanoWolfOptions
{
    /// <summary>Route prefix of the calls.</summary>
    public string Prefix { get; set; } = "/rpc";

    /// <summary>Header required in POST calls. A cross-site form or link cannot send it.</summary>
    public string RequiredHeader { get; set; } = "X-NanoWolf";

    /// <summary>Header carrying the catalog hash in every response.</summary>
    public string CatalogHeader { get; set; } = "X-NanoWolf-Catalog";

    /// <summary>Maximum size of a POST body.</summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Public address of the application as people reach it (behind a reverse proxy, the proxy address), for example
    /// "https://example.com/kno". The application itself uses <b>relative</b> addresses everywhere; this is only for what
    /// has to leave it: links printed from the console and, later, the return addresses of external sign in providers.
    /// A path in it (here "/kno") is used as the base path of the application.
    /// </summary>
    public string PublicUrl { get; set; } = "https://localhost:5001";

    /// <summary>
    /// Addresses (or networks such as "10.0.0.0/8") of the reverse proxies whose forwarded headers are trusted to know
    /// the real address of the client. Empty means only the machine itself (loopback). Never trust more than needed:
    /// anyone else could pretend to be any address and escape the limit of failed attempts.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>PBKDF2 iterations for password hashes.</summary>
    public int PasswordIterations { get; set; } = 210_000;

    /// <summary>
    /// The session cookie is only sent over HTTPS (and gets the "__Host-" prefix). Disable only for
    /// local development over plain HTTP.
    /// </summary>
    public bool RequireSecureCookie { get; set; } = true;

    public string CookieName => RequireSecureCookie ? "__Host-nanowolf" : "nanowolf";

    internal JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson() => new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}
