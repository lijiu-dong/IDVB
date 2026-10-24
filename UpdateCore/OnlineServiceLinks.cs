namespace IDVBuff.UpdateCore;

public static partial class OnlineServiceLinks
{
    public static Uri? TryResolve(string operation, params (string Name, string Value)[] parameters)
    {
        Uri? result = null;
        ResolvePrivate(operation, parameters, ref result);
        return result;
    }
    public static string? TryCommunityAsset(string path)
    {
        string? result = null;
        ResolveCommunityAssetPrivate(path, ref result);
        return result;
    }
    static partial void ResolvePrivate(string operation, (string Name, string Value)[] parameters, ref Uri? result);
    static partial void ResolveCommunityAssetPrivate(string path, ref string? result);
}
