using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private readonly ToggleSwitch _diagnosticModeToggle = new()
    {
        Header = "诊断模式",
        OffContent = "已关闭",
        OnContent = "按对局收集配准图"
    };

    private async void DiagnosticModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        var diagnosticMode = _diagnosticModeToggle.IsOn;
        try
        {
            if (!diagnosticMode
                && !await ConfirmDataCleanupAsync(
                    "关闭诊断模式",
                    "关闭后会清理诊断模式文件夹中的全部图片，此操作不可恢复。"))
            {
                RestoreToggle(_diagnosticModeToggle);
                return;
            }
            await _runtime.SetDiagnosticModeAsync(diagnosticMode);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }
}
