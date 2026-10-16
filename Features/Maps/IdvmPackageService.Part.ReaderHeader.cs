using System.Buffers.Binary;
using System.Text.Json;
namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private readonly record struct ParsedHeader(
        ushort MajorVersion,
        ushort MinorVersion,
        Guid PackageId,
        long CreatedAtUnixMilliseconds,
        byte[] ManifestSha256);

    private static ParsedHeader ParseHeader(byte[] bytes)
    {
        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
        if (!bytes.AsSpan(0, 4).SequenceEqual("IDVM"u8)
            || major != 1
            || minor is not (0 or 1 or 2 or 3 or 4 or 5 or 6)
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)) != HeaderSize
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2)) != 0
            || bytes.AsSpan(68, 12).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException("IDVM header 的 magic、版本、flags 或保留字段无效。");
        }
        return new ParsedHeader(
            major,
            minor,
            ReadRfc4122Guid(bytes.AsSpan(12, 16)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(28, 8)),
            bytes.AsSpan(36, 32).ToArray());
    }

    private static Guid ReadRfc4122Guid(ReadOnlySpan<byte> source)
    {
        var hex = Convert.ToHexString(source);
        return Guid.ParseExact(hex, "N");
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task CopyStreamLimitedAsync(
        Stream input,
        Stream output,
        long expectedLength,
        CancellationToken cancellationToken,
        long maximumLength = MaxSingleFileBytes)
    {
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            written += read;
            if (written > expectedLength || written > maximumLength)
                throw new InvalidDataException("ZIP 条目实际展开大小超过声明值。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (written != expectedLength)
            throw new InvalidDataException("ZIP 条目实际大小与声明值不一致。");
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(
        string path,
        long maximum,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > maximum)
            throw new InvalidDataException($"JSON 文件超过 {maximum} 字节限制：{Path.GetFileName(path)}");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"IDVM 包缺少 {Path.GetFileName(path)}。");
        return Deserialize<T>(
            await ReadLimitedBytesAsync(path, MaxJsonBytes, cancellationToken),
            Path.GetFileName(path));
    }

    private static T Deserialize<T>(byte[] bytes, string name)
    {
        try
        {
            using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 }))
                ValidateJsonShape(document.RootElement, name);
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new InvalidDataException($"{name} 不能为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{name} 不是有效的 IDVM JSON。", exception);
        }
    }

}
