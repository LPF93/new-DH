using System.Text.RegularExpressions;
using System.Threading.Channels;
using AcqEngine.Core;

namespace AcqEngine.Storage.Abstractions;

public interface IContainerWriter : IAsyncDisposable
{
	IReadOnlyList<WrittenSegmentInfo> CompletedSegments { get; }
	ValueTask OpenSessionAsync(SessionContext session, CancellationToken ct);
	ValueTask OpenSegmentAsync(StreamKind streamKind, int segmentNo, CancellationToken ct);
	ValueTask WriteBlockAsync(DataBlock block, CancellationToken ct);
	ValueTask FlushAsync(CancellationToken ct);
	ValueTask CloseSegmentAsync(CancellationToken ct);
	ValueTask CloseSessionAsync(CancellationToken ct);
}

public interface IFileNamingPolicy
{
	string BuildSessionDirectory(SessionContext session);
	string BuildSegmentFileName(SessionContext session, StreamKind stream, int segmentNo);
}

public sealed record SegmentPolicy(int SegmentSeconds = 1, long SegmentMaxBytes = 512L * 1024 * 1024)
{
	public TimeSpan SegmentDuration => TimeSpan.FromSeconds(Math.Max(1, SegmentSeconds));
}

public sealed record StorageQueueOptions(int Capacity = 512);

public sealed class StorageOrchestrator : IAsyncDisposable
{
	private readonly IContainerWriter _rawWriter;
	private readonly IContainerWriter? _processedWriter;
	private readonly SegmentPolicy _segmentPolicy;
	private readonly StorageQueueOptions _queueOptions;

	private StreamWorker? _rawWorker;
	private StreamWorker? _processedWorker;
	private int _started;

	public StorageOrchestrator(
		IContainerWriter rawWriter,
		IContainerWriter? processedWriter,
		SegmentPolicy segmentPolicy,
		StorageQueueOptions? queueOptions = null)
	{
		_rawWriter = rawWriter;
		_processedWriter = processedWriter;
		_segmentPolicy = segmentPolicy;
		_queueOptions = queueOptions ?? new StorageQueueOptions();
	}

	public async Task StartAsync(SessionContext session, CancellationToken ct)
	{
		if (Interlocked.Exchange(ref _started, 1) == 1)
		{
			throw new InvalidOperationException("存储编排器已经启动，不能重复启动。");
		}

		_rawWorker = await StreamWorker.StartAsync(_rawWriter, session, StreamKind.Raw, _segmentPolicy, _queueOptions, ct);
		if (_processedWriter is not null && session.WriteProcessed)
		{
			_processedWorker = await StreamWorker.StartAsync(_processedWriter, session, StreamKind.Processed, _segmentPolicy, _queueOptions, ct);
		}
	}

	public bool TryEnqueueRaw(DataBlock block)
	{
		return _rawWorker?.TryEnqueue(block) == true;
	}

	public bool TryEnqueueProcessed(DataBlock block)
	{
		return _processedWorker?.TryEnqueue(block) == true;
	}

	public int RawPendingBlocks => _rawWorker?.PendingBlocks ?? 0;

	public int ProcessedPendingBlocks => _processedWorker?.PendingBlocks ?? 0;

	public async Task StopAsync(CancellationToken ct)
	{
		if (Interlocked.Exchange(ref _started, 0) == 0)
		{
			return;
		}

		if (_rawWorker is not null)
		{
			_rawWorker.Complete();
		}

		if (_processedWorker is not null)
		{
			_processedWorker.Complete();
		}

		var tasks = new List<Task>();
		if (_rawWorker is not null)
		{
			tasks.Add(_rawWorker.WaitForCompletionAsync(ct));
		}

		if (_processedWorker is not null)
		{
			tasks.Add(_processedWorker.WaitForCompletionAsync(ct));
		}

		if (tasks.Count > 0)
		{
			await Task.WhenAll(tasks);
		}
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync(CancellationToken.None);
	}

	public IReadOnlyList<WrittenSegmentInfo> GetCompletedSegments()
	{
		var segments = new List<WrittenSegmentInfo>();

		segments.AddRange(_rawWriter.CompletedSegments);
		if (_processedWriter is not null)
		{
			segments.AddRange(_processedWriter.CompletedSegments);
		}

		return segments
			.OrderBy(static item => item.StartedAt)
			.ThenBy(static item => item.StreamKind)
			.ThenBy(static item => item.SegmentNo)
			.ToArray();
	}

	private sealed class StreamWorker
	{
		private readonly IContainerWriter _writer;
		private readonly StreamKind _streamKind;
		private int _pendingBlocks;
		private int _segmentNo = 1;

		private StreamWorker(IContainerWriter writer, StreamKind streamKind, SegmentPolicy policy, StorageQueueOptions queueOptions)
		{
			_writer = writer;
			_streamKind = streamKind;
			_ = policy;
			Queue = Channel.CreateBounded<DataBlock>(new BoundedChannelOptions(Math.Max(1, queueOptions.Capacity))
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.Wait,
				AllowSynchronousContinuations = false
			});
		}

		public Channel<DataBlock> Queue { get; }

		public Task LoopTask { get; private set; } = Task.CompletedTask;

		public int PendingBlocks => Volatile.Read(ref _pendingBlocks);

		public bool TryEnqueue(DataBlock block)
		{
			block.AddRef();
			Interlocked.Increment(ref _pendingBlocks);
			if (!Queue.Writer.TryWrite(block))
			{
				Interlocked.Decrement(ref _pendingBlocks);
				block.Release();
				return false;
			}

			return true;
		}

		public static async Task<StreamWorker> StartAsync(
			IContainerWriter writer,
			SessionContext session,
			StreamKind streamKind,
			SegmentPolicy policy,
			StorageQueueOptions queueOptions,
			CancellationToken ct)
		{
			var worker = new StreamWorker(writer, streamKind, policy, queueOptions);
			await writer.OpenSessionAsync(session, ct);
			await writer.OpenSegmentAsync(streamKind, worker._segmentNo, ct);
			worker.LoopTask = worker.RunLoopAsync();
			return worker;
		}

		public void Complete()
		{
			Queue.Writer.TryComplete();
		}

		public async Task WaitForCompletionAsync(CancellationToken ct)
		{
			await LoopTask.WaitAsync(ct);
		}

		private async Task RunLoopAsync()
		{
			await foreach (var block in Queue.Reader.ReadAllAsync())
			{
				try
				{
					await _writer.WriteBlockAsync(block, CancellationToken.None);
				}
				finally
				{
					block.Release();
					Interlocked.Decrement(ref _pendingBlocks);
				}
			}

			await _writer.FlushAsync(CancellationToken.None);
			await _writer.CloseSegmentAsync(CancellationToken.None);
			await _writer.CloseSessionAsync(CancellationToken.None);
		}
	}
}

public sealed class NamingTemplateFileNamingPolicy : IFileNamingPolicy
{
	private static readonly Regex TokenRegex = new("\\{(?<name>[A-Za-z]+)(:(?<format>[^}]+))?\\}", RegexOptions.Compiled);
	private readonly string _basePath;
	private readonly string _template;

	public NamingTemplateFileNamingPolicy(string basePath, string template)
	{
		_basePath = string.IsNullOrWhiteSpace(basePath)
			? throw new ArgumentException("必须提供数据根目录。", nameof(basePath))
			: basePath;
		_template = string.IsNullOrWhiteSpace(template)
			? "{TaskName}_{Stream}_{Date:yyyyMMdd}_{StartTime:HHmmss}_{AutoInc:0000}_seg{SegmentNo:0000}"
			: template;
	}

	public string BuildSessionDirectory(SessionContext session)
	{
		return _basePath;
	}

	public string BuildSegmentFileName(SessionContext session, StreamKind stream, int segmentNo)
	{
		var fileName = session.FileNameMode switch
		{
			StorageFileNameMode.Custom => ResolveCustomFileName(session),
			StorageFileNameMode.StorageTime => session.StartTime.ToLocalTime().ToString(
				string.IsNullOrWhiteSpace(session.StorageTimeFormat) ? "yyyyMMdd_HHmmss" : session.StorageTimeFormat),
			_ => TokenRegex.Replace(_template, m => ResolveToken(m, session, stream, segmentNo))
		};

		return SanitizeFileName(fileName);
	}

	private static string ResolveCustomFileName(SessionContext session)
	{
		if (!string.IsNullOrWhiteSpace(session.CustomFileName))
		{
			return session.CustomFileName;
		}

		return string.IsNullOrWhiteSpace(session.TaskName) ? "session" : session.TaskName;
	}

	private static string ResolveToken(Match match, SessionContext session, StreamKind stream, int segmentNo)
	{
		var name = match.Groups["name"].Value;
		var format = match.Groups["format"].Success ? match.Groups["format"].Value : string.Empty;

		return name switch
		{
			"TaskName" => session.TaskName,
			"Date" => session.StartTime.ToString(string.IsNullOrWhiteSpace(format) ? "yyyyMMdd" : format),
			"StartTime" => session.StartTime.ToString(string.IsNullOrWhiteSpace(format) ? "HHmmss" : format),
			"Stream" => ResolveStreamName(stream),
			"AutoInc" => session.AutoIncrementNo.ToString(string.IsNullOrWhiteSpace(format) ? "0000" : format),
			"SegmentNo" => segmentNo.ToString(string.IsNullOrWhiteSpace(format) ? "0000" : format),
			"DeviceGroup" => "全部采集源",
			"Operator" => session.OperatorName,
			"BatchNo" => session.BatchNo,
			_ => match.Value
		};
	}

	private static string ResolveStreamName(StreamKind stream)
	{
		return stream switch
		{
			StreamKind.Raw => "原始",
			StreamKind.Processed => "处理后",
			StreamKind.Event => "事件",
			StreamKind.State => "状态",
			_ => stream.ToString()
		};
	}

	private static string SanitizeFileName(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "session";
		}

		var invalid = Path.GetInvalidFileNameChars();
		var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
		var sanitized = new string(chars).Trim().Trim('.');
		return string.IsNullOrWhiteSpace(sanitized) ? "session" : sanitized;
	}
}
