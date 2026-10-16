using Microsoft.UI.Xaml;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private FeedbackDialog? _feedbackDialog;
    private bool _feedbackOpen;

    private async void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_feedbackOpen) return;
        _feedbackOpen = true;
        try
        {
            if (_feedbackDialog is null)
            {
                var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request("UI.Feedback.Dialog", nativeModule: "Microsoft.UI.Xaml.dll");
                _feedbackDialog = new FeedbackDialog(XamlRoot);
                IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(_feedbackDialog, "UI.Feedback.Dialog", null,
                    "Microsoft.UI.Xaml.dll", request);
                IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(_feedbackDialog, "UI.Feedback.Dialog");
            }
            var dialog = _feedbackDialog;
            if (!dialog.ResetForOpen()) return;
            dialog.XamlRoot = XamlRoot;
            using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("UI.Feedback.Open", "feedback");
            await dialog.ShowThemedAsync();
        }
        catch (Exception exception)
        {
            // 防御性捕获，防止多次点击或 WinUI 并发对话框限制引发未处理异常
            System.Diagnostics.Debug.WriteLine($"[Feedback] 打开反馈对话框失败: {exception.Message}");
        }
        finally
        {
            _feedbackOpen = false;
            if (!IsLoaded) ReleaseFeedbackDialog();
        }
    }

    private void ReleaseFeedbackDialog()
    {
        if (_feedbackDialog is null) return;
        IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(_feedbackDialog);
        _feedbackDialog = null;
    }
}
