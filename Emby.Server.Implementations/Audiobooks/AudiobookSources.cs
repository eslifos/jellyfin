namespace Emby.Server.Implementations.Audiobooks;

/// <summary>
/// The online catalogs used for audiobooks and the provider id keys saved on the items.
/// </summary>
public static class AudiobookSources
{
    /// <summary>Audible (ASIN). Best data for audiobooks: narrator, saga, length and large covers.</summary>
    public const string Audible = "Audible";

    /// <summary>Apple Books (iTunes store id). Good Latin American Spanish catalog.</summary>
    public const string AppleBooks = "AppleBooks";

    /// <summary>Google Books (volume id). Spanish synopsis, publisher and ISBN.</summary>
    public const string GoogleBooks = "GoogleBooks";

    /// <summary>Open Library (work key). Covers by ISBN as a last resort.</summary>
    public const string OpenLibrary = "OpenLibrary";

    /// <summary>Provider id key that keeps the saga of a book stored as a folder of parts.</summary>
    public const string SagaKey = "AudiobookSaga";

    /// <summary>Theme of the audiobook libraries (books libraries with their own start screen).</summary>
    public const string Theme = "audiobooks";
}
