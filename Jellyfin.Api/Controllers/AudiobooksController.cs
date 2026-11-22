using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Home;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Home;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Start screen of an audiobooks library: the book being listened to, sagas in order, authors, narrators,
/// short and long listens, new and finished books. A book is a standalone audiobook (one file) or a book in
/// parts (a folder of numbered files, stored as an album whose tracks are the parts).
/// The books are prepared in the background (<see cref="IAudiobookCatalog"/>); a visit only reads where the
/// user is in them, with one query for all the parts.
/// </summary>
[Route("Audiobooks")]
[Authorize]
public class AudiobooksController : BaseJellyfinApiController
{
    private const int RowLimit = 24;
    private static readonly long _shortTicks = TimeSpan.FromHours(5).Ticks;
    private static readonly long _longTicks = TimeSpan.FromHours(15).Ticks;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IAudiobookCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudiobooksController"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="catalog">The prepared books of each library.</param>
    public AudiobooksController(IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userDataManager, IAudiobookCatalog catalog)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _catalog = catalog;
    }

    /// <summary>
    /// Gets the start screen of an audiobooks library for the current user.
    /// </summary>
    /// <param name="libraryId">The library id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Start screen returned.</response>
    /// <response code="404">Library not found.</response>
    /// <returns>The sections, as item ids in order, and the books they show.</returns>
    [HttpGet("{libraryId}/Home")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AudiobookHomeDto>> GetHome([FromRoute, Required] Guid libraryId, CancellationToken cancellationToken = default)
    {
        var user = _userManager.GetUserById(User.GetUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        if (_libraryManager.GetItemById(libraryId) is not Folder library || !library.IsVisible(user))
        {
            return NotFound();
        }

        var entries = await _catalog.GetAsync(libraryId, cancellationToken).ConfigureAwait(false);

        // Where the user is: every part in one query, their user data in one more
        var partIds = entries.SelectMany(e => e.PartIds).Distinct().ToArray();
        var parts = partIds.Length == 0
            ? []
            : _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = partIds, DtoOptions = new DtoOptions(false) });
        var partById = parts.ToDictionary(p => p.Id);
        var data = parts.Count == 0 ? [] : _userDataManager.GetUserDataBatch(parts, user);

        var books = entries.Select(e => Describe(e, partById, data)).ToList();

        var listening = books.Where(b => b.InProgress).OrderByDescending(b => b.LastPlayed).ToList();
        var unstarted = books.Where(b => !b.Started).ToList();
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(unstarted));

        var home = new AudiobookHomeDto
        {
            Continue = listening.Take(RowLimit).Select(ToProgress).ToList(),
            Latest = books.OrderByDescending(b => b.Entry.DateCreated).Take(RowLimit).Select(b => b.Entry.Id).ToList(),
            Finished = books.Where(b => b.Finished).OrderByDescending(b => b.LastPlayed).Take(RowLimit).Select(b => b.Entry.Id).ToList(),
            Unstarted = unstarted.Take(60).Select(b => b.Entry.Id).ToList(),
            Short = Pick(books.Where(b => b.RunTimeTicks > 0 && b.RunTimeTicks <= _shortTicks)),
            Long = Pick(books.Where(b => b.RunTimeTicks >= _longTicks)),
            Sagas = books.Where(b => !string.IsNullOrEmpty(b.Entry.Saga))
                .GroupBy(b => b.Entry.Saga!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Any(b => b.InProgress))
                .ThenByDescending(g => g.Count())
                .Take(10)
                .Select(g => new AudiobookShelfDto
                {
                    Name = g.First().Entry.Saga!,
                    ItemIds = g.OrderBy(b => b.Entry.SagaIndex ?? int.MaxValue).ThenBy(b => b.Entry.Name, StringComparer.OrdinalIgnoreCase).Select(b => b.Entry.Id).ToList(),
                    Count = g.Count(),
                    Finished = g.Count(b => b.Finished)
                })
                .ToList(),
            Authors = Shelves(books, b => b.Entry.Authors, 12),
            Narrators = Shelves(books, b => b.Entry.Narrators, 8),
            TotalBooks = books.Count,
            TotalTicks = books.Sum(b => b.RunTimeTicks),
            FinishedCount = books.Count(b => b.Finished)
        };

        home.Hero = home.Continue.FirstOrDefault();

        // The books the sections show, with what the cards need (no second request for them)
        var shown = new HashSet<Guid>(home.Continue.Select(c => c.ItemId)
            .Concat(home.Latest).Concat(home.Finished).Concat(home.Unstarted).Concat(home.Short).Concat(home.Long)
            .Concat(home.Sagas.Concat(home.Authors).Concat(home.Narrators).SelectMany(s => s.ItemIds)));
        home.Books = books.Where(b => shown.Contains(b.Entry.Id)).Select(b => new AudiobookCardDto
        {
            Id = b.Entry.Id,
            Name = b.Entry.Name,
            Type = b.Entry.Type,
            Author = b.Entry.Authors.FirstOrDefault(),
            Narrator = b.Entry.Narrators.FirstOrDefault(),
            RunTimeTicks = b.RunTimeTicks,
            PrimaryTag = b.Entry.PrimaryTag,
            Overview = b.Entry.Overview,
            Year = b.Entry.Year,
            Rating = b.Entry.Rating,
            Genres = b.Entry.Genres,
            Saga = b.Entry.Saga,
            SagaIndex = b.Entry.SagaIndex,
            Progress = b.Progress,
            Finished = b.Finished
        }).ToList();
        return home;
    }

    private static List<Guid> Pick(IEnumerable<Book> books)
        // Not started first, the order of the rest changes each time
        => books.OrderBy(b => b.Started).ThenBy(_ => Random.Shared.Next()).Take(RowLimit).Select(b => b.Entry.Id).ToList();

    private static string NameKey(string name)
        => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static List<AudiobookShelfDto> Shelves(List<Book> books, Func<Book, IReadOnlyList<string>> names, int limit)
        => books.SelectMany(b => names(b).Select(n => (Name: n, Book: b)))
            // "J. K. Rowling" and "J.K. Rowling" are the same person: compare only letters and digits
            .GroupBy(x => NameKey(x.Name), StringComparer.Ordinal)
            .Select(g => (Name: g.GroupBy(x => x.Name, StringComparer.Ordinal).OrderByDescending(v => v.Count()).First().Key, Books: g.Select(x => x.Book).DistinctBy(b => b.Entry.Id).ToList()))
            .Where(g => g.Books.Count >= 2)
            .OrderByDescending(g => g.Books.Count)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(g => new AudiobookShelfDto
            {
                Name = g.Name,
                ItemIds = g.Books.OrderBy(b => b.Entry.Saga ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(b => b.Entry.SagaIndex ?? int.MaxValue)
                    .ThenBy(b => b.Entry.Year ?? int.MaxValue)
                    .Take(RowLimit)
                    .Select(b => b.Entry.Id)
                    .ToList(),
                Count = g.Books.Count,
                Finished = g.Books.Count(b => b.Finished)
            })
            .ToList();

    private static AudiobookProgressDto ToProgress(Book b) => new()
    {
        ItemId = b.Entry.Id,
        ResumeItemId = b.ResumeIndex >= 0 ? b.Entry.PartIds[b.ResumeIndex] : b.Entry.Id,
        StartPositionTicks = b.ResumeTicks,
        PartIds = b.Entry.PartIds,
        PartIndex = Math.Max(0, b.ResumeIndex),
        Progress = b.Progress,
        RunTimeTicks = b.RunTimeTicks,
        RemainingTicks = Math.Max(0, b.RunTimeTicks - b.DoneTicks)
    };

    private static Book Describe(AudiobookEntry entry, Dictionary<Guid, BaseItem> partById, Dictionary<Guid, MediaBrowser.Controller.Entities.UserItemData> data)
    {
        var book = new Book(entry);
        var count = entry.PartIds.Length;
        var recent = -1;
        DateTime? recentDate = null;
        for (var i = 0; i < count; i++)
        {
            var id = entry.PartIds[i];
            var length = partById.TryGetValue(id, out var part) ? part.RunTimeTicks ?? 0 : entry.PartTicks.ElementAtOrDefault(i);
            book.RunTimeTicks += length;
            if (!data.TryGetValue(id, out var userData))
            {
                continue;
            }

            if (userData.Played)
            {
                book.DoneTicks += length;
                book.PlayedParts++;
            }
            else if (userData.PlaybackPositionTicks > 0)
            {
                book.DoneTicks += Math.Min(length > 0 ? length : long.MaxValue, userData.PlaybackPositionTicks);
                book.Started = true;
            }

            if (userData.LastPlayedDate is DateTime last && (recentDate is null || last > recentDate))
            {
                recentDate = last;
                recent = i;
            }
        }

        book.Started |= book.PlayedParts > 0;
        book.Finished = count > 0 && book.PlayedParts == count;
        book.InProgress = book.Started && !book.Finished;
        book.LastPlayed = recentDate ?? DateTime.MinValue;
        book.Progress = book.Finished ? 1 : book.RunTimeTicks > 0 ? Math.Clamp((double)book.DoneTicks / book.RunTimeTicks, 0, 1) : 0;

        // Where to carry on: the part being listened to, otherwise the next one not finished
        if (book.InProgress)
        {
            var start = Math.Max(recent, 0);
            for (var i = start; i < count + start; i++)
            {
                var index = i % count;
                if (!data.TryGetValue(entry.PartIds[index], out var userData) || !userData.Played)
                {
                    book.ResumeIndex = index;
                    book.ResumeTicks = userData?.PlaybackPositionTicks ?? 0;
                    break;
                }
            }
        }

        return book;
    }

    private sealed class Book(AudiobookEntry entry)
    {
        public AudiobookEntry Entry { get; } = entry;

        public long RunTimeTicks { get; set; }

        public long DoneTicks { get; set; }

        public int PlayedParts { get; set; }

        public bool Started { get; set; }

        public bool Finished { get; set; }

        public bool InProgress { get; set; }

        public DateTime LastPlayed { get; set; }

        public double Progress { get; set; }

        public int ResumeIndex { get; set; } = -1;

        public long ResumeTicks { get; set; }
    }

    /// <summary>
    /// A book as the cards show it.
    /// </summary>
    public class AudiobookCardDto
    {
        /// <summary>Gets or sets the book id.</summary>
        public Guid Id { get; set; }

        /// <summary>Gets or sets the name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the item kind (AudioBook, MusicAlbum, Book).</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Gets or sets the first author.</summary>
        public string? Author { get; set; }

        /// <summary>Gets or sets the first narrator.</summary>
        public string? Narrator { get; set; }

        /// <summary>Gets or sets the length.</summary>
        public long RunTimeTicks { get; set; }

        /// <summary>Gets or sets the cover tag.</summary>
        public string? PrimaryTag { get; set; }

        /// <summary>Gets or sets the synopsis.</summary>
        public string? Overview { get; set; }

        /// <summary>Gets or sets the year.</summary>
        public int? Year { get; set; }

        /// <summary>Gets or sets the rating.</summary>
        public float? Rating { get; set; }

        /// <summary>Gets or sets the genres.</summary>
        public IReadOnlyList<string> Genres { get; set; } = [];

        /// <summary>Gets or sets the saga.</summary>
        public string? Saga { get; set; }

        /// <summary>Gets or sets the number in the saga.</summary>
        public int? SagaIndex { get; set; }

        /// <summary>Gets or sets how much the user listened to, 0 to 1.</summary>
        public double Progress { get; set; }

        /// <summary>Gets or sets a value indicating whether the user finished it.</summary>
        public bool Finished { get; set; }
    }

    /// <summary>
    /// Start screen of an audiobooks library.
    /// </summary>
    public class AudiobookHomeDto
    {
        /// <summary>Gets or sets the book being listened to (the most recent), if any.</summary>
        public AudiobookProgressDto? Hero { get; set; }

        /// <summary>Gets or sets the books being listened to, most recent first.</summary>
        public IReadOnlyList<AudiobookProgressDto> Continue { get; set; } = [];

        /// <summary>Gets or sets the newest books.</summary>
        public IReadOnlyList<Guid> Latest { get; set; } = [];

        /// <summary>Gets or sets the finished books, most recent first.</summary>
        public IReadOnlyList<Guid> Finished { get; set; } = [];

        /// <summary>Gets or sets books not started, in random order (discover, surprise me).</summary>
        public IReadOnlyList<Guid> Unstarted { get; set; } = [];

        /// <summary>Gets or sets books of 5 hours or less.</summary>
        public IReadOnlyList<Guid> Short { get; set; } = [];

        /// <summary>Gets or sets books of 15 hours or more.</summary>
        public IReadOnlyList<Guid> Long { get; set; } = [];

        /// <summary>Gets or sets the sagas with two or more books, each in reading order.</summary>
        public IReadOnlyList<AudiobookShelfDto> Sagas { get; set; } = [];

        /// <summary>Gets or sets the authors with two or more books.</summary>
        public IReadOnlyList<AudiobookShelfDto> Authors { get; set; } = [];

        /// <summary>Gets or sets the narrators with two or more books.</summary>
        public IReadOnlyList<AudiobookShelfDto> Narrators { get; set; } = [];

        /// <summary>Gets or sets the number of books.</summary>
        public int TotalBooks { get; set; }

        /// <summary>Gets or sets the length of all the books.</summary>
        public long TotalTicks { get; set; }

        /// <summary>Gets or sets the number of finished books.</summary>
        public int FinishedCount { get; set; }

        /// <summary>Gets or sets the books the sections show, with what the cards need.</summary>
        public IReadOnlyList<AudiobookCardDto> Books { get; set; } = [];
    }

    /// <summary>
    /// A book being listened to.
    /// </summary>
    public class AudiobookProgressDto
    {
        /// <summary>Gets or sets the book.</summary>
        public Guid ItemId { get; set; }

        /// <summary>Gets or sets the file to carry on with (the part, or the book itself).</summary>
        public Guid ResumeItemId { get; set; }

        /// <summary>Gets or sets where to carry on in that file.</summary>
        public long StartPositionTicks { get; set; }

        /// <summary>Gets or sets the files of the book in order (one for a standalone audiobook).</summary>
        public IReadOnlyList<Guid> PartIds { get; set; } = [];

        /// <summary>Gets or sets the position of the resume file among the parts.</summary>
        public int PartIndex { get; set; }

        /// <summary>Gets or sets how much was listened to, 0 to 1.</summary>
        public double Progress { get; set; }

        /// <summary>Gets or sets the length of the book.</summary>
        public long RunTimeTicks { get; set; }

        /// <summary>Gets or sets the time left.</summary>
        public long RemainingTicks { get; set; }
    }

    /// <summary>
    /// A shelf: the books of a saga, an author or a narrator.
    /// </summary>
    public class AudiobookShelfDto
    {
        /// <summary>Gets or sets the saga, author or narrator.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the books, in order.</summary>
        public IReadOnlyList<Guid> ItemIds { get; set; } = [];

        /// <summary>Gets or sets how many books the library has of it.</summary>
        public int Count { get; set; }

        /// <summary>Gets or sets how many of them are finished.</summary>
        public int Finished { get; set; }
    }
}
