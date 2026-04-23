using System.Text.Json;
using AcqEngine.Core;
using AcqEngine.Replay;
using AcqEngine.Storage.Abstractions;
using AcqEngine.Storage.Hdf5;
using AcqShell.Contracts;

namespace AcqEngine.Tests;

public sealed class Hdf5StorageTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "AcqEngine.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Hdf5Writer_Should_Record_Completed_Segment_Metadata()
    {
        var policy = new NamingTemplateFileNamingPolicy(_rootPath, "{TaskName}_{Stream}_seg{SegmentNo:0000}");
        var session = CreateSession();
        var pool = new BlockPool();

        await using var writer = new Hdf5Writer(policy);
        await writer.OpenSessionAsync(session, CancellationToken.None);
        await writer.OpenSegmentAsync(StreamKind.Raw, 1, CancellationToken.None);

        var block = CreateBlock(pool, sourceId: 7, sequence: 11, payload: [1, 2, 3, 4]);
        await writer.WriteBlockAsync(block, CancellationToken.None);
        block.Release();

        await writer.CloseSessionAsync(CancellationToken.None);

        var segment = AssertSingleCompletedSegment(writer);
        Assert.Equal("HDF5", ReadSegmentValue<string>(segment, "ContainerFormat"));
        Assert.Equal(StreamKind.Raw, ReadSegmentValue<StreamKind>(segment, "StreamKind"));
        Assert.Equal(1, ReadSegmentValue<int>(segment, "SegmentNo"));
        Assert.Equal(1L, ReadSegmentValue<long>(segment, "BlockCount"));
        Assert.Equal(4L, ReadSegmentValue<long>(segment, "PayloadBytes"));
        Assert.True(ReadSegmentValue<long>(segment, "FileBytes") > ReadSegmentValue<long>(segment, "PayloadBytes"));
        Assert.True(File.Exists(ReadSegmentValue<string>(segment, "FilePath")));
    }

    [Fact]
    public async Task Hdf5ReplayService_Should_Load_Manifest_And_Read_Blocks()
    {
        var policy = new NamingTemplateFileNamingPolicy(_rootPath, "{TaskName}_{Stream}_seg{SegmentNo:0000}");
        var session = CreateSession();
        var pool = new BlockPool();

        await using var writer = new Hdf5Writer(policy);
        await writer.OpenSessionAsync(session, CancellationToken.None);
        await writer.OpenSegmentAsync(StreamKind.Raw, 1, CancellationToken.None);

        var block = CreateBlock(pool, sourceId: 3, sequence: 21, payload: [10, 20, 30, 40, 50, 60]);
        await writer.WriteBlockAsync(block, CancellationToken.None);
        block.Release();
        await writer.CloseSessionAsync(CancellationToken.None);

        var sessionDirectory = policy.BuildSessionDirectory(session);
        var segment = AssertSingleCompletedSegment(writer);
        var manifestPath = Path.Combine(sessionDirectory, "session.manifest.json");
        var manifest = new SessionManifestDto(
            session.SessionId,
            session.TaskName,
            session.OperatorName,
            session.BatchNo,
            session.StartTime,
            session.EndTime,
            "HDF5",
            true,
            false,
            [
                new SessionManifestSourceDto(3, "Source-0003", 1, 1000, SampleType.Int16.ToString())
            ],
            [
                new SessionSegmentDto(
                    "HDF5",
                    StreamKind.Raw.ToString(),
                    1,
                    Path.GetRelativePath(sessionDirectory, ReadSegmentValue<string>(segment, "FilePath")),
                    ReadSegmentValue<DateTimeOffset>(segment, "StartedAt"),
                    ReadSegmentValue<DateTimeOffset>(segment, "EndedAt"),
                    ReadSegmentValue<long>(segment, "BlockCount"),
                    ReadSegmentValue<long>(segment, "PayloadBytes"),
                    ReadSegmentValue<long>(segment, "FileBytes"))
            ],
            new SessionMetricsDto(1, ReadSegmentValue<long>(segment, "PayloadBytes"), 0, 0, new Dictionary<int, long> { [3] = 1 }));

        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            CancellationToken.None);

        var replay = new Hdf5SessionReplayService();
        var loadedManifest = await replay.LoadManifestAsync(manifestPath, CancellationToken.None);
        Assert.NotNull(loadedManifest);
        Assert.Single(loadedManifest!.Segments);

        var blocks = new List<Hdf5ReplayBlock>();
        await foreach (var replayBlock in replay.ReadBlocksAsync(manifestPath, loadedManifest.Segments[0], CancellationToken.None))
        {
            blocks.Add(replayBlock);
        }

        var restored = Assert.Single(blocks);
        Assert.Equal(3, restored.Header.SourceId);
        Assert.Equal(21, restored.Header.Sequence);
        Assert.Equal(SampleType.Int16, restored.Header.SampleType);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, restored.Payload);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private static SessionContext CreateSession()
    {
        return new SessionContext
        {
            SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            TaskName = "Hdf5Storage",
            OperatorName = "Tester",
            BatchNo = "Batch-01",
            StartTime = new DateTimeOffset(2026, 4, 11, 3, 30, 0, TimeSpan.Zero),
            StorageFormat = StorageFormat.Hdf5,
            WriteRaw = true,
            WriteProcessed = false
        };
    }

    private static DataBlock CreateBlock(BlockPool pool, int sourceId, long sequence, byte[] payload)
    {
        var block = pool.Rent(payload.Length);
        block.CopyFrom(payload);
        block.Header = new DataBlockHeader(
            Guid.NewGuid(),
            sourceId,
            StreamKind.Raw,
            sequence,
            0,
            payload.Length / 2,
            1,
            SampleType.Int16,
            TimestampNs.Now(),
            TimestampNs.Now());
        return block;
    }

    private static object AssertSingleCompletedSegment(Hdf5Writer writer)
    {
        var property = typeof(Hdf5Writer).GetProperty("CompletedSegments")
            ?? throw new InvalidOperationException("CompletedSegments property was not found.");
        var segments = ((System.Collections.IEnumerable?)property.GetValue(writer))
            ?.Cast<object>()
            .ToArray()
            ?? throw new InvalidOperationException("CompletedSegments returned null.");
        return Assert.Single(segments);
    }

    private static T ReadSegmentValue<T>(object segment, string propertyName)
    {
        var property = segment.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Segment property '{propertyName}' was not found.");
        return (T)(property.GetValue(segment)
            ?? throw new InvalidOperationException($"Segment property '{propertyName}' was null."));
    }
}
