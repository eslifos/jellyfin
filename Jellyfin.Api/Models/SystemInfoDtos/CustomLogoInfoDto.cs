namespace Jellyfin.Api.Models.SystemInfoDtos;

/// <summary>
/// Information about the custom branding logos.
/// A tag is null when no custom image of that kind is set.
/// </summary>
public record CustomLogoInfoDto
{
    /// <summary>
    /// Gets the cache tag of the custom square icon, if any.
    /// </summary>
    public string? IconTag { get; init; }

    /// <summary>
    /// Gets the cache tag of the custom wide banner, if any.
    /// </summary>
    public string? BannerTag { get; init; }
}
