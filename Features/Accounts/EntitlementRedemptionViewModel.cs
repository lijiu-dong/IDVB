using System.Globalization;
using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Features.Accounts;

internal sealed class EntitlementRedemptionViewModel : UsageObservable, IDisposable
{
    private readonly IEntitlementRedemptionService _service;
    private readonly IAccountService _accounts;
    private readonly Func<CancellationToken, Task<bool>> _refreshUsage;
    private readonly Action<Action> _dispatch;
    private readonly CancellationTokenSource _lifetime = new();
    private string _code = "";
    private bool _disposed;
    private bool _invalidated;

    public EntitlementRedemptionViewModel(IEntitlementRedemptionService service, IAccountService accounts,
        Func<CancellationToken, Task<bool>> refreshUsage, Action<Action> dispatch)
    {
        _service = service;
        _accounts = accounts;
        _refreshUsage = refreshUsage;
        _dispatch = dispatch;
        _accounts.Changed += AccountChanged;
    }

    public string Code
    {
        get => _code;
        set { _code = value; AllChanged(); }
    }
    public bool IsBusy { get; private set; }
    public bool CanEdit => !IsBusy && !_disposed && !_invalidated;
    public bool CanRedeem => CanEdit && !string.IsNullOrWhiteSpace(Code);
    public string AccountName => _accounts.Identity?.DisplayName ?? "未登录";
    public string? Message { get; private set; }
    public bool HasMessage => Message is not null;
    public bool IsError { get; private set; }
    public event Action? CloseRequested;

    public async Task RedeemAsync()
    {
        if (!CanRedeem) return;
        var session = _accounts.Capture();
        if (session.Identity is null || string.IsNullOrWhiteSpace(session.Token))
        {
            Message = "请先登录后再兑换。";
            IsError = true;
            AllChanged();
            return;
        }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, session.Lifetime);
        var token = request.Token;
        IsBusy = true;
        IsError = false;
        Message = "正在兑换，请稍候…";
        AllChanged();
        try
        {
            var result = await _service.RedeemAsync(Code.Trim(), token);
            if (!CanApply()) return;
            if (result.Grant is null && result.Points is null)
            {
                IsError = true;
                Message = result.Message ?? "兑换失败，请稍后重试。";
                return;
            }
            _code = "";
            var prefix = result.AlreadyRedeemed ? "此码已兑换到当前账户，未重复发放。" : "兑换成功！";
            if (result.Points is { } points)
            {
                IsError = false;
                Message = $"{prefix}\n{(points.IsFree ? "免费点数" : "点数")} +{UsagePointsPresentation.Format(points.Points)}";
            }
            else if (result.Grant is { } grant)
            {
                IsError = grant.RevokedAt is not null;
                Message = grant.RevokedAt is not null ? "此码已兑换过，但对应权益已被管理员撤销，不会再次发放。"
                    : $"{prefix}\n{grant.DisplayName}\n{Time(grant.StartsAt)} 起 · {(grant.EndsAt is { } end ? Time(end) + " 到期" : "永久有效")}";
            }
            AllChanged();
            // Redemption is already committed. A failed summary refresh must never invite a second purchase.
            try
            {
                var refreshed = await _refreshUsage(token);
                if (CanApply() && !refreshed) Message += "\n使用情况暂未刷新，请稍后在“使用情况”中重试。";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (CanApply()) Message += "\n使用情况暂未刷新，请稍后在“使用情况”中重试。";
                System.Diagnostics.Trace.TraceWarning("Post-redemption usage refresh failed: {0}", exception.GetType().Name);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (CanApply())
            {
                IsError = true;
                Message = "暂时无法确认兑换结果。可使用同一码重试，不会重复发放权益。";
            }
            System.Diagnostics.Trace.TraceWarning("Entitlement redemption failed: {0}", exception.GetType().Name);
        }
        finally
        {
            if (!_disposed && !_invalidated)
            {
                IsBusy = false;
                AllChanged();
            }
        }
        bool CanApply() => !_disposed && !_invalidated && !token.IsCancellationRequested && _accounts.IsCurrent(session);
    }

    private static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.GetCultureInfo("zh-CN"));

    private void AccountChanged(object? sender, EventArgs args)
    {
        _lifetime.Cancel();
        _dispatch(() =>
        {
            if (_disposed) return;
            _invalidated = true;
            _code = "";
            Message = null;
            IsBusy = false;
            AllChanged();
            CloseRequested?.Invoke();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accounts.Changed -= AccountChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
