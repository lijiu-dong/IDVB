using System.ComponentModel;
using IDVBuff.Features.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class EntitlementRedemptionDialog : ContentDialog
{
    private readonly EntitlementRedemptionViewModel _viewModel;

    internal EntitlementRedemptionDialog(EntitlementRedemptionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        DefaultButton = ContentDialogButton.Primary;
        Opened += (_, _) => CodeInput.Focus(FocusState.Programmatic);
        _viewModel.PropertyChanged += ViewModelChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= ViewModelChanged;
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args) =>
        ResultInfo.Severity = _viewModel.IsError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    private async void Redeem_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await _viewModel.RedeemAsync();
    }
}
