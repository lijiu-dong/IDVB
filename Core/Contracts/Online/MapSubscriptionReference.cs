namespace IDVBuff.Core.Contracts.Online;

/// <summary>Keyless publication identity. Resolving it requires an explicitly configured grant protocol.</summary>
public sealed record MapSubscriptionReference(Guid PublicationId, string PublisherKeyId);
