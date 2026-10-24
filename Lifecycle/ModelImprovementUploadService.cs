namespace IDVBuff.Lifecycle;

public static partial class ModelImprovementUploadService
{
    internal static Uri UploadEndpoint => IDVBuff.UpdateCore.OnlineServiceLinks.TryResolve("model_improvement.upload")
        ?? throw new IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("model_improvement.upload");
    public static Task TryUploadDailyAsync(MainProgramPreferences preferences, CancellationToken cancellationToken = default)
    {
        Task? work = null;
        StartPrivate(preferences, cancellationToken, ref work);
        return work ?? Task.CompletedTask;
    }
    static partial void StartPrivate(MainProgramPreferences preferences, CancellationToken cancellationToken, ref Task? work);
}
