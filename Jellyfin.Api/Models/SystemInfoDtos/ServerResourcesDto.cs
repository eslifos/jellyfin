using System.Collections.Generic;

namespace Jellyfin.Api.Models.SystemInfoDtos;

/// <summary>
/// A snapshot of the server resource usage (CPU, memory, network).
/// Values that cannot be read on the current platform are null.
/// </summary>
public record ServerResourcesDto
{
    /// <summary>
    /// Gets the overall CPU usage of the host in percent (0-100).
    /// </summary>
    public double? CpuUsagePercent { get; init; }

    /// <summary>
    /// Gets the CPU usage of the Jellyfin process in percent of all cores (0-100).
    /// </summary>
    public double ProcessCpuPercent { get; init; }

    /// <summary>
    /// Gets the number of logical CPU cores.
    /// </summary>
    public int CpuCores { get; init; }

    /// <summary>
    /// Gets the 1, 5 and 15 minute load averages.
    /// </summary>
    public IReadOnlyList<double>? LoadAverage { get; init; }

    /// <summary>
    /// Gets the total memory of the host in bytes.
    /// </summary>
    public long? MemoryTotalBytes { get; init; }

    /// <summary>
    /// Gets the available memory of the host in bytes.
    /// </summary>
    public long? MemoryAvailableBytes { get; init; }

    /// <summary>
    /// Gets the memory used by the container (cgroup), without inactive file cache, in bytes.
    /// </summary>
    public long? ContainerMemoryUsedBytes { get; init; }

    /// <summary>
    /// Gets the memory limit of the container (cgroup) in bytes, if any.
    /// </summary>
    public long? ContainerMemoryLimitBytes { get; init; }

    /// <summary>
    /// Gets the working set of the Jellyfin process in bytes.
    /// </summary>
    public long ProcessMemoryBytes { get; init; }

    /// <summary>
    /// Gets the managed heap size of the Jellyfin process in bytes.
    /// </summary>
    public long ManagedHeapBytes { get; init; }

    /// <summary>
    /// Gets the network receive rate in bytes per second.
    /// </summary>
    public double? NetworkReceiveBytesPerSecond { get; init; }

    /// <summary>
    /// Gets the network transmit rate in bytes per second.
    /// </summary>
    public double? NetworkTransmitBytesPerSecond { get; init; }

    /// <summary>
    /// Gets the total bytes received since the network interface came up.
    /// </summary>
    public long? NetworkReceiveTotalBytes { get; init; }

    /// <summary>
    /// Gets the total bytes transmitted since the network interface came up.
    /// </summary>
    public long? NetworkTransmitTotalBytes { get; init; }

    /// <summary>
    /// Gets the number of running ffmpeg processes.
    /// </summary>
    public int FfmpegProcessCount { get; init; }

    /// <summary>
    /// Gets the number of threads of the Jellyfin process.
    /// </summary>
    public int ThreadCount { get; init; }

    /// <summary>
    /// Gets the uptime of the Jellyfin process in seconds.
    /// </summary>
    public double UptimeSeconds { get; init; }
}
