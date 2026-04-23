using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AcqEngine.Core;

public enum StreamKind
{
	Raw,
	Processed,
	Event,
	State
}

public enum StorageFormat
{
	Tdms,
	Hdf5
}

public enum SampleType
{
	Int16,
	Int32,
	Float32,
	Float64
}

public readonly record struct DataBlockHeader(
	Guid SessionId,
	int SourceId,
	StreamKind StreamKind,
	long Sequence,
	long StartSampleIndex,
	long SampleCountPerChannel,
	int ChannelCount,
	SampleType SampleType,
	long DeviceTimestampNs,
	long HostTimestampNs);

public static class TimestampNs
{
	public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
}

public sealed record SessionContext
{
	public Guid SessionId { get; init; } = Guid.NewGuid();
	public string TaskName { get; init; } = string.Empty;
	public string OperatorName { get; init; } = string.Empty;
	public string BatchNo { get; init; } = string.Empty;
	public DateTimeOffset StartTime { get; init; } = DateTimeOffset.UtcNow;
	public DateTimeOffset? EndTime { get; init; }
	public StorageFormat StorageFormat { get; init; } = StorageFormat.Tdms;
	public string FileNamingTemplate { get; init; } = "{TaskName}_{Stream}_{Date:yyyyMMdd}_{StartTime:HHmmss}_{AutoInc:0000}_seg{SegmentNo:0000}";
	public IReadOnlyList<SourceDescriptor> Sources { get; init; } = Array.Empty<SourceDescriptor>();
	public bool WriteRaw { get; init; } = true;
	public bool WriteProcessed { get; init; } = true;
	public int AutoIncrementNo { get; init; } = 1;
}

public sealed record SourceDescriptor
{
	public int SourceId { get; init; }
	public string DeviceName { get; init; } = string.Empty;
	public int ChannelCount { get; init; }
	public double SampleRateHz { get; init; }
	public SampleType SampleType { get; init; } = SampleType.Int16;
}

public sealed record PreviewFrame(
	int SourceId,
	DateTimeOffset WindowStart,
	DateTimeOffset WindowEnd,
	IReadOnlyList<double> Samples,
	int Decimation);

public interface IAcquisitionSource : IDisposable
{
	int SourceId { get; }
	event Action<DataBlock>? BlockArrived;
	void Start(CancellationToken cancellationToken = default);
	void Stop();
}

public interface IFrameBus
{
	void Subscribe(string name, Action<DataBlock> handler);
	void Publish(DataBlock block);
}

public interface IRecentDataCache
{
	void Put(DataBlock block);
	PreviewFrame GetPreview(int sourceId, TimeSpan window, int maxPoints = 1024);
}

public sealed class DataBlock : IDisposable
{
	private readonly ArrayPool<byte> _pool;
	private byte[]? _buffer;
	private int _refCount;
	private int _returned;

	internal DataBlock(ArrayPool<byte> pool, byte[] buffer, int payloadLength)
	{
		_pool = pool;
		_buffer = buffer;
		_refCount = 1;
		PayloadLength = payloadLength;
	}

	public DataBlockHeader Header { get; set; }

	public int PayloadLength { get; private set; }

	public int Capacity => _buffer?.Length ?? 0;

	public ReadOnlyMemory<byte> Payload
	{
		get
		{
			var buffer = _buffer ?? throw new ObjectDisposedException(nameof(DataBlock));
			return new ReadOnlyMemory<byte>(buffer, 0, PayloadLength);
		}
	}

	public Span<byte> GetWritableSpan(int bytes)
	{
		var buffer = _buffer ?? throw new ObjectDisposedException(nameof(DataBlock));
		if (bytes > buffer.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(bytes), "Requested payload size exceeds block capacity.");
		}

		PayloadLength = bytes;
		return buffer.AsSpan(0, bytes);
	}

	public void CopyFrom(ReadOnlySpan<byte> source)
	{
		source.CopyTo(GetWritableSpan(source.Length));
	}

	public void AddRef()
	{
		if (_buffer is null)
		{
			throw new ObjectDisposedException(nameof(DataBlock));
		}

		Interlocked.Increment(ref _refCount);
	}

	public void Release()
	{
		var count = Interlocked.Decrement(ref _refCount);
		if (count == 0)
		{
			ReturnBufferToPool();
			return;
		}

		if (count < 0)
		{
			throw new InvalidOperationException("DataBlock reference count dropped below zero.");
		}
	}

	public void Dispose()
	{
		Release();
	}

	private void ReturnBufferToPool()
	{
		if (Interlocked.Exchange(ref _returned, 1) == 1)
		{
			return;
		}

		var buffer = Interlocked.Exchange(ref _buffer, null);
		if (buffer is not null)
		{
			_pool.Return(buffer);
		}
	}
}

public sealed class BlockPool
{
	private readonly ArrayPool<byte> _arrayPool;

	public BlockPool(ArrayPool<byte>? arrayPool = null)
	{
		_arrayPool = arrayPool ?? ArrayPool<byte>.Shared;
	}

	public DataBlock Rent(int payloadBytes)
	{
		if (payloadBytes <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(payloadBytes), "Payload bytes must be positive.");
		}

		var buffer = _arrayPool.Rent(payloadBytes);
		return new DataBlock(_arrayPool, buffer, payloadBytes);
	}
}

public sealed class FrameBus : IFrameBus
{
	private readonly object _gate = new();
	private readonly List<FrameSubscriber> _subscribers = new();

	public void Subscribe(string name, Action<DataBlock> handler)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("Subscriber name is required.", nameof(name));
		}

		if (handler is null)
		{
			throw new ArgumentNullException(nameof(handler));
		}

		lock (_gate)
		{
			_subscribers.Add(new FrameSubscriber(name, handler));
		}
	}

	public void Publish(DataBlock block)
	{
		FrameSubscriber[] subscribers;
		lock (_gate)
		{
			subscribers = _subscribers.ToArray();
		}

		if (subscribers.Length == 0)
		{
			block.Release();
			return;
		}

		foreach (var subscriber in subscribers)
		{
			block.AddRef();
			try
			{
				subscriber.Handler(block);
			}
			catch
			{
				// Protect ingest path: subscriber failures should not break callback-to-disk chain.
			}
			finally
			{
				block.Release();
			}
		}

		block.Release();
	}

	private sealed record FrameSubscriber(string Name, Action<DataBlock> Handler);
}

public sealed class IngestDispatcher : IAsyncDisposable
{
	private readonly IFrameBus _frameBus;
	private readonly ConcurrentDictionary<int, SourceRegistration> _registrations = new();
	private readonly CancellationTokenSource _disposeCts = new();

	public IngestDispatcher(IFrameBus frameBus)
	{
		_frameBus = frameBus;
	}

	public void RegisterSource(IAcquisitionSource source, int queueCapacity = 4096)
	{
		var queue = Channel.CreateUnbounded<DataBlock>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false
		});

		void OnBlockArrived(DataBlock block)
		{
			if (!queue.Writer.TryWrite(block))
			{
				block.Release();
			}
		}

		source.BlockArrived += OnBlockArrived;

		var pumpTask = Task.Run(() => PumpSourceAsync(queue.Reader, _disposeCts.Token));
		var registration = new SourceRegistration(source, queue, OnBlockArrived, pumpTask);
		if (!_registrations.TryAdd(source.SourceId, registration))
		{
			source.BlockArrived -= OnBlockArrived;
			queue.Writer.TryComplete();
			throw new InvalidOperationException($"Source {source.SourceId} has already been registered.");
		}
	}

	public async ValueTask DisposeAsync()
	{
		_disposeCts.Cancel();

		foreach (var registration in _registrations.Values)
		{
			registration.Source.BlockArrived -= registration.OnBlockArrived;
			registration.Queue.Writer.TryComplete();
		}

		var tasks = _registrations.Values.Select(static x => x.PumpTask).ToArray();
		if (tasks.Length > 0)
		{
			try
			{
				await Task.WhenAll(tasks);
			}
			catch (OperationCanceledException)
			{
				// Ignore cancellation during shutdown.
			}
		}

		_disposeCts.Dispose();
	}

	private async Task PumpSourceAsync(ChannelReader<DataBlock> reader, CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var block in reader.ReadAllAsync(cancellationToken))
			{
				_frameBus.Publish(block);
			}
		}
		catch (OperationCanceledException)
		{
			// Dispatcher is stopping.
		}
	}

	private sealed record SourceRegistration(
		IAcquisitionSource Source,
		Channel<DataBlock> Queue,
		Action<DataBlock> OnBlockArrived,
		Task PumpTask);
}

public sealed class SessionManager
{
	private readonly object _gate = new();

	public SessionContext? CurrentSession { get; private set; }

	public SessionContext Start(SessionContext session)
	{
		lock (_gate)
		{
			if (CurrentSession is not null)
			{
				throw new InvalidOperationException("A session is already running.");
			}

			CurrentSession = session;
			return session;
		}
	}

	public SessionContext Stop()
	{
		lock (_gate)
		{
			var current = CurrentSession ?? throw new InvalidOperationException("No running session to stop.");
			var stopped = current with { EndTime = DateTimeOffset.UtcNow };
			CurrentSession = null;
			return stopped;
		}
	}
}

public sealed class RecentDataCache : IRecentDataCache
{
	private readonly TimeSpan _maxWindow;
	private readonly ConcurrentDictionary<int, SourceCache> _sources = new();

	public RecentDataCache(TimeSpan? maxWindow = null)
	{
		_maxWindow = maxWindow ?? TimeSpan.FromSeconds(60);
	}

	public void Put(DataBlock block)
	{
		var source = _sources.GetOrAdd(block.Header.SourceId, static _ => new SourceCache());
		var sample = TryReadFirstSample(block.Header.SampleType, block.Payload.Span, out var value) ? value : 0d;
		var nowNs = block.Header.HostTimestampNs;
		var cutoff = nowNs - (long)_maxWindow.TotalMilliseconds * 1_000_000;

		lock (source.SyncRoot)
		{
			source.Points.Enqueue(new CachePoint(nowNs, sample));
			while (source.Points.Count > 0 && source.Points.TryPeek(out var head) && head.TimestampNs < cutoff)
			{
				source.Points.TryDequeue(out _);
			}
		}
	}

	public PreviewFrame GetPreview(int sourceId, TimeSpan window, int maxPoints = 1024)
	{
		if (!_sources.TryGetValue(sourceId, out var source))
		{
			var now = DateTimeOffset.UtcNow;
			return new PreviewFrame(sourceId, now, now, Array.Empty<double>(), 1);
		}

		var effectiveWindow = window <= TimeSpan.Zero ? _maxWindow : window;
		var nowNs = TimestampNs.Now();
		var cutoff = nowNs - (long)effectiveWindow.TotalMilliseconds * 1_000_000;

		List<CachePoint> points;
		lock (source.SyncRoot)
		{
			points = source.Points.Where(x => x.TimestampNs >= cutoff).ToList();
		}

		if (points.Count == 0)
		{
			var now = DateTimeOffset.UtcNow;
			return new PreviewFrame(sourceId, now, now, Array.Empty<double>(), 1);
		}

		var step = Math.Max(1, points.Count / Math.Max(1, maxPoints));
		var decimated = new List<double>((points.Count / step) + 1);
		for (var i = 0; i < points.Count; i += step)
		{
			decimated.Add(points[i].Value);
		}

		var start = FromUnixNs(points[0].TimestampNs);
		var end = FromUnixNs(points[^1].TimestampNs);
		return new PreviewFrame(sourceId, start, end, decimated, step);
	}

	private static bool TryReadFirstSample(SampleType sampleType, ReadOnlySpan<byte> payload, out double value)
	{
		value = 0d;
		switch (sampleType)
		{
			case SampleType.Int16 when payload.Length >= sizeof(short):
				value = BinaryPrimitives.ReadInt16LittleEndian(payload);
				return true;
			case SampleType.Int32 when payload.Length >= sizeof(int):
				value = BinaryPrimitives.ReadInt32LittleEndian(payload);
				return true;
			case SampleType.Float32 when payload.Length >= sizeof(float):
				value = BitConverter.ToSingle(payload.Slice(0, sizeof(float)));
				return true;
			case SampleType.Float64 when payload.Length >= sizeof(double):
				value = BitConverter.ToDouble(payload.Slice(0, sizeof(double)));
				return true;
			default:
				return false;
		}
	}

	private static DateTimeOffset FromUnixNs(long unixNs)
	{
		var milliseconds = unixNs / 1_000_000;
		return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
	}

	private sealed class SourceCache
	{
		public object SyncRoot { get; } = new();
		public Queue<CachePoint> Points { get; } = new();
	}

	private readonly record struct CachePoint(long TimestampNs, double Value);
}
