using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DirectPlayGuard.Web;

/// <summary>
/// Injects a small helper into Jellyfin Web that replaces Jellyfin's generic
/// unsupported-media dialog with the configured Direct Play Guard guidance.
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
                    _logger.LogWarning(
                        "Direct Play Guard could not find Jellyfin Web index.html. Web path: {WebPath}",
                        _paths.WebPath);
                    await following().ConfigureAwait(false);
                    return;
                }

                try
                {
                    var html = await File.ReadAllTextAsync(indexPath, context.RequestAborted)
                        .ConfigureAwait(false);

                    var output = Inject(html, config);
                    var bytes = Encoding.UTF8.GetBytes(output);

                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    context.Response.Headers.Pragma = "no-cache";
                    context.Response.Headers.Expires = "0";
                    context.Response.ContentLength = bytes.Length;

                    if (!HttpMethods.IsHead(context.Request.Method))
                    {
                        await context.Response.Body.WriteAsync(bytes, context.RequestAborted)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Direct Play Guard could not inject its Jellyfin Web helper");
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

    private static string Inject(
        string html,
        Configuration.PluginConfiguration config)
    {
        const string marker = "data-direct-play-guard=\"inline-v2\"";
        if (html.Contains(marker, StringComparison.Ordinal))
        {
            return html;
        }

        var options = new
        {
            title = config.MessageTitle ?? string.Empty,
            message = config.MessageText ?? string.Empty,
            apps = config.RecommendedApps ?? string.Empty,
            helpUrl = NormalizeHelpUrl(config.HelpUrl)
        };

        var json = JsonSerializer.Serialize(options);
        var script = BuildInlineScript(json);

        var closingBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closingBody < 0)
        {
            return html;
        }

        var tag = $"<script data-direct-play-guard=\"inline-v2\">{script}</script>\n";
        return html.Insert(closingBody, tag);
    }

    private static string NormalizeHelpUrl(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return uri.ToString();
        }

        return "https://jellyfin.org/clients/";
    }

    private static string BuildInlineScript(string configJson)
    {
        return $$"""
(function () {
  'use strict';

  if (window.__directPlayGuardInlineV2) return;
  window.__directPlayGuardInlineV2 = true;

  const cfg = {{configJson}};
  let lastShown = 0;

  function compatibilityFailureText(text) {
    text = String(text || '').toLowerCase();
    return text.includes('media is not supported by this client')
      || text.includes('not supported by this client')
      || text.includes('not compatible with the media')
      || text.includes("isn't compatible with the media")
      || text.includes('not sending a compatible media format')
      || text.includes('server is not sending a compatible media format');
  }

  function showMessage() {
    const now = Date.now();
    if (now - lastShown < 1500 || document.getElementById('direct-play-guard-modal')) {
      return;
    }

    lastShown = now;

    const overlay = document.createElement('div');
    overlay.id = 'direct-play-guard-modal';
    overlay.style.cssText =
      'position:fixed;inset:0;z-index:2147483647;display:flex;align-items:center;' +
      'justify-content:center;background:rgba(0,0,0,.76);padding:20px;box-sizing:border-box';

    const box = document.createElement('div');
    box.style.cssText =
      'width:min(620px,100%);max-height:90vh;overflow:auto;background:#151515;color:#fff;' +
      'border-radius:12px;padding:28px;box-shadow:0 20px 60px rgba(0,0,0,.6);font-family:inherit';

    const title = document.createElement('h2');
    title.textContent = cfg.title || 'Playback not supported on this device';
    title.style.cssText = 'margin:0 0 14px;font-size:1.55rem;line-height:1.25';

    const message = document.createElement('p');
    message.textContent =
      cfg.message ||
      'This server does not transcode media. Your current client cannot play this file directly. Use a recommended Jellyfin app and set playback quality to Original or Maximum.';
    message.style.cssText = 'margin:0 0 18px;line-height:1.55;opacity:.95';

    box.appendChild(title);
    box.appendChild(message);

    if (cfg.apps) {
      const apps = document.createElement('div');
      apps.textContent = cfg.apps;
      apps.style.cssText =
        'white-space:pre-wrap;line-height:1.55;margin:0 0 20px;padding:14px 16px;' +
        'border-radius:8px;background:rgba(255,255,255,.07)';
      box.appendChild(apps);
    }

    const buttons = document.createElement('div');
    buttons.style.cssText =
      'display:flex;gap:10px;justify-content:flex-end;align-items:center;flex-wrap:wrap';

    if (cfg.helpUrl) {
      const getApps = document.createElement('a');
      getApps.textContent = 'Get recommended Jellyfin apps';
      getApps.href = cfg.helpUrl;
      getApps.target = '_blank';
      getApps.rel = 'noopener noreferrer';
      getApps.style.cssText =
        'display:inline-block;text-decoration:none;background:#00a4dc;color:#fff;' +
        'border-radius:6px;padding:11px 16px;font-weight:600';
      buttons.appendChild(getApps);
    }

    const close = document.createElement('button');
    close.type = 'button';
    close.textContent = 'Close';
    close.style.cssText =
      'border:0;border-radius:6px;padding:11px 16px;cursor:pointer;' +
      'background:#333;color:#fff;font:inherit;font-weight:600';
    close.addEventListener('click', function () {
      overlay.remove();
    });

    buttons.appendChild(close);
    box.appendChild(buttons);
    overlay.appendChild(box);

    overlay.addEventListener('click', function (event) {
      if (event.target === overlay) overlay.remove();
    });

    document.body.appendChild(overlay);
  }

  function handleDialog(dialog) {
    if (!dialog || dialog.nodeType !== 1) return false;
    if (dialog.dataset && dialog.dataset.directPlayGuardHandled === '1') return false;

    const text = (dialog.innerText || dialog.textContent || '').trim();
    const lower = text.toLowerCase();

    if (!lower.includes('playback error') || !compatibilityFailureText(lower)) {
      return false;
    }

    if (dialog.dataset) {
      dialog.dataset.directPlayGuardHandled = '1';
    }

    const buttons = dialog.querySelectorAll ? dialog.querySelectorAll('button') : [];
    if (buttons && buttons.length) {
      try {
        buttons[buttons.length - 1].click();
      } catch (_) {
        dialog.style.display = 'none';
      }
    } else {
      dialog.style.display = 'none';
    }

    setTimeout(showMessage, 0);
    return true;
  }

  function scan(root) {
    try {
      const candidates = [];

      if (root && root.nodeType === 1) {
        candidates.push(root);
      }

      const scope = root && root.querySelectorAll ? root : document;
      const selectors = [
        '.formDialog',
        '[role="dialog"]',
        '.MuiDialog-root',
        '.MuiDialog-container',
        '.MuiPaper-root'
      ];

      for (const selector of selectors) {
        for (const node of scope.querySelectorAll(selector)) {
          candidates.push(node);
        }
      }

      for (const dialog of candidates) {
        if (handleDialog(dialog)) return true;
      }
    } catch (_) { }

    return false;
  }

  function start() {
    scan(document);

    if (!document.body) return;

    const observer = new MutationObserver(function (mutations) {
      for (const mutation of mutations) {
        if (mutation.target && mutation.target.nodeType === 1) {
          scan(mutation.target);
        }

        for (const node of mutation.addedNodes || []) {
          if (node && node.nodeType === 1) {
            scan(node);
          }
        }
      }
    });

    observer.observe(document.body, {
      childList: true,
      subtree: true,
      characterData: true
    });

    // Safety net for Jellyfin UI changes that update dialogs outside the
    // mutation shape above.
    window.setInterval(function () {
      scan(document);
    }, 1000);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start, { once: true });
  } else {
    start();
  }
})();
""";
    }
}
