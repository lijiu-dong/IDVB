using System.Text.Json;
using IDVBuff.Core.Contracts.Online;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Runtime classifier inputs come from the authorized IDVA, never local defaults.</summary>
internal sealed record NativeRecognitionProfile(Scalar RoomLow, Scalar RoomHigh, Scalar Room2Low, Scalar Room2High,
    Scalar CorridorLow, Scalar CorridorHigh, double CannyLow, double CannyHigh)
{
    private static readonly object Sync = new();
    private static string? _version;
    private static NativeRecognitionProfile? _profile;
    internal static NativeRecognitionProfile Current
    {
        get
        {
            var version = RecognitionResources.Version;
            lock (Sync)
            {
                if (_profile is not null && _version == version) return _profile;
                using var bytes = new RecognitionAssetReader.Bytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Algorithms", "structure-native-observed-v2.idva"));
                using var document = JsonDocument.Parse(bytes.Data);
                var root = document.RootElement;
                if (root.GetProperty("algorithm_id").GetString() != "structure.native-observed.v2") throw new InvalidDataException("识别算法身份无效。");
                var parameters = root.GetProperty("parameters");
                var room = parameters.GetProperty("room"); var corridor = parameters.GetProperty("corridor");
                static (int Low, int High) Range(JsonElement element, int maximum)
                {
                    if (element.GetArrayLength() != 2) throw new InvalidDataException("识别范围无效。");
                    var low = element[0].GetInt32(); var high = element[1].GetInt32();
                    if (low < 0 || high < low || high > maximum) throw new InvalidDataException("识别范围无效。");
                    return (low, high);
                }
                var hues = room.GetProperty("hue_ranges");
                if (hues.GetArrayLength() != 2) throw new InvalidDataException("识别色域无效。");
                var h = Range(hues[0], 179); var h2 = Range(hues[1], 179);
                var s = Range(room.GetProperty("saturation_range"), 255); var v = Range(room.GetProperty("value_range"), 255);
                var ch = Range(corridor.GetProperty("hue_range"), 179);
                var cs = Range(corridor.GetProperty("saturation_range"), 255); var cv = Range(corridor.GetProperty("value_range"), 255);
                var fog = parameters.GetProperty("fog_frontier");
                var low = fog.GetProperty("canny_low").GetDouble(); var high = fog.GetProperty("canny_high").GetDouble();
                if (!double.IsFinite(low + high) || low < 1 || high <= low || high > 1024) throw new InvalidDataException("识别边缘参数无效。");
                var profile = new NativeRecognitionProfile(new(h.Low, s.Low, v.Low), new(h.High, s.High, v.High),
                    new(h2.Low, s.Low, v.Low), new(h2.High, s.High, v.High), new(ch.Low, cs.Low, cv.Low), new(ch.High, cs.High, cv.High), low, high);
                RecognitionResources.Demand();
                _profile = profile; _version = version;
                return profile;
            }
        }
    }
}
