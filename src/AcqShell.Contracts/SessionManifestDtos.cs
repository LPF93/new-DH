namespace AcqShell.Contracts;

public sealed record SessionManifestSourceDto(
    int SourceId,
    string DeviceName,
    int ChannelCount,
    double SampleRateHz,
    string SampleType);

public sealed record SessionSegmentDto(
    string ContainerFormat,
    string Stream,
    int SegmentNo,
    string RelativePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long BlockCount,
    long PayloadBytes,
    long FileBytes);

public sealed record SessionMetricsDto(
    long TotalBlocks,
    long TotalBytes,
    long RawEnqueueFailures,
    long ProcessEnqueueFailures,
    IReadOnlyDictionary<int, long> BlocksBySource);

public sealed record SessionManifestDto(
    Guid SessionId,
    string TaskName,
    string OperatorName,
    string BatchNo,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    string StorageFormat,
    bool WriteRaw,
    bool WriteProcessed,
    IReadOnlyList<SessionManifestSourceDto> Sources,
    IReadOnlyList<SessionSegmentDto> Segments,
    SessionMetricsDto Metrics);
