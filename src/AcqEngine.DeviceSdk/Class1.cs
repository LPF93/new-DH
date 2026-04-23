using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AcqEngine.Core;

namespace AcqEngine.DeviceSdk;

public sealed class MockSdkBridge
{
	[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
	public delegate void SampleDataChangeEventHandle(
		long sampleTime,
		int groupIdSize,
		IntPtr groupInfo,
		int messageType,
		int groupId,
		int channelStyle,
		int channelId,
		int machineId,
		long totalDataCount,
		int dataCountPerChannel,
		int bufferCount,
		int blockIndex,
		long sampleData);

	private SampleDataChangeEventHandle? _callback;

	public int SetDataChangeCallBackFun(SampleDataChangeEventHandle callback)
	{
		_callback = callback;
		return 1;
	}

	public int DA_ReleaseBuffer(long point)
	{
		if (point != 0)
		{
			Marshal.FreeHGlobal((IntPtr)point);
		}

		return 1;
	}

	public void RaiseSampleData(SdkSampleDataEvent evt)
	{
		_callback?.Invoke(
			evt.SampleTime,
			evt.GroupIdSize,
			evt.GroupInfo,
			evt.MessageType,
			evt.GroupId,
			evt.ChannelStyle,
			evt.ChannelId,
			evt.MachineId,
			evt.TotalDataCount,
			evt.DataCountPerChannel,
			evt.BufferCount,
			evt.BlockIndex,
			evt.SampleData);
	}
}

public sealed record SdkSampleDataEvent(
	long SampleTime,
	int GroupIdSize,
	IntPtr GroupInfo,
	int MessageType,
	int GroupId,
	int ChannelStyle,
	int ChannelId,
	int MachineId,
	long TotalDataCount,
	int DataCountPerChannel,
	int BufferCount,
	int BlockIndex,
	long SampleData);

public sealed record SampleCallbackContext(
	int MessageType,
	int GroupId,
	int ChannelStyle,
	int ChannelId,
	int MachineId,
	long StartPos,
	int PerChannelCount,
	int TotalBytesCount,
	int TriggerBlockIndex,
	long SampleTime,
	long SampleDataPtr);

public sealed class DemoCallbackAcquisitionSource : IDescriptorAcquisitionSource
{
	private readonly SourceDescriptor _descriptor;
	private readonly BlockPool _blockPool;
	private readonly MockSdkBridge _sdkBridge;
	private readonly int _samplesPerChannelPerBlock;
	private readonly TimeSpan _blockInterval;
	private readonly Random _random = new();

	private long _sequence;
	private long _sampleIndex;
	private long _callbackPos;
	private int _blockIndex;
	private CancellationTokenSource? _cts;
	private Task? _producerTask;
	private MockSdkBridge.SampleDataChangeEventHandle? _sampleDataHandler;

	public DemoCallbackAcquisitionSource(
		SourceDescriptor descriptor,
		BlockPool blockPool,
		MockSdkBridge? sdkBridge = null,
		int samplesPerChannelPerBlock = 256,
		TimeSpan? blockInterval = null)
	{
		_descriptor = descriptor;
		_blockPool = blockPool;
		_sdkBridge = sdkBridge ?? new MockSdkBridge();
		_samplesPerChannelPerBlock = samplesPerChannelPerBlock;
		_blockInterval = blockInterval ?? TimeSpan.FromMilliseconds(10);
	}

	public int SourceId => _descriptor.SourceId;

	public SourceDescriptor Descriptor => _descriptor;

	public event Action<DataBlock>? BlockArrived;

	public void Start(CancellationToken cancellationToken = default)
	{
		if (_producerTask is not null)
		{
			return;
		}

		_sampleDataHandler = DealSampleData;
		_sdkBridge.SetDataChangeCallBackFun(_sampleDataHandler);

		_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_producerTask = Task.Run(() => ProduceLoopAsync(_cts.Token), CancellationToken.None);
	}

	public void Stop()
	{
		if (_cts is null)
		{
			return;
		}

		_cts.Cancel();
		try
		{
			_producerTask?.Wait();
		}
		catch (AggregateException ex) when (ex.InnerExceptions.All(static e => e is TaskCanceledException or OperationCanceledException))
		{
			// Expected during shutdown.
		}
		finally
		{
			_cts.Dispose();
			_cts = null;
			_producerTask = null;
		}
	}

	public void Dispose()
	{
		Stop();
	}

	private async Task ProduceLoopAsync(CancellationToken ct)
	{
		var bytesPerSample = GetBytesPerSample(_descriptor.SampleType);
		var blockBytes = _descriptor.ChannelCount * _samplesPerChannelPerBlock * bytesPerSample;

		while (!ct.IsCancellationRequested)
		{
			var sampleStartPos = Interlocked.Read(ref _sampleIndex);
			Interlocked.Add(ref _sampleIndex, _samplesPerChannelPerBlock);

			var managedBuffer = new byte[blockBytes];
			FillPayload(managedBuffer.AsSpan(), _descriptor.SampleType, sampleStartPos);

			var ptr = Marshal.AllocHGlobal(blockBytes);
			Marshal.Copy(managedBuffer, 0, ptr, blockBytes);

			var evt = new SdkSampleDataEvent(
				TimestampNs.Now(),
				0,
				IntPtr.Zero,
				1,
				_descriptor.SourceId,
				0,
				_descriptor.SourceId,
				_descriptor.SourceId,
				sampleStartPos,
				_samplesPerChannelPerBlock,
				blockBytes,
				Interlocked.Increment(ref _blockIndex),
				ptr.ToInt64());

			_sdkBridge.RaiseSampleData(evt);

			await Task.Delay(_blockInterval, ct);
		}
	}

	private void DealSampleData(
		long sampleTime,
		int groupIdSize,
		IntPtr groupInfo,
		int messageType,
		int groupId,
		int channelStyle,
		int channelId,
		int machineId,
		long totalDataCount,
		int dataCountPerChannel,
		int bufferCount,
		int blockIndex,
		long sampleData)
	{
		var evt = new SdkSampleDataEvent(
			sampleTime,
			groupIdSize,
			groupInfo,
			messageType,
			groupId,
			channelStyle,
			channelId,
			machineId,
			totalDataCount,
			dataCountPerChannel,
			bufferCount,
			blockIndex,
			sampleData);

		ProcessSampleData(evt);
	}

	private void ProcessSampleData(SdkSampleDataEvent evt)
	{
		try
		{
			var context = InitEventInfo(evt);
			if (context.StartPos + context.PerChannelCount > _callbackPos)
			{
				_callbackPos = context.StartPos + context.PerChannelCount;
			}

			DealSampleData(evt, context);
		}
		catch
		{
			if (evt.SampleData != 0)
			{
				_sdkBridge.DA_ReleaseBuffer(evt.SampleData);
			}
		}
	}

	private SampleCallbackContext InitEventInfo(SdkSampleDataEvent evt)
	{
		return new SampleCallbackContext(
			evt.MessageType,
			evt.GroupId,
			evt.ChannelStyle,
			evt.ChannelId,
			evt.MachineId,
			evt.TotalDataCount,
			evt.DataCountPerChannel,
			evt.BufferCount,
			evt.BlockIndex,
			evt.SampleTime,
			evt.SampleData);
	}

	private void DealSampleData(SdkSampleDataEvent evt, SampleCallbackContext context)
	{
		if (context.TotalBytesCount <= 0 || context.SampleDataPtr == 0)
		{
			return;
		}

		var rawBytes = new byte[context.TotalBytesCount];
		try
		{
			Marshal.Copy((IntPtr)context.SampleDataPtr, rawBytes, 0, rawBytes.Length);
		}
		finally
		{
			_sdkBridge.DA_ReleaseBuffer(context.SampleDataPtr);
		}

		var block = _blockPool.Rent(rawBytes.Length);
		block.CopyFrom(rawBytes);

		var sequence = Interlocked.Increment(ref _sequence);
		block.Header = new DataBlockHeader(
			Guid.Empty,
			_descriptor.SourceId,
			StreamKind.Raw,
			sequence,
			context.StartPos,
			context.PerChannelCount,
			_descriptor.ChannelCount,
			_descriptor.SampleType,
			context.SampleTime,
			TimestampNs.Now());

		var callback = BlockArrived;
		if (callback is null)
		{
			block.Release();
			return;
		}

		callback.Invoke(block);
	}

	private void FillPayload(Span<byte> buffer, SampleType sampleType, long sampleStartPos)
	{
		switch (sampleType)
		{
			case SampleType.Int16:
			{
				for (var i = 0; i < buffer.Length; i += sizeof(short))
				{
					var value = (short)_random.Next(short.MinValue, short.MaxValue);
					BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(i, sizeof(short)), value);
				}

				break;
			}
			case SampleType.Int32:
			{
				for (var i = 0; i < buffer.Length; i += sizeof(int))
				{
					var value = _random.Next(int.MinValue, int.MaxValue);
					BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(i, sizeof(int)), value);
				}

				break;
			}
			case SampleType.Float32:
			{
				FillFloatSineWave(buffer, sampleStartPos);
				break;
			}
			case SampleType.Float64:
			{
				FillDoubleSineWave(buffer, sampleStartPos);
				break;
			}
			default:
				throw new ArgumentOutOfRangeException(nameof(sampleType), sampleType, "不支持的采样类型。");
		}
	}

	private void FillFloatSineWave(Span<byte> buffer, long sampleStartPos)
	{
		var channelCount = Math.Max(1, _descriptor.ChannelCount);
		var samplesPerChannel = buffer.Length / sizeof(float) / channelCount;
		if (samplesPerChannel <= 0)
		{
			return;
		}

		var sampleRate = Math.Max(1d, _descriptor.SampleRateHz);
		var baseFrequency = Math.Max(1d, sampleRate / 80d);
		var angularStep = 2d * Math.PI * baseFrequency / sampleRate;
		var channelPhaseOffset = Math.PI / Math.Max(4d, channelCount);

		for (var sampleIndex = 0; sampleIndex < samplesPerChannel; sampleIndex++)
		{
			var timePhase = (sampleStartPos + sampleIndex) * angularStep;

			for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
			{
				var amplitude = 0.9d - channelIndex * 0.02d;
				var harmonic = 0.08d * Math.Sin(timePhase * 3d + channelIndex * 0.35d);
				var value = (float)(amplitude * Math.Sin(timePhase + channelIndex * channelPhaseOffset) + harmonic);
				var rawIndex = sampleIndex * channelCount + channelIndex;
				var offset = rawIndex * sizeof(float);
				BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(float)), value);
			}
		}
	}

	private void FillDoubleSineWave(Span<byte> buffer, long sampleStartPos)
	{
		var channelCount = Math.Max(1, _descriptor.ChannelCount);
		var samplesPerChannel = buffer.Length / sizeof(double) / channelCount;
		if (samplesPerChannel <= 0)
		{
			return;
		}

		var sampleRate = Math.Max(1d, _descriptor.SampleRateHz);
		var baseFrequency = Math.Max(1d, sampleRate / 80d);
		var angularStep = 2d * Math.PI * baseFrequency / sampleRate;
		var channelPhaseOffset = Math.PI / Math.Max(4d, channelCount);

		for (var sampleIndex = 0; sampleIndex < samplesPerChannel; sampleIndex++)
		{
			var timePhase = (sampleStartPos + sampleIndex) * angularStep;

			for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
			{
				var amplitude = 0.9d - channelIndex * 0.02d;
				var harmonic = 0.08d * Math.Sin(timePhase * 3d + channelIndex * 0.35d);
				var value = amplitude * Math.Sin(timePhase + channelIndex * channelPhaseOffset) + harmonic;
				var rawIndex = sampleIndex * channelCount + channelIndex;
				var offset = rawIndex * sizeof(double);
				BitConverter.TryWriteBytes(buffer.Slice(offset, sizeof(double)), value);
			}
		}
	}

	private static int GetBytesPerSample(SampleType sampleType)
	{
		return sampleType switch
		{
			SampleType.Int16 => sizeof(short),
			SampleType.Int32 => sizeof(int),
			SampleType.Float32 => sizeof(float),
			SampleType.Float64 => sizeof(double),
			_ => throw new ArgumentOutOfRangeException(nameof(sampleType), sampleType, "不支持的采样类型。")
		};
	}
}
