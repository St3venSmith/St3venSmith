using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.DirectPlayGuard.Web;

/// <summary>
/// Integrates Direct Play Guard with IAmParadox27's File Transformation plugin.
/// </summary>
public static class FileTransformationIntegration
{
    // IMPORTANT: File Transformation prefers exact filename pipelines over regex
    // pipelines. index.html MUST therefore be registered as the exact string
    // "index.html", otherwise another plugin's exact registration can prevent
    // ours from ever running.
    public const string IndexTransformationId = "75f5d73c-fde5-4a74-9f99-2cf19da26977";
    public const string StringsTransformationId = "df61d86e-fc40-43ec-a9f7-94db77e2b916";

    private static int _registered;

    public static bool IsAvailable()
        => FindAssembly() is not null;

    public static bool IsRegistered
        => Volatile.Read(ref _registered) == 1;

    public static bool Register(ILogger logger)
    {
        var assembly = FindAssembly();
        if (assembly is null)
        {
            return false;
        }

        var pluginInterface = assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        var register = pluginInterface?.GetMethod("RegisterTransformation");
        if (register is null)
        {
            logger.LogWarning(
                "Direct Play Guard found File Transformation but RegisterTransformation was unavailable");
            return false;
        }

        try
        {
            // Join File Transformation's EXACT index.html pipeline. Using a regex
            // here is unsafe because File Transformation selects an exact pipeline
            // before considering any regex pipeline.
            register.Invoke(null, new object?[]
            {
                new JObject
                {
                    ["id"] = IndexTransformationId,
                    ["fileNamePattern"] = "index.html",
                    ["callbackAssembly"] = typeof(FileTransformationIntegration).Assembly.FullName,
                    ["callbackClass"] = typeof(FileTransformationIntegration).FullName,
                    ["callbackMethod"] = nameof(TransformIndexHtml),
                },
            });

            // Jellyfin 12 loads translations from hashed chunks such as:
            // en-us-json.45f09214281c189457b1.chunk.js
            //
            // This fallback edits the native Jellyfin playback-error translation
            // itself. Even if our browser helper fails to load, Jellyfin's own
            // modal will show the Direct Play Guard guidance.
            register.Invoke(null, new object?[]
            {
                new JObject
                {
                    ["id"] = StringsTransformationId,
                    ["fileNamePattern"] = "^[A-Za-z0-9_-]+-json\\.[A-Za-z0-9]+\\.chunk\\.js$",
                    ["callbackAssembly"] = typeof(FileTransformationIntegration).Assembly.FullName,
                    ["callbackClass"] = typeof(FileTransformationIntegration).FullName,
                    ["callbackMethod"] = nameof(TransformLocalizationChunk),
                },
            });

            Interlocked.Exchange(ref _registered, 1);
            logger.LogInformation(
                "Direct Play Guard registered exact index.html and localization transformations with File Transformation");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Direct Play Guard could not register with File Transformation");
            return false;
        }
    }

    /// <summary>
    /// Injects the optional richer browser helper into Jellyfin Web.
    /// </summary>
    public static string TransformIndexHtml(FileTransformationPayload payload)
    {
        var contents = payload?.Contents ?? string.Empty;
        var config = Plugin.Instance?.Configuration;

        if (config is null || !config.Enabled || !config.EnableWebMessage)
        {
            return contents;
        }

        const string marker = "data-direct-play-guard=\"file-transformation\"";
        if (contents.Contains(marker, StringComparison.Ordinal))
        {
            return contents;
        }

        var version = Plugin.Instance?.Version.ToString() ?? "0";
        var tag =
            $"<script data-direct-play-guard=\"file-transformation\" " +
            $"src=\"../DirectPlayGuard/client.js?v={version}\" defer></script>";

        return Regex.Replace(
            contents,
            "(</body>)",
            tag + "$1",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Rewrites the exact native Jellyfin messages that are shown when
    /// transcoding is disabled and the current client cannot play the media.
    /// </summary>
    public static string TransformLocalizationChunk(FileTransformationPayload payload)
    {
        var contents = payload?.Contents ?? string.Empty;
        var config = Plugin.Instance?.Configuration;

        if (config is null || !config.Enabled || !config.EnableWebMessage)
        {
            return contents;
        }

        var html = BuildNativeDialogHtml(config);
        var escaped = EscapeJavaScriptString(html);

        // The screenshoted Jellyfin 12 message is PlaybackError.MEDIA_NOT_SUPPORTED.
        // Patch NoCompatibleStream as well because Jellyfin can reach either path
        // depending on where compatibility fails.
        contents = ReplaceTranslationValue(
            contents,
            "PlaybackError.MEDIA_NOT_SUPPORTED",
            escaped);

        contents = ReplaceTranslationValue(
            contents,
            "PlaybackErrorNoCompatibleStream",
            escaped);

        return contents;
    }

    private static string ReplaceTranslationValue(
        string contents,
        string key,
        string escapedValue)
    {
        var pattern =
            "(?<prefix>\\\"" +
            Regex.Escape(key) +
            "\\\"\\s*:\\s*\\\")" +
            "(?<value>(?:\\\\.|[^\\\"\\\\])*)" +
            "(?<suffix>\\\")";

        return Regex.Replace(
            contents,
            pattern,
            match => match.Groups["prefix"].Value
                + escapedValue
                + match.Groups["suffix"].Value,
            RegexOptions.None,
            TimeSpan.FromSeconds(1));
    }

    private static string BuildNativeDialogHtml(
        Configuration.PluginConfiguration config)
    {
        static string Encode(string? value)
            => WebUtility.HtmlEncode(value ?? string.Empty);

        var title = Encode(config.MessageTitle);
        var message = Encode(config.MessageText);
        var apps = Encode(config.RecommendedApps)
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);

        var helpUrl = NormalizeHelpUrl(config.HelpUrl);

        return
            $"<strong>{title}</strong><br><br>" +
            $"{message}<br><br>" +
            $"<strong>Recommended apps</strong><br>{apps}<br><br>" +
            $"<a href='{WebUtility.HtmlEncode(helpUrl)}' target='_blank' rel='noopener noreferrer'>" +
            "Get recommended Jellyfin apps</a>";
    }

    private static string EscapeJavaScriptString(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string NormalizeHelpUrl(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return uri.ToString();
        }

        return "https://jellyfin.org/clients/";
    }

    private static Assembly? FindAssembly()
        => AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(assembly =>
                assembly.FullName?.Contains(
                    ".FileTransformation",
                    StringComparison.OrdinalIgnoreCase) == true);
}

public sealed class FileTransformationPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}

/// <summary>
/// File Transformation and Direct Play Guard can load in either order.
/// Retry briefly after server startup so registration succeeds regardless of
/// plugin load order.
/// </summary>
public sealed class FileTransformationRegistrationService : IHostedService
{
    private readonly ILogger<FileTransformationRegistrationService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public FileTransformationRegistrationService(
        ILogger<FileTransformationRegistrationService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = RegisterWithRetryAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RegisterWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 45; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FileTransformationIntegration.Register(_logger))
            {
                return;
            }

            if (attempt == 1)
            {
                _logger.LogInformation(
                    "Direct Play Guard is waiting for File Transformation. " +
                    "The request-time Jellyfin Web injector remains available as a fallback.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Direct Play Guard did not detect File Transformation after startup. " +
            "Install File Transformation to enable the native Jellyfin error-message fallback.");
    }
}
