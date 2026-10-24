using IDVBuff.Core.Contracts.Online;
using IDVBuff.Features.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MainSettingsPage
{
    private Border CreateClearAccountUsageCard()
    {
        var layout = new Grid { MinHeight = 86, Padding = new Thickness(26, 15, 24, 15), ColumnSpacing = 20 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = "清空当前账户额度", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        labels.Children.Add(new TextBlock
        {
            Text = "清空当前登录账户的全部免费点数、点数、今日剩余时长和剩余额外时长。时长仍按原周期重置，操作不可撤销。",
            FontSize = 14, Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap
        });
        var button = new Button
        {
            Content = "清空全部额度", VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(14, 7, 14, 7), CornerRadius = new CornerRadius(6)
        };
        ThemeButton.Apply(button, IDVBuff.Appearance.ThemeButtonRole.Danger);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await ClearAccountUsageAsync(); }
            finally { button.IsEnabled = true; }
        };
        layout.Children.Add(labels);
        Grid.SetColumn(button, 1);
        layout.Children.Add(button);
        return new Border
        {
            Background = FluentTheme.CardBrush(this), BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = layout
        };
    }

    private async Task ClearAccountUsageAsync()
    {
        var account = AccountSession.Service.Capture();
        if (account.Identity is null || account.Token is null)
        {
            await ShowUsageClearMessageAsync("无法清空额度", "请先登录要清空额度的账户。");
            return;
        }
        if (DesktopUsageMeterHost.Meter is not IUsageQuotaClearing clearing)
        {
            await ShowUsageClearMessageAsync("功能不可用", "当前构建未配置账户额度清空服务。");
            return;
        }
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "清空当前账户的全部额度？",
            Content = $"当前账户：{account.Identity.DisplayName}（{account.Identity.PublisherHandle}）\n\n免费点数和点数将归零，今日剩余时长和剩余额外时长将耗尽，正在计时的会话将结束。\n\n仅影响此账户，订阅档位与到期时间不变，时长按原周期重置。此操作不可撤销。",
            PrimaryButtonText = "确认清空", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowThemedAsync() != ContentDialogResult.Primary) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await clearing.ClearCurrentAccountAsync(account, timeout.Token);
            await ShowUsageClearMessageAsync(result.Succeeded ? "额度已清空" : "清空未完成",
                result.Succeeded ? "当前账户的免费点数、点数、今日剩余时长和剩余额外时长已全部清空。"
                    : result.Message ?? "请刷新账户额度后重试。");
        }
        catch (OperationCanceledException)
        {
            await ShowUsageClearMessageAsync("未能确认清空结果", "请求超时或账户已切换，请刷新账户额度以确认结果。");
        }
        catch (Exception error)
        {
            await ShowUsageClearMessageAsync("清空未完成", error.Message);
        }
    }

    private Task<ContentDialogResult> ShowUsageClearMessageAsync(string title, string message) => new ContentDialog
    {
        XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "知道了"
    }.ShowThemedAsync();
}
