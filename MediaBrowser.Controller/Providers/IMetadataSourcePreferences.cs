using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Providers;

/// <summary>
/// Per-series choice of the main metadata source (e.g. "TheTVDB" or "TheMovieDb").
/// The chosen source is asked first; every other source still fills what it left empty.
/// </summary>
public interface IMetadataSourcePreferences
{
    /// <summary>
    /// Gets the main source for a series, or for the series of a season / episode.
    /// </summary>
    /// <param name="item">A series, season or episode.</param>
    /// <returns>The metadata fetcher name, or <c>null</c> when the library order applies.</returns>
    string? GetPrimarySource(BaseItem item);

    /// <summary>
    /// Gets the main source stored for a series.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <returns>The metadata fetcher name, or <c>null</c>.</returns>
    string? GetPrimarySource(Guid seriesId);

    /// <summary>
    /// Sets (or clears with <c>null</c>) the main source of a series.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="source">The metadata fetcher name.</param>
    void SetPrimarySource(Guid seriesId, string? source);

    /// <summary>
    /// Gets every stored choice.
    /// </summary>
    /// <returns>Series id to source name.</returns>
    IReadOnlyDictionary<Guid, string> GetAll();
}
