using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace Emby.Server.Implementations.MediaSegments;

/// <summary>
/// Media segment provider that exposes the intros and credits found by the
/// "Detect intros and credits" scheduled task.
/// </summary>
public class IntroCreditsSegmentProvider : IMediaSegmentProvider
{
    /// <summary>
    /// The provider name (also used to enable / disable it per library).
    /// </summary>
    public const string ProviderName = "Intro and Credits Detector";

    private readonly IntroCreditsStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroCreditsSegmentProvider"/> class.
    /// </summary>
    /// <param name="store">The results store.</param>
    public IntroCreditsSegmentProvider(IntroCreditsStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var segments = new List<MediaSegmentDto>();
        var record = _store.Get(request.ItemId);
        if (record is not null)
        {
            if (record.IntroStartTicks.HasValue && record.IntroEndTicks > record.IntroStartTicks)
            {
                segments.Add(new MediaSegmentDto
                {
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Intro,
                    StartTicks = record.IntroStartTicks.Value,
                    EndTicks = record.IntroEndTicks.Value
                });
            }

            if (record.CreditsStartTicks.HasValue && record.CreditsEndTicks > record.CreditsStartTicks)
            {
                segments.Add(new MediaSegmentDto
                {
                    ItemId = request.ItemId,
                    Type = MediaSegmentType.Outro,
                    StartTicks = record.CreditsStartTicks.Value,
                    EndTicks = record.CreditsEndTicks.Value
                });
            }
        }

        return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(segments);
    }

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie);

    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        if (_store.Remove(itemId))
        {
            return _store.SaveAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }
}
