// Public contract and unavailable implementation. Private sources are selected by the build.
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.UpdateCore;

public interface IUpdateFeedVerifier
{
    VerifiedUpdateFeed Verify(string envelopeJson, string expectedChannel);
}

public sealed class EcdsaUpdateFeedVerifier : IUpdateFeedVerifier
{

    public EcdsaUpdateFeedVerifier(IReadOnlyDictionary<string, string> publicKeysById)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public VerifiedUpdateFeed Verify(string envelopeJson, string expectedChannel)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}

public sealed class UpdateTrustException : Exception
{
    public UpdateTrustException(string message) : base(message) { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
    public UpdateTrustException(string message, Exception innerException) : base(message, innerException) { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
