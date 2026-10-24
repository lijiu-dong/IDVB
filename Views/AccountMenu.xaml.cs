using IDVBuff.Appearance;
using IDVBuff.Features.Accounts;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace IDVBuff.Views;

/// <summary>
/// Account menu with a cascading usage panel. Opening the panel keeps the menu open,
/// matching a submenu rather than replacing the menu with a dialog.
/// </summary>
public sealed partial class AccountMenu : UserControl
{
    private readonly AccountUsageViewModel _viewModel;
    private Brush _selectedBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    internal AccountMenu(AccountUsageViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public bool IsUsageOpen => UsageCard.Visibility == Visibility.Visible;
    public event EventHandler? LogoutRequested;
    public event EventHandler? RedeemRequested;
    /// <summary>Raised when Escape is pressed with no submenu left to close.</summary>
    public event EventHandler? DismissRequested;

    internal void ApplyTheme(ThemeResources resources)
    {
        MenuCard.Background = UsageCard.Background = resources[ThemeToken.Flyout];
        MenuCard.BorderBrush = UsageCard.BorderBrush = resources[ThemeToken.SurfaceBorder];
        _selectedBrush = resources[ThemeToken.ControlHover];
        if (IsUsageOpen) UsageItem.Background = _selectedBrush;
    }

    internal void FocusFirstItem(FocusState state) => UsageItem.Focus(state);

    internal void SetUsageOpen(bool open)
    {
        if (open == IsUsageOpen) return;
        UsageCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        UsageItem.ClearValue(BackgroundProperty);
        if (open) UsageItem.Background = _selectedBrush;
        AutomationProperties.SetItemStatus(UsageItem, open ? "已展开" : "");
        // The host refreshes once when the account menu opens. Expanding this submenu
        // consumes that same request and its loading/error state without refreshing again.
    }

    private void UsageItem_Click(object sender, RoutedEventArgs e) => SetUsageOpen(!IsUsageOpen);

    private void LogoutItem_Click(object sender, RoutedEventArgs e) => LogoutRequested?.Invoke(this, EventArgs.Empty);

    private void RedeemItem_Click(object sender, RoutedEventArgs e) => RedeemRequested?.Invoke(this, EventArgs.Empty);

    private void Menu_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Right when ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), UsageItem):
                SetUsageOpen(true);
                if (!_viewModel.IsUsageUnavailable) PlansButton.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
            case VirtualKey.Left when IsUsageOpen:
            case VirtualKey.Escape when IsUsageOpen:
                SetUsageOpen(false);
                UsageItem.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                DismissRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }
}
