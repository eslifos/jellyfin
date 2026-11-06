namespace Jellyfin.Api.Models.SystemInfoDtos;

/// <summary>
/// The kind of custom branding logo.
/// </summary>
public enum CustomLogoType
{
    /// <summary>
    /// Square icon (navigation, browser tab, toolbar).
    /// </summary>
    Icon = 0,

    /// <summary>
    /// Wide banner with text (login, wizard).
    /// </summary>
    Banner = 1
}
