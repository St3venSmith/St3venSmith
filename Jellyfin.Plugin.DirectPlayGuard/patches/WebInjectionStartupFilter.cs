using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DirectPlayGuard.Web;

/// <summary>
/// Rewrites Jellyfin Web's actual index.html RESPONSE and injects the Direct
/// Play Guard client script. This follows the Jellyfin-12 request-time pattern
/// used by current JavaScript Injector builds: let Jellyfin generate the page,
/// buffer it, then make one additive change.
/// </summary>
public sealed class WebInjectionStartupFilter : IStartupFilter
{
    private readonly ILogger<WebInjectionStartupFilter> _logger;
    private int _loggedOnce;

    public WebInjectionStartupFilter(
        ILogger<WebInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Register before Jellyfin's remaining pipeline so we can inspect
            // the final index.html response after downstream middleware runs.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> nextMiddleware)
    {
        if (!IsIndexRequest(context.Request.Path.Value)
            || !HttpMethods.IsGet(context.Request.Method))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.EnableWebMessage)
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        // A compressed or partial response cannot be safely rewritten.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await nextMiddleware().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains(
                "text/html",
                StringComparison.OrdinalIgnoreCase) ?? false);

        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            const string marker = "data-direct-play-guard=";
            if (!html.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                var bodyClose = html.LastIndexOf(
                    "</body>",
                    StringComparison.OrdinalIgnoreCase);

                if (bodyClose >= 0)
                {
                    var version = Plugin.Instance?.Version.ToString() ?? "0";
                    var stamp = DateTime.UtcNow.Ticks;
                    var tag =
                        $"<script data-direct-play-guard=\"middleware\" " +
                        $"defer src=\"../DirectPlayGuard/client.js?v={version}-{stamp}\"></script>";

                    html = html[..bodyClose]
                        + tag
                        + "\n"
                        + html[bodyClose..];

                    if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                    {
                        _logger.LogInformation(
                            "Direct Play Guard injected its Jellyfin Web helper using request-time index.html rewriting");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Direct Play Guard index.html response rewrite failed; serving the original page");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        // Downstream validators describe the unmodified document and are now
        // invalid. Removing them also prevents a stale 304 from hiding updates.
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";

        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith(
                "/web/index.html",
                StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(
                "/web/",
                StringComparison.OrdinalIgnoreCase)
            || path.Equals(
                "/web",
                StringComparison.OrdinalIgnoreCase);
    }
}
