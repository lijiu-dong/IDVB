namespace IDVBuff.Lifecycle;

internal sealed class UpdateShutdownServer : IAsyncDisposable
{
    public UpdateShutdownServer(Action requestShutdown) { }
    public void Start() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
