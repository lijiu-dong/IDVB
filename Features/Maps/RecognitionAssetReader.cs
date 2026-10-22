using System.Security.Cryptography;
using IDVBuff.Core.Contracts.Online;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class RecognitionAssetReader
{
    internal static Mat Decode(string path, ImreadModes mode)
    {
        using var data = new Bytes(path);
        return Cv2.ImDecode(data.Data, mode);
    }
    internal static bool Exists(string path)
    {
#if IDVB_PRIVATE_ONLINE_SERVICES
        if (RecognitionResources.IsProtected(path)) { RecognitionResources.Demand(); return true; }
#endif
        return File.Exists(path);
    }
    internal sealed class Bytes : IDisposable
    {
        internal Bytes(string path) => Data = RecognitionResources.ReadAllBytes(path);
        internal byte[] Data { get; }
        public void Dispose() => CryptographicOperations.ZeroMemory(Data);
    }
}
