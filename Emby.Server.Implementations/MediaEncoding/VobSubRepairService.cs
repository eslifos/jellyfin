using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.MediaEncoding;

/// <summary>
/// Repairs VobSub subtitle tracks whose codec private data declares a 0x0 frame size.
/// Uses mkvtoolnix: the broken tracks are extracted, their "size:" line is fixed and
/// the file is remuxed (no re-encoding). The original is only replaced after the new
/// file has been verified with ffprobe.
/// </summary>
public sealed partial class VobSubRepairService : IVobSubRepairService, IDisposable
{
    private const string Ntsc = "720x480";
    private const string Pal = "720x576";

    private readonly ILogger<VobSubRepairService> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryManager _libraryManager;

    // Repairs read and write whole files on the NAS: one at a time.
    private readonly SemaphoreSlim _repairLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="VobSubRepairService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="mediaEncoder">The media encoder (for the ffprobe path).</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="libraryManager">The library manager.</param>
    public VobSubRepairService(ILogger<VobSubRepairService> logger, IMediaEncoder mediaEncoder, IProviderManager providerManager, IFileSystem fileSystem, ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _mediaEncoder = mediaEncoder;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    [GeneratedRegex(@"^size:\s*(\d+)x(\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SizeRegex();

    /// <inheritdoc />
    public async Task<string?> GetToolsVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync("mkvmerge", ["--version"], cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StdOut.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<VobSubRepairResult> InspectAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var path = item.Path;
        if (!IsLocalMkv(item))
        {
            return new VobSubRepairResult { Status = VobSubRepairStatus.Unsupported, Path = path };
        }

        if (await GetToolsVersionAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return new VobSubRepairResult { Status = VobSubRepairStatus.ToolsMissing, Path = path };
        }

        try
        {
            using var info = await IdentifyAsync(path, cancellationToken).ConfigureAwait(false);
            var broken = GetBrokenTracks(info.RootElement).Select(t => t.GetProperty("id").GetInt32()).ToList();
            return new VobSubRepairResult
            {
                Status = broken.Count > 0 ? VobSubRepairStatus.NeedsRepair : VobSubRepairStatus.NotNeeded,
                Path = path,
                BrokenTracks = broken,
                Size = broken.Count > 0 ? GuessSize(info.RootElement) : null
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            return new VobSubRepairResult { Status = VobSubRepairStatus.Failed, Path = path, Message = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<VobSubRepairResult> RepairAsync(BaseItem item, bool keepBackup, CancellationToken cancellationToken)
    {
        var inspection = await InspectAsync(item, cancellationToken).ConfigureAwait(false);
        if (inspection.Status != VobSubRepairStatus.NeedsRepair)
        {
            return inspection;
        }

        await _repairLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var path = item.Path;
        var folder = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(folder, ".vobsubfix-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tmp);
            using var info = await IdentifyAsync(path, cancellationToken).ConfigureAwait(false);
            var tracks = GetBrokenTracks(info.RootElement).ToList();
            var size = GuessSize(info.RootElement);
            var originalTrackCount = info.RootElement.GetProperty("tracks").GetArrayLength();

            // 1. Extract the broken tracks.
            var extractArgs = new List<string> { path, "tracks" };
            foreach (var t in tracks)
            {
                extractArgs.Add(string.Create(CultureInfo.InvariantCulture, $"{t.GetProperty("id").GetInt32()}:{IdxPath(tmp, t)}"));
            }

            var extract = await RunAsync("mkvextract", extractArgs, cancellationToken).ConfigureAwait(false);
            if (extract.ExitCode > 1)
            {
                return Fail(inspection, "mkvextract: " + Tail(extract));
            }

            // 2. Fix the size line of every .idx.
            foreach (var t in tracks)
            {
                var idx = IdxPath(tmp, t);
                var text = await File.ReadAllTextAsync(idx, Encoding.Latin1, cancellationToken).ConfigureAwait(false);
                text = SizeRegex().IsMatch(text)
                    ? SizeRegex().Replace(text, "size: " + size, 1)
                    : "size: " + size + "\n" + text;
                await File.WriteAllTextAsync(idx, text, Encoding.Latin1, cancellationToken).ConfigureAwait(false);
            }

            // 3. Remux: everything except the broken tracks, plus the fixed ones.
            var output = Path.Combine(tmp, "repaired.mkv");
            var mergeArgs = new List<string>
            {
                "-q", "-o", output,
                "--subtitle-tracks", "!" + string.Join(',', tracks.Select(t => t.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture))),
                path
            };
            foreach (var t in tracks)
            {
                var props = t.TryGetProperty("properties", out var p) ? p : default;
                mergeArgs.Add("--language");
                mergeArgs.Add("0:" + (GetString(props, "language_ietf") ?? GetString(props, "language") ?? "und"));
                var name = GetString(props, "track_name");
                if (!string.IsNullOrEmpty(name))
                {
                    mergeArgs.Add("--track-name");
                    mergeArgs.Add("0:" + name);
                }

                mergeArgs.Add("--default-track-flag");
                mergeArgs.Add("0:" + (GetBool(props, "default_track") ? "yes" : "no"));
                mergeArgs.Add("--forced-display-flag");
                mergeArgs.Add("0:" + (GetBool(props, "forced_track") ? "yes" : "no"));
                mergeArgs.Add(IdxPath(tmp, t));
            }

            var merge = await RunAsync("mkvmerge", mergeArgs, cancellationToken).ConfigureAwait(false);
            if (merge.ExitCode > 1)
            {
                return Fail(inspection, "mkvmerge: " + Tail(merge));
            }

            // 4. Verify the new file.
            using (var newInfo = await IdentifyAsync(output, cancellationToken).ConfigureAwait(false))
            {
                if (newInfo.RootElement.GetProperty("tracks").GetArrayLength() != originalTrackCount)
                {
                    return Fail(inspection, "The repaired file does not have the same tracks.");
                }
            }

            var probe = await RunAsync(_mediaEncoder.ProbePath, ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", output], cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode != 0 || !probe.StdOut.Contains("\"streams\"", StringComparison.Ordinal))
            {
                return Fail(inspection, "ffprobe still cannot read the file: " + Tail(probe));
            }

            // 5. Replace the original.
            cancellationToken.ThrowIfCancellationRequested();
            var backup = path + ".bak";
            File.Move(path, backup, true);
            try
            {
                File.Move(output, path);
            }
            catch (IOException)
            {
                // Put the original back if the new file could not be moved in place.
                File.Move(backup, path, true);
                throw;
            }

            if (!keepBackup)
            {
                File.Delete(backup);
            }

            _logger.LogInformation("Repaired VobSub tracks {Tracks} of {Path} (size {Size})", inspection.BrokenTracks, path, size);
            QueueRefresh(item);

            return new VobSubRepairResult
            {
                Status = VobSubRepairStatus.Repaired,
                Path = path,
                BrokenTracks = inspection.BrokenTracks,
                Size = size,
                BackupPath = keepBackup ? backup : null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            _logger.LogError(ex, "Could not repair VobSub tracks of {Path}", path);
            return Fail(inspection, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete temporary folder {Path}", tmp);
            }

            _repairLock.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RepairBackupInfo> GetBackups(CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo],
            IsVirtualItem = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        });

        var backups = new List<RepairBackupInfo>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backup = ToBackup(item);
            if (backup is not null)
            {
                backups.Add(backup);
            }
        }

        return backups.OrderByDescending(b => b.DateModified).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<RepairBackupInfo> DeleteBackups(IReadOnlyCollection<Guid>? itemIds, CancellationToken cancellationToken)
    {
        IEnumerable<RepairBackupInfo> targets = itemIds is { Count: > 0 }
            ? itemIds.Select(id => _libraryManager.GetItemById(id)).OfType<BaseItem>().Select(ToBackup).OfType<RepairBackupInfo>()
            : GetBackups(cancellationToken);

        var deleted = new List<RepairBackupInfo>();
        foreach (var backup in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(backup.Path!);
                deleted.Add(backup);
                _logger.LogInformation("Deleted repair backup {Path}", backup.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete repair backup {Path}", backup.Path);
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public void Dispose() => _repairLock.Dispose();

    /// <summary>
    /// The backup of an item: "&lt;path&gt;.bak", only when both it and the repaired video exist.
    /// </summary>
    private static RepairBackupInfo? ToBackup(BaseItem item)
    {
        if (!item.IsFileProtocol || string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        var bak = item.Path + ".bak";
        var info = new FileInfo(bak);
        if (!info.Exists || !File.Exists(item.Path))
        {
            return null;
        }

        var name = item is Episode e
            ? string.Create(CultureInfo.InvariantCulture, $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00}")
            : item.ProductionYear is int year ? string.Create(CultureInfo.InvariantCulture, $"{item.Name} ({year})") : item.Name;

        return new RepairBackupInfo
        {
            ItemId = item.Id,
            Name = name,
            Path = bak,
            Size = info.Length,
            DateModified = info.LastWriteTimeUtc
        };
    }

    private static bool IsLocalMkv(BaseItem item)
        => item.IsFileProtocol
            && !string.IsNullOrEmpty(item.Path)
            && item.Path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
            && File.Exists(item.Path);

    private static string IdxPath(string folder, JsonElement track)
        => Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"track{track.GetProperty("id").GetInt32()}.idx"));

    private static string? GetString(JsonElement props, string name)
        => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement props, string name)
        => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// VobSub tracks whose codec private data has no size, or a 0x0 size.
    /// </summary>
    private static IEnumerable<JsonElement> GetBrokenTracks(JsonElement root)
    {
        if (!root.TryGetProperty("tracks", out var tracks))
        {
            yield break;
        }

        foreach (var t in tracks.EnumerateArray())
        {
            var props = t.TryGetProperty("properties", out var p) ? p : default;
            var isVobSub = (t.TryGetProperty("codec", out var codec) && codec.GetString() == "VobSub")
                || GetString(props, "codec_id") == "S_VOBSUB";
            if (!isVobSub)
            {
                continue;
            }

            var hex = GetString(props, "codec_private_data") ?? string.Empty;
            string text;
            try
            {
                text = Encoding.Latin1.GetString(Convert.FromHexString(hex));
            }
            catch (FormatException)
            {
                text = string.Empty;
            }

            var m = SizeRegex().Match(text);
            if (!m.Success || m.Groups[1].Value == "0" || m.Groups[2].Value == "0")
            {
                yield return t;
            }
        }
    }

    /// <summary>
    /// 720x576 for PAL (25 fps) sources, 720x480 for NTSC.
    /// </summary>
    private static string GuessSize(JsonElement root)
    {
        foreach (var t in root.GetProperty("tracks").EnumerateArray())
        {
            if (!t.TryGetProperty("type", out var type) || type.GetString() != "video")
            {
                continue;
            }

            var props = t.TryGetProperty("properties", out var p) ? p : default;
            var dims = GetString(props, "pixel_dimensions");
            if (dims is Ntsc or Pal)
            {
                return dims;
            }

            if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty("default_duration", out var dur) && dur.TryGetInt64(out var ns) && ns > 0
                && Math.Abs((1e9 / ns) - 25) < 0.5)
            {
                return Pal;
            }
        }

        return Ntsc;
    }

    private static VobSubRepairResult Fail(VobSubRepairResult inspection, string message)
        => new()
        {
            Status = VobSubRepairStatus.Failed,
            Path = inspection.Path,
            BrokenTracks = inspection.BrokenTracks,
            Size = inspection.Size,
            Message = message
        };

    private static string Tail(ProcessResult r)
    {
        var text = (r.StdErr + "\n" + r.StdOut).Trim();
        return text.Length > 400 ? text[^400..] : text;
    }

    private static async Task<JsonDocument> IdentifyAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync("mkvmerge", ["-J", path], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode > 1 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new InvalidOperationException("mkvmerge -J failed: " + Tail(result));
        }

        return JsonDocument.Parse(result.StdOut);
    }

    private static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Without a UTF-8 locale mkvtoolnix cuts its output (and misreads paths) at the
        // first non ASCII character, e.g. accents in titles or folder names.
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment["LANG"] = "C.UTF-8";

        var isMkvToolNix = fileName.StartsWith("mkv", StringComparison.Ordinal);
        if (isMkvToolNix)
        {
            psi.ArgumentList.Add("--command-line-charset");
            psi.ArgumentList.Add("UTF-8");
            psi.ArgumentList.Add("--output-charset");
            psi.ArgumentList.Add("UTF-8");
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private void QueueRefresh(BaseItem item)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.Default,
            ForceSave = true,
            IsAutomated = false
        };
        _providerManager.QueueRefresh(item.Id, options, RefreshPriority.High);
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
