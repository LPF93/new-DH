using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AcqEngine.Core;
using AcqShell.Contracts;

namespace AcqEngine.Replay;

public sealed record Hdf5ReplayBlock(
    DataBlockHeader Header,
    byte[] Payload);

public sealed class Hdf5SessionReplayService
{
    public async Task<SessionManifestDto?> LoadManifestAsync(string manifestPath, CancellationToken ct)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<SessionManifestDto>(stream, cancellationToken: ct);
    }

    public string ResolveSegmentPath(string manifestPath, SessionSegmentDto segment)
    {
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new InvalidOperationException("Manifest directory could not be resolved.");
        return Path.GetFullPath(Path.Combine(manifestDirectory, segment.RelativePath));
    }

    public async IAsyncEnumerable<Hdf5ReplayBlock> ReadBlocksAsync(
        string manifestPath,
        SessionSegmentDto segment,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!segment.ContainerFormat.Equals("HDF5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Segment {segment.RelativePath} is not an HDF5 segment.");
        }

        var segmentPath = ResolveSegmentPath(manifestPath, segment);
        await using var stream = new FileStream(
            segmentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var headerBuffer = new byte[56];
        while (true)
        {
            var headerBytes = await FillBufferAsync(stream, headerBuffer, ct);
            if (headerBytes == 0)
            {
                yield break;
            }

            if (headerBytes != headerBuffer.Length)
            {
                throw new InvalidDataException("Unexpected end of HDF5 segment header.");
            }

            var header = ParseHeader(headerBuffer);
            var payload = new byte[header.PayloadLength];
            var payloadBytes = await FillBufferAsync(stream, payload, ct);
            if (payloadBytes != payload.Length)
            {
                throw new InvalidDataException("Unexpected end of HDF5 segment payload.");
            }

            yield return new Hdf5ReplayBlock(
                new DataBlockHeader(
                    Guid.Empty,
                    header.SourceId,
                    ParseStreamKind(segment.Stream),
                    header.Sequence,
                    0,
                    header.SampleCountPerChannel,
                    header.ChannelCount,
                    header.SampleType,
                    header.DeviceTimestampNs,
                    header.HostTimestampNs),
                payload);
        }
    }

    private static SegmentBlockHeader ParseHeader(ReadOnlySpan<byte> buffer)
    {
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(0, 4));
        if (magic != 0x35464448)
        {
            throw new InvalidDataException("Invalid HDF5 segment magic.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported HDF5 segment version: {version}.");
        }

        return new SegmentBlockHeader(
            BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(12, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(20, 8)),
            BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(28, 4)),
            (SampleType)BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(32, 4)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(36, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(44, 8)),
            BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(52, 4)));
    }

    private static async Task<int> FillBufferAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static StreamKind ParseStreamKind(string stream)
    {
        return Enum.TryParse<StreamKind>(stream, ignoreCase: true, out var result)
            ? result
            : StreamKind.Raw;
    }

    private sealed record SegmentBlockHeader(
        int SourceId,
        long Sequence,
        long SampleCountPerChannel,
        int ChannelCount,
        SampleType SampleType,
        long DeviceTimestampNs,
        long HostTimestampNs,
        int PayloadLength);
}
