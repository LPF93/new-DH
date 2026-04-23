using System.Text.Json;

namespace AcqEngine.Replay;

public sealed record SessionSegmentInfo(
	string Stream,
	int SegmentNo,
	string Path,
	DateTimeOffset StartTime,
	DateTimeOffset EndTime);

public sealed record SessionManifest(
	Guid SessionId,
	string TaskName,
	DateTimeOffset StartTime,
	DateTimeOffset? EndTime,
	IReadOnlyList<SessionSegmentInfo> Segments);

public sealed class SessionReplayService
{
	private readonly Hdf5SessionReplayService _hdf5ReplayService = new();

	public async Task<SessionManifest?> LoadManifestAsync(string manifestPath, CancellationToken ct)
	{
		var manifest = await _hdf5ReplayService.LoadManifestAsync(manifestPath, ct);
		return manifest is null ? null : MapManifest(manifest);
	}

	private static SessionManifest MapManifest(AcqShell.Contracts.SessionManifestDto manifest)
	{
		return new SessionManifest(
			manifest.SessionId,
			manifest.TaskName,
			manifest.StartTime,
			manifest.EndTime,
			manifest.Segments
				.Select(static segment => new SessionSegmentInfo(
					segment.Stream,
					segment.SegmentNo,
					segment.RelativePath,
					segment.StartedAt,
					segment.EndedAt))
				.ToArray());
	}
}
