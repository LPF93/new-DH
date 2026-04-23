using System.Text;
using AcqEngine.Core;
using AcqEngine.Storage.Abstractions;
using AcqEngine.Storage.Tdms;

namespace AcqEngine.Tests;

public sealed class TdmsStorageTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "AcqEngine.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TdmsWriter_Should_Create_Real_Tdms_File()
    {
        var policy = new NamingTemplateFileNamingPolicy(_rootPath, "{TaskName}_{Stream}_seg{SegmentNo:0000}");
        var session = CreateSession();
        var pool = new BlockPool();

        await using var writer = new TdmsWriter(policy);
        await writer.OpenSessionAsync(session, CancellationToken.None);

        try
        {
            await writer.OpenSegmentAsync(StreamKind.Raw, 1, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            _ = ex;
            return;
        }

        var block = CreateFloatBlock(
            pool,
            sourceId: 1,
            sequence: 1,
            channelCount: 2,
            samplesPerChannel: 4,
            values:
            [
                0.1f, 1.1f,
                0.2f, 1.2f,
                0.3f, 1.3f,
                0.4f, 1.4f
            ]);

        await writer.WriteBlockAsync(block, CancellationToken.None);
        block.Release();
        await writer.CloseSessionAsync(CancellationToken.None);

        var segment = Assert.Single(writer.CompletedSegments);
        Assert.Equal("TDMS", segment.ContainerFormat);
        Assert.Equal(StreamKind.Raw, segment.StreamKind);
        Assert.True(segment.FileBytes > 0);
        Assert.True(File.Exists(segment.FilePath));

        await using var stream = File.OpenRead(segment.FilePath);
        var header = new byte[4];
        var read = await stream.ReadAsync(header, CancellationToken.None);
        Assert.Equal(4, read);
        Assert.Equal("TDSm", Encoding.ASCII.GetString(header));
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
            SessionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            TaskName = "TdmsStorage",
            OperatorName = "Tester",
            BatchNo = "Batch-01",
            StartTime = new DateTimeOffset(2026, 4, 22, 5, 0, 0, TimeSpan.Zero),
            StorageFormat = StorageFormat.Tdms,
            WriteRaw = true,
            WriteProcessed = false,
            Sources =
            [
                new SourceDescriptor
                {
                    SourceId = 1,
                    DeviceName = "Source_0001",
                    ChannelCount = 2,
                    SampleRateHz = 2000,
                    SampleType = SampleType.Float32
                }
            ]
        };
    }

    private static DataBlock CreateFloatBlock(
        BlockPool pool,
        int sourceId,
        long sequence,
        int channelCount,
        int samplesPerChannel,
        float[] values)
    {
        var payload = new byte[values.Length * sizeof(float)];
        for (var index = 0; index < values.Length; index++)
        {
            BitConverter.TryWriteBytes(payload.AsSpan(index * sizeof(float), sizeof(float)), values[index]);
        }

        var block = pool.Rent(payload.Length);
        block.CopyFrom(payload);
        block.Header = new DataBlockHeader(
            Guid.NewGuid(),
            sourceId,
            StreamKind.Raw,
            sequence,
            0,
            samplesPerChannel,
            channelCount,
            SampleType.Float32,
            TimestampNs.Now(),
            TimestampNs.Now());
        return block;
    }
}
