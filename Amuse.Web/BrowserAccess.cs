using System.Net;

namespace Amuse.Web;

internal static class BrowserAccess
{
    public static bool Allows(HttpContext context, ServerOptions options)
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)) return false;
        var host = context.Request.Host;
        var localHost = host.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
        var proxyUri = options.ColabOrigin is null ? null : new Uri(options.ColabOrigin);
        var proxyHost = proxyUri is not null && host.Host.Trim('[', ']').Equals(proxyUri.Host, StringComparison.OrdinalIgnoreCase)
            && (host.Port ?? 443) == proxyUri.Port;
        if (!(localHost && (host.Port ?? 80) == options.Port) && !proxyHost) return false;
        var origin = context.Request.Headers.Origin.ToString();
        var localOrigin = localHost && Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
            uri.UserInfo.Length == 0 && uri.Host.Equals(host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) && uri.Port == options.Port;
        if (origin.Length > 0 && !localOrigin && !string.Equals(origin, options.ColabOrigin, StringComparison.OrdinalIgnoreCase)) return false;
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
        {
            // Colab's output frame navigates to the port proxy from another origin.
            // API requests and all writes retain the normal same-origin/token checks.
            var frameNavigation = options.ColabOrigin is not null &&
                context.Request.Method is "GET" or "HEAD" &&
                context.Request.Headers["Sec-Fetch-Mode"] == "navigate" &&
                context.Request.Headers["Sec-Fetch-Dest"] == "iframe" &&
                !context.Request.Path.StartsWithSegments("/api");
            // A notebook link opens the tunnel as a top-level cross-site navigation.
            // Allow only the UI landing page on the explicitly configured proxy host.
            var documentNavigation = proxyHost && context.Request.Method is "GET" or "HEAD" &&
                context.Request.Headers["Sec-Fetch-Mode"] == "navigate" &&
                context.Request.Headers["Sec-Fetch-Dest"] == "document" &&
                context.Request.Path == "/";
            if (!frameNavigation && !documentNavigation) return false;
        }
        return true;
    }

    public static string FramePolicy(ServerOptions options) => options.ColabOrigin is null ? "'none'" :
        "'self' https://colab.research.google.com" + (options.FrameOrigin is null ? "" : " " + options.FrameOrigin);
}
