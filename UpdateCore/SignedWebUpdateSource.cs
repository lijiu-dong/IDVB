// Public contract and unavailable implementation. Private sources are selected by the build.
using System.Net.Http;
using System.Security.Cryptography;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace IDVBuff.UpdateCore;

/// <summary>
/// Velopack source backed by an IDVB ECDSA-signed envelope. The feed is never
/// handed to Velopack until its signature, channel, package id, and asset paths
/// have been validated against the embedded trust root.
/// </summary>
public sealed class SignedWebUpdateSource : IUpdateSource, IDisposable
{

    public SignedWebUpdateSource(
        Uri updateRoot,
        string expectedChannel,
        IUpdateFeedVerifier verifier,
        HttpClient? httpClient = null)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public UpdateFeedPayload? LastVerifiedPayload { get; private set; }

    public  Task<VelopackAssetFeed> FetchVerifiedFeedAsync(
        CancellationToken cancellationToken = default)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public  Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public  Task DownloadReleaseEntry(
        IVelopackLogger logger,
        VelopackAsset releaseEntry,
        string localFile,
        Action<int> progress,
        CancellationToken cancelToken = default)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public void Dispose()
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
