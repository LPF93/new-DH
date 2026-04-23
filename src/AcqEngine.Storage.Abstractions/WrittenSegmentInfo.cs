using AcqEngine.Core;

namespace AcqEngine.Storage.Abstractions;

public sealed record WrittenSegmentInfo(
    string ContainerFormat,
    StreamKind StreamKind,
    int SegmentNo,
    string FilePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long BlockCount,
    long PayloadBytes,
    long FileBytes);
