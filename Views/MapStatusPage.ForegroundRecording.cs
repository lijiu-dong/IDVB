using IDVBuff.Features.Maps;
using Microsoft.UI.Dispatching;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private DispatcherQueueTimer? _foregroundRecordingTimer;
    private ForegroundBindingRecorder? _foregroundRecorder;

    // While recording, also accept a press made after switching to the game.
    // Per-application mouse mappings (G HUB etc.) only apply there, so this
    // records the key the game and the global input hooks actually receive.
    private void StartForegroundRecording()
    {
        StopForegroundRecording();
        _foregroundRecorder = new ForegroundBindingRecorder(ForegroundInputState.IsKeyDown);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(10);
        timer.Tick += ForegroundRecordingTimer_Tick;
        _foregroundRecordingTimer = timer;
        timer.Start();
    }

    private void StopForegroundRecording()
    {
        if (_foregroundRecordingTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= ForegroundRecordingTimer_Tick;
        }
        _foregroundRecordingTimer = null;
        _foregroundRecorder = null;
    }

    private async void ForegroundRecordingTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_recording is null || _foregroundRecorder is not { } recorder)
        {
            StopForegroundRecording();
            return;
        }
        if (recorder.Observe(ForegroundInputState.IsOtherApplicationForeground()) is not { } binding)
            return;
        StopForegroundRecording();
        await SaveRecordedBindingAsync(binding);
    }
}
