using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Api.Models.SystemInfoDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Custom branding logos (square icon and wide banner) that replace the Jellyfin logo in the web client.
/// </summary>
[Route("Branding/Logo")]
public class CustomLogoController : BaseJellyfinApiController
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string> _extensionsByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif"
    };

    private static readonly Dictionary<string, string> _contentTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private readonly IServerApplicationPaths _appPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLogoController"/> class.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IServerApplicationPaths"/> interface.</param>
    public CustomLogoController(IServerApplicationPaths appPaths)
    {
        _appPaths = appPaths;
    }

    private string LogoDirectory => Path.Combine(_appPaths.DataPath, "branding");

    /// <summary>
    /// Gets which custom logos are set (public, used by the login page).
    /// </summary>
    /// <response code="200">Logo information returned.</response>
    /// <returns>A <see cref="CustomLogoInfoDto"/>.</returns>
    [HttpGet("Info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CustomLogoInfoDto> GetCustomLogoInfo()
    {
        Response.Headers.CacheControl = "no-cache";
        return new CustomLogoInfoDto
        {
            IconTag = GetTag(FindLogo(CustomLogoType.Icon)),
            BannerTag = GetTag(FindLogo(CustomLogoType.Banner))
        };
    }

    /// <summary>
    /// Gets a custom logo image.
    /// </summary>
    /// <param name="type">The kind of logo.</param>
    /// <param name="tag">Optional cache tag; when present the image is cached for a long time.</param>
    /// <response code="200">Image returned.</response>
    /// <response code="404">No custom logo of that kind.</response>
    /// <returns>The image file.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetCustomLogo([FromQuery, Required] CustomLogoType type, [FromQuery] string? tag)
    {
        var path = FindLogo(type);
        if (path is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = string.IsNullOrEmpty(tag) ? "no-cache" : "public, max-age=31536000";
        var contentType = _contentTypesByExtension.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
        return PhysicalFile(path, contentType);
    }

    /// <summary>
    /// Uploads a custom logo. The body is the base64 encoded image and the content type its mime type.
    /// </summary>
    /// <param name="type">The kind of logo.</param>
    /// <response code="204">Logo saved.</response>
    /// <response code="400">Unsupported or too large image.</response>
    /// <response code="403">User does not have permission to change the branding.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> UploadCustomLogo([FromQuery, Required] CustomLogoType type)
    {
        var mediaType = Request.ContentType?.Split(';')[0].Trim();
        if (mediaType is null || !_extensionsByContentType.TryGetValue(mediaType, out var extension))
        {
            return BadRequest("Unsupported image type. Use PNG, JPEG, WebP or GIF.");
        }

        byte[] bytes;
        using (var base64 = new CryptoStream(Request.Body, new FromBase64Transform(), CryptoStreamMode.Read))
        using (var memory = new MemoryStream())
        {
            await base64.CopyToAsync(memory).ConfigureAwait(false);
            if (memory.Length == 0 || memory.Length > MaxImageBytes)
            {
                return BadRequest("The image is empty or larger than 10 MB.");
            }

            bytes = memory.ToArray();
        }

        Directory.CreateDirectory(LogoDirectory);
        DeleteLogos(type);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(LogoDirectory, GetFileName(type) + extension), bytes).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Deletes a custom logo, restoring the default Jellyfin logo.
    /// </summary>
    /// <param name="type">The kind of logo.</param>
    /// <response code="204">Logo deleted.</response>
    /// <response code="403">User does not have permission to change the branding.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult DeleteCustomLogo([FromQuery, Required] CustomLogoType type)
    {
        DeleteLogos(type);
        return NoContent();
    }

    private static string GetFileName(CustomLogoType type)
        => type == CustomLogoType.Banner ? "logo-banner" : "logo-icon";

    private static string? GetTag(string? path)
        => path is null ? null : System.IO.File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);

    private string? FindLogo(CustomLogoType type)
    {
        if (!Directory.Exists(LogoDirectory))
        {
            return null;
        }

        return Directory.GetFiles(LogoDirectory, GetFileName(type) + ".*")
            .FirstOrDefault(f => _contentTypesByExtension.ContainsKey(Path.GetExtension(f)));
    }

    private void DeleteLogos(CustomLogoType type)
    {
        if (!Directory.Exists(LogoDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(LogoDirectory, GetFileName(type) + ".*"))
        {
            System.IO.File.Delete(file);
        }
    }
}
