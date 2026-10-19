using IDVBuff.Core.Contracts.Online.Security;

namespace IDVBuff.Services.Online;

public sealed class UnavailableDesktopSecurityRuntime : IDesktopSecurityRuntime
{
    public DesktopSecurityState State { get; } = new(false, true, 0, "not-configured");
    public void Start() { }
    public Task SynchronizeAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public void Dispose() { }
}
