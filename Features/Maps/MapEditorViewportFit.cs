namespace IDVBuff.Features.Maps;

public readonly record struct MapEditorViewportFit(double DisplayScale, float ZoomFactor)
{
    public static MapEditorViewportFit? Calculate(double imageWidth, double imageHeight, double viewportWidth, double viewportHeight)
    {
        if (!double.IsFinite(imageWidth) || !double.IsFinite(imageHeight)
            || !double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight)
            || imageWidth <= 0 || imageHeight <= 0 || viewportWidth <= 24 || viewportHeight <= 24) return null;
        var scale = Math.Min(8d, Math.Min((viewportWidth - 24) / imageWidth, (viewportHeight - 24) / imageHeight));
        // ScrollViewer cannot zoom below 0.1. Scale its content host only when
        // needed, retaining original source coordinates on the editing canvas.
        var displayScale = Math.Min(1d, scale / .1d);
        return new(displayScale, (float)Math.Max(.1d, scale / displayScale));
    }
}
