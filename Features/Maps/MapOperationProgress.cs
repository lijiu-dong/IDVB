namespace IDVBuff.Features.Maps;

public readonly record struct MapOperationUpdate(double Fraction, string Operation);

/// <summary>Operation-local progress, carried through awaits and worker tasks without global UI state.</summary>
public static class MapOperationProgress
{
    private sealed record Context(IProgress<MapOperationUpdate> Target, double Start, double End)
    {
        public double Map(double fraction) => fraction switch
        {
            <= 0 => Start,
            >= 1 => End,
            _ => Start + (End - Start) * fraction
        };
    }
    private static readonly AsyncLocal<Context?> Current = new();

    public static IDisposable Begin(IProgress<MapOperationUpdate> target) => Set(new(target, 0, 1));

    public static IDisposable Range(double start, double end)
    {
        var parent = Current.Value;
        return Set(parent is null ? null : new(parent.Target, parent.Map(start), parent.Map(end)));
    }

    public static void Report(double fraction, string operation)
    {
        var context = Current.Value;
        if (context is null || !double.IsFinite(fraction)) return;
        context.Target.Report(new(context.Map(fraction), operation));
    }

    public static Action<double, string> Capture()
    {
        var context = Current.Value;
        return (fraction, operation) =>
        {
            if (context is not null && double.IsFinite(fraction))
                context.Target.Report(new(context.Map(fraction), operation));
        };
    }

    public static async Task<T> RunAsync<T>(double start, double end, Func<Task<T>> action)
    {
        using var range = Range(start, end);
        return await action();
    }

    public static async Task RunAsync(double start, double end, Func<Task> action)
    {
        using var range = Range(start, end);
        await action();
    }

    private static IDisposable Set(Context? context)
    {
        var previous = Current.Value;
        Current.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore(Context? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
