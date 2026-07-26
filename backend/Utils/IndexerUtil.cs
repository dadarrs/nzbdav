namespace NzbWebDAV.Utils;

public static class IndexerUtil
{
    /// <summary>
    /// The host (plus an explicit non-default port) of a download URL, used as the
    /// immediate indexer fallback before the real name is resolved from the arr queue.
    /// Only the host is kept — the rest of the URL carries the indexer apikey and must
    /// never be persisted. Returns null for uploads (no URL) or unparseable input.
    /// </summary>
    public static string? HostFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            var host = uri.Host.ToLowerInvariant();
            if (string.IsNullOrEmpty(host)) return null;
            // keep the port when it isn't the scheme default so a proxy (e.g. prowlarr
            // on :9696) stays distinguishable from anything else on the same host
            return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        }
        catch
        {
            return null;
        }
    }
}
