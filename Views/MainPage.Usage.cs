using IDVBuff.Features.Accounts;
using IDVBuff.Core.Contracts.Online;
using IDVBuff.Presentation.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Animation;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private const double AccountMenuGap = 4;
    private const double AccountMenuMargin = 8;
    private AccountUsageViewModel? _accountUsage;
    private SponsorshipPlanDialog? _sponsorshipPlanDialog;
    private bool _sponsorshipPlansOpen;
    private EntitlementRedemptionDialog? _entitlementRedemptionDialog;
    private bool _entitlementRedemptionOpen;
    private Popup? _accountMenuPopup;
    private AccountMenu? _accountMenu;
    private ThemeScope? _accountMenuTheme;
    private FrameworkElement? _accountMenuAnchor;
    private long _accountMenuDismissedAt;

    private async Task InitializeAccountUsageAsync()
    {
        try
        {
            await App.ServicesReadyTask;
            if (!IsLoaded || _accountUsage is not null) return;
            _accountUsage = App.Services.GetRequiredService<AccountUsageViewModel>();
            _accountUsage.PlansRequested += AccountUsage_PlansRequested;
            _accountUsage.CloseRequested += CloseAccountMenu;
            AccountNavigationPlan.SetBinding(TextBlock.TextProperty,
                new Binding { Source = _accountUsage, Path = new PropertyPath(nameof(AccountUsageViewModel.CurrentPlanName)) });
            if (AccountSession.Identity is not null) _ = _accountUsage.LoadAsync();
        }
        catch (Exception exception)
        {
            IDVBuff.Diagnostics.OutputLog.Write("ERROR", "ACCOUNT", "Initialize usage presentation failed.", exception);
        }
    }

    private async Task ToggleAccountMenuAsync(FrameworkElement anchor)
    {
        if (_accountMenuPopup is not null) { CloseAccountMenu(); return; }
        // Light dismiss closes the menu on pointer-down over the account button; the click that
        // follows must not immediately reopen it.
        if (Environment.TickCount64 - _accountMenuDismissedAt < 300) return;
        try
        {
            await InitializeAccountUsageAsync();
            if (!IsLoaded || XamlRoot is null || AccountSession.Identity is null
                || _accountUsage is not { } viewModel || _accountMenuPopup is not null) return;
            // Refresh the row hint even when the usage submenu is never opened.
            _ = viewModel.LoadAsync();
            var menu = new AccountMenu(viewModel);
            var theme = ThemeService.AttachRegion(menu, ThemeService.ProfileFor(this));
            menu.ApplyTheme(theme.Resources);
            menu.LogoutRequested += (_, _) => { CloseAccountMenu(); _ = ConfirmLogoutAsync(); };
            menu.RedeemRequested += (_, _) => { CloseAccountMenu(); _ = OpenEntitlementRedemptionAsync(); };
            menu.DismissRequested += (_, _) => { CloseAccountMenu(); anchor.Focus(FocusState.Keyboard); };
            // The usage panel grows upward, so every size change re-anchors the bottom edge.
            menu.SizeChanged += (_, args) => PositionAccountMenu(args.NewSize);
            var popup = new Popup
            {
                XamlRoot = XamlRoot,
                Child = menu,
                IsLightDismissEnabled = true,
                ShouldConstrainToRootBounds = true,
                ChildTransitions = [new PopupThemeTransition()]
            };
            popup.Closed += (_, _) =>
            {
                if (!ReferenceEquals(popup, _accountMenuPopup)) return;
                _accountMenuDismissedAt = Environment.TickCount64;
                ReleaseAccountMenu();
            };
            _accountMenuPopup = popup;
            _accountMenu = menu;
            _accountMenuTheme = theme;
            _accountMenuAnchor = anchor;
            menu.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            PositionAccountMenu(menu.DesiredSize);
            popup.IsOpen = true;
            menu.FocusFirstItem(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            IDVBuff.Diagnostics.OutputLog.Write("ERROR", "ACCOUNT", "Open account menu failed.", exception);
            CloseAccountMenu();
        }
    }

    private void PositionAccountMenu(Windows.Foundation.Size size)
    {
        if (_accountMenuPopup is not { } popup || _accountMenuAnchor is not { } anchor || XamlRoot is null) return;
        var origin = anchor.TransformToVisual(null).TransformPoint(new(0, 0));
        var bounds = XamlRoot.Size;
        popup.HorizontalOffset = Math.Max(AccountMenuMargin, Math.Min(origin.X, bounds.Width - size.Width - AccountMenuMargin));
        popup.VerticalOffset = Math.Max(AccountMenuMargin, origin.Y - size.Height - AccountMenuGap);
    }

    private void CloseAccountMenu()
    {
        if (_accountMenuPopup is not { } popup) return;
        ReleaseAccountMenu();
        popup.IsOpen = false;
    }

    private void ReleaseAccountMenu()
    {
        _accountMenuPopup = null;
        _accountMenu = null;
        _accountMenuAnchor = null;
        _accountMenuTheme?.Dispose();
        _accountMenuTheme = null;
    }

    private async Task ConfirmLogoutAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "确定退出登录？",
                Content = "退出后需要重新登录才能继续使用 IDVB。",
                PrimaryButtonText = "退出登录",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowThemedAsync(this) != ContentDialogResult.Primary) return;
            await AccountSession.LogoutAsync();
        }
        // Local logout has already stopped the runtime; a failed remote revoke must not
        // surface as an unhandled async event while the window is being retired.
        catch (Exception exception) { IDVBuff.Diagnostics.OutputLog.Write("ERROR", "ACCOUNT", "Logout revoke failed.", exception); }
    }

    private void AccountUsage_PlansRequested()
    {
        CloseAccountMenu();
        _ = OpenSponsorshipPlansAsync();
    }

    private async Task OpenSponsorshipPlansAsync()
    {
        if (_sponsorshipPlansOpen || _entitlementRedemptionOpen) return;
        _sponsorshipPlansOpen = true;
        try
        {
            if (!IsLoaded || AccountSession.Identity is null || _accountUsage is not { } viewModel) return;
            var dialog = new SponsorshipPlanDialog(viewModel) { XamlRoot = XamlRoot };
            _sponsorshipPlanDialog = dialog;
            viewModel.CloseRequested += dialog.Hide;
            try { await dialog.ShowThemedAsync(this); }
            finally { viewModel.CloseRequested -= dialog.Hide; }
        }
        catch (Exception exception)
        {
            IDVBuff.Diagnostics.OutputLog.Write("ERROR", "ACCOUNT", "Open sponsorship plans failed.", exception);
        }
        finally { _sponsorshipPlanDialog = null; _sponsorshipPlansOpen = false; }
    }

    private async Task OpenEntitlementRedemptionAsync()
    {
        if (_entitlementRedemptionOpen || _sponsorshipPlansOpen) return;
        _entitlementRedemptionOpen = true;
        try
        {
            if (!IsLoaded || AccountSession.Identity is null || _accountUsage is not { } usage) return;
            using var viewModel = new EntitlementRedemptionViewModel(
                App.Services.GetRequiredService<IEntitlementRedemptionService>(), AccountSession.Service,
                usage.ReloadAsync, action => DispatcherQueue.TryEnqueue(() => action()));
            var dialog = new EntitlementRedemptionDialog(viewModel) { XamlRoot = XamlRoot };
            _entitlementRedemptionDialog = dialog;
            viewModel.CloseRequested += dialog.Hide;
            try { await dialog.ShowThemedAsync(this); }
            finally { viewModel.CloseRequested -= dialog.Hide; }
        }
        catch (Exception exception)
        {
            IDVBuff.Diagnostics.OutputLog.Write("ERROR", "ACCOUNT", "Open entitlement redemption failed.", exception);
        }
        finally { _entitlementRedemptionDialog = null; _entitlementRedemptionOpen = false; }
    }

    private void ReleaseAccountUsage()
    {
        CloseAccountMenu();
        _sponsorshipPlanDialog?.Hide();
        _entitlementRedemptionDialog?.Hide();
        if (_accountUsage is { } usage)
        {
            usage.PlansRequested -= AccountUsage_PlansRequested;
            usage.CloseRequested -= CloseAccountMenu;
            usage.Dispose();
        }
        _accountUsage = null;
    }
}
