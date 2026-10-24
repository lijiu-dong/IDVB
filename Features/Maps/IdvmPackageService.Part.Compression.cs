using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private const string SolidCompression = "brotli-solid-v1";
    private const string SolidPayloadPath = "payload.br";

    private sealed class ManifestFileDto
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public long? StorageOffset { get; set; }
    }

    private static void ValidateExportLimits(ManifestDto manifest, long manifestLength = 0)
    {
        if (manifest.Files.Count is < 1 or > MaxEntries - 2)
            throw new InvalidDataException("IDVM 逻辑文件数量超出限制。");
        long total = HeaderSize + manifestLength;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null)
                throw new InvalidDataException("IDVM 文件清单不能包含空项。");
            ValidateLogicalPath(file.Path);
            if (!file.Path.StartsWith("maps/", StringComparison.Ordinal) || !paths.Add(file.Path)
                || file.Size < 0 || file.Size > MaxSingleFileBytes || !IsSha256(file.Sha256))
                throw new InvalidDataException($"IDVM 文件清单无效：{file.Path}");
            total = checked(total + file.Size);
            if (total > MaxExpandedBytes)
                throw new InvalidDataException("IDVM 逻辑展开大小超过 512 MiB。");
            if (file.Path.EndsWith(".json", StringComparison.Ordinal) && file.Size > MaxJsonBytes)
                throw new InvalidDataException($"IDVM JSON 超过 8 MiB：{file.Path}");
        }
    }

    private static async Task WriteSolidPayloadAsync(string root, ManifestDto manifest, CancellationToken token)
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Idvm.ExportCompression", "brotli-quality-11-window-17");
        var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request("Idvm.BrotliCompressor", nativeModule: "System.IO.Compression.Native.dll");
        using var ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own("Idvm.BrotliCompressor",
            nativeModule: "System.IO.Compression.Native.dll", requestId: request);
        await using var output = File.Create(ToPhysicalPath(root, SolidPayloadPath));
        // Solid compression shares a dictionary across similar assets; byte-identical
        // images/IDVA files shared by maps and floors are stored exactly once.
        // Retain quality 11 with a bounded 128KiB history window. The preserved
        // real payload uses ~46MiB less native peak memory for ~3% more bytes;
        // the standard Brotli stream and all original asset bytes are preserved.
        using var encoder = new BrotliEncoder(quality: 11, window: 17);
        var inputBuffer = new byte[65536];
        var outputBuffer = new byte[65536];
        long offset = 0;
        var groups = manifest.Files.GroupBy(file => (file.Sha256, file.Size))
            .OrderBy(group => Path.GetExtension(group.First().Path), StringComparer.Ordinal)
            .ThenBy(group => group.Key.Sha256, StringComparer.Ordinal).ToArray();
        var total = groups.Sum(group => group.Key.Size);
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            foreach (var file in group)
                file.StorageOffset = offset;
            await using var source = File.OpenRead(ToPhysicalPath(root, group.First().Path));
            int read;
            while ((read = await source.ReadAsync(inputBuffer, token)) != 0)
            {
                var consumedOffset = 0;
                OperationStatus status;
                do
                {
                    token.ThrowIfCancellationRequested();
                    status = encoder.Compress(inputBuffer.AsSpan(consumedOffset, read - consumedOffset), outputBuffer,
                        out var consumed, out var written, isFinalBlock: false);
                    if (status == OperationStatus.InvalidData
                        || consumed == 0 && written == 0 && consumedOffset < read)
                        throw new InvalidDataException("IDVM Brotli 压缩无法继续处理数据。");
                    consumedOffset += consumed;
                    await output.WriteAsync(outputBuffer.AsMemory(0, written), token);
                } while (consumedOffset < read || status == OperationStatus.DestinationTooSmall);
            }
            offset = checked(offset + group.Key.Size);
            MapOperationProgress.Report(total == 0 ? 1 : (double)offset / total, "正在压缩地图……");
        }
        OperationStatus finalStatus;
        do
        {
            token.ThrowIfCancellationRequested();
            finalStatus = encoder.Compress(ReadOnlySpan<byte>.Empty, outputBuffer,
                out _, out var written, isFinalBlock: true);
            if (finalStatus == OperationStatus.InvalidData
                || written == 0 && finalStatus != OperationStatus.Done)
                throw new InvalidDataException("IDVM Brotli 压缩未完整结束。");
            await output.WriteAsync(outputBuffer.AsMemory(0, written), token);
        } while (finalStatus != OperationStatus.Done);
    }

    private static async Task RestoreSolidPayloadAsync(string root, ManifestDto manifest, CancellationToken token)
    {
        var physicalFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => ToLogicalPath(root, path)).ToHashSet(StringComparer.Ordinal);
        if (!physicalFiles.SetEquals(new[] { "header", "manifest.json", SolidPayloadPath }))
            throw new InvalidDataException("IDVM 1.6 必须且只能包含 header、manifest.json 和 payload.br。");
        if (manifest.Files.Any(file => file.StorageOffset is null or < 0))
            throw new InvalidDataException("IDVM 1.6 缺少有效存储偏移。");
        var groups = manifest.Files.GroupBy(file => (file.StorageOffset, file.Size, file.Sha256))
            .OrderBy(group => group.Key.StorageOffset).ThenBy(group => group.Key.Size).ToArray();
        long length = 0;
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group.Key.StorageOffset != length || !hashes.Add(group.Key.Sha256))
                throw new InvalidDataException("IDVM 压缩数据存在重叠、空洞或不一致的去重引用。");
            length = checked(length + group.Key.Size);
        }
        var expandedPath = Path.Combine(root, "payload.expanded");
        try
        {
            await using (var input = File.OpenRead(ToPhysicalPath(root, SolidPayloadPath)))
            await using (var output = new FileStream(expandedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await DecodeSolidAsync(input, output, length, token);
            await using var expanded = File.OpenRead(expandedPath);
            var buffer = new byte[81920];
            foreach (var group in groups)
            {
                var first = group.First();
                var target = ToPhysicalPath(root, first.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    long remaining = first.Size;
                    while (remaining > 0)
                    {
                        var read = await expanded.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                        if (read == 0) throw new InvalidDataException("IDVM 压缩数据被截断。");
                        remaining -= read;
                        hash.AppendData(buffer.AsSpan(0, read));
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }
                if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), first.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"IDVM 解压文件摘要不匹配：{first.Path}");
                foreach (var file in group.Skip(1))
                    await CopyFileAsync(target, ToPhysicalPath(root, file.Path), token);
            }
            File.Delete(ToPhysicalPath(root, SolidPayloadPath));
        }
        finally
        {
            if (File.Exists(expandedPath)) File.Delete(expandedPath);
        }
    }

    // BrotliStream alone does not provide an explicit "complete stream" result.
    // Require Done and exact input/output lengths to reject truncation and suffixes.
    private static async Task DecodeSolidAsync(Stream input, Stream output, long expected, CancellationToken token)
    {
        var source = new byte[81920];
        var target = new byte[81920];
        using var decoder = new BrotliDecoder();
        int start = 0, count = 0;
        long total = 0;
        var needInput = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (needInput)
            {
                source.AsSpan(start, count - start).CopyTo(source);
                count -= start;
                start = 0;
                var read = await input.ReadAsync(source.AsMemory(count), token);
                if (read == 0) throw new InvalidDataException("IDVM Brotli 数据不完整。");
                count += read;
            }
            var status = decoder.Decompress(source.AsSpan(start, count - start), target, out var consumed, out var written);
            start += consumed;
            total = checked(total + written);
            if (total > expected) throw new InvalidDataException("IDVM Brotli 展开大小超过声明值。");
            await output.WriteAsync(target.AsMemory(0, written), token);
            MapOperationProgress.Report(expected == 0 ? 1 : .7 * total / expected, "正在解压地图……");
            if (status == OperationStatus.Done)
            {
                if (total != expected || start != count || input.ReadByte() != -1)
                    throw new InvalidDataException("IDVM Brotli 数据长度不一致或包含尾随数据。");
                return;
            }
            if (status == OperationStatus.InvalidData || (consumed == 0 && written == 0 && count == source.Length))
                throw new InvalidDataException("IDVM Brotli 数据无效。");
            needInput = status == OperationStatus.NeedMoreData;
        }
    }
}
