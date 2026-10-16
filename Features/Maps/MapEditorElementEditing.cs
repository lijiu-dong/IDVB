namespace IDVBuff.Features.Maps;

public enum MapEditorElementKind { Annotation, Anchor, Crop, Background }

public static class MapEditorElementEditing
{
    public static bool CanEdit(bool specialElementsReadOnly, MapEditorElementKind kind) =>
        !specialElementsReadOnly || kind == MapEditorElementKind.Annotation;

    public static bool Delete(FloorRecognitionProfile profile, bool specialElementsReadOnly,
        MapEditorElementKind? kind, Guid? id, IReadOnlySet<Guid> selectedAnnotationIds)
    {
        if (selectedAnnotationIds.Count > 0)
            return profile.Annotations.RemoveAll(annotation => selectedAnnotationIds.Contains(annotation.Id)) > 0;
        if (kind is not { } selectionKind || !CanEdit(specialElementsReadOnly, selectionKind)) return false;
        switch (selectionKind)
        {
            case MapEditorElementKind.Annotation when id is { } annotationId:
                return profile.Annotations.RemoveAll(annotation => annotation.Id == annotationId) > 0;
            case MapEditorElementKind.Crop:
                if (profile.RecognitionRegion is null && profile.FreeCropPoints.Count == 0) return false;
                MapRecognitionCoordinates.ApplyRecognitionRegion(profile, new NormalizedRectangle { Width = 1, Height = 1 });
                profile.RecognitionRegion = null;
                profile.FreeCropPoints.Clear();
                return true;
            case MapEditorElementKind.Anchor when id is { } anchorId:
                var anchor = profile.Anchors.Find(item => item.Id == anchorId);
                if (anchor is null) return false;
                if (anchor.IsBuiltIn) anchor.Bounds = null;
                else profile.Anchors.Remove(anchor);
                return true;
            case MapEditorElementKind.Background when id is { } backgroundId:
                return profile.BackgroundLayers.RemoveAll(layer => layer.Id == backgroundId) > 0;
            default:
                return false;
        }
    }
}
