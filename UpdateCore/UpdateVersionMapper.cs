// Public contract and unavailable implementation. Private sources are selected by the build.
using System.Globalization;
using System.Text.RegularExpressions;

namespace IDVBuff.UpdateCore;

public static partial class UpdateVersionMapper
{
    public static string ToVelopackVersion(string publicVersion, string productVersion)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
