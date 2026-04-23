using System.Collections.Concurrent;
using AcqEngine.Core;

namespace AcqEngine.Diagnostics;

public sealed record DiagnosticsSnapshot(
	long TotalBlocks,
	long TotalBytes,
	long RawEnqueueFailures,
	long ProcessEnqueueFailures,
	IReadOnlyDictionary<int, long> BlocksBySource);

public sealed class RuntimeDiagnostics
{
	private readonly ConcurrentDictionary<int, long> _blocksBySource = new();
	private long _totalBlocks;
	private long _totalBytes;
	private long _rawEnqueueFailures;
	private long _processEnqueueFailures;

	public void OnBlockPublished(DataBlock block)
	{
		Interlocked.Increment(ref _totalBlocks);
		Interlocked.Add(ref _totalBytes, block.PayloadLength);
		_blocksBySource.AddOrUpdate(block.Header.SourceId, 1, static (_, value) => value + 1);
	}

	public void MarkRawEnqueueFailure()
	{
		Interlocked.Increment(ref _rawEnqueueFailures);
	}

	public void MarkProcessEnqueueFailure()
	{
		Interlocked.Increment(ref _processEnqueueFailures);
	}

	public DiagnosticsSnapshot Snapshot()
	{
		return new DiagnosticsSnapshot(
			Volatile.Read(ref _totalBlocks),
			Volatile.Read(ref _totalBytes),
			Volatile.Read(ref _rawEnqueueFailures),
			Volatile.Read(ref _processEnqueueFailures),
			new Dictionary<int, long>(_blocksBySource));
	}
}
