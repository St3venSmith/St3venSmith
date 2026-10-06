using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DirectPlayGuard.Api;

/// <summary>Serves the Jellyfin Web helper used for custom playback guidance.</summary>
[ApiController]
[AllowAnonymous]
[Route("DirectPlayGuard")]
public sealed class ClientController : ControllerBase
{
    [HttpGet("client.js")]
    [Produces("application/javascript")]
    public IActionResult GetClientScript()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EnableWebMessage)
        {
            return Content(
                "/* Direct Play Guard web message disabled */",
                "application/javascript");
        }

        var options = new
        {
            title = cfg.MessageTitle ?? string.Empty,
            message = cfg.MessageText ?? string.Empty,
            apps = cfg.RecommendedApps ?? string.Empty,
            helpUrl = NormalizeHelpUrl(cfg.HelpUrl),
        };

        var json = JsonSerializer.Serialize(options);
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";

        return Content(
            BuildClientScript(json),
            "application/javascript; charset=utf-8");
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

    private static string BuildClientScript(string configJson)
    {
        return $$"""
(function () {
  'use strict';

  // Version 3 browser helper. This marker lets us verify from DevTools that
  // the script actually loaded, independently of whether playback succeeds.
  window.__directPlayGuardLoaded = 'v3';
  try {
    document.documentElement.setAttribute('data-direct-play-guard', 'v3');
    console.info('[Direct Play Guard] Jellyfin Web helper loaded');
  } catch (_) { }

  const cfg = {{configJson}};
  let lastHandled = 0;

  function isCompatibilityMessage(text) {
    const value = String(text || '').toLowerCase();

    return value.includes('playback failed because the media is not supported by this client')
      || value.includes('media is not supported by this client')
      || value.includes('not supported by this client')
      || value.includes('not compatible with the media')
      || value.includes("isn't compatible with the media")
      || value.includes('not sending a compatible media format')
      || value.includes('server is not sending a compatible media format');
  }

  function looksLikePlaybackDialog(dialog) {
    if (!dialog || dialog.nodeType !== 1) return false;

    const text = (dialog.innerText || dialog.textContent || '').trim();
    if (!isCompatibilityMessage(text)) return false;

    const lower = text.toLowerCase();
    return lower.includes('playback error')
      || dialog.querySelector('.formDialogHeaderTitle')
      || dialog.getAttribute('role') === 'dialog';
  }

  function fillDialog(dialog) {
    if (!looksLikePlaybackDialog(dialog)) return false;
    if (dialog.dataset && dialog.dataset.directPlayGuardHandled === '1') return true;

    const now = Date.now();
    if (now - lastHandled < 250) return true;
    lastHandled = now;

    try {
      if (dialog.dataset) dialog.dataset.directPlayGuardHandled = '1';

      const title =
        dialog.querySelector('.formDialogHeaderTitle')
        || dialog.querySelector('[class*="DialogTitle"]')
        || dialog.querySelector('h1, h2, h3');

      if (title) {
        title.textContent = cfg.title || 'Playback not supported on this device';
      }

      let body =
        dialog.querySelector('.text')
        || dialog.querySelector('.dialogContentInner')
        || dialog.querySelector('[class*="DialogContent"]');

      if (body) {
        body.textContent = '';

        const message = document.createElement('p');
        message.textContent =
          cfg.message ||
          'This server does not transcode media. Your current client cannot play this file directly. Use a recommended Jellyfin app and set playback quality to Original or Maximum.';
        message.style.cssText = 'margin:0 0 16px;line-height:1.55';
        body.appendChild(message);

        if (cfg.apps) {
          const apps = document.createElement('div');
          apps.textContent = cfg.apps;
          apps.style.cssText =
            'white-space:pre-wrap;line-height:1.55;margin:0 0 16px;padding:12px 14px;' +
            'border-radius:8px;background:rgba(255,255,255,.07)';
          body.appendChild(apps);
        }

        if (cfg.helpUrl) {
          const link = document.createElement('a');
          link.textContent = 'Get recommended Jellyfin apps';
          link.href = cfg.helpUrl;
          link.target = '_blank';
          link.rel = 'noopener noreferrer';
          link.style.cssText =
            'display:inline-block;margin-top:2px;text-decoration:none;font-weight:600';
          body.appendChild(link);
        }
      } else {
        // If Jellyfin changes its dialog markup again, use our own overlay
        // rather than leaving the user with the generic error.
        showFallbackOverlay();
        dialog.style.display = 'none';
      }

      const buttons = dialog.querySelectorAll('button');
      if (buttons.length) {
        buttons[buttons.length - 1].textContent = 'Close';
      }

      console.info('[Direct Play Guard] Replaced Jellyfin playback error dialog');
      return true;
    } catch (err) {
      console.error('[Direct Play Guard] Failed to rewrite playback dialog', err);
      return false;
    }
  }

  function showFallbackOverlay() {
    if (document.getElementById('direct-play-guard-modal')) return;

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

    const actions = document.createElement('div');
    actions.style.cssText =
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
      actions.appendChild(getApps);
    }

    const close = document.createElement('button');
    close.type = 'button';
    close.textContent = 'Close';
    close.style.cssText =
      'border:0;border-radius:6px;padding:11px 16px;cursor:pointer;' +
      'background:#333;color:#fff;font:inherit;font-weight:600';
    close.addEventListener('click', function () { overlay.remove(); });

    actions.appendChild(close);
    box.appendChild(actions);
    overlay.appendChild(box);
    document.body.appendChild(overlay);
  }

  function scan(root) {
    try {
      const found = new Set();
      const scope = root && root.querySelectorAll ? root : document;

      if (root && root.nodeType === 1) found.add(root);

      const selectors = [
        '.formDialog',
        '[role="dialog"]',
        '.MuiDialog-root',
        '.MuiDialog-container',
        '.MuiPaper-root'
      ];

      for (const selector of selectors) {
        for (const node of scope.querySelectorAll(selector)) {
          found.add(node);
        }
      }

      for (const dialog of found) {
        if (fillDialog(dialog)) return true;
      }
    } catch (_) { }

    return false;
  }

  function start() {
    scan(document);

    if (!document.body) return;

    const observer = new MutationObserver(function () {
      // Jellyfin builds the dialog in multiple DOM operations. Scanning the
      // whole document here is intentional so we also catch a dialog whose
      // text/title was populated after the container was inserted.
      scan(document);
    });

    observer.observe(document.body, {
      childList: true,
      subtree: true,
      characterData: true
    });

    // Extra safety net for React/MUI/dialog implementation changes.
    setInterval(function () {
      scan(document);
    }, 500);
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
