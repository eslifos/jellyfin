namespace MediaBrowser.Controller.Persistence;

/// <summary>
/// Totals of the items matching a query, computed by the database without loading the items.
/// </summary>
/// <param name="Count">Number of items.</param>
/// <param name="Size">Sum of the file sizes, in bytes.</param>
/// <param name="RunTimeTicks">Sum of the runtimes, in ticks.</param>
/// <param name="PhotoCount">Number of photos.</param>
/// <param name="VideoCount">Number of plain videos.</param>
public sealed record ItemFileTotals(int Count, long Size, long RunTimeTicks, int PhotoCount, int VideoCount);
