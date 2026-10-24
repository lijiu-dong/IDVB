using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed record MapMatchVariantIdentity(Guid GroupId, IReadOnlyList<Guid> MapIds,
    Guid? ConfirmedMemberId, MapCatalogRevision CatalogRevision);

public sealed partial class MapCvRecognitionService
{
    private (MapRecord[] Maps, AutomaticIdentityFloorWork[] Work) BuildAutomaticIdentitySnapshot(
        string? mapClass, string? floorKey, MapMatchVariantIdentity? confirmedGroup)
    {
        var maps = _maps.Where(map => string.IsNullOrWhiteSpace(mapClass)
                || string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase))
            .Where(map => confirmedGroup is null || confirmedGroup.MapIds.Contains(map.Id))
            .Select(map => map.Clone()).OrderBy(map => map.SequenceNumber).ThenBy(map => map.Id).ToArray();
        var work = BuildAutomaticIdentityFloorInputs(maps, floorKey)
            .Select(input => new AutomaticIdentityFloorWork(input)).ToArray();
        return (maps, work);
    }

    private MapRecognitionAttempt CurrentPoseFailure(string reason) =>
        MapCvRecognitionDiagnostics.Failure(
            MapCvRecognitionDiagnostics.CreateDiagnostics(ReadyMapCount, TotalMapCount),
            $"当前帧位姿未通过验证：{reason}");

    // Source-resolution geometry comes from this leased floor. Do not reconstruct
    // it from a dilated mask or borrow the primary floor's raster or scale.
    private static ReadOnlyMemory<Point> ReadAutomaticReferencePoints(Vpsg3PreparedFloor floor)
    {
        using var geometry = floor.CreateGeometryImage()
            ?? throw new InvalidOperationException("Prepared floor geometry is unavailable.");
        using var points = new Mat();
        Cv2.FindNonZero(geometry, points);
        if (points.Empty()) return ReadOnlyMemory<Point>.Empty;
        points.GetArray(out Point[] result);
        return result;
    }
}
