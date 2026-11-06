using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Models.SystemInfoDtos;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Server resource monitor (CPU, memory and network usage) for the admin dashboard.
/// </summary>
public class ResourceMonitorController : BaseJellyfinApiController
{
    private const int SampleMilliseconds = 500;

    /// <summary>
    /// Gets a snapshot of the server resource usage.
    /// CPU and network rates are measured over a short sampling window.
    /// </summary>
    /// <response code="200">Resource usage retrieved.</response>
    /// <response code="403">User does not have permission to retrieve information.</response>
    /// <returns>A <see cref="ServerResourcesDto"/> with the current resource usage.</returns>
    [HttpGet]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ServerResourcesDto>> GetServerResources()
    {
        using var process = Process.GetCurrentProcess();

        var cpuBefore = ReadCpuTimes();
        var netBefore = ReadNetworkBytes();
        var processCpuBefore = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();

        await Task.Delay(SampleMilliseconds).ConfigureAwait(false);

        process.Refresh();
        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        var cpuAfter = ReadCpuTimes();
        var netAfter = ReadNetworkBytes();
        var processCpuAfter = process.TotalProcessorTime;

        double? cpuUsage = null;
        if (cpuBefore.HasValue && cpuAfter.HasValue)
        {
            var total = cpuAfter.Value.Total - cpuBefore.Value.Total;
            var idle = cpuAfter.Value.Idle - cpuBefore.Value.Idle;
            if (total > 0)
            {
                cpuUsage = Math.Clamp(100.0 * (total - idle) / total, 0, 100);
            }
        }

        var cores = Environment.ProcessorCount;
        var processCpu = elapsedSeconds > 0
            ? Math.Clamp(100.0 * (processCpuAfter - processCpuBefore).TotalSeconds / (elapsedSeconds * cores), 0, 100)
            : 0;

        double? rxRate = null;
        double? txRate = null;
        if (netBefore.HasValue && netAfter.HasValue && elapsedSeconds > 0)
        {
            rxRate = Math.Max(0, netAfter.Value.Rx - netBefore.Value.Rx) / elapsedSeconds;
            txRate = Math.Max(0, netAfter.Value.Tx - netBefore.Value.Tx) / elapsedSeconds;
        }

        var memInfo = ReadMemInfo();
        var (containerUsed, containerLimit) = ReadCgroupMemory();

        return new ServerResourcesDto
        {
            CpuUsagePercent = cpuUsage,
            ProcessCpuPercent = processCpu,
            CpuCores = cores,
            LoadAverage = ReadLoadAverage(),
            MemoryTotalBytes = memInfo.TryGetValue("MemTotal", out var memTotal) ? memTotal : null,
            MemoryAvailableBytes = memInfo.TryGetValue("MemAvailable", out var memAvailable) ? memAvailable : null,
            ContainerMemoryUsedBytes = containerUsed,
            ContainerMemoryLimitBytes = containerLimit,
            ProcessMemoryBytes = process.WorkingSet64,
            ManagedHeapBytes = GC.GetTotalMemory(false),
            NetworkReceiveBytesPerSecond = rxRate,
            NetworkTransmitBytesPerSecond = txRate,
            NetworkReceiveTotalBytes = netAfter?.Rx,
            NetworkTransmitTotalBytes = netAfter?.Tx,
            FfmpegProcessCount = CountFfmpegProcesses(),
            ThreadCount = process.Threads.Count,
            UptimeSeconds = (DateTime.Now - process.StartTime).TotalSeconds
        };
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long ParseLong(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    /// <summary>
    /// Reads the aggregated CPU times from /proc/stat (Linux only).
    /// </summary>
    private static (long Total, long Idle)? ReadCpuTimes()
    {
        var content = ReadFile("/proc/stat");
        var line = content?.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        // user nice system idle iowait irq softirq steal
        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(ParseLong).ToArray();
        if (values.Length < 5)
        {
            return null;
        }

        return (values.Sum(), values[3] + values[4]);
    }

    /// <summary>
    /// Reads received/transmitted bytes of all non-loopback interfaces from /proc/net/dev.
    /// </summary>
    private static (long Rx, long Tx)? ReadNetworkBytes()
    {
        var content = ReadFile("/proc/net/dev");
        if (content is null)
        {
            return null;
        }

        long rx = 0;
        long tx = 0;
        foreach (var line in content.Split('\n').Skip(2))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            if (name == "lo")
            {
                continue;
            }

            var fields = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 9)
            {
                rx += ParseLong(fields[0]);
                tx += ParseLong(fields[8]);
            }
        }

        return (rx, tx);
    }

    /// <summary>
    /// Reads /proc/meminfo values in bytes.
    /// </summary>
    private static Dictionary<string, long> ReadMemInfo()
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var content = ReadFile("/proc/meminfo");
        if (content is null)
        {
            return result;
        }

        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2)
            {
                var number = parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (number is not null)
                {
                    result[parts[0].Trim()] = ParseLong(number) * 1024;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Reads container memory usage and limit from cgroup v2 (falls back to v1).
    /// </summary>
    private static (long? Used, long? Limit) ReadCgroupMemory()
    {
        var current = ReadFile("/sys/fs/cgroup/memory.current");
        if (current is not null)
        {
            var used = ParseLong(current.Trim());
            var stat = ReadFile("/sys/fs/cgroup/memory.stat");
            var inactiveLine = stat?.Split('\n').FirstOrDefault(l => l.StartsWith("inactive_file ", StringComparison.Ordinal));
            if (inactiveLine is not null)
            {
                used = Math.Max(0, used - ParseLong(inactiveLine["inactive_file ".Length..].Trim()));
            }

            var max = ReadFile("/sys/fs/cgroup/memory.max")?.Trim();
            long? limit = max is null || max == "max" ? null : ParseLong(max);
            return (used, limit);
        }

        var v1Usage = ReadFile("/sys/fs/cgroup/memory/memory.usage_in_bytes");
        if (v1Usage is not null)
        {
            var v1Limit = ParseLong(ReadFile("/sys/fs/cgroup/memory/memory.limit_in_bytes")?.Trim() ?? "0");

            // cgroup v1 reports a huge number when there is no limit
            return (ParseLong(v1Usage.Trim()), v1Limit is > 0 and < long.MaxValue / 2 ? v1Limit : null);
        }

        return (null, null);
    }

    private static IReadOnlyList<double>? ReadLoadAverage()
    {
        var content = ReadFile("/proc/loadavg");
        if (content is null)
        {
            return null;
        }

        return content.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(3)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0)
            .ToList();
    }

    private static int CountFfmpegProcesses()
    {
        var processes = Process.GetProcessesByName("ffmpeg");
        var count = processes.Length;
        foreach (var p in processes)
        {
            p.Dispose();
        }

        return count;
    }
}
