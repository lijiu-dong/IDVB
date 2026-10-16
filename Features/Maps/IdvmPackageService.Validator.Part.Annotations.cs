namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private static void ValidateAnnotationGeometry(AnnotationDto annotation)
    {
        if (annotation.Type == "route")
        {
            if (annotation.Points is not { Count: >= 2 and <= 8192 })
                throw new InvalidDataException("自动路线阶段的折线点数无效。");
            foreach (var point in annotation.Points) ValidatePoint(point, "annotation.points");
            ValidatePoint(annotation.Start, "annotation.start"); ValidatePoint(annotation.End, "annotation.end");
            var first = annotation.Points[0]; var last = annotation.Points[^1];
            if (first.X != annotation.Start!.X || first.Y != annotation.Start.Y
                || last.X != annotation.End!.X || last.Y != annotation.End.Y
                || Math.Abs(first.X - last.X) <= 1e-6 && Math.Abs(first.Y - last.Y) <= 1e-6)
                throw new InvalidDataException("自动路线阶段端点与折线不一致。");
            return;
        }
        if (annotation.Points is not null) throw new InvalidDataException("普通标注不能包含路线折线。");
        if (annotation.Type == "text")
            ValidateTextStyle(annotation);
        if (annotation.Type != "line")
        {
            ValidateRectangle(annotation.Bounds, allowNull: false, "annotation.bounds");
            if (annotation.Start is not null || annotation.End is not null)
                throw new InvalidDataException("非直线标注不能包含端点。");
            return;
        }

        ValidatePoint(annotation.Start, "annotation.start");
        ValidatePoint(annotation.End, "annotation.end");
        if (Math.Abs(annotation.Start!.X - annotation.End!.X) <= 0.000001d
            && Math.Abs(annotation.Start.Y - annotation.End.Y) <= 0.000001d)
        {
            throw new InvalidDataException("直线标注的两个端点不能重合。");
        }
        ValidateRectangle(annotation.Bounds, allowNull: true, "annotation.bounds");
    }

    private static void ValidateTextStyle(AnnotationDto annotation)
    {
        if (annotation.FontFamily is { Length: > 256 }
            || annotation.FontFamily?.Any(char.IsControl) is true
            || annotation.FontSize is { } size && (!double.IsFinite(size) || size is < 1d or > 256d))
        {
            throw new InvalidDataException("文字标注样式无效。");
        }
    }

}
