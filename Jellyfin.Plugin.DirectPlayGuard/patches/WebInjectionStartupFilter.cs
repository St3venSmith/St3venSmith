using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DirectPlayGuard.Web;

/// <summary>
/// Last-resort Jellyfin Web injection path. When File Transformation is
/// installed, this filter stands down so File Transformation can preserve
/// transformations from every installed UI plugin.
/// </summary>
public sealed class WebInjectionStartupFilter : IStartupFilter
{
    private readonly IApplicationPaths _paths;
    private readonly ILogger<WebInjectionStartupFilter> _logger;

    public WebInjectionStartupFilter(
        IApplicationPaths paths,
        ILogger<WebInjectionStartupFilter> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, following) =>
            {
                if (!ShouldInject(context.Request))
                {
                    await following().ConfigureAwait(false);
                    return;
                }

                // Never short-circuit File Transformation. It is the preferred
                // path and allows multiple Jellyfin UI plugins to coexist.
                if (FileTransformationIntegration.IsAvailable())
                {
                    await following().ConfigureAwait(false);
                    return;
                }

                var config = Plugin.Instance?.Configuration;
                if (config is null || !config.Enabled || !config.EnableWebMessage)
                {
                    await following().ConfigureAwait(false);
                    return;
                }

                var indexPath = string.IsNullOrWhiteSpace(_paths.WebPath)
                    ? null
                    : Path.Combine(_paths.WebPath, "index.html");

                if (indexPath is null || !File.Exists(indexPath))
                {
                    await following().ConfigureAwait(false);
                    return;
                }

                try
                {
                    var html = await File.ReadAllTextAsync(
                        indexPath,
                        context.RequestAborted).ConfigureAwait(false);

                    var output = Inject(html);
                    var bytes = Encoding.UTF8.GetBytes(output);

                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    context.Response.Headers.Pragma = "no-cache";
                    context.Response.Headers.Expires = "0";
                    context.Response.ContentLength = bytes.Length;

                    if (!HttpMethods.IsHead(context.Request.Method))
                    {
                        await context.Response.Body.WriteAsync(
                            bytes,
                            context.RequestAborted).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Direct Play Guard fallback index.html injection failed");
                    await following().ConfigureAwait(false);
                }
            });

            next(app);
        };
    }

    private static bool ShouldInject(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        return string.Equals(path, "/web", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static string Inject(string html)
    {
        const string marker = "data-direct-play-guard=\"fallback\"";
        if (html.Contains(marker, StringComparison.Ordinal))
        {
            return html;
        }

        const string closingBody = "</body>";
        var bodyIndex = html.LastIndexOf(
            closingBody,
            StringComparison.OrdinalIgnoreCase);

        if (bodyIndex < 0)
        {
            return html;
        }

        var version = Plugin.Instance?.Version.ToString() ?? "0";
        var tag =
            $"<script data-direct-play-guard=\"fallback\" " +
            $"src=\"../DirectPlayGuard/client.js?v={version}\" defer></script>\n";

        return html.Insert(bodyIndex, tag);
    }
}
