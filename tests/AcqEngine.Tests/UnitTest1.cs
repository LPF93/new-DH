using AcqEngine.Core;
using AcqEngine.Processing;
using AcqEngine.Storage.Abstractions;

namespace AcqEngine.Tests;

public class UnitTest1
{
    [Fact]
    public void NamingPolicy_Should_Render_Tokens()
    {
        var session = new SessionContext
        {
            TaskName = "BearingTest",
            OperatorName = "OperatorA",
            BatchNo = "Batch-01",
            StartTime = new DateTimeOffset(2026, 4, 10, 15, 30, 12, TimeSpan.Zero),
            AutoIncrementNo = 1
        };

        var policy = new NamingTemplateFileNamingPolicy(
            "D:\\AcqData",
            "{TaskName}_{Stream}_{Date:yyyyMMdd}_{StartTime:HHmmss}_{AutoInc:0000}_seg{SegmentNo:0000}");

        var name = policy.BuildSegmentFileName(session, StreamKind.Raw, 3);
        Assert.Equal("BearingTest_原始_20260410_153012_0001_seg0003", name);
    }

    [Fact]
    public void BlockPool_Should_Rent_And_Copy_Payload()
    {
        var pool = new BlockPool();
        var block = pool.Rent(16);

        block.CopyFrom(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(4, block.PayloadLength);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, block.Payload.ToArray());

        block.Release();
    }

    [Fact]
    public async Task ProcessingPipeline_Should_Emit_Processed_Block()
    {
        var pool = new BlockPool();
        var outputs = new List<DataBlock>();
        using var signal = new ManualResetEventSlim(false);

        await using var pipeline = new ProcessingPipeline(
            new IProcessor[] { new PassThroughProcessor(pool) },
            block =>
            {
                block.AddRef();
                outputs.Add(block);
                signal.Set();
                return true;
            });

        pipeline.Start();

        var raw = pool.Rent(8);
        raw.CopyFrom(new byte[] { 10, 11, 12, 13, 14, 15, 16, 17 });
        raw.Header = new DataBlockHeader(
            Guid.NewGuid(),
            1,
            StreamKind.Raw,
            1,
            0,
            4,
            1,
            SampleType.Int16,
            TimestampNs.Now(),
            TimestampNs.Now());

        Assert.True(pipeline.TryEnqueue(raw));
        raw.Release();

        Assert.True(signal.Wait(TimeSpan.FromSeconds(2)));
        Assert.Single(outputs);
        Assert.Equal(StreamKind.Processed, outputs[0].Header.StreamKind);

        foreach (var block in outputs)
        {
            block.Release();
        }

        await pipeline.StopAsync(CancellationToken.None);
    }
}