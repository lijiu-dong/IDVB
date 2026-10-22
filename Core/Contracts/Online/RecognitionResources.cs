using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("IDVB")]
[assembly: InternalsVisibleTo("IDVB.Test")]
[assembly: InternalsVisibleTo("IDVB.RealCLI")]
[assembly: InternalsVisibleTo("IdentityVisionBridge.Vision")]
[assembly: InternalsVisibleTo("IDVBuff.WinUI.RegressionTests")]

namespace IDVBuff.Core.Contracts.Online;

// Host-private port: resource bytes and keys are deliberately absent from the plugin contracts.
internal interface IRecognitionResourceSession
{
    string Version { get; }
    void Demand();
    byte[] Read(string name);
}

internal static class RecognitionResources
{
    private static Func<IRecognitionResourceSession?>? _current;
    internal static void Bind(Func<IRecognitionResourceSession?> current)
    {
        if (Interlocked.CompareExchange(ref _current, current, null) is not null)
            throw new InvalidOperationException("识别资源宿主已经绑定。");
    }

    internal static void Demand()
    {
#if IDVB_PRIVATE_ONLINE_SERVICES
        (Volatile.Read(ref _current)?.Invoke() ?? throw new RecognitionAuthorizationException()).Demand();
#endif
    }

    internal static string Version
    {
        get
        {
#if IDVB_PRIVATE_ONLINE_SERVICES
            var session = Volatile.Read(ref _current)?.Invoke() ?? throw new RecognitionAuthorizationException();
            session.Demand();
            return session.Version;
#else
            return "local";
#endif
        }
    }

    internal static bool IsProtected(string path) => Name(path) is not null;
    private static string? Name(string path) => Path.GetFileName(path) switch
    {
        "Gate.png" or "1F.png" or "2F.png" or "player_01.png" or "player_02.png" or "player_03.png" or "player_04.png"
            => "Assets/" + Path.GetFileName(path),
        "hard-1f.png" or "hard-2f.png" or "nightmare-1f.png" or "nightmare-2f.png" or "nightmare-b1f.png" or "registry.json"
            => "Assets/FloorIndicators/" + Path.GetFileName(path),
        "structure-doom-girl-hard-wangqingxin-v1.idva" or "structure-native-observed-v2.idva"
            or "structure-semantic-contours.idva" or "structure-zongcai-native-reference-v1.idva"
            => "Assets/Algorithms/" + Path.GetFileName(path),
        _ => null
    };

    internal static byte[] ReadAllBytes(string path)
    {
#if IDVB_PRIVATE_ONLINE_SERVICES
        if (Name(path) is { } name)
        {
            var session = Volatile.Read(ref _current)?.Invoke() ?? throw new RecognitionAuthorizationException();
            session.Demand();
            return session.Read(name);
        }
#endif
        return File.ReadAllBytes(path);
    }
}

internal sealed class RecognitionAuthorizationException : InvalidOperationException
{
    internal RecognitionAuthorizationException() : base("当前无法启用") { }
}
