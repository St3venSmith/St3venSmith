using Jellyfin.Plugin.DirectPlayGuard.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DirectPlayGuard.Api;

/// <summary>Handles the custom popup image used by Direct Play Guard.</summary>
[ApiController]
[Route("DirectPlayGuard")]
public sealed class ImageController : ControllerBase
{
    private const long MaxUploadBytes = 10L * 1024L * 1024L;
    private readonly IApplicationPaths _paths;

    public ImageController(IApplicationPaths paths)
    {
        _paths = paths;
    }

    /// <summary>Serves the currently uploaded popup image.</summary>
    [HttpGet("image")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetImage()
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        if (plugin is null || config is null || string.IsNullOrWhiteSpace(config.UploadedImageFileName))
        {
            return NotFound();
        }

        var path = GetSafeAssetPath(config.UploadedImageFileName);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";

        return PhysicalFile(path, ContentTypeFromExtension(Path.GetExtension(path)));
    }

    /// <summary>Uploads a PNG, JPEG, GIF, or WebP popup image.</summary>
    [HttpPost("image")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult<ImageUploadResponse>> UploadImage(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0)
        {
            return BadRequest("No image was uploaded.");
        }

        if (file.Length > MaxUploadBytes)
        {
            return BadRequest("Image must be 10 MB or smaller.");
        }

        await using var input = file.OpenReadStream();
        var detected = await DetectImageTypeAsync(input, cancellationToken).ConfigureAwait(false);
        if (detected is null)
        {
            return BadRequest("Only PNG, JPG/JPEG, GIF, and WebP images are supported.");
        }

        var directory = AssetDirectory;
        Directory.CreateDirectory(directory);

        var fileName = "popup" + detected.Value.Extension;
        var finalPath = Path.Combine(directory, fileName);
        var tempPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");

        input.Seek(0, SeekOrigin.Begin);
        await using (var output = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        DeleteOtherAssets(fileName);
        System.IO.File.Move(tempPath, finalPath, overwrite: true);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            System.IO.File.Delete(finalPath);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        plugin.Configuration.UploadedImageFileName = fileName;
        plugin.Configuration.ImageMode = "upload";
        plugin.SaveConfiguration();

        return Ok(new ImageUploadResponse(
            fileName,
            detected.Value.ContentType,
            file.Length,
            "/DirectPlayGuard/image"));
    }

    /// <summary>Deletes the currently uploaded popup image.</summary>
    [HttpDelete("image")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult DeleteImage()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!string.IsNullOrWhiteSpace(plugin.Configuration.UploadedImageFileName))
        {
            var path = GetSafeAssetPath(plugin.Configuration.UploadedImageFileName);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }

        plugin.Configuration.UploadedImageFileName = string.Empty;
        if (string.Equals(plugin.Configuration.ImageMode, "upload", StringComparison.OrdinalIgnoreCase))
        {
            plugin.Configuration.ImageMode = "none";
        }

        plugin.SaveConfiguration();
        return NoContent();
    }

    private string AssetDirectory
        => Path.Combine(_paths.DataPath, "direct-play-guard", "assets");

    private string GetSafeAssetPath(string fileName)
        => Path.Combine(AssetDirectory, Path.GetFileName(fileName));

    private void DeleteOtherAssets(string keepFileName)
    {
        if (!Directory.Exists(AssetDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(AssetDirectory, "popup.*"))
        {
            if (!string.Equals(Path.GetFileName(file), keepFileName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    System.IO.File.Delete(file);
                }
                catch
                {
                    // A stale image is harmless. The configured filename is authoritative.
                }
            }
        }
    }

    private static async Task<DetectedImage?> DetectImageTypeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(
                header.AsMemory(read, header.Length - read),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read >= 8
            && header[0] == 0x89
            && header[1] == 0x50
            && header[2] == 0x4E
            && header[3] == 0x47
            && header[4] == 0x0D
            && header[5] == 0x0A
            && header[6] == 0x1A
            && header[7] == 0x0A)
        {
            return new DetectedImage(".png", "image/png");
        }

        if (read >= 3
            && header[0] == 0xFF
            && header[1] == 0xD8
            && header[2] == 0xFF)
        {
            return new DetectedImage(".jpg", "image/jpeg");
        }

        if (read >= 6
            && header[0] == 0x47
            && header[1] == 0x49
            && header[2] == 0x46
            && header[3] == 0x38
            && (header[4] == 0x37 || header[4] == 0x39)
            && header[5] == 0x61)
        {
            return new DetectedImage(".gif", "image/gif");
        }

        if (read >= 12
            && header[0] == 0x52
            && header[1] == 0x49
            && header[2] == 0x46
            && header[3] == 0x46
            && header[8] == 0x57
            && header[9] == 0x45
            && header[10] == 0x42
            && header[11] == 0x50)
        {
            return new DetectedImage(".webp", "image/webp");
        }

        return null;
    }

    private static string ContentTypeFromExtension(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

    private readonly record struct DetectedImage(string Extension, string ContentType);
}

/// <summary>Response returned after a successful image upload.</summary>
public sealed record ImageUploadResponse(
    string FileName,
    string ContentType,
    long Size,
    string Url);
