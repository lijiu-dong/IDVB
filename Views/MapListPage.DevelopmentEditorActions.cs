#if IDVB_DEVELOPMENT_BUILD
using System.Security.Cryptography;
using IDVBuff.Core.Diagnostics;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private static async Task<bool> MatchesSourceAsync(string path, byte[] expected, CancellationToken cancellation)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellation);
        return expected.AsSpan().SequenceEqual(SHA256.HashData(bytes));
    }

    // Called only for an exact newly-created ID from the data replay receipt.
    // Source maps remain covered by its unchanged-content deletion guard.
    private async Task<object> ReplayCopiedEditorActionsAsync(MapRecord copied, CancellationToken cancellation)
    {
        if (!AppDataPaths.IsMemoryTest || copied.IsCoCreationTemplate)
            throw new InvalidOperationException("Editor action replay requires an isolated ordinary map copy.");
        await EditMapAsync(copied);
        await WaitReplayAsync(() => _modernBitmap is { IsReady: true } && _modernEditorActive, cancellation);
        if (_modernDownsampleButton is not { IsEnabled: true })
            throw new InvalidOperationException("The normal downsample UI must be available on the copied map.");
        var copyRoot = Path.GetFullPath(Path.GetDirectoryName(_repository.GetFloorOnePath(copied))!);
        string FloorPath(string key)
        {
            var full = Path.GetFullPath(GetFloorImagePath(key)
                ?? throw new InvalidOperationException("The copied floor has no image path."));
            if (!full.StartsWith(copyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Editor replay image escaped the receipt-owned map copy.");
            return full;
        }
        var dimensions = _draft!.Floors.OrderBy(f => f.SortOrder)
            .Select(f => (f.Key, Path: FloorPath(f.Key), Size: MapImageDimensions.Read(FloorPath(f.Key))))
            .ToArray();
        var path = dimensions[0].Path;
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path, cancellation));
        var openedOwner = _modernBitmap!;
        var openedPixels = (Windows.Graphics.Imaging.SoftwareBitmap?)typeof(ModernFloorBitmap)
            .GetField("_pixels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(openedOwner);
        if (openedPixels is null) throw new InvalidOperationException("The copied editor must hold real decoded pixels.");

        var pending = ShowModernDownsampleDialogAsync();
        var dialog = await WaitDataDialogAsync("原图降采样（整套地图系列）", cancellation, pending);
        InvokeDataDialog(dialog, "CloseButton");
        await pending.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        if (!ReferenceEquals(_modernBitmap, openedOwner) || openedOwner.IsDisposed
            || !await MatchesSourceAsync(path, before, cancellation))
            throw new InvalidOperationException("Cancelling the downsample dialog changed its pixels or source.");
        MemoryJournal.Current?.Record("full-client-editor-action", new { copied.Id, action = "downsample-cancel", originalUnchanged = true });

        // Reproduce a pending file-owning decode deterministically. The same
        // decode gate and a real read-only file handle must drain before write.
        await _modernImageLoadGate.WaitAsync(cancellation);
        var held = true;
        FileStream? reader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            pending = ShowModernDownsampleDialogAsync();
            dialog = await WaitDataDialogAsync("原图降采样（整套地图系列）", cancellation, pending);
            InvokeDataDialog(dialog, "PrimaryButton");
            await WaitReplayAsync(() => openedOwner.IsDisposed, cancellation);
            await Task.Delay(250, cancellation);
            if (pending.IsCompleted || CurrentDataDialog()?.Title?.ToString() == "降采样失败"
                || !await MatchesSourceAsync(path, before, cancellation))
                throw new InvalidOperationException("Downsample did not wait for the outstanding decode's file ownership.");
            reader.Dispose(); reader = null;
            _modernImageLoadGate.Release(); held = false;
            await pending.WaitAsync(TimeSpan.FromMinutes(2), cancellation);
        }
        finally
        {
            reader?.Dispose();
            if (held) _modernImageLoadGate.Release();
        }
        await WaitReplayAsync(() => _modernBitmap is { IsReady: true }, cancellation);
        foreach (var item in dimensions)
        {
            var actual = MapImageDimensions.Read(FloorPath(item.Key));
            if (actual.Width != Math.Max(1, item.Size.Width / 2) || actual.Height != Math.Max(1, item.Size.Height / 2))
                throw new InvalidOperationException("Downsample output dimensions do not match the selected factor.");
        }
        try
        {
            using var buffer = openedPixels.LockBuffer(Windows.Graphics.Imaging.BitmapBufferAccessMode.Read);
            throw new InvalidOperationException("Retired editor pixels remain open after replacement.");
        }
        catch (ObjectDisposedException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        MemoryJournal.Current?.Record("full-client-editor-action", new { copied.Id, action = "downsample-wait-and-apply", factor = 2, retiredPixelsClosed = true });

        // A failed replacement must keep the original bytes and delete only
        // its own temporary encoding. No file outside this copied map is locked.
        path = FloorPath(dimensions[0].Key);
        before = SHA256.HashData(await File.ReadAllBytesAsync(path, cancellation));
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            pending = ShowModernDownsampleDialogAsync();
            dialog = await WaitDataDialogAsync("原图降采样（整套地图系列）", cancellation, pending);
            InvokeDataDialog(dialog, "PrimaryButton");
            dialog = await WaitDataDialogAsync("降采样失败", cancellation, pending);
            InvokeDataDialog(dialog, "CloseButton");
            await pending.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        }
        if (!await MatchesSourceAsync(path, before, cancellation)
            || Directory.EnumerateFiles(Path.GetDirectoryName(path)!, ".downsample-*.tmp").Any())
            throw new InvalidOperationException("Failed replacement changed original data or left its encoded temporary file.");
        await WaitReplayAsync(() => _modernBitmap is { IsReady: true }, cancellation);
        MemoryJournal.Current?.Record("full-client-editor-action", new { copied.Id, action = "downsample-file-failure", sourceUnchanged = true, temporaryFiles = 0 });
        InvokeReplay(ReplayDescendants(this).OfType<Button>().Single(button => Equals(button.Content, "取消")));
        await WaitReplayAsync(() => !_modernEditorActive && _draft is null && _classComboBox is not null, cancellation);
        return new { copied.Id, cancelled = true, waitedForDecoder = true, factor = 2, fileFailure = true, retiredPixelsClosed = true };
    }
}
#endif
