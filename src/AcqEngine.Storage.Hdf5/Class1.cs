using System.Buffers.Binary;
using AcqEngine.Core;
using AcqEngine.Storage.Abstractions;

namespace AcqEngine.Storage.Hdf5;

public sealed class Hdf5Writer : IContainerWriter
{
	private readonly IFileNamingPolicy _namingPolicy;
	private readonly string _extension;
	private readonly List<WrittenSegmentInfo> _completedSegments = new();
	private SessionContext? _session;
	private FileStream? _segmentStream;
	private StreamKind _currentStreamKind;
	private int _currentSegmentNo;
	private string? _currentSegmentPath;
	private DateTimeOffset _currentSegmentStartedAt;
	private long _currentBlockCount;
	private long _currentPayloadBytes;

	public Hdf5Writer(IFileNamingPolicy namingPolicy, string extension = ".h5")
	{
		_namingPolicy = namingPolicy;
		_extension = extension;
	}

	public IReadOnlyList<WrittenSegmentInfo> CompletedSegments => _completedSegments;

	public ValueTask OpenSessionAsync(SessionContext session, CancellationToken ct)
	{
		_session = session;
		_completedSegments.Clear();
		Directory.CreateDirectory(_namingPolicy.BuildSessionDirectory(session));
		return ValueTask.CompletedTask;
	}

	public async ValueTask OpenSegmentAsync(StreamKind streamKind, int segmentNo, CancellationToken ct)
	{
		if (_session is null)
		{
			throw new InvalidOperationException("Session has not been opened.");
		}

		await CloseSegmentAsync(ct);

		var sessionDir = _namingPolicy.BuildSessionDirectory(_session);
		var streamDir = Path.Combine(sessionDir, streamKind == StreamKind.Raw ? "raw" : "proc");
		Directory.CreateDirectory(streamDir);

		var segmentFile = _namingPolicy.BuildSegmentFileName(_session, streamKind, segmentNo);
		if (!segmentFile.EndsWith(_extension, StringComparison.OrdinalIgnoreCase))
		{
			segmentFile += _extension;
		}

		var segmentPath = Path.Combine(streamDir, segmentFile);
		_segmentStream = new FileStream(
			segmentPath,
			FileMode.Create,
			FileAccess.Write,
			FileShare.Read,
			1024 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan);

		_currentStreamKind = streamKind;
		_currentSegmentNo = segmentNo;
		_currentSegmentPath = segmentPath;
		_currentSegmentStartedAt = DateTimeOffset.UtcNow;
		_currentBlockCount = 0;
		_currentPayloadBytes = 0;
	}

	public async ValueTask WriteBlockAsync(DataBlock block, CancellationToken ct)
	{
		if (_segmentStream is null)
		{
			throw new InvalidOperationException("Segment has not been opened.");
		}

		var headerBytes = BuildHeader(block);

		await _segmentStream.WriteAsync(headerBytes, ct);
		await _segmentStream.WriteAsync(block.Payload, ct);
		_currentBlockCount++;
		_currentPayloadBytes += block.PayloadLength;
	}

	public async ValueTask FlushAsync(CancellationToken ct)
	{
		if (_segmentStream is not null)
		{
			await _segmentStream.FlushAsync(ct);
		}
	}

	public async ValueTask CloseSegmentAsync(CancellationToken ct)
	{
		if (_segmentStream is not null)
		{
			await _segmentStream.FlushAsync(ct);
			var fileBytes = _segmentStream.Length;
			await _segmentStream.DisposeAsync();

			if (!string.IsNullOrWhiteSpace(_currentSegmentPath))
			{
				_completedSegments.Add(new WrittenSegmentInfo(
					"HDF5",
					_currentStreamKind,
					_currentSegmentNo,
					_currentSegmentPath,
					_currentSegmentStartedAt,
					DateTimeOffset.UtcNow,
					_currentBlockCount,
					_currentPayloadBytes,
					fileBytes));
			}

			_segmentStream = null;
			_currentSegmentPath = null;
			_currentBlockCount = 0;
			_currentPayloadBytes = 0;
		}
	}

	public async ValueTask CloseSessionAsync(CancellationToken ct)
	{
		await CloseSegmentAsync(ct);
		_session = null;
	}

	public async ValueTask DisposeAsync()
	{
		await CloseSessionAsync(CancellationToken.None);
	}

	private static byte[] BuildHeader(DataBlock block)
	{
		var headerBytes = new byte[56];
		var span = headerBytes.AsSpan();
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), 0x35464448); // HDF5
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), 1);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), block.Header.SourceId);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(12, 8), block.Header.Sequence);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(20, 8), block.Header.SampleCountPerChannel);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), block.Header.ChannelCount);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(32, 4), (int)block.Header.SampleType);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(36, 8), block.Header.DeviceTimestampNs);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(44, 8), block.Header.HostTimestampNs);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(52, 4), block.PayloadLength);
		return headerBytes;
	}
}
