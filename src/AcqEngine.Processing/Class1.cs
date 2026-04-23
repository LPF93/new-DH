using System.Buffers.Binary;
using System.Threading.Channels;
using AcqEngine.Core;

namespace AcqEngine.Processing;

public interface IProcessedBlockSink
{
	void Emit(DataBlock block);
}

public interface IProcessor
{
	string Name { get; }
	bool Enabled { get; }
	void Process(DataBlock rawBlock, IProcessedBlockSink sink);
}

public sealed class ProcessingPipeline : IProcessedBlockSink, IAsyncDisposable
{
	private readonly IReadOnlyList<IProcessor> _processors;
	private readonly Func<DataBlock, bool> _onProcessedBlock;
	private readonly Channel<DataBlock> _queue;
	private int _started;

	public ProcessingPipeline(IEnumerable<IProcessor> processors, Func<DataBlock, bool> onProcessedBlock)
	{
		_processors = processors.Where(static x => x.Enabled).ToArray();
		_onProcessedBlock = onProcessedBlock;
		_queue = Channel.CreateUnbounded<DataBlock>(new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false
		});
	}

	public Task WorkerTask { get; private set; } = Task.CompletedTask;

	public void Start()
	{
		if (Interlocked.Exchange(ref _started, 1) == 1)
		{
			return;
		}

		WorkerTask = Task.Run(RunLoopAsync);
	}

	public bool TryEnqueue(DataBlock rawBlock)
	{
		if (Volatile.Read(ref _started) == 0)
		{
			return false;
		}

		rawBlock.AddRef();
		if (!_queue.Writer.TryWrite(rawBlock))
		{
			rawBlock.Release();
			return false;
		}

		return true;
	}

	public async Task StopAsync(CancellationToken ct)
	{
		if (Interlocked.Exchange(ref _started, 0) == 0)
		{
			return;
		}

		_queue.Writer.TryComplete();
		await WorkerTask.WaitAsync(ct);
	}

	public void Emit(DataBlock block)
	{
		_onProcessedBlock(block);
		block.Release();
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync(CancellationToken.None);
	}

	private async Task RunLoopAsync()
	{
		await foreach (var rawBlock in _queue.Reader.ReadAllAsync())
		{
			try
			{
				foreach (var processor in _processors)
				{
					processor.Process(rawBlock, this);
				}
			}
			catch
			{
				// Keep processing pipeline alive for industrial resilience.
			}
			finally
			{
				rawBlock.Release();
			}
		}
	}
}

public sealed class PassThroughProcessor : IProcessor
{
	private readonly BlockPool _blockPool;

	public PassThroughProcessor(BlockPool blockPool)
	{
		_blockPool = blockPool;
	}

	public string Name => "PassThrough";

	public bool Enabled => true;

	public void Process(DataBlock rawBlock, IProcessedBlockSink sink)
	{
		var processed = _blockPool.Rent(rawBlock.PayloadLength);
		processed.CopyFrom(rawBlock.Payload.Span);
		processed.Header = rawBlock.Header with
		{
			StreamKind = StreamKind.Processed,
			HostTimestampNs = TimestampNs.Now()
		};

		sink.Emit(processed);
	}
}

public sealed class BasicStatsProcessor : IProcessor
{
	private readonly BlockPool _blockPool;

	public BasicStatsProcessor(BlockPool blockPool)
	{
		_blockPool = blockPool;
	}

	public string Name => "BasicStats";

	public bool Enabled => true;

	public void Process(DataBlock rawBlock, IProcessedBlockSink sink)
	{
		if (rawBlock.Header.SampleType != SampleType.Int16)
		{
			return;
		}

		var payload = rawBlock.Payload.Span;
		var count = payload.Length / sizeof(short);
		if (count == 0)
		{
			return;
		}

		double sum = 0;
		double sqrSum = 0;
		for (var i = 0; i < payload.Length; i += sizeof(short))
		{
			var value = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(i, sizeof(short)));
			sum += value;
			sqrSum += value * value;
		}

		var mean = sum / count;
		var rms = Math.Sqrt(sqrSum / count);

		var processed = _blockPool.Rent(sizeof(double) * 2);
		var buffer = processed.GetWritableSpan(sizeof(double) * 2);
		BitConverter.TryWriteBytes(buffer.Slice(0, sizeof(double)), mean);
		BitConverter.TryWriteBytes(buffer.Slice(sizeof(double), sizeof(double)), rms);
		processed.Header = new DataBlockHeader(
			rawBlock.Header.SessionId,
			rawBlock.Header.SourceId,
			StreamKind.Processed,
			rawBlock.Header.Sequence,
			rawBlock.Header.StartSampleIndex,
			2,
			1,
			SampleType.Float64,
			rawBlock.Header.DeviceTimestampNs,
			TimestampNs.Now());

		sink.Emit(processed);
	}
}
