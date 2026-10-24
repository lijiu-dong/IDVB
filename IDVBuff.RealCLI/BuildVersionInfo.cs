namespace IDVBuff;

internal static partial class BuildVersionInfo
{
    public const string BuildVersion = "RealCLI";
    public static string NumericVersion => ProductVersion.Split('-')[0] + ".0";
}
