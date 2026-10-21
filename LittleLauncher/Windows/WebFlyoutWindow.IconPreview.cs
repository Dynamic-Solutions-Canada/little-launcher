using LittleLauncher.Classes;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using global::Windows.Storage.Streams;

namespace LittleLauncher.Windows;

/// <summary>Non-mutating automatic-icon previews using only an existing, awake matching page.</summary>
public sealed partial class WebFlyoutWindow
{
    // Shared verbatim with the service-worker bridge so discovery policy cannot drift.
    private const string PageIconDiscoveryScript = """
            function bestIconUrl() {
                var best = { url: '', size: 0 };

                function consider(url, size) {
                    if (!url || size <= best.size) return;
                    try { best = { url: new URL(url, location.href).href, size: size }; } catch (e) { }
                }

                function sizeOf(attr) {
                    if (!attr) return 0;
                    var m = /(\d+)\s*x\s*(\d+)/i.exec(attr);
                    return m ? parseInt(m[1], 10) : 0;
                }

                document.querySelectorAll('link[rel~="icon"]').forEach(function (l) {
                    var href = l.getAttribute('href') || '';
                    var declared = sizeOf(l.getAttribute('sizes'));

                    // An .ico is a container, and what it holds is not declared anywhere: a plain
                    // favicon.ico routinely carries a 256px frame alongside the 16 and 32 the tab
                    // uses. Discord is the case that proved it - no manifest, no apple-touch-icon,
                    // one <link rel=icon> to favicon.ico, and a 256px PNG inside it. Guessing high
                    // enough to be fetched is the only way to find out; the host measures what
                    // actually arrives and will not shrink what it already has, so guessing wrong
                    // costs a download and nothing else. Below a declared manifest icon on purpose,
                    // so a site that does say what it has still wins.
                    var isIco = /\.ico($|[?#])/i.test(href);
                    consider(href, declared || (isIco ? 128 : 32));
                });
                document.querySelectorAll('link[rel~="apple-touch-icon"], link[rel~="apple-touch-icon-precomposed"]').forEach(function (l) {
                    consider(l.getAttribute('href'), sizeOf(l.getAttribute('sizes')) || 180);
                });

                var manifest = document.querySelector('link[rel~="manifest"]');
                if (!manifest || !manifest.getAttribute('href')) return Promise.resolve(best);

                return fetch(new URL(manifest.getAttribute('href'), location.href).href, { credentials: 'include' })
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (j) {
                        (j && j.icons ? j.icons : []).forEach(function (i) {
                            consider(i.src, sizeOf(i.sizes));
                        });
                        return best;
                    })
                    .catch(function () { return best; });
            }
        """;

    /// <summary>
    /// Tries the current signed-in page first, then a fresh direct-site HTTP read. No browser
    /// lifecycle or icon/cache mutation occurs here. The chooser supplies its live URL/kind
    /// predicate and cancellation token so every awaited result is rejected after context changes.
    /// </summary>
    internal static async Task<byte[]?> FetchPageIconPreviewAsync(
        string launcherId, string url, Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        bool IsCurrent() => !cancellationToken.IsCancellationRequested && isCurrent();
        if (!IsCurrent()) return null;

        byte[]? loaded = null;
        try
        {
            loaded = await TryFetchLoadedPageIconPreviewAsync(launcherId, url, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "The loaded page could not provide an icon preview; trying its site directly");
        }
        if (!IsCurrent()) return null;
        if (loaded is { Length: > 0 }) return loaded;

        byte[]? direct = await Services.FaviconService.FetchSiteIconPreviewAsync(NormalizeUrl(url), cancellationToken);
        if (!IsCurrent()) return null;
        return direct == null ? null : NormalizeAdoptedIcon(direct);
    }

    /// <summary>
    /// Reads a matching owned tab without creating, resuming, navigating or adopting an icon.
    /// Null means the existing page cannot provide a usable raster. Each asynchronous phase
    /// rejects navigation, launcher changes, suspension and cancellation before using its result.
    /// </summary>
    internal static async Task<byte[]?> TryFetchLoadedPageIconPreviewAsync(
        string launcherId, string url, CancellationToken cancellationToken)
    {
        if (!Instances.TryGetValue(launcherId, out var window))
        {
            return null;
        }
        var tab = window._tabs.FirstOrDefault(candidate => candidate.HomeKey != null && candidate.HasLoaded
            && candidate.View.CoreWebView2 is { } candidateCore
            && MatchesPageIconPreviewUrl(candidateCore.Source, url));
        if (tab?.View.CoreWebView2 is not { } core)
        {
            return null;
        }

        bool IsCurrent() => !cancellationToken.IsCancellationRequested
            && Instances.TryGetValue(launcherId, out var current) && ReferenceEquals(current, window)
            && window._launcher.IsWebLauncher
            && string.Equals(window._launcher.WebAddress, url, StringComparison.Ordinal)
            && window._tabs.Contains(tab) && ReferenceEquals(tab.View.CoreWebView2, core)
            && !core.IsSuspended
            && MatchesPageIconPreviewUrl(core.Source, url);

        if (!IsCurrent())
        {
            return null;
        }
        try
        {
            // No postMessage: the normal bridge's pageIcon handler adopts immediately.
            // The preview expression returns bytes only, through CDP's isolated result channel.
            string expression = "(async function () {\n" + PageIconDiscoveryScript + "\n" + """
                var timeout;
                var controller = new AbortController();
                try {
                    var best = await Promise.race([
                        bestIconUrl(),
                        new Promise(function (_, reject) {
                            timeout = setTimeout(function () { controller.abort(); reject(new Error('Icon discovery timed out')); }, 7000);
                        })
                    ]);
                    if (!best.url) return null;
                    var response = await fetch(best.url, { credentials: 'include', cache: 'reload', signal: controller.signal });
                    if (!response.ok) return null;
                    var blob = await response.blob();
                    if (!blob.size || blob.size > 512 * 1024 || blob.type.indexOf('svg') >= 0) return null;
                    return await new Promise(function (resolve, reject) {
                        var reader = new FileReader();
                        reader.onload = function () { resolve(reader.result); };
                        reader.onerror = reject;
                        reader.readAsDataURL(blob);
                    });
                } finally {
                    clearTimeout(timeout);
                    controller.abort();
                }
                })()
                """;
            string parameters = JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true });
            async Task<string> EvaluateAsync() => await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters);
            string result = await EvaluateAsync().WaitAsync(TimeSpan.FromSeconds(9), cancellationToken);
            if (!IsCurrent())
            {
                return null;
            }
            string? dataUrl = JsonNode.Parse(result)?["result"]?["value"]?.GetValue<string>();
            if (dataUrl?.StartsWith("data:", StringComparison.Ordinal) == true && dataUrl.Length <= 720_000)
            {
                int separator = dataUrl.IndexOf(",", StringComparison.Ordinal);
                if (separator > 0 && dataUrl[..separator].EndsWith(";base64", StringComparison.Ordinal))
                {
                    byte[]? png = NormalizeAdoptedIcon(Convert.FromBase64String(dataUrl[(separator + 1)..]));
                    if (png != null && IsCurrent())
                    {
                        return png;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Debug(ex, "Reading the loaded page's declared icon preview failed");
        }

        // Some sites declare only Chromium's tab favicon. This read also needs an awake
        // matching core, and may hang after a renderer exit, so bound the caller's wait.
        if (!IsCurrent())
        {
            return null;
        }
        async Task<byte[]?> ReadFaviconAsync()
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (!IsCurrent() || stream == null || stream.Size == 0 || stream.Size > 512 * 1024)
            {
                return null;
            }
            uint size = (uint)stream.Size;
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            if (!IsCurrent())
            {
                return null;
            }
            var raw = new byte[size];
            reader.ReadBytes(raw);
            return NormalizeAdoptedIcon(raw);
        }
        byte[]? favicon = await ReadFaviconAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        return IsCurrent() ? favicon : null;
    }

    /// <summary>
    /// Compares canonical absolute addresses, including case-sensitive path, query and fragment.
    /// Uri normalizes Chromium's added root slash and host casing without allowing a redirect.
    /// The captured launcher setting is still checked exactly by the request and Confirm guards.
    /// </summary>
    private static bool MatchesPageIconPreviewUrl(string actual, string requested)
    {
        return Uri.TryCreate(NormalizeUrl(actual), UriKind.Absolute, out var actualUri)
            && Uri.TryCreate(NormalizeUrl(requested), UriKind.Absolute, out var requestedUri)
            && string.Equals(actualUri.AbsoluteUri, requestedUri.AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the exact pending bytes only after Confirm, including a smaller explicit choice.
    /// This synchronous atomic swap keeps the caller's URL check adjacent to model adoption.
    /// </summary>
    internal static string StorePageIconPreview(string launcherId, PageIconPreview preview) =>
        preview.Store(GetPageIconPath(launcherId));
}
