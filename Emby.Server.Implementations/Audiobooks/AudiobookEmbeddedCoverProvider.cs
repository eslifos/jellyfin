using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// The cover inside the audio files, for books no online catalog has.
/// It has the same name as the online provider so it is enabled wherever that one is, and runs after it:
/// the catalog cover (larger) wins, the embedded one only fills a book left without cover.
/// A book in parts takes the cover of its first part that has one.
/// </summary>
public class AudiobookEmbeddedCoverProvider : IDynamicImageProvider, IHasOrder
{
    private const int MaxFilesTried = 3;

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<AudiobookEmbeddedCoverProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudiobookEmbeddedCoverProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public AudiobookEmbeddedCoverProvider(ILibraryManager libraryManager, IMediaEncoder mediaEncoder, IApplicationPaths appPaths, ILogger<AudiobookEmbeddedCoverProvider> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => AudiobookMetadata.ProviderName;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item switch
    {
        AudioBook book => book.IsFileProtocol && !book.IsShortcut,
        MusicAlbum album => album.IsFileProtocol && _libraryManager.GetInheritedContentType(album) == CollectionType.books,
        _ => false
    };

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => [ImageType.Primary];

    /// <inheritdoc />
    public async Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
    {
        var files = item switch
        {
            MusicAlbum album => PartFiles(album.Path),
            _ => string.IsNullOrEmpty(item.Path) ? [] : [item.Path]
        };

        var target = Path.Join(_appPaths.CachePath, "audiobook-covers", item.Id.ToString("N", CultureInfo.InvariantCulture) + ".jpg");
        if (File.Exists(target))
        {
            return new DynamicImageResponse { HasImage = true, Path = target };
        }

        foreach (var file in files.Take(MaxFilesTried))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Without a stream index ffmpeg takes the attached picture of the file
                var extracted = await _mediaEncoder.ExtractAudioImage(file, null, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(extracted) || new FileInfo(extracted).Length == 0)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(extracted, target, true);
                TryDelete(extracted);
                return new DynamicImageResponse { HasImage = true, Path = target };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A file without a cover: try the next part
                _logger.LogDebug(ex, "No embedded cover in {File}", file);
            }
        }

        return new DynamicImageResponse { HasImage = false };
    }

    /// <summary>The audio files of a book in parts, in the order of their names (part 1 first).</summary>
    private static List<string> PartFiles(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        string[] audio = [".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".opus", ".wma", ".wav"];
        return Directory.EnumerateFiles(folder)
            .Where(f => audio.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => LeadingNumber(Path.GetFileNameWithoutExtension(f)))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int LeadingNumber(string name)
    {
        var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return digits.Length is > 0 and < 7 ? int.Parse(digits, CultureInfo.InvariantCulture) : int.MaxValue;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
