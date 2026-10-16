#if IDVB_DEVELOPMENT_BUILD
using IDVBuff.Core.Diagnostics;
using IDVBuff.Diagnostics;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    internal async Task<object> ReplayDevelopmentClicksAsync(int rounds, int burst, CancellationToken cancellation)
    {
        if (!Program.IsDevelopmentInstance || App.IsSafeMode)
            throw new InvalidOperationException("连点回放要求正常模式的完整开发客户端。");
        await App.ServicesReadyTask.WaitAsync(cancellation);
        await InitialReady.WaitAsync(cancellation);
        await ActivateDevelopmentReplayWindowAsync(cancellation);
        var runId = Guid.NewGuid();
        MemoryJournal.Current?.Record("full-client-clicks-start", new { runId, rounds, burst,
            build = BuildVersionInfo.BuildVersion, pid = Environment.ProcessId, fullApplication = true,
            scope = "Real footer ButtonAutomationPeer invocation. Support navigation and feedback cancellation; no feedback submission." }, durable: true);
        var supportButton = PageReplayDescendants(this).OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "支持开发");
        FeedbackDialog? firstDialog = null;
        void Burst(Button button)
        {
            for (var click = 0; click < burst; click++)
            {
                cancellation.ThrowIfCancellationRequested();
                MemoryBudget.Process.ThrowIfExceeded();
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            }
        }
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                await NavigateToAsync("map-status");
                Burst(supportButton);
                await WaitClickReplayAsync(() => ModuleContentHost.Content is SponsorshipPage { IsLoaded: true }, cancellation);
                var support = (SponsorshipPage)ModuleContentHost.Content;
                Burst(supportButton);
                await Task.Delay(50, cancellation);
                if (!ReferenceEquals(support, ModuleContentHost.Content))
                    throw new InvalidOperationException("支持开发连点重新创建了已显示的页面。");
                if ((round & 1) == 0)
                    await WaitClickReplayAsync(() => support.HasVerifiedArtwork && PageReplayDescendants(support).OfType<Image>()
                        .Count(image => image.Source is SoftwareBitmapSource) == 2, cancellation);
                var artwork = PageReplayDescendants(support).OfType<Image>()
                    .Where(image => AutomationProperties.GetName(image).Contains("收款码，收款人", StringComparison.Ordinal)).ToArray();
                if (artwork.Length != 2) throw new InvalidOperationException("实际页面未找到两张具名收款码。");
                await NavigateToAsync("map-status");
                await WaitClickReplayAsync(() => !support.IsLoaded, cancellation);
                if (artwork.Any(image => image.Source is not null))
                    throw new InvalidOperationException("支持开发离开页面后仍持有显示图片。");
                Checkpoint("support-burst-and-leave", round);

                Burst(FeedbackButton);
                await WaitClickReplayAsync(() => _feedbackDialog is { IsLoaded: true } && _feedbackOpen, cancellation);
                var dialog = _feedbackDialog!;
                firstDialog ??= dialog;
                if (!ReferenceEquals(firstDialog, dialog))
                    throw new InvalidOperationException("反馈连点未复用当前主页面的对话框。");
                var description = PageReplayDescendants(dialog).OfType<TextBox>().Single(box => box.AcceptsReturn);
                if (description.Text.Length != 0 || dialog.IsPrimaryButtonEnabled)
                    throw new InvalidOperationException("反馈重新打开未清空内容或意外允许空反馈提交。");
                description.Text = "内存回放取消内容，不应发送。";
                var cancel = PageReplayDescendants(dialog).OfType<Button>()
                    .Single(button => button.Name == "CloseButton" && Equals(button.Content, dialog.CloseButtonText));
                ((IInvokeProvider)new ButtonAutomationPeer(cancel).GetPattern(PatternInterface.Invoke)).Invoke();
                await WaitClickReplayAsync(() => !_feedbackOpen && !dialog.IsLoaded, cancellation);
                Checkpoint("feedback-burst-and-cancel", round);
            }
            return new { runId, rounds, burst, supportInvocations = rounds * burst * 2,
                feedbackInvocations = rounds * burst, feedbackCancellations = rounds,
                reading = ProcessMemoryReader.Read(), scope = "Support and feedback events only; not complete application coverage." };
        }
        finally
        {
            if (_feedbackOpen && !MemoryBudget.Process.Exceeded)
            {
                _feedbackDialog?.Hide();
                await WaitClickReplayAsync(() => !_feedbackOpen, CancellationToken.None);
            }
        }

        void Checkpoint(string action, int iteration) => MemoryJournal.Current?.Record("full-client-click-step",
            new { runId, action, iteration, reading = ProcessMemoryReader.Read(), managedLiveBytes = GC.GetTotalMemory(false) });
    }

    private static async Task WaitClickReplayAsync(Func<bool> ready, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ready())
        {
            cancellation.ThrowIfCancellationRequested();
            MemoryBudget.Process.ThrowIfExceeded();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("客户端连点回放等待状态超时。");
            await Task.Delay(50, cancellation);
        }
    }
}
#endif
