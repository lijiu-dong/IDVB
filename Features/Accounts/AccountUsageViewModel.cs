using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Features.Accounts;

internal abstract class UsageObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected void AllChanged() => Changed(string.Empty);
}

internal sealed class UsageCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
    public event EventHandler? CanExecuteChanged;
    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class SponsorshipCardViewModel : UsageObservable
{
    private readonly SponsorshipPlanData[] _variants;
    private int _variantIndex;
    private string? _currentKey;
    public SponsorshipCardViewModel(SponsorshipPlanData[] variants, Action<SponsorshipPlanData> requestChange)
    {
        _variants = variants;
        SwitchCommand = new(() => requestChange(Plan), () => !IsCurrent);
    }
    public SponsorshipPlanData Plan => _variants[_variantIndex];
    public bool HasVariants => _variants.Length > 1;
    public bool IsCurrent => string.Equals(Plan.Key, _currentKey, StringComparison.Ordinal);
    public bool ShowTopCurrentBadge => IsCurrent && !HasVariants;
    public bool ShowVariantCurrentBadge => IsCurrent && HasVariants;
    public string ButtonText => IsCurrent ? "当前计划" : $"切换到 {Plan.Name}";
    public UsageCommand SwitchCommand { get; }
    public int VariantIndex
    {
        get => _variantIndex;
        set
        {
            if (value < 0 || value >= _variants.Length || value == _variantIndex) return;
            _variantIndex = value;
            AllChanged();
            SwitchCommand.Refresh();
        }
    }
    public void SetCurrent(string? key, bool selectCurrentVariant)
    {
        _currentKey = key;
        var index = Array.FindIndex(_variants, plan => plan.Key == key);
        if (selectCurrentVariant) _variantIndex = Math.Max(0, index);
        AllChanged();
        SwitchCommand.Refresh();
    }
}

internal sealed class AccountUsageViewModel : UsageObservable, IDisposable
{
    private readonly IUsageMeter _meter;
    private readonly IAccountService _accounts;
    private readonly Action<Action> _dispatch;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _load;
    private long _revision;
    private long _snapshotRevision;
    private DateTimeOffset _snapshotTime;
    private bool _disposed;
    private string? _currentKey;
    public AccountUsageViewModel(IUsageMeter meter, IAccountService accounts, SponsorshipPlanCatalog catalog, Action<Action> dispatch)
    {
        _meter = meter;
        _accounts = accounts;
        _dispatch = dispatch;
        Cards = [new([catalog.Plans[0]], RequestChange), new([catalog.Plans[1]], RequestChange),
            new([catalog.Plans[2]], RequestChange), new([catalog.Plans[3], catalog.Plans[4]], RequestChange)];
        RetryCommand = new(() => _ = LoadAsync(), () => !IsLoading);
        ShowPlansCommand = new(() => { SelectCurrentVariant(); AllChanged(); PlansRequested?.Invoke(); }, () => !IsUsageUnavailable);
        CloseCommand = new(() => CloseRequested?.Invoke());
        _meter.Changed += UsageChanged;
        _accounts.Changed += AccountChanged;
    }

    public SponsorshipCardViewModel[] Cards { get; }
    public UsagePoolPresentation Daily { get; private set; } = UsagePoolPresentation.From(null, false);
    public UsagePoolPresentation Extra { get; private set; } = UsagePoolPresentation.From(null, true);
    public string FreePointsText { get; private set; } = "--";
    public string PaidPointsText { get; private set; } = "--";
    // Follow the whole-point values the user sees; unknown balances retain their placeholders.
    public bool ShowPointsSection => FreePointsText != "0" || PaidPointsText != "0";
    public bool ShowFreePoints { get; private set; }
    public double PaidPointsFontSize => ShowFreePoints ? 14 : 24;
    public double PaidPointsTitleFontSize => ShowFreePoints ? 12 : 14;
    public string PointsRateText { get; private set; } = "";
    public string CurrentPlanName { get; private set; } = "--";
    public bool IsLoading { get; private set; }
    // The loading ring occupies the percentage slot, so the two never show together.
    public bool ShowPercentages => !IsLoading;
    public bool HasError { get; private set; }
    public bool IsUsageUnavailable { get; private set; }
    public bool ShowUsageDetails => !IsUsageUnavailable;
    /// <summary>Trailing hint on the account menu row, like a cascading menu's current value.</summary>
    public string MenuHint => IsUsageUnavailable ? "无效" : IsLoading || Daily.Level == UsageLevel.Unknown ? ""
        : $"剩余 {Math.Floor(Math.Max(0, 100 - Daily.Percent)).ToString(System.Globalization.CultureInfo.InvariantCulture)}%";
    public bool IsDailyExhausted => Daily.Level == UsageLevel.Exhausted;
    public bool IsDailyWarning => Daily.Level == UsageLevel.Warning;
    public bool IsDailyNormal => Daily.Level is UsageLevel.Normal or UsageLevel.Unknown;
    public UsageCommand RetryCommand { get; }
    public UsageCommand ShowPlansCommand { get; }
    public UsageCommand CloseCommand { get; }
    public event Action? CloseRequested;
    public event Action? PlansRequested;
    public event Action<SponsorshipPlanData>? PlanChangeRequested;

    public Task LoadAsync() => LoadAsync(CancellationToken.None, false);

    internal async Task<bool> ReloadAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken, true);
        return !HasError && !cancellationToken.IsCancellationRequested;
    }

    private async Task LoadAsync(CancellationToken cancellationToken, bool force)
    {
        if (_disposed || (IsLoading && !force)) return;
        if (force) _load?.Cancel();
        var session = _accounts.Capture();
        var revision = ++_revision;
        var snapshotRevision = _snapshotRevision;
        _load?.Dispose();
        _load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, session.Lifetime, cancellationToken);
        var token = _load.Token;
        IsLoading = true;
        HasError = false;
        Daily = UsagePoolPresentation.From(null, false);
        Extra = UsagePoolPresentation.From(null, true);
        ResetPoints();
        AllChanged();
        RetryCommand.Refresh();
        try
        {
            var snapshot = await _meter.RefreshAsync(token);
            // A heartbeat (or this refresh's Changed event) may already have supplied newer state.
            if (!CanApply() || snapshotRevision != _snapshotRevision) return;
            if (snapshot is null) HasError = true;
            else Apply(snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (CanApply() && snapshotRevision == _snapshotRevision) HasError = true;
            System.Diagnostics.Trace.TraceWarning("Usage refresh failed: {0}", exception.GetType().Name);
        }
        finally
        {
            if (!_disposed && revision == _revision)
            {
                IsLoading = false;
                AllChanged();
                RetryCommand.Refresh();
            }
        }
        bool CanApply() => !_disposed && !token.IsCancellationRequested && revision == _revision && _accounts.IsCurrent(session);
    }

    private void UsageChanged(object? sender, UsageSnapshot snapshot)
    {
        var session = _accounts.Capture();
        _dispatch(() =>
        {
            if (_disposed || !_accounts.IsCurrent(session) || session.Identity is null) return;
            Apply(snapshot);
            HasError = false;
            AllChanged();
        });
    }

    private void AccountChanged(object? sender, EventArgs args) => _dispatch(() =>
    {
        if (_disposed) return;
        ++_revision;
        _load?.Cancel();
        IsLoading = false;
        HasError = false;
        IsUsageUnavailable = false;
        _snapshotTime = default;
        CurrentPlanName = "--";
        _currentKey = null;
        Daily = UsagePoolPresentation.From(null, false);
        Extra = UsagePoolPresentation.From(null, true);
        ResetPoints();
        SelectCurrentVariant();
        AllChanged();
        RetryCommand.Refresh();
        ShowPlansCommand.Refresh();
        CloseRequested?.Invoke();
        if (_accounts.Identity is not null) _ = LoadAsync();
    });

    private void Apply(UsageSnapshot snapshot)
    {
        // Summary replies also publish Changed; reject older server data on that path as well.
        if (snapshot.ServerTime < _snapshotTime) return;
        _snapshotTime = snapshot.ServerTime;
        ++_snapshotRevision;
        IsUsageUnavailable = !snapshot.Enforced;
        var dailyPools = snapshot.Pools.Where(pool => pool.Kind is UsagePoolKind.Daily or UsagePoolKind.FreeDaily).ToArray();
        var daily = dailyPools.Length == 0 ? null : new UsagePool(UsagePoolKind.Daily, dailyPools.Sum(pool => pool.UsedSeconds),
            dailyPools.Any(pool => pool.LimitSeconds is null) ? null : dailyPools.Sum(pool => pool.LimitSeconds ?? 0), dailyPools[0].ResetsAt);
        Daily = UsagePoolPresentation.From(IsUsageUnavailable ? null : daily, false);
        Extra = UsagePoolPresentation.From(IsUsageUnavailable ? null
            : snapshot.Pools?.FirstOrDefault(pool => pool.Kind == UsagePoolKind.WeeklyExtra), true);
        var points = snapshot.Points;
        FreePointsText = points is null ? "--" : UsagePointsPresentation.Format(points.FreePoints);
        PaidPointsText = points is null ? "--" : UsagePointsPresentation.Format(points.PaidPoints);
        ShowFreePoints = points?.FreePoints > 0;
        PointsRateText = points is null ? "" : $"1 点数 = {Math.Max(0, points.HoursPerPoint).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)} 小时";
        CurrentPlanName = string.IsNullOrWhiteSpace(snapshot.Tier?.DisplayName) ? "--" : snapshot.Tier.DisplayName;
        var changed = _currentKey != snapshot.Tier?.Key;
        _currentKey = snapshot.Tier?.Key;
        foreach (var card in Cards) card.SetCurrent(_currentKey, changed);
        AllChanged();
        ShowPlansCommand.Refresh();
    }
    private void ResetPoints()
    {
        FreePointsText = PaidPointsText = "--";
        ShowFreePoints = false;
        PointsRateText = "";
    }
    private void SelectCurrentVariant() { foreach (var card in Cards) card.SetCurrent(_currentKey, true); }
    private void RequestChange(SponsorshipPlanData plan) => PlanChangeRequested?.Invoke(plan);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_revision;
        _meter.Changed -= UsageChanged;
        _accounts.Changed -= AccountChanged;
        _lifetime.Cancel();
        _load?.Cancel();
        _load?.Dispose();
        _lifetime.Dispose();
    }
}
