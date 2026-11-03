using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace XWolf.NanoWolf;

public static class NanoWolfProxyExtensions
{
    /// <summary>
    /// Prepares the application to live behind a reverse proxy: the real address of the client from the forwarded
    /// headers of the trusted proxies, and the base path taken from <see cref="NanoWolfOptions.PublicUrl"/>.
    /// Call it first in the pipeline. Works whether the proxy strips the base path or not.
    /// </summary>
    public static IApplicationBuilder UseNanoWolfBase(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<NanoWolfOptions>();
        if (!Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var publicUrl) || publicUrl.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"NanoWolf:PublicUrl '{options.PublicUrl}' is not a valid http(s) address.");

        var forwarded = new ForwardedHeadersOptions
        {
            // The host is not forwarded: the public address is configured, it is not taken from a header.
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        if (options.TrustedProxies.Length > 0)
        {
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var entry in options.TrustedProxies)
            {
                if (System.Net.IPNetwork.TryParse(entry, out var network))
                    forwarded.KnownIPNetworks.Add(network);
                else if (IPAddress.TryParse(entry, out var address))
                    forwarded.KnownProxies.Add(address);
                else
                    throw new InvalidOperationException($"NanoWolf:TrustedProxies: '{entry}' is not an address or a network.");
            }
        }
        app.UseForwardedHeaders(forwarded);

        var basePath = publicUrl.AbsolutePath.TrimEnd('/');
        if (basePath.Length > 0)
            app.UsePathBase(basePath);
        return app;
    }
}
