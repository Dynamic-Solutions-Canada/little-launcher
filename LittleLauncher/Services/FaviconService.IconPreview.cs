using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace LittleLauncher.Services;

/// <summary>Fresh, bounded site-icon reads that do not write the favicon cache or use third-party fallbacks.</summary>
internal static partial class FaviconService
{
    /// <summary>Preview-only network policy; null preserves ordinary favicon-fetch behavior.</summary>
    private sealed record IconFetchOptions(CancellationToken CancellationToken, int MaxBytes);

    /// <summary>
    /// Fetches a site's declared raster, then its root favicon, without reading/writing icon caches.
    /// Uses existing HTML/manifest selection and self-hosted certificate policy. Google fallback is
    /// deliberately absent. The total direct attempt is bounded to 15 seconds and can be cancelled.
    /// Null means the page exposes no usable raster without its signed-in browser context.
    /// </summary>
    internal static async Task<byte[]?> FetchSiteIconPreviewAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var options = new IconFetchOptions(timeout.Token, MaxBytes: 512 * 1024);
        try
        {
            byte[]? bytes = RasterOrNull(await TryFetchIconFromSiteAsync(url, options));
            timeout.Token.ThrowIfCancellationRequested();
            if (bytes is not { Length: >= 100 })
            {
                string faviconUrl = new Uri(uri, "/favicon.ico").AbsoluteUri;
                bytes = RasterOrNull(await TryDownloadAsync(TolerantHttp, faviconUrl, options));
            }
            timeout.Token.ThrowIfCancellationRequested();
            return bytes is { Length: >= 100 } ? bytes : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Debug("Direct page-icon preview timed out");
            return null;
        }
    }

    /// <summary>Forces origin revalidation only for preview requests, leaving normal fetches unchanged.</summary>
    private static void ApplyIconFetchOptions(HttpRequestMessage request, IconFetchOptions? options)
    {
        if (options == null) return;
        options.CancellationToken.ThrowIfCancellationRequested();
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
    }

    /// <summary>Returns headers only so manifest/image response bodies can be bounded before buffering.</summary>
    private static async Task<HttpResponseMessage> SendIconPreviewRequestAsync(
        HttpClient client, string url, IconFetchOptions options)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyIconFetchOptions(request, options);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, options.CancellationToken);
    }

    /// <summary>Rejects declared and streaming oversize bodies; cancellation covers every body read.</summary>
    private static async Task<byte[]?> ReadBoundedIconPreviewAsync(HttpResponseMessage response, IconFetchOptions options)
    {
        if (response.Content.Headers.ContentLength is long length && length > options.MaxBytes)
        {
            return null;
        }
        using var stream = await response.Content.ReadAsStreamAsync(options.CancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(chunk.AsMemory(), options.CancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > options.MaxBytes) return null;
            buffer.Write(chunk, 0, count);
        }
        options.CancellationToken.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }
}
