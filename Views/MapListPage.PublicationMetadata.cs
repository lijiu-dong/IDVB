using IDVBuff.Features.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private async Task ShowPublicationMetadataDialogAsync(Button importButton, Button publishButton)
    {
        if (_packagePromptBusy || _isPackageOperation) return;
        _packagePromptBusy = true;
        var uploaded = false;
        try
        {
            var identity = await _accountService.RequirePublishAccessAsync();
            var previous = GetOwnedPublication()
                ?? throw new InvalidOperationException("请选择当前账号已发布的地图类。");
            var name = previous.PackageName ?? _selectedClass;
            string? coverPath = null;
            while (true)
            {
                var nameInput = new TextBox { Header = "地图包名称", Text = name, MaxLength = 80 };
                var content = new StackPanel { Spacing = 10, MaxWidth = 560 };
                content.Children.Add(nameInput);
                content.Children.Add(new TextBlock { Text = coverPath is null
                    ? "封面：保持官网当前封面" : $"新封面：{Path.GetFileName(coverPath)}", TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new TextBlock { Text = "这里只修改社区名称和封面。本地地图内容的改动，请使用“更新到官网”发布。",
                    TextWrapping = TextWrapping.Wrap });
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "修改社区地图包资料", Content = content,
                    PrimaryButtonText = "保存资料", SecondaryButtonText = "选择封面", CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary };
                var result = await dialog.ShowThemedAsync(this);
                name = nameInput.Text.Trim();
                if (result == ContentDialogResult.None) return;
                if (result == ContentDialogResult.Secondary)
                {
                    await Task.Delay(250);
                    coverPath = await PickPublicationCoverAsync() ?? coverPath;
                    continue;
                }
                if (name.Length > 0) break;
            }
            SetPackageOperationState(importButton, publishButton, isBusy: true, "正在保存资料…");
            await RunMapOperationAsync("正在修改社区资料……", async () =>
            {
                var update = await _mapPublicationService.PrepareMetadataAsync(previous.Id, identity.PublisherHandle,
                    identity.DisplayName, identity.IsOfficial, identity.IsBuilder, name, coverPath);
                await _communityService.UpdateMetadataAsync(new(update.PublicationId, update.PublisherHandle,
                    update.SubscriptionLink, update.PackageName, update.CoverPath, update.FeedBytes,
                    update.ExpectedFeedSha256), CancellationToken.None);
                uploaded = true;
                await _mapSubscriptionService.RegisterMetadataAsync(update);
                await ShowListAsync();
            });
            await ShowMessageAsync("社区资料已更新", "地图版本和订阅链接保持不变，地图包无需重新上传。");
        }
        catch (Exception exception)
        {
            IDVBuff.Diagnostics.OutputLog.Write("WARN", "MAP/METADATA", "Map publication metadata update did not complete.", exception);
            await ShowMessageAsync(uploaded ? "社区资料已更新，本地登记未完成" : "社区资料修改失败", exception.Message);
        }
        finally
        {
            SetPackageOperationState(importButton, publishButton, isBusy: false, null);
            _packagePromptBusy = false;
        }
    }
}
