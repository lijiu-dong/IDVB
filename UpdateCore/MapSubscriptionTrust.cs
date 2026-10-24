// Public contract and unavailable implementation. Private sources are selected by the build.
namespace IDVBuff.UpdateCore;

public static class MapSubscriptionTrust
{
    public static string LoadOfficialPublicKey(string applicationBaseDirectory)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
