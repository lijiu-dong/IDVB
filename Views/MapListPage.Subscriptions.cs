using IDVBuff.UpdateCore;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private async Task<string?> PickPublicationFolderAsync()
    {
        try
        {
            var picker = new FolderPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "发布到此文件夹"
            };
            var result = await picker.PickSingleFolderAsync();
            return result is null || string.IsNullOrWhiteSpace(result.Path) ? null : result.Path;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开文件夹选择器", exception.Message);
            return null;
        }
    }

    private static string BuildDefaultPublisherHandle()
    {
        var configuredOfficialKeyPath = Environment.GetEnvironmentVariable(
            "IDVB_MAP_OFFICIAL_PRIVATE_KEY_PATH");
        var officialKeyPaths = new[]
        {
            configuredOfficialKeyPath,
            Path.Combine(
                Directory.GetCurrentDirectory(),
                ".secrets",
                "idvb-update-2026-01-private.pem")
        };
        if (officialKeyPaths.Any(path =>
                !string.IsNullOrWhiteSpace(path) && File.Exists(path)))
            return MapSubscriptionProtocol.OfficialPublisherHandle;

        var name = new string(Environment.UserName.Select(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray());
        return "@" + (string.IsNullOrWhiteSpace(name) ? "publisher" : name);
    }
}
