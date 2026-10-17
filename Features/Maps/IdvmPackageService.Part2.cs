namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    internal async Task<IReadOnlyList<MapRecord>> GetExportMapsAsync(
        IdvmExportScope scope,
        string? className,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SelectMaps((await _repository.GetCatalogSnapshotAsync()).Maps, scope, className);
    }

    internal async Task<MapCatalogSnapshot> GetExportSnapshotAsync(IdvmExportScope scope, string? className,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return scope == IdvmExportScope.CurrentClass && !string.IsNullOrWhiteSpace(className)
            ? await _repository.GetClassCatalogSnapshotAsync(className)
            : await _repository.GetCatalogSnapshotAsync();
    }

    private static MapRecord[] SelectMaps(
        IEnumerable<MapRecord> maps,
        IdvmExportScope scope,
        string? className) => scope == IdvmExportScope.CurrentClass
            ? maps.Where(map => string.Equals(
                map.Class,
                className,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : maps.ToArray();
}
