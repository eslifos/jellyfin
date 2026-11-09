using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.EpisodeOrder;

/// <summary>
/// Episode orders a series has in TVDB / TMDb, how well the library matches each one, and the state of every episode.
/// </summary>
public class EpisodeOrderAnalysis
{
    /// <summary>
    /// Gets or sets the series id.
    /// </summary>
    public Guid SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the current display order ("" = aired).
    /// </summary>
    public string CurrentOrder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the main metadata source chosen for the series (null = library order).
    /// </summary>
    public string? PrimarySource { get; set; }

    /// <summary>
    /// Gets or sets the main metadata source in use (the chosen one or the first of the library).
    /// </summary>
    public string? EffectiveSource { get; set; }

    /// <summary>
    /// Gets or sets the metadata sources available for series.
    /// </summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets the orders found in the providers.
    /// </summary>
    public IReadOnlyList<EpisodeOrderOption> Orders { get; set; } = [];

    /// <summary>
    /// Gets or sets the episodes on disk.
    /// </summary>
    public IReadOnlyList<EpisodeOrderEpisode> Episodes { get; set; } = [];

    /// <summary>
    /// Gets or sets notes about providers that could not be checked (codes: "TvdbNoPlugin", "TvdbError", "TmdbError", "NoIds").
    /// </summary>
    public IReadOnlyList<string> Notes { get; set; } = [];
}

/// <summary>
/// One episode order of one provider.
/// </summary>
public class EpisodeOrderOption
{
    /// <summary>
    /// Gets or sets the provider: "Tvdb" or "Tmdb".
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the value saved in the series display order ("" = aired).
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider's name of the order.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider's alternative name (TVDB shows e.g. "International Order" for the DVD order).
    /// </summary>
    public string? AlternateName { get; set; }

    /// <summary>
    /// Gets or sets the number of seasons.
    /// </summary>
    public int SeasonCount { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes in the provider.
    /// </summary>
    public int EpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets how many library episodes exist with the same season / number in this order.
    /// </summary>
    public int Matched { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is the order in use.
    /// </summary>
    public bool IsCurrent { get; set; }
}

/// <summary>
/// One episode file and how it fits the order in use.
/// </summary>
public class EpisodeOrderEpisode
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int? Season { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    public int? Number { get; set; }

    /// <summary>
    /// Gets or sets the last episode number of a multi-episode file.
    /// </summary>
    public int? NumberEnd { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a provider filled its data.
    /// </summary>
    public bool HasProviderData { get; set; }

    /// <summary>
    /// Gets or sets whether the season / number exists in the order in use (null = could not be checked).
    /// </summary>
    public bool? InOrder { get; set; }

    /// <summary>
    /// Gets or sets the provider's name for that season / number in the order in use.
    /// </summary>
    public string? ExpectedName { get; set; }
}
