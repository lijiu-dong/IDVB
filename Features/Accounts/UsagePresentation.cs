using System.Globalization;
using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Features.Accounts;

internal enum UsageLevel { Unknown, Normal, Warning, Exhausted }

internal static class UsagePointsPresentation
{
    internal static string Format(decimal value) =>
        decimal.Floor(Math.Max(0, value)).ToString("0", CultureInfo.InvariantCulture);
}

internal sealed record UsagePoolPresentation(string Amount, double Percent, string Percentage, UsageLevel Level)
{
    internal static UsagePoolPresentation From(UsagePool? pool, bool remaining)
    {
        // A zero limit is an absent pool, not a division by zero. Null means unlimited
        // in the existing contract, but there is no finite denominator to display.
        if (pool?.LimitSeconds is not > 0 || pool.UsedSeconds < 0)
            return new("--", 0, "--", UsageLevel.Unknown);
        var limit = pool.LimitSeconds.Value;
        var used = Math.Min(pool.UsedSeconds, limit);
        var seconds = remaining ? limit - used : used;
        var percent = 100d * seconds / limit;
        var level = remaining ? UsageLevel.Normal
            : used >= limit ? UsageLevel.Exhausted
            : (double)used / limit >= .9 ? UsageLevel.Warning : UsageLevel.Normal;
        var amount = used >= limit ? FormatReset(pool)
            : $"{FormatDuration(seconds)} / {FormatDuration(limit)}";
        return new(amount, percent,
            remaining && seconds == 0 ? "额外时长已用完" : $"{Math.Floor(percent).ToString(CultureInfo.InvariantCulture)}%", level);
    }

    private static string FormatReset(UsagePool pool)
    {
        if (pool.ResetsAt == default) return "下次重置：--";
        // Quota periods are defined by the service in Beijing time, regardless of the device's timezone.
        var reset = pool.ResetsAt.ToOffset(TimeSpan.FromHours(8));
        var day = pool.Kind == UsagePoolKind.WeeklyExtra
            ? $"周{new[] { "日", "一", "二", "三", "四", "五", "六" }[(int)reset.DayOfWeek]} " : "";
        return $"下次重置：{day}{reset.ToString("H:mm", CultureInfo.InvariantCulture)}";
    }

    internal static string FormatDuration(long seconds)
    {
        var minutes = Math.Max(0, seconds) / 60;
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return hours == 0 ? $"{remainder} 分"
            : remainder == 0 ? $"{hours} 小时" : $"{hours} 小时 {remainder} 分";
    }
}

/// <summary>Presentation data supplied by configuration or a future catalog adapter, never sample quotas.</summary>
internal sealed record SponsorshipPlanData(string Key, string Name)
{
    public string? Description { get; init; }
    public decimal? Price { get; init; }
    public string? BillingLabel { get; init; }
    public long? DailySeconds { get; init; }
    public bool DailyUnlimited { get; init; }
    public long? ExtraSeconds { get; init; }
    public string? ExtraPeriodLabel { get; init; }
    public string? FeatureSummary { get; init; }

    public string DescriptionText => TextOrPlaceholder(Description);
    public string PriceText => Price is >= 0 && BillingLabel is not null
        ? $"¥{Price.Value.ToString("0.##", CultureInfo.InvariantCulture)}{(BillingLabel.Length == 0 ? "" : $" / {BillingLabel}")}" : "--";
    public string DailyText => DailyUnlimited ? "每日不限时长"
        : DailySeconds is >= 0 ? $"每日 {UsagePoolPresentation.FormatDuration(DailySeconds.Value)}" : "每日时长：--";
    public string ExtraText => ExtraSeconds is >= 0 && !string.IsNullOrWhiteSpace(ExtraPeriodLabel)
        ? $"{ExtraPeriodLabel} {UsagePoolPresentation.FormatDuration(ExtraSeconds.Value)}额外时长" : "额外时长：--";
    public string FeaturesText => TextOrPlaceholder(FeatureSummary);
    private static string TextOrPlaceholder(string? text) => string.IsNullOrWhiteSpace(text) ? "--" : text;
}

internal sealed class SponsorshipPlanCatalog
{
    // Only the requested identities are built in. All commercial details remain absent until supplied.
    public IReadOnlyList<SponsorshipPlanData> Plans { get; init; } =
    [new("origin", "Origin"), new("swift", "Swift"), new("probe", "Probe"), new("abyss", "Abyss"), new("void", "Void")];
}
