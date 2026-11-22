using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Downloads;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Downloads;
using MediaBrowser.Model.Globalization;

namespace Emby.Server.Implementations.Downloads;

/// <summary>
/// Downloads reported by the devices, kept in memory. A device that stops reporting for a while (closed app)
/// shows its download as interrupted; finished and failed ones are kept 24 hours.
/// </summary>
public sealed class DownloadMonitor : IDownloadMonitor
{
    private const int MaxEntries = 300;
    private static readonly TimeSpan _keep = TimeSpan.FromHours(24);
    private static readonly TimeSpan _silent = TimeSpan.FromMinutes(2);
    private static readonly string[] _active = ["queued", "preparing", "downloading"];

    private readonly ConcurrentDictionary<string, DownloadProgressInfo> _downloads = new(StringComparer.Ordinal);
    private readonly IActivityManager _activityManager;
    private readonly ILocalizationManager _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadMonitor"/> class.
    /// </summary>
    /// <param name="activityManager">The activity log.</param>
    /// <param name="localization">The localization manager.</param>
    public DownloadMonitor(IActivityManager activityManager, ILocalizationManager localization)
    {
        _activityManager = activityManager;
        _localization = localization;
    }

    /// <inheritdoc />
    public async Task ReportAsync(DownloadProgressInfo info)
    {
        var key = info.DeviceId + "|" + info.ItemId.ToString("N", CultureInfo.InvariantCulture);
        if (info.Status == "removed")
        {
            _downloads.TryRemove(key, out _);
            return;
        }

        var now = DateTime.UtcNow;
        _downloads.TryGetValue(key, out var previous);
        info.StartedAt = previous?.StartedAt ?? now;
        info.UpdatedAt = now;
        info.CompletedAt = info.Status == "done" ? previous?.CompletedAt ?? now : null;
        _downloads[key] = info;
        Prune(now);

        // A download that just finished goes to the activity log as well
        if (info.Status == "done" && previous?.Status != "done")
        {
            var name = string.IsNullOrEmpty(info.GroupName) ? info.Name : $"{info.GroupName} - {info.Name}";
            await _activityManager.CreateAsync(new ActivityLog(
                string.Format(CultureInfo.InvariantCulture, _localization.GetServerLocalizedString("UserDownloadedItemWithValues"), info.UserName, name, info.DeviceName),
                "OfflineDownload",
                info.UserId)
            {
                ItemId = info.ItemId.ToString("N", CultureInfo.InvariantCulture),
                ShortOverview = info.Quality
            }).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DownloadProgressInfo> GetAll()
    {
        var now = DateTime.UtcNow;
        Prune(now);
        foreach (var download in _downloads.Values)
        {
            // The app was closed (or lost the connection) in the middle of a download
            if (_active.Contains(download.Status) && now - download.UpdatedAt > _silent)
            {
                download.Status = "paused";
                download.Reason = "silent";
                download.SpeedBytesPerSecond = 0;
                download.EtaSeconds = null;
            }
        }

        return _downloads.Values
            .OrderBy(d => _active.Contains(d.Status) ? 0 : 1)
            .ThenByDescending(d => d.UpdatedAt)
            .ToList();
    }

    private void Prune(DateTime now)
    {
        foreach (var (key, download) in _downloads)
        {
            if (now - download.UpdatedAt > _keep)
            {
                _downloads.TryRemove(key, out _);
            }
        }

        if (_downloads.Count > MaxEntries)
        {
            foreach (var (key, _) in _downloads.OrderBy(d => d.Value.UpdatedAt).Take(_downloads.Count - MaxEntries))
            {
                _downloads.TryRemove(key, out _);
            }
        }
    }
}
