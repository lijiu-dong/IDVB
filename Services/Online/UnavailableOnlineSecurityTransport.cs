using IDVBuff.Core.Contracts.Online;
using IDVBuff.Core.Contracts.Online.Security;

namespace IDVBuff.Services.Online;

public sealed class UnavailableOnlineSecurityTransport : IOnlineSecurityTransport
{
    public bool IsConfigured => false;
    public ValueTask<IUnverifiedSecurityResponseLease> SendAsync(SecurityTransportRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<IUnverifiedSecurityResponseLease>(new OnlineServiceUnavailableException(request.Operation.Id()));
}
