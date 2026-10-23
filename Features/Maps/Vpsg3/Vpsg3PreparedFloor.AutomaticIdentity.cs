namespace IDVBuff.Features.Maps;

public sealed partial class Vpsg3PreparedFloor
{
    // Built before floor publication. Retired with the same lease, map and floor;
    // repeated same-frame pose checks never re-extract the source's corners.
    internal MapFrontEntryReferenceIndex? AutomaticEntryIndex { get; set; }
}
