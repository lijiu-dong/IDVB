namespace IDVBuff.Features.Maps;

public sealed partial class MapRecord
{
    // A list does not own complete annotations/concealment/crop strokes. Its editor entry
    // must resolve the complete current record, even at an unchanged revision.
    internal bool IsListProjection { get; set; }
}
