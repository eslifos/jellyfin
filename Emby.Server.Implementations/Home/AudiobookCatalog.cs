using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Home;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Home;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Home;

/// <summary>
/// Prepares the books of each audiobooks library in the background and keeps them in memory and on disk.
/// A visit then only reads where the user is in each book (one query for all the parts).
/// </summary>
public sealed class AudiobookCatalog : IAudiobookCatalog
{
    private const string SagaKey = "AudiobookSaga";
    private static readonly TimeSpan _maxAge = TimeSpan.FromHours(3);

    private readonly ILibraryManager _libraryManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<AudiobookCatalog> _logger;
    private readonly ConcurrentDictionary<Guid, (DateTime Built, List<AudiobookEntry> Books)> _cache = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<List<AudiobookEntry>>>> _building = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AudiobookCatalog"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public AudiobookCatalog(ILibraryManager libraryManager, IImageProcessor imageProcessor, IApplicationPaths paths, ILogger<AudiobookCatalog> logger)
    {
        _libraryManager = libraryManager;
        _imageProcessor = imageProcessor;
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AudiobookEntry>> GetAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(libraryId, out var cached) || TryLoad(libraryId, out cached))
        {
            if (DateTime.UtcNow - cached.Built > _maxAge)
            {
                _ = Build(libraryId);
            }

            return cached.Books;
        }

        // The very first time: prepared now (leaving the page does not cancel it)
        return await Build(libraryId).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task BuildAllAsync(CancellationToken cancellationToken)
    {
        var libraries = _libraryManager.GetUserRootFolder().Children.OfType<CollectionFolder>()
            .Where(f => f.CollectionType == CollectionType.books)
            .ToList();
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Build(library.Id).WaitAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<List<AudiobookEntry>> Build(Guid libraryId)
    {
        var lazy = _building.GetOrAdd(libraryId, id => new Lazy<Task<List<AudiobookEntry>>>(() => Task.Run(async () =>
        {
            try
            {
                var books = await ReadAsync(id).ConfigureAwait(false);
                _cache[id] = (DateTime.UtcNow, books);
                Save(id, books);
                _logger.LogInformation("Audiobooks prepared: {Count} books", books.Count);
                return books;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audiobooks could not be prepared");
                return _cache.TryGetValue(id, out var old) ? old.Books : [];
            }
            finally
            {
                _building.TryRemove(id, out _);
            }
        })));
        return lazy.Value;
    }

    private async Task<List<AudiobookEntry>> ReadAsync(Guid libraryId)
    {
        if (_libraryManager.GetItemById(libraryId) is not Folder library)
        {
            return [];
        }

        // All the parts in one query, grouped by their book (album)
        var parts = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.AudioBook],
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false)
        });
        var tops = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum, BaseItemKind.Book],
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false)
        });

        var albums = tops.OfType<MusicAlbum>().Select(a => a.Id).ToHashSet();
        var byAlbum = parts
            .Where(p => albums.Contains(p.ParentId))
            .GroupBy(p => p.ParentId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.IndexNumber ?? int.MaxValue).ThenBy(p => p.SortName, StringComparer.OrdinalIgnoreCase).ToList());

        var books = new List<AudiobookEntry>();
        var items = tops.Concat(parts.Where(p => !albums.Contains(p.ParentId))).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            List<BaseItem> bookParts = item is MusicAlbum ? byAlbum.GetValueOrDefault(item.Id) ?? [] : [item];
            if (bookParts.Count == 0)
            {
                continue;
            }

            books.Add(Describe(item, bookParts));

            // Reading the people is one query per book: a short pause now and then keeps the server answering
            if (i % 40 == 39)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        return books;
    }

    private AudiobookEntry Describe(BaseItem item, List<BaseItem> parts)
    {
        var saga = item switch
        {
            AudioBook audioBook when !string.IsNullOrWhiteSpace(audioBook.SeriesName) => audioBook.SeriesName,
            _ => item.GetProviderId(SagaKey)
        };

        var people = _libraryManager.GetPeople(item);
        var authors = item is IHasAlbumArtist { AlbumArtists.Count: > 0 } withArtists
            ? withArtists.AlbumArtists
            : people.Where(p => p.Type == PersonKind.Author).Select(p => p.Name).ToList();

        return new AudiobookEntry
        {
            Id = item.Id,
            Name = item.Name ?? string.Empty,
            Type = item.GetBaseItemKind().ToString(),
            PartIds = parts.Select(p => p.Id).ToArray(),
            PartTicks = parts.Select(p => p.RunTimeTicks ?? 0).ToArray(),
            Saga = string.IsNullOrWhiteSpace(saga) ? null : saga,
            SagaIndex = string.IsNullOrWhiteSpace(saga) ? null : item.IndexNumber,
            Authors = authors.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Narrators = people.Where(p => p.Type == PersonKind.Narrator && !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            DateCreated = item.DateCreated,
            PrimaryTag = item.GetImageInfo(ImageType.Primary, 0) is { } info ? _imageProcessor.GetImageCacheTag(item, info) : null,
            Overview = item.Overview,
            Year = item.ProductionYear,
            Rating = item.CommunityRating,
            Genres = item.Genres.Take(3).ToArray()
        };
    }

    private string FilePath(Guid libraryId) => Path.Combine(_paths.DataPath, "home", "audiobooks-" + libraryId.ToString("N", System.Globalization.CultureInfo.InvariantCulture) + ".json");

    private bool TryLoad(Guid libraryId, out (DateTime Built, List<AudiobookEntry> Books) loaded)
    {
        loaded = default;
        try
        {
            var path = FilePath(libraryId);
            if (!File.Exists(path))
            {
                return false;
            }

            var books = JsonSerializer.Deserialize<List<AudiobookEntry>>(File.ReadAllText(path));
            if (books is null)
            {
                return false;
            }

            loaded = (File.GetLastWriteTimeUtc(path), books);
            _cache[libraryId] = loaded;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saved audiobooks could not be read");
            return false;
        }
    }

    private void Save(Guid libraryId, List<AudiobookEntry> books)
    {
        try
        {
            var path = FilePath(libraryId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(books));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audiobooks could not be saved");
        }
    }
}
