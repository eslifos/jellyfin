namespace MediaBrowser.Model.Discovery;

/// <summary>
/// A YouTube trailer chosen in the language of the library.
/// </summary>
public class TrailerInfo
{
    /// <summary>
    /// Gets or sets the YouTube video key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the video name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language it was found in (es-MX, es-ES, en-US or the original language).
    /// </summary>
    public string Language { get; set; } = string.Empty;
}
