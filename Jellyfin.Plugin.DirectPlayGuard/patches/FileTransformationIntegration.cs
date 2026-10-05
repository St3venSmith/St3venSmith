using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.DirectPlayGuard.Web;

/// <summary>
/// Registers Direct Play Guard with IAmParadox27's File Transformation plugin.
/// File Transformation modifies Jellyfin Web files in memory as they are served,
/// which is more reliable than editing index.html on disk and works with
/// read-only Docker/package web roots.
/// </summary>
public static class FileTransformationIntegration
{
    public const string TransformationId = "75f5d73c-fde5-4a74-9f99-2cf19da26977";

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

        var payload = new JObject
        {
            ["id"] = TransformationId,
            ["fileNamePattern"] = "index\\.html$",
            ["callbackAssembly"] = typeof(FileTransformationIntegration).Assembly.FullName,
            ["callbackClass"] = typeof(FileTransformationIntegration).FullName,
            ["callbackMethod"] = nameof(TransformIndexHtml),
        };

        try
        {
            register.Invoke(null, new object?[] { payload });
            Interlocked.Exchange(ref _registered, 1);
            logger.LogInformation(
                "Direct Play Guard registered its Jellyfin Web transformation with File Transformation");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Direct Play Guard could not register with File Transformation");
            return false;
        }
    }

    /// <summary>
    /// Callback invoked by File Transformation whenever Jellyfin Web's
    /// index.html is served.
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
                    "The built-in index.html middleware remains available as a fallback.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Direct Play Guard did not detect File Transformation after startup. " +
            "Install File Transformation for the most reliable Jellyfin Web message injection.");
    }
}
