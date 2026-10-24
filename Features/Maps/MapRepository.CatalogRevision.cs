using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private async Task<MapCatalogRevision?> CommitCatalogReplacementAsync(string temporary, byte[]? expectedHash = null)
    {
        byte[] writtenHash;
        await using (var writtenFile = File.Open(temporary, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete))
        {
            writtenHash = expectedHash ?? await SHA256.HashDataAsync(writtenFile);
            File.Move(temporary, CatalogPath, overwrite: true);
        }
        // Closing the last handle commits NTFS's rename/close journal record.
        // Verify the published bytes before associating any cached state with
        // that final revision. A replacement cannot occur while reading it.
        var verified = false;
        var writtenRevision = default(MapCatalogRevision);
        await using (var publishedFile = File.OpenRead(CatalogPath))
        {
            var publishedHash = await SHA256.HashDataAsync(publishedFile);
            if (CryptographicOperations.FixedTimeEquals(writtenHash, publishedHash))
            {
                writtenRevision = ReadCatalogFileRevision(publishedFile);
                verified = true;
            }
        }
        return verified && GetCatalogRevision() == writtenRevision ? writtenRevision : null;
    }

    private static MapCatalogRevision ReadCatalogFileRevision(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Metadata access does not conflict with an exclusive read handle.
            // The journal distinguishes committed changes whose timestamps
            // and lengths happen to be identical.
            using var handle = CatalogRevisionNative.Open(path, 0x80, FileShare.Read | FileShare.Delete,
                IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) return MapCatalogRevision.Empty;
                throw new IOException("无法读取地图目录修订。", new Win32Exception(error));
            }
            if (TryReadJournalRevision(handle, out var revision)) return revision;
        }
        else if (!File.Exists(path)) return MapCatalogRevision.Empty;

        // Some NTFS volumes have no journal, and network shares do not expose
        // one. Equal timestamps and lengths are not evidence of equal bytes.
        using var stream = File.OpenRead(path);
        return ReadCatalogFileRevision(stream);
    }

    private static MapCatalogRevision ReadCatalogFileRevision(FileStream stream)
    {
        if (OperatingSystem.IsWindows() && TryReadJournalRevision(stream.SafeFileHandle, out var revision))
            return revision;
        var position = stream.Position;
        try
        {
            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            var written = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
            return new(written, stream.Length) { ContentVersion = "sha256:" + hash };
        }
        finally { stream.Position = position; }
    }

    private static bool TryReadJournalRevision(SafeFileHandle handle, out MapCatalogRevision revision)
    {
        revision = default;
        var record = new byte[512];
        if (!CatalogRevisionNative.ReadUsn(handle, 0x000900EB, IntPtr.Zero, 0, record,
                (uint)record.Length, out var returned, IntPtr.Zero) || returned < 32)
            return false;
        var version = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
        var offset = version switch { 2 => 24, 3 => 40, _ => -1 };
        if (offset < 0 || returned < offset + sizeof(long)) return false;
        var usn = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(offset));
        // A successful query can return zero when the volume journal is off.
        if (usn <= 0) return false;
        if (!CatalogRevisionNative.ReadInformation(handle, out var info))
            throw new IOException("无法读取地图目录文件信息。", new Win32Exception(Marshal.GetLastWin32Error()));
        var fileIdLength = version == 2 ? 8 : 16;
        var identity = Convert.ToHexString(record.AsSpan(8, fileIdLength));
        var written = DateTime.FromFileTimeUtc(((long)info.Written.dwHighDateTime << 32)
            | (uint)info.Written.dwLowDateTime).Ticks;
        var length = ((long)info.SizeHigh << 32) | info.SizeLow;
        revision = new(written, length)
        {
            ContentVersion = $"usn:{info.Volume:X8}:{identity}:{usn:X16}"
        };
        return true;
    }

    private static class CatalogRevisionNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Information
        {
            public uint Attributes;
            public FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle Open(string path, uint access, FileShare share, IntPtr security,
            FileMode mode, uint flags, IntPtr template);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadUsn(SafeFileHandle handle, uint operation, IntPtr input, uint inputLength,
            [Out] byte[] output, uint outputLength, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadInformation(SafeFileHandle handle, out Information info);
    }
}
