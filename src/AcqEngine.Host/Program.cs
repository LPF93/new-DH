using System.Text.Json;
using AcqEngine.Core;
using AcqEngine.DeviceSdk;
using AcqEngine.Diagnostics;
using AcqEngine.Processing;
using AcqEngine.Storage.Abstractions;
using AcqEngine.Storage.Hdf5;
using AcqEngine.Storage.Tdms;
using AcqShell.Contracts;

var options = EngineOptions.Load("appsettings.json");
var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
	args.Cancel = true;
	cancellationTokenSource.Cancel();
};

var blockPool = new BlockPool();
var diagnostics = new RuntimeDiagnostics();
var frameBus = new FrameBus();
var cache = new RecentDataCache(TimeSpan.FromSeconds(options.Ui.PreviewSeconds));
var sessionManager = new SessionManager();

var storageFormat = ParseStorageFormat(options.Storage.PrimaryFormat);
var sampleType = ParseSampleType(options.Acquisition.SampleType);
var sources = BuildSources(options, sampleType, blockPool).ToArray();
if (sources.Length == 0)
{
	Console.WriteLine("未配置采集源，程序退出。");
	return;
}

var session = sessionManager.Start(new SessionContext
{
	SessionId = Guid.NewGuid(),
	TaskName = options.Session.TaskName,
	OperatorName = options.Session.Operator,
	BatchNo = options.Session.BatchNo,
	StartTime = DateTimeOffset.UtcNow,
	StorageFormat = storageFormat,
	FileNamingTemplate = options.Naming.Template,
	Sources = sources.Select(static x => x.Descriptor).ToArray(),
	WriteRaw = options.Storage.WriteRaw,
	WriteProcessed = options.Storage.WriteProcessed,
	AutoIncrementNo = 1
});

var namingPolicy = new NamingTemplateFileNamingPolicy(options.Storage.BasePath, options.Naming.Template);
var rawWriter = CreateWriter(storageFormat, namingPolicy);
var processedWriter = options.Storage.WriteProcessed ? CreateWriter(storageFormat, namingPolicy) : null;

await using var storage = new StorageOrchestrator(
	rawWriter,
	processedWriter,
	new SegmentPolicy(options.Storage.SegmentSeconds, options.Storage.SegmentMaxBytes));
await storage.StartAsync(session, cancellationTokenSource.Token);

await using var ingestDispatcher = new IngestDispatcher(frameBus);
await using var processingPipeline = new ProcessingPipeline(
	CreateProcessors(options, blockPool),
	storage.TryEnqueueProcessed);
processingPipeline.Start();

frameBus.Subscribe("raw-writer", block =>
{
	if (!storage.TryEnqueueRaw(block))
	{
		diagnostics.MarkRawEnqueueFailure();
	}
});
frameBus.Subscribe("processing", block =>
{
	if (!options.Processing.Enabled)
	{
		return;
	}

	if (!processingPipeline.TryEnqueue(block))
	{
		diagnostics.MarkProcessEnqueueFailure();
	}
});
frameBus.Subscribe("cache", cache.Put);
frameBus.Subscribe("diagnostics", diagnostics.OnBlockPublished);

foreach (var source in sources)
{
	ingestDispatcher.RegisterSource(source);
	source.Start(cancellationTokenSource.Token);
}

Console.WriteLine($"会话已启动：{session.SessionId}");
Console.WriteLine("按 Ctrl+C 可停止采集。");

var monitorTask = MonitorLoopAsync(diagnostics, cache, sources[0].SourceId, cancellationTokenSource.Token);
try
{
	await Task.Delay(Timeout.InfiniteTimeSpan, cancellationTokenSource.Token);
}
catch (OperationCanceledException)
{
}

foreach (var source in sources)
{
	source.Stop();
	source.Dispose();
}

await processingPipeline.StopAsync(CancellationToken.None);
await storage.StopAsync(CancellationToken.None);

try
{
	await monitorTask;
}
catch (OperationCanceledException)
{
}

var stoppedSession = sessionManager.Stop();
var snapshot = diagnostics.Snapshot();
var manifestPath = WriteSessionManifest(
	options.Storage.BasePath,
	stoppedSession,
	snapshot,
	storage.GetCompletedSegments());

Console.WriteLine($"会话已停止：{stoppedSession.SessionId}");
Console.WriteLine($"清单文件已写入：{manifestPath}");

return;

static IEnumerable<IDescriptorAcquisitionSource> BuildSources(EngineOptions options, SampleType sampleType, BlockPool blockPool)
{
	if (IsNativeSdkMode(options.Sdk.Mode))
	{
		var descriptor = new SourceDescriptor
		{
			SourceId = 1,
			DeviceName = "SDK回调采集源",
			ChannelCount = Math.Max(1, options.Acquisition.DefaultChannelsPerSource),
			SampleRateHz = Math.Max(1, options.Acquisition.SampleRateHz),
			SampleType = SampleType.Float32
		};

		yield return new DhSdkAcquisitionSource(
			descriptor,
			blockPool,
			new DhSdkOptions
			{
				SdkDirectory = options.Sdk.SdkDirectory,
				ConfigDirectory = options.Sdk.ConfigDirectory,
				DataCountPerCallback = options.Sdk.DataCountPerCallback,
				SingleMachineMode = options.Sdk.SingleMachineMode,
				AutoConnectDevices = options.Sdk.AutoConnectDevices
			});

		yield break;
	}

	var sourceCount = Math.Max(1, options.Acquisition.ExpectedSources);
	var channelsPerSource = Math.Max(1, options.Acquisition.DefaultChannelsPerSource);
	var sampleRate = Math.Max(1, options.Acquisition.SampleRateHz);
	var sdkBridge = new MockSdkBridge();

	for (var i = 1; i <= sourceCount; i++)
	{
		var descriptor = new SourceDescriptor
		{
			SourceId = i,
			DeviceName = $"采集源_{i:D4}",
			ChannelCount = channelsPerSource,
			SampleRateHz = sampleRate,
			SampleType = sampleType
		};

		yield return new DemoCallbackAcquisitionSource(descriptor, blockPool, sdkBridge);
	}
}

static bool IsNativeSdkMode(string mode)
{
	return mode.Equals("SDK", StringComparison.OrdinalIgnoreCase)
		|| mode.Equals("Native", StringComparison.OrdinalIgnoreCase)
		|| mode.Contains("真实", StringComparison.OrdinalIgnoreCase);
}

static StorageFormat ParseStorageFormat(string value)
{
	if (value.Equals("HDF5", StringComparison.OrdinalIgnoreCase)
		|| value.Contains("HDF", StringComparison.OrdinalIgnoreCase)
		|| value.Contains("层次", StringComparison.OrdinalIgnoreCase))
	{
		return StorageFormat.Hdf5;
	}

	return StorageFormat.Tdms;
}

static SampleType ParseSampleType(string value)
{
	return value.ToUpperInvariant() switch
	{
		"INT16" => SampleType.Int16,
		"整型16" => SampleType.Int16,
		"INT32" => SampleType.Int32,
		"整型32" => SampleType.Int32,
		"FLOAT32" => SampleType.Float32,
		"单精度" => SampleType.Float32,
		"FLOAT64" => SampleType.Float64,
		"双精度" => SampleType.Float64,
		_ => SampleType.Int16
	};
}

static IContainerWriter CreateWriter(StorageFormat storageFormat, IFileNamingPolicy namingPolicy)
{
	return storageFormat switch
	{
		StorageFormat.Hdf5 => new Hdf5Writer(namingPolicy),
		_ => new TdmsWriter(namingPolicy)
	};
}

static IReadOnlyList<IProcessor> CreateProcessors(EngineOptions options, BlockPool blockPool)
{
	var enabledProcessors = new HashSet<string>(options.Processing.Processors, StringComparer.OrdinalIgnoreCase);
	var processors = new List<IProcessor>();

	if (enabledProcessors.Count == 0 || enabledProcessors.Contains("PassThrough"))
	{
		processors.Add(new PassThroughProcessor(blockPool));
	}

	if (enabledProcessors.Contains("透传"))
	{
		processors.Add(new PassThroughProcessor(blockPool));
	}

	if (enabledProcessors.Contains("BasicStats"))
	{
		processors.Add(new BasicStatsProcessor(blockPool));
	}

	if (enabledProcessors.Contains("基础统计"))
	{
		processors.Add(new BasicStatsProcessor(blockPool));
	}

	return processors;
}

static async Task MonitorLoopAsync(
	RuntimeDiagnostics diagnostics,
	IRecentDataCache cache,
	int previewSourceId,
	CancellationToken ct)
{
	using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
	while (await timer.WaitForNextTickAsync(ct))
	{
		var snapshot = diagnostics.Snapshot();
		var preview = cache.GetPreview(previewSourceId, TimeSpan.FromSeconds(2), 64);
		Console.WriteLine(
			$"块数={snapshot.TotalBlocks}, 字节={snapshot.TotalBytes}, 原始入队失败={snapshot.RawEnqueueFailures}, " +
			$"处理入队失败={snapshot.ProcessEnqueueFailures}, 预览点数={preview.Samples.Count}");
	}
}

static string WriteSessionManifest(
	string basePath,
	SessionContext session,
	DiagnosticsSnapshot snapshot,
	IReadOnlyList<WrittenSegmentInfo> segments)
{
	var sanitizedTaskName = SanitizePathSegment(session.TaskName);
	var sessionDir = Path.Combine(basePath, session.StartTime.ToString("yyyyMMdd"), sanitizedTaskName, session.SessionId.ToString("N"));
	Directory.CreateDirectory(sessionDir);

	var manifest = new SessionManifestDto(
		session.SessionId,
		session.TaskName,
		session.OperatorName,
		session.BatchNo,
		session.StartTime,
		session.EndTime,
		session.StorageFormat.ToString(),
		session.WriteRaw,
		session.WriteProcessed,
		session.Sources
			.Select(static source => new SessionManifestSourceDto(
				source.SourceId,
				source.DeviceName,
				source.ChannelCount,
				source.SampleRateHz,
				source.SampleType.ToString()))
			.ToArray(),
		segments
			.Select(segment => new SessionSegmentDto(
				segment.ContainerFormat,
				segment.StreamKind.ToString(),
				segment.SegmentNo,
				Path.GetRelativePath(sessionDir, segment.FilePath),
				segment.StartedAt,
				segment.EndedAt,
				segment.BlockCount,
				segment.PayloadBytes,
				segment.FileBytes))
			.ToArray(),
		new SessionMetricsDto(
			snapshot.TotalBlocks,
			snapshot.TotalBytes,
			snapshot.RawEnqueueFailures,
			snapshot.ProcessEnqueueFailures,
			new Dictionary<int, long>(snapshot.BlocksBySource)));

	var path = Path.Combine(sessionDir, "session.manifest.json");
	var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
	File.WriteAllText(path, json);
	return path;
}

static string SanitizePathSegment(string value)
{
	if (string.IsNullOrWhiteSpace(value))
	{
		return "任务";
	}

	var invalid = Path.GetInvalidPathChars();
	var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
	return new string(chars);
}

internal sealed class EngineOptions
{
	public SessionOptions Session { get; set; } = new();
	public AcquisitionOptions Acquisition { get; set; } = new();
	public SdkOptions Sdk { get; set; } = new();
	public StorageOptions Storage { get; set; } = new();
	public NamingOptions Naming { get; set; } = new();
	public UiOptions Ui { get; set; } = new();
	public ProcessingOptions Processing { get; set; } = new();

	public static EngineOptions Load(string path)
	{
		if (!File.Exists(path))
		{
			return new EngineOptions();
		}

		try
		{
			var json = File.ReadAllText(path);
			return JsonSerializer.Deserialize<EngineOptions>(json, new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			}) ?? new EngineOptions();
		}
		catch
		{
			return new EngineOptions();
		}
	}
}

internal sealed class SessionOptions
{
	public string TaskName { get; set; } = "轴承测试";
	public string Operator { get; set; } = "操作员";
	public string BatchNo { get; set; } = "批次-001";
}

internal sealed class AcquisitionOptions
{
	public int ExpectedSources { get; set; } = 2;
	public int DefaultChannelsPerSource { get; set; } = 16;
	public double SampleRateHz { get; set; } = 10000;
	public string SampleType { get; set; } = "整型16";
}

internal sealed class SdkOptions
{
	public string Mode { get; set; } = "模拟";
	public string SdkDirectory { get; set; } = "";
	public string ConfigDirectory { get; set; } = "";
	public int DataCountPerCallback { get; set; } = 128;
	public bool SingleMachineMode { get; set; } = true;
	public bool AutoConnectDevices { get; set; } = true;
}

internal sealed class StorageOptions
{
    public string PrimaryFormat { get; set; } = "TDMS";
	public int SegmentSeconds { get; set; } = 1;
	public long SegmentMaxBytes { get; set; } = 512L * 1024 * 1024;
	public bool WriteRaw { get; set; } = true;
	public bool WriteProcessed { get; set; } = true;
	public string BasePath { get; set; } = "D:\\AcqData";
}

internal sealed class NamingOptions
{
	public string Template { get; set; } = "{TaskName}_{Stream}_{Date:yyyyMMdd}_{StartTime:HHmmss}_{AutoInc:0000}_seg{SegmentNo:0000}";
}

internal sealed class UiOptions
{
	public int PreviewSeconds { get; set; } = 30;
}

internal sealed class ProcessingOptions
{
	public bool Enabled { get; set; } = true;
	public string[] Processors { get; set; } = ["透传", "基础统计"];
}
