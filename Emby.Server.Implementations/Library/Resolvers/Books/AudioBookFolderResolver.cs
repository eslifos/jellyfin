using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Emby.Naming.Audio;
using Emby.Naming.AudioBook;
using Emby.Naming.Common;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;

namespace Emby.Server.Implementations.Library.Resolvers.Books;

/// <summary>
/// A book stored as a folder of parts (Author / Book / 01.mp3, 02.mp3…) in a books library becomes one item
/// that holds its parts in order, like an album holds its tracks. Jellyfin alone skips these books or shows every
/// part as a separate book. Books stored as one file (.m4b) are still single audiobooks.
/// </summary>
public partial class AudioBookFolderResolver : ItemResolver<MusicAlbum>
{
    private readonly NamingOptions _namingOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioBookFolderResolver"/> class.
    /// </summary>
    /// <param name="namingOptions">The naming options.</param>
    public AudioBookFolderResolver(NamingOptions namingOptions)
    {
        _namingOptions = namingOptions;
    }

    /// <summary>
    /// Gets the priority: before the audio resolver, which would make every part a book.
    /// </summary>
    public override ResolverPriority Priority => ResolverPriority.Fourth;

    /// <inheritdoc />
    protected override MusicAlbum? Resolve(ItemResolveArgs args)
    {
        if (!args.IsDirectory
            || args.GetCollectionType() != CollectionType.books
            || args.Parent is null
            || args.Parent.IsRoot
            || args.HasParent<MusicAlbum>())
        {
            return null;
        }

        var audio = args.FileSystemChildren
            .Where(f => !f.IsDirectory
                && AudioFileParser.IsAudioFile(f.FullName, _namingOptions)
                && !f.FullName.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (audio.Count < 2)
        {
            return null;
        }

        // The same book in two formats (book.m4b + book.mp3) is one book with versions, not parts
        var books = new AudioBookListResolver(_namingOptions).Resolve(audio).ToList();
        if (books.Count == 1 && books[0].Files.Count == 1)
        {
            return null;
        }

        var names = audio.Select(f => Path.GetFileNameWithoutExtension(f.FullName)).ToList();
        if (!LooksLikeParts(names))
        {
            // Several different books loose in an author folder
            return null;
        }

        return new MusicAlbum
        {
            Path = args.Path,
            Name = Path.GetFileName(args.Path)
        };
    }

    /// <summary>
    /// Parts are numbered ("01", "01 - Prólogo", "Capítulo 3", "Parte 2") or share the same name with a number.
    /// </summary>
    private static bool LooksLikeParts(System.Collections.Generic.List<string> names)
    {
        if (names.All(n => n.Length > 0 && char.IsDigit(n.TrimStart()[0])))
        {
            return true;
        }

        if (names.All(n => PartWord().IsMatch(n)))
        {
            return true;
        }

        return names.Select(n => Digits().Replace(n, string.Empty).Trim(' ', '-', '_', '.', '(', ')').ToLowerInvariant()).Distinct().Count() == 1;
    }

    [GeneratedRegex(@"(cap[ií]tulo|cap\b|chapter|ch\b|parte|part|pista|track|cd|disco|disc|libro)\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex PartWord();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
