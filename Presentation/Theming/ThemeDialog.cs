using IDVBuff.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Presentation.Theming;

internal static class ThemeDialog
{
    // WinUI permits one ContentDialog per UI thread, including separate windows.
    // Reject a competing click before allocating its theme resource dictionary.
    private sealed class DialogSlot { public ContentDialog? Active; }
    [ThreadStatic] private static DialogSlot? _threadSlot;

    public static async Task<ContentDialogResult> ShowThemedAsync(
        this ContentDialog dialog, FrameworkElement? owner = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        owner ??= dialog.XamlRoot?.Content as FrameworkElement
            ?? throw new InvalidOperationException("主题对话框需要所属窗口的 XamlRoot。");
        var slot = _threadSlot ??= new DialogSlot();
        if (slot.Active is not null) return ContentDialogResult.None;
        slot.Active = dialog;
        try
        {
            // Explicitly resolve the native template before ShowAsync for WinUI #8476
            // (missing entrance animation). Keep caller-provided styles intact.
            if (dialog.Style is null)
                dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
            using var scope = ThemeService.AttachRegion(dialog, ThemeService.ProfileFor(owner));
            // ContentDialog initializes its background before entering the popup tree.
            // Bind its container explicitly; its native children use the local dictionary.
            dialog.Background = scope.Resources[ThemeToken.Dialog];
            dialog.Foreground = scope.Resources[ThemeToken.Text];
            dialog.BorderBrush = scope.Resources[ThemeToken.SurfaceBorder];
            var dispatcher = dialog.DispatcherQueue;
            using var cancellation = cancellationToken.Register(() =>
                dispatcher.TryEnqueue(() => dialog.Hide()));
            var result = await dialog.ShowAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (ReferenceEquals(slot.Active, dialog)) slot.Active = null;
        }
    }
}
