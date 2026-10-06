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
/// Only index.html is transformed. Localization bundles are intentionally never
/// modified because corrupting a locale chunk can break all Jellyfin UI labels.
/// </summary>
public static class FileTransformationIntegration
{
    public const string IndexTransformationId = "75f5d73c-fde5-4a74-9f99-2cf19da26977";

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
            // File Transformation prefers exact filename pipelines over regex
            // pipelines. Register against exact index.html so we compose with
            // other Jellyfin UI plugins that also transform index.html.
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

            Interlocked.Exchange(ref _registered, 1);
            logger.LogInformation(
                "Direct Play Guard registered its exact index.html transformation with File Transformation");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Direct Play Guard could not register with File Transformation");
            return false;
        }
    }

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
            "The built-in request-time index.html injector will remain active.");
    }
}
