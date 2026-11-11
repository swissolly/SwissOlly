namespace SwissOlly.Core.Models;

public sealed record MetricPoint(DateTimeOffset Timestamp, double Value);

public sealed record ProcessActivitySample(
    int ProcessId,
    string Name,
    double CpuPercent,
    double MemoryGigabytes,
    int ThreadCount,
    int HandleCount);

public sealed record LiveBoardDetails(
    IReadOnlyList<ProcessActivitySample> Processes);

public sealed record MetricSample(
    string PanelKey,
    string PanelTitle,
    string SeriesKey,
    string SeriesName,
    MetricCategory Category,
    MetricUnit Unit,
    double Value,
    DateTimeOffset Timestamp);

public sealed record MetricSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<MetricSample> Samples,
    LiveBoardDetails? LiveDetails = null);

public sealed record MetricSeriesHistoryItem(
    string SeriesKey,
    string SeriesName,
    IReadOnlyList<MetricPoint> Points);

public sealed record MetricSeriesHistory(
    string PanelKey,
    string PanelTitle,
    MetricCategory Category,
    MetricUnit Unit,
    IReadOnlyList<MetricSeriesHistoryItem> Series);
