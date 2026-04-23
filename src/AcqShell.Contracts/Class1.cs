namespace AcqShell.Contracts;

public sealed record StartSessionRequest(
	string TaskName,
	string Operator,
	string BatchNo,
	string StorageFormat,
	bool WriteRaw,
	bool WriteProcessed);

public sealed record StopSessionRequest(Guid SessionId);

public sealed record SessionStatusDto(
	Guid SessionId,
	string State,
	DateTimeOffset StartTime,
	DateTimeOffset? EndTime,
	int SourceCount,
	long TotalBlocks,
	long TotalBytes);

public sealed record EngineHealthDto(
	int SourceQueueDepth,
	int RawQueueDepth,
	int ProcessQueueDepth,
	int UiDrops,
	DateTimeOffset Timestamp);
