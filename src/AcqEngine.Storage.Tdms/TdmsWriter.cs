using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AcqEngine.Core;
using AcqEngine.Storage.Abstractions;

namespace AcqEngine.Storage.Tdms;

public sealed class TdmsWriter : IContainerWriter
{
    private readonly IFileNamingPolicy _namingPolicy;
    private readonly string _extension;
    private readonly List<WrittenSegmentInfo> _completedSegments = new();
    private readonly Dictionary<int, SourceDescriptor> _sourceDescriptors = new();
    private readonly Dictionary<int, TdmsSourceState> _sourceStates = new();

    private SessionContext? _session;
    private IntPtr _fileHandle;
    private StreamKind _currentStreamKind;
    private int _currentSegmentNo;
    private string? _currentSegmentPath;
    private DateTimeOffset _currentSegmentStartedAt;
    private long _currentBlockCount;
    private long _currentPayloadBytes;

    public TdmsWriter(IFileNamingPolicy namingPolicy, string extension = ".tdms")
    {
        _namingPolicy = namingPolicy;
        _extension = extension;
    }

    public IReadOnlyList<WrittenSegmentInfo> CompletedSegments => _completedSegments;

    public ValueTask OpenSessionAsync(SessionContext session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _session = session;
        _completedSegments.Clear();
        _sourceDescriptors.Clear();
        _sourceStates.Clear();

        foreach (var source in session.Sources)
        {
            _sourceDescriptors[source.SourceId] = source;
        }

        Directory.CreateDirectory(_namingPolicy.BuildSessionDirectory(session));
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenSegmentAsync(StreamKind streamKind, int segmentNo, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_session is null)
        {
            throw new InvalidOperationException("尚未打开会话，无法创建 TDMS 段文件。");
        }

        TdmsNative.EnsureAvailable();

        CloseCurrentSegment();

        var sessionDir = _namingPolicy.BuildSessionDirectory(_session);
        Directory.CreateDirectory(sessionDir);

        var segmentFile = _namingPolicy.BuildSegmentFileName(_session, streamKind, segmentNo);
        if (!segmentFile.EndsWith(_extension, StringComparison.OrdinalIgnoreCase))
        {
            segmentFile += _extension;
        }

        var segmentPath = ResolveUniqueFilePath(sessionDir, segmentFile);

        var fileName = SanitizeAscii(Path.GetFileNameWithoutExtension(segmentPath));
        var fileHandle = IntPtr.Zero;
        ThrowIfError(
            TdmsNative.DDC_CreateFile(segmentPath, "TDMS", fileName, string.Empty, fileName, "AcqEngine", ref fileHandle),
            "DDC_CreateFile");

        _fileHandle = fileHandle;
        _currentStreamKind = streamKind;
        _currentSegmentNo = segmentNo;
        _currentSegmentPath = segmentPath;
        _currentSegmentStartedAt = DateTimeOffset.UtcNow;
        _currentBlockCount = 0;
        _currentPayloadBytes = 0;

        TryCreateFileProperty("session_id", _session.SessionId.ToString("N"));
        TryCreateFileProperty("task_name", _session.TaskName);
        TryCreateFileProperty("operator", _session.OperatorName);
        TryCreateFileProperty("batch_no", _session.BatchNo);
        TryCreateFileProperty("stream_kind", streamKind.ToString());

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteBlockAsync(DataBlock block, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_fileHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("尚未打开 TDMS 段文件。");
        }

        var sourceState = GetOrCreateSourceState(block.Header.SourceId);
        AppendBlockSamples(block, sourceState);

        _currentBlockCount++;
        _currentPayloadBytes += block.PayloadLength;
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_fileHandle != IntPtr.Zero)
        {
            ThrowIfError(TdmsNative.DDC_SaveFile(_fileHandle), "DDC_SaveFile");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CloseSegmentAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CloseCurrentSegment();
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseSessionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CloseCurrentSegment();
        _session = null;
        _sourceDescriptors.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        CloseCurrentSegment();
        return ValueTask.CompletedTask;
    }

    private TdmsSourceState GetOrCreateSourceState(int sourceId)
    {
        if (_sourceStates.TryGetValue(sourceId, out var state))
        {
            return state;
        }

        if (_fileHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("TDMS 文件句柄未打开。");
        }

        _sourceDescriptors.TryGetValue(sourceId, out var descriptor);

        var groupHandle = IntPtr.Zero;
        var groupName = $"Source_{sourceId:D4}";
        var groupDescription = SanitizeAscii(descriptor?.DeviceName ?? $"Source {sourceId}");
        ThrowIfError(
            TdmsNative.DDC_AddChannelGroup(_fileHandle, groupName, groupDescription, ref groupHandle),
            "DDC_AddChannelGroup");

        state = new TdmsSourceState(sourceId, descriptor, groupHandle);
        _sourceStates[sourceId] = state;
        return state;
    }

    private void AppendBlockSamples(DataBlock block, TdmsSourceState sourceState)
    {
        var channelCount = ResolveChannelCount(block);
        var sampleCountPerChannel = ResolveSamplesPerChannel(block, channelCount);
        var sampleIntervalSeconds = ResolveSampleIntervalSeconds(sourceState.Descriptor);
        var payload = block.Payload.Span;

        switch (block.Header.SampleType)
        {
            case SampleType.Int16:
                AppendInt16Channels(payload, sourceState, channelCount, sampleCountPerChannel, sampleIntervalSeconds);
                break;
            case SampleType.Int32:
                AppendInt32Channels(payload, sourceState, channelCount, sampleCountPerChannel, sampleIntervalSeconds);
                break;
            case SampleType.Float32:
                AppendFloatChannels(payload, sourceState, channelCount, sampleCountPerChannel, sampleIntervalSeconds);
                break;
            case SampleType.Float64:
                AppendDoubleChannels(payload, sourceState, channelCount, sampleCountPerChannel, sampleIntervalSeconds);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(block.Header.SampleType), block.Header.SampleType, "不支持的样本类型。");
        }
    }

    private void AppendInt16Channels(
        ReadOnlySpan<byte> payload,
        TdmsSourceState sourceState,
        int channelCount,
        int sampleCountPerChannel,
        double sampleIntervalSeconds)
    {
        var totalValueCount = payload.Length / sizeof(short);
        for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            var values = new short[sampleCountPerChannel];
            for (var sampleIndex = 0; sampleIndex < sampleCountPerChannel; sampleIndex++)
            {
                var rawIndex = sampleIndex * channelCount + channelIndex;
                if (rawIndex >= totalValueCount)
                {
                    break;
                }

                var offset = rawIndex * sizeof(short);
                values[sampleIndex] = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(offset, sizeof(short)));
            }

            var channelState = GetOrCreateChannelState(sourceState, channelIndex + 1, SampleType.Int16, sampleIntervalSeconds);
            ThrowIfError(
                TdmsNative.DDC_AppendDataValuesInt16(channelState.ChannelHandle, values, (uint)values.Length),
                "DDC_AppendDataValuesInt16");
        }
    }

    private void AppendInt32Channels(
        ReadOnlySpan<byte> payload,
        TdmsSourceState sourceState,
        int channelCount,
        int sampleCountPerChannel,
        double sampleIntervalSeconds)
    {
        var totalValueCount = payload.Length / sizeof(int);
        for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            var values = new int[sampleCountPerChannel];
            for (var sampleIndex = 0; sampleIndex < sampleCountPerChannel; sampleIndex++)
            {
                var rawIndex = sampleIndex * channelCount + channelIndex;
                if (rawIndex >= totalValueCount)
                {
                    break;
                }

                var offset = rawIndex * sizeof(int);
                values[sampleIndex] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, sizeof(int)));
            }

            var channelState = GetOrCreateChannelState(sourceState, channelIndex + 1, SampleType.Int32, sampleIntervalSeconds);
            ThrowIfError(
                TdmsNative.DDC_AppendDataValuesInt32(channelState.ChannelHandle, values, (uint)values.Length),
                "DDC_AppendDataValuesInt32");
        }
    }

    private void AppendFloatChannels(
        ReadOnlySpan<byte> payload,
        TdmsSourceState sourceState,
        int channelCount,
        int sampleCountPerChannel,
        double sampleIntervalSeconds)
    {
        var totalValueCount = payload.Length / sizeof(float);
        for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            var values = new float[sampleCountPerChannel];
            for (var sampleIndex = 0; sampleIndex < sampleCountPerChannel; sampleIndex++)
            {
                var rawIndex = sampleIndex * channelCount + channelIndex;
                if (rawIndex >= totalValueCount)
                {
                    break;
                }

                var offset = rawIndex * sizeof(float);
                values[sampleIndex] = BitConverter.ToSingle(payload.Slice(offset, sizeof(float)));
            }

            var channelState = GetOrCreateChannelState(sourceState, channelIndex + 1, SampleType.Float32, sampleIntervalSeconds);
            ThrowIfError(
                TdmsNative.DDC_AppendDataValuesFloat(channelState.ChannelHandle, values, (uint)values.Length),
                "DDC_AppendDataValuesFloat");
        }
    }

    private void AppendDoubleChannels(
        ReadOnlySpan<byte> payload,
        TdmsSourceState sourceState,
        int channelCount,
        int sampleCountPerChannel,
        double sampleIntervalSeconds)
    {
        var totalValueCount = payload.Length / sizeof(double);
        for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            var values = new double[sampleCountPerChannel];
            for (var sampleIndex = 0; sampleIndex < sampleCountPerChannel; sampleIndex++)
            {
                var rawIndex = sampleIndex * channelCount + channelIndex;
                if (rawIndex >= totalValueCount)
                {
                    break;
                }

                var offset = rawIndex * sizeof(double);
                values[sampleIndex] = BitConverter.ToDouble(payload.Slice(offset, sizeof(double)));
            }

            var channelState = GetOrCreateChannelState(sourceState, channelIndex + 1, SampleType.Float64, sampleIntervalSeconds);
            ThrowIfError(
                TdmsNative.DDC_AppendDataValuesDouble(channelState.ChannelHandle, values, (uint)values.Length),
                "DDC_AppendDataValuesDouble");
        }
    }

    private TdmsChannelState GetOrCreateChannelState(
        TdmsSourceState sourceState,
        int channelNo,
        SampleType sampleType,
        double sampleIntervalSeconds)
    {
        if (sourceState.Channels.TryGetValue(channelNo, out var state))
        {
            if (state.SampleType != sampleType)
            {
                throw new InvalidOperationException(
                    $"通道 {channelNo} 的样本类型发生变化，当前为 {sampleType}，已存在为 {state.SampleType}。");
            }

            return state;
        }

        var channelHandle = IntPtr.Zero;
        var channelName = $"CH{channelNo:D2}";
        var channelDescription = SanitizeAscii($"{sourceState.SourceDisplayName} 通道 {channelNo}");
        ThrowIfError(
            TdmsNative.DDC_AddChannel(
                sourceState.GroupHandle,
                TdmsNative.MapSampleType(sampleType),
                channelName,
                channelDescription,
                ResolveUnitString(sampleType),
                ref channelHandle),
            "DDC_AddChannel");

        TryCreateChannelProperty(channelHandle, "wf_xname", "Time");
        TryCreateChannelProperty(channelHandle, "wf_xunit_string", "s");
        TryCreateChannelProperty(channelHandle, "wf_increment", sampleIntervalSeconds);
        TryCreateChannelProperty(channelHandle, "source_id", sourceState.SourceId.ToString(CultureInfo.InvariantCulture));
        TryCreateChannelProperty(channelHandle, "source_name", sourceState.SourceDisplayName);
        TryCreateChannelProperty(channelHandle, "channel_no", channelNo.ToString(CultureInfo.InvariantCulture));
        TryCreateChannelProperty(channelHandle, "sample_type", sampleType.ToString());

        state = new TdmsChannelState(channelNo, channelHandle, sampleType);
        sourceState.Channels[channelNo] = state;
        return state;
    }

    private int ResolveChannelCount(DataBlock block)
    {
        var bytesPerSample = GetBytesPerSample(block.Header.SampleType);
        var totalValueCount = Math.Max(1, block.PayloadLength / bytesPerSample);
        var expectedChannelCount = Math.Max(1, block.Header.ChannelCount);

        if (block.Header.SourceId > 0 &&
            _sourceDescriptors.TryGetValue(block.Header.SourceId, out var descriptor) &&
            descriptor.ChannelCount > 0 &&
            totalValueCount % descriptor.ChannelCount == 0)
        {
            return descriptor.ChannelCount;
        }

        if (totalValueCount % expectedChannelCount == 0)
        {
            return expectedChannelCount;
        }

        return 1;
    }

    private static int ResolveSamplesPerChannel(DataBlock block, int channelCount)
    {
        var bytesPerSample = GetBytesPerSample(block.Header.SampleType);
        var totalValueCount = Math.Max(1, block.PayloadLength / bytesPerSample);
        return Math.Max(1, totalValueCount / Math.Max(1, channelCount));
    }

    private static int GetBytesPerSample(SampleType sampleType)
    {
        return sampleType switch
        {
            SampleType.Int16 => sizeof(short),
            SampleType.Int32 => sizeof(int),
            SampleType.Float32 => sizeof(float),
            SampleType.Float64 => sizeof(double),
            _ => throw new ArgumentOutOfRangeException(nameof(sampleType), sampleType, "不支持的样本类型。")
        };
    }

    private static double ResolveSampleIntervalSeconds(SourceDescriptor? descriptor)
    {
        var sampleRate = descriptor?.SampleRateHz ?? 0d;
        if (sampleRate <= 0d)
        {
            return 1d;
        }

        return 1d / sampleRate;
    }

    private static string ResolveUnitString(SampleType sampleType)
    {
        return sampleType switch
        {
            SampleType.Int16 => "count",
            SampleType.Int32 => "count",
            _ => "V"
        };
    }

    private void CloseCurrentSegment()
    {
        if (_fileHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            ThrowIfError(TdmsNative.DDC_SaveFile(_fileHandle), "DDC_SaveFile");
        }
        finally
        {
            ThrowIfError(TdmsNative.DDC_CloseFile(_fileHandle), "DDC_CloseFile");
            _fileHandle = IntPtr.Zero;
        }

        var segmentPath = _currentSegmentPath;
        var fileBytes = !string.IsNullOrWhiteSpace(segmentPath) && File.Exists(segmentPath)
            ? new FileInfo(segmentPath).Length
            : 0L;

        if (!string.IsNullOrWhiteSpace(segmentPath))
        {
            _completedSegments.Add(new WrittenSegmentInfo(
                "TDMS",
                _currentStreamKind,
                _currentSegmentNo,
                segmentPath,
                _currentSegmentStartedAt,
                DateTimeOffset.UtcNow,
                _currentBlockCount,
                _currentPayloadBytes,
                fileBytes));
        }

        _sourceStates.Clear();
        _currentSegmentPath = null;
        _currentBlockCount = 0;
        _currentPayloadBytes = 0;
    }

    private void TryCreateFileProperty(string propertyName, string value)
    {
        if (_fileHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            TdmsNative.DDC_CreateFilePropertyString(_fileHandle, SanitizeAscii(propertyName), SanitizeAscii(value));
        }
        catch
        {
        }
    }

    private static void TryCreateChannelProperty(IntPtr channelHandle, string propertyName, string value)
    {
        try
        {
            TdmsNative.DDC_CreateChannelPropertyString(channelHandle, SanitizeAscii(propertyName), SanitizeAscii(value));
        }
        catch
        {
        }
    }

    private static void TryCreateChannelProperty(IntPtr channelHandle, string propertyName, double value)
    {
        try
        {
            TdmsNative.DDC_CreateChannelPropertyDouble(channelHandle, SanitizeAscii(propertyName), value);
        }
        catch
        {
        }
    }

    private static void ThrowIfError(int result, string operation)
    {
        if (result == 0)
        {
            return;
        }

        var description = TdmsNative.DescribeError(result);
        var suffix = string.IsNullOrWhiteSpace(description) ? string.Empty : $" {description}";
        throw new IOException($"{operation} 失败: {result}.{suffix}");
    }

    private static string SanitizeAscii(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "session";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(ch <= 0x7F ? ch : '_');
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "session" : result;
    }

    private static string ResolveUniqueFilePath(string directory, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        var index = 1;

        while (File.Exists(candidate) || File.Exists(candidate + "_index"))
        {
            candidate = Path.Combine(directory, $"{baseName}_{index:000}{extension}");
            index++;
        }

        return candidate;
    }

    private sealed class TdmsSourceState
    {
        public TdmsSourceState(int sourceId, SourceDescriptor? descriptor, IntPtr groupHandle)
        {
            SourceId = sourceId;
            Descriptor = descriptor;
            GroupHandle = groupHandle;
        }

        public int SourceId { get; }

        public SourceDescriptor? Descriptor { get; }

        public IntPtr GroupHandle { get; }

        public string SourceDisplayName => Descriptor?.DeviceName ?? $"Source {SourceId}";

        public Dictionary<int, TdmsChannelState> Channels { get; } = new();
    }

    private sealed class TdmsChannelState
    {
        public TdmsChannelState(int channelNo, IntPtr channelHandle, SampleType sampleType)
        {
            ChannelNo = channelNo;
            ChannelHandle = channelHandle;
            SampleType = sampleType;
        }

        public int ChannelNo { get; }

        public IntPtr ChannelHandle { get; }

        public SampleType SampleType { get; }
    }
}
