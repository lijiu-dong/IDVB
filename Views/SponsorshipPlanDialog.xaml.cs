using IDVBuff.Features.Accounts;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class SponsorshipPlanDialog : ContentDialog
{
    internal SponsorshipPlanDialog(AccountUsageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
