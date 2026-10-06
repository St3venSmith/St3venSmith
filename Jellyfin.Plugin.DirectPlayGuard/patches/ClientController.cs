using System.Text.Json;
using System.Text.RegularExpressions;
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

        var version = Plugin.Instance?.Version.ToString() ?? "0";
        var imageUrl = ResolveImageUrl(cfg, version);

        var options = new
        {
            title = cfg.MessageTitle ?? string.Empty,
            message = cfg.MessageText ?? string.Empty,
            apps = cfg.RecommendedApps ?? string.Empty,
            showApps = cfg.ShowRecommendedApps,
            helpUrl = NormalizeHttpUrl(cfg.HelpUrl),
            footer = cfg.FooterText ?? string.Empty,
            imageUrl,
            imageMaxHeight = Math.Clamp(cfg.ImageMaxHeight, 80, 600),
            imageFit = string.Equals(cfg.ImageFit, "cover", StringComparison.OrdinalIgnoreCase) ? "cover" : "contain",
            imageRadius = Math.Clamp(cfg.ImageCornerRadius, 0, 60),
            modalWidth = Math.Clamp(cfg.ModalWidth, 360, 1000),
            accent = NormalizeColor(cfg.AccentColor),
            align = string.Equals(cfg.TextAlignment, "left", StringComparison.OrdinalIgnoreCase) ? "left" : "center",
        };

        var json = JsonSerializer.Serialize(options);
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";

        return Content(
            BuildClientScript(json),
            "application/javascript; charset=utf-8");
    }

    private static string? ResolveImageUrl(
        Configuration.PluginConfiguration cfg,
        string version)
    {
        if (string.Equals(cfg.ImageMode, "upload", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(cfg.UploadedImageFileName))
        {
            return "../DirectPlayGuard/image?v=" + Uri.EscapeDataString(version);
        }

        if (string.Equals(cfg.ImageMode, "url", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeHttpUrl(cfg.ImageUrl);
        }

        return null;
    }

    private static string? NormalizeHttpUrl(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return uri.ToString();
        }

        return null;
    }

    private static string NormalizeColor(string? value)
    {
        var color = value?.Trim() ?? string.Empty;
        return Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)
            ? color.ToUpperInvariant()
            : "#00A4DC";
    }

    private static string BuildClientScript(string configJson)
    {
        return $$"""
(function () {
  'use strict';

  window.__directPlayGuardLoaded = 'v4';
  try {
    document.documentElement.setAttribute('data-direct-play-guard', 'v4');
    console.info('[Direct Play Guard] Jellyfin Web helper v4 loaded');
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
      || !!dialog.querySelector('.formDialogHeaderTitle')
      || dialog.getAttribute('role') === 'dialog';
  }

  function createImage() {
    if (!cfg.imageUrl) return null;

    const wrap = document.createElement('div');
    wrap.style.cssText =
      'display:flex;justify-content:center;align-items:center;width:100%;' +
      'margin:0 0 20px;overflow:hidden';

    const img = document.createElement('img');
    img.src = cfg.imageUrl;
    img.alt = '';
    img.decoding = 'async';
    img.style.cssText =
      'display:block;max-width:100%;width:auto;height:auto;object-fit:' + cfg.imageFit + ';' +
      'max-height:' + cfg.imageMaxHeight + 'px;border-radius:' + cfg.imageRadius + 'px';

    img.addEventListener('error', function () {
      wrap.remove();
    }, { once: true });

    wrap.appendChild(img);
    return wrap;
  }

  function createMessage() {
    const el = document.createElement('p');
    el.textContent = cfg.message ||
      'This server does not transcode media. Your current client cannot play this file directly. Use a recommended Jellyfin app and set playback quality to Original or Maximum.';
    el.style.cssText =
      'margin:0 0 18px;line-height:1.55;text-align:' + cfg.align + ';opacity:.96';
    return el;
  }

  function createAppsCard() {
    if (!cfg.showApps || !String(cfg.apps || '').trim()) return null;

    const card = document.createElement('div');
    card.style.cssText =
      'margin:0 0 20px;padding:14px 16px;border-radius:10px;' +
      'background:rgba(255,255,255,.07);text-align:left';

    const heading = document.createElement('div');
    heading.textContent = 'Recommended apps';
    heading.style.cssText =
      'font-weight:700;margin:0 0 10px;font-size:.95rem;opacity:.92';
    card.appendChild(heading);

    const lines = String(cfg.apps)
      .split(/\r?\n/)
      .map(function (line) { return line.trim(); })
      .filter(Boolean);

    lines.forEach(function (line, index) {
      const row = document.createElement('div');
      row.style.cssText =
        'display:flex;flex-wrap:wrap;gap:4px 8px;line-height:1.45;' +
        (index ? 'margin-top:7px;' : '');

      const splitAt = line.indexOf(':');
      if (splitAt > 0) {
        const label = document.createElement('strong');
        label.textContent = line.slice(0, splitAt).trim() + ':';
        label.style.cssText = 'min-width:125px';

        const value = document.createElement('span');
        value.textContent = line.slice(splitAt + 1).trim();

        row.appendChild(label);
        row.appendChild(value);
      } else {
        row.textContent = line;
      }

      card.appendChild(row);
    });

    return card;
  }

  function createFooterText() {
    if (!String(cfg.footer || '').trim()) return null;
    const el = document.createElement('div');
    el.textContent = cfg.footer;
    el.style.cssText =
      'margin-top:4px;font-size:.88rem;line-height:1.4;opacity:.68;text-align:' + cfg.align;
    return el;
  }

  function styleDialog(dialog) {
    const content = dialog.querySelector('.formDialogContent')
      || dialog.querySelector('[class*="DialogContent"]');

    if (content) {
      content.style.maxWidth = cfg.modalWidth + 'px';
      content.style.width = 'min(' + cfg.modalWidth + 'px, calc(100vw - 36px))';
    }

    const inner = dialog.querySelector('.dialogContentInner');
    if (inner) {
      inner.style.paddingLeft = '24px';
      inner.style.paddingRight = '24px';
    }

    const title =
      dialog.querySelector('.formDialogHeaderTitle')
      || dialog.querySelector('[class*="DialogTitle"]')
      || dialog.querySelector('h1, h2, h3');

    if (title) {
      title.textContent = cfg.title || 'Playback not supported on this device';
      title.style.textAlign = cfg.align;
      title.style.lineHeight = '1.25';
    }

    return title;
  }

  function addHelpButton(dialog) {
    if (!cfg.helpUrl) return;

    const footer = dialog.querySelector('.formDialogFooter');
    if (!footer || footer.querySelector('[data-direct-play-guard-help="1"]')) return;

    const link = document.createElement('a');
    link.setAttribute('data-direct-play-guard-help', '1');
    link.href = cfg.helpUrl;
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
    link.textContent = 'Get recommended Jellyfin apps';
    link.style.cssText =
      'display:inline-flex;align-items:center;justify-content:center;' +
      'box-sizing:border-box;min-height:44px;padding:10px 16px;margin:6px;' +
      'border-radius:6px;text-decoration:none;font-weight:600;color:#fff;' +
      'background:' + cfg.accent;

    footer.insertBefore(link, footer.firstChild);
  }

  function fillDialog(dialog) {
    if (!looksLikePlaybackDialog(dialog)) return false;
    if (dialog.dataset && dialog.dataset.directPlayGuardHandled === '1') return true;

    const now = Date.now();
    if (now - lastHandled < 150) return true;
    lastHandled = now;

    try {
      if (dialog.dataset) dialog.dataset.directPlayGuardHandled = '1';

      styleDialog(dialog);

      const body =
        dialog.querySelector('.text')
        || dialog.querySelector('.dialogContentInner')
        || dialog.querySelector('[class*="DialogContent"]');

      if (!body) {
        showFallbackOverlay();
        dialog.style.display = 'none';
        return true;
      }

      body.textContent = '';
      body.style.textAlign = cfg.align;

      const image = createImage();
      if (image) body.appendChild(image);

      body.appendChild(createMessage());

      const apps = createAppsCard();
      if (apps) body.appendChild(apps);

      const footerText = createFooterText();
      if (footerText) body.appendChild(footerText);

      const buttons = dialog.querySelectorAll('button');
      if (buttons.length) {
        buttons[buttons.length - 1].textContent = 'Close';
      }

      addHelpButton(dialog);

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
      'justify-content:center;background:rgba(0,0,0,.76);padding:18px;box-sizing:border-box';

    const box = document.createElement('div');
    box.style.cssText =
      'box-sizing:border-box;width:min(' + cfg.modalWidth + 'px,100%);max-height:90vh;' +
      'overflow:auto;background:#151515;color:#fff;border-radius:12px;padding:26px;' +
      'box-shadow:0 20px 60px rgba(0,0,0,.6);font-family:inherit';

    const image = createImage();
    if (image) box.appendChild(image);

    const title = document.createElement('h2');
    title.textContent = cfg.title || 'Playback not supported on this device';
    title.style.cssText =
      'margin:0 0 14px;font-size:1.55rem;line-height:1.25;text-align:' + cfg.align;
    box.appendChild(title);

    box.appendChild(createMessage());

    const apps = createAppsCard();
    if (apps) box.appendChild(apps);

    const footerText = createFooterText();
    if (footerText) box.appendChild(footerText);

    const actions = document.createElement('div');
    actions.style.cssText =
      'display:flex;gap:10px;justify-content:center;align-items:center;' +
      'flex-wrap:wrap;margin-top:22px';

    if (cfg.helpUrl) {
      const getApps = document.createElement('a');
      getApps.textContent = 'Get recommended Jellyfin apps';
      getApps.href = cfg.helpUrl;
      getApps.target = '_blank';
      getApps.rel = 'noopener noreferrer';
      getApps.style.cssText =
        'display:inline-flex;align-items:center;justify-content:center;' +
        'text-decoration:none;background:' + cfg.accent + ';color:#fff;' +
        'border-radius:6px;padding:11px 16px;font-weight:600;min-height:44px;box-sizing:border-box';
      actions.appendChild(getApps);
    }

    const close = document.createElement('button');
    close.type = 'button';
    close.textContent = 'Close';
    close.style.cssText =
      'border:0;border-radius:6px;padding:11px 16px;cursor:pointer;' +
      'background:#333;color:#fff;font:inherit;font-weight:600;min-height:44px';
    close.addEventListener('click', function () { overlay.remove(); });

    actions.appendChild(close);
    box.appendChild(actions);
    overlay.appendChild(box);

    overlay.addEventListener('click', function (event) {
      if (event.target === overlay) overlay.remove();
    });

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
      scan(document);
    });

    observer.observe(document.body, {
      childList: true,
      subtree: true,
      characterData: true
    });

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
