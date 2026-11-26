#nullable disable

#pragma warning disable CS1591

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.MediaEncoding
{
    public interface ISubtitleEncoder
    {
        /// <summary>
        /// Gets the subtitles.
        /// </summary>
        /// <param name="item">Item to use.</param>
        /// <param name="mediaSourceId">Media source.</param>
        /// <param name="subtitleStreamIndex">Subtitle stream to use.</param>
        /// <param name="outputFormat">Output format to use.</param>
        /// <param name="startTimeTicks">Start time.</param>
        /// <param name="endTimeTicks">End time.</param>
        /// <param name="preserveOriginalTimestamps">Option to preserve original timestamps.</param>
        /// <param name="cancellationToken">The cancellation token for the operation.</param>
        /// <returns>Task{Stream}.</returns>
        Task<Stream> GetSubtitles(
            BaseItem item,
            string mediaSourceId,
            int subtitleStreamIndex,
            string outputFormat,
            long startTimeTicks,
            long endTimeTicks,
            bool preserveOriginalTimestamps,
            CancellationToken cancellationToken);

        /// <summary>
        /// Gets the subtitle language encoding parameter.
        /// </summary>
        /// <param name="subtitleStream">The subtitle stream.</param>
        /// <param name="language">The language.</param>
        /// <param name="mediaSource">The media source.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>System.String.</returns>
        Task<string> GetSubtitleFileCharacterSet(MediaStream subtitleStream, string language, MediaSourceInfo mediaSource, CancellationToken cancellationToken);

        /// <summary>
        /// Gets the path to a subtitle file.
        /// </summary>
        /// <param name="subtitleStream">The subtitle stream.</param>
        /// <param name="mediaSource">The media source.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>System.String.</returns>
        Task<string> GetSubtitleFilePath(MediaStream subtitleStream, MediaSourceInfo mediaSource, CancellationToken cancellationToken);

        /// <summary>
        /// Extracts all extractable subtitles (text and pgs). The extraction runs on its own: cancelling only stops
        /// waiting for it (a request dropped by a proxy does not throw away the work), and asking again while it runs
        /// waits for the same one.
        /// </summary>
        /// <param name="mediaSource">The mediaSource.</param>
        /// <param name="cancellationToken">Stops waiting.</param>
        /// <returns>Task.</returns>
        Task ExtractAllExtractableSubtitles(MediaSourceInfo mediaSource, CancellationToken cancellationToken);

        /// <summary>
        /// Gets the part of a text subtitle that is already extracted from the video file, so players can show it
        /// while the extraction (which reads the whole file) is still running. Starts the extraction when nobody did.
        /// </summary>
        /// <param name="item">Item to use.</param>
        /// <param name="mediaSourceId">Media source.</param>
        /// <param name="subtitleStreamIndex">Subtitle stream to use.</param>
        /// <param name="outputFormat">Output format to use.</param>
        /// <param name="retry">Try again an extraction that failed.</param>
        /// <param name="positionTicks">Where the video is: past the part read so far, the subtitle is also read from there.</param>
        /// <param name="cancellationToken">The cancellation token for the operation.</param>
        /// <returns>The subtitle so far and where its extraction stands.</returns>
        Task<PartialSubtitles> GetPartialSubtitles(BaseItem item, string mediaSourceId, int subtitleStreamIndex, string outputFormat, bool retry, long? positionTicks, CancellationToken cancellationToken);
    }
}
