using IDVBuff.Core.Diagnostics;
using IDVBuff.Features.Maps;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private void RecordModernEditorMemory(string stage, string? imagePath = null)
    {
        var journal = MemoryJournal.Current;
        if (journal is null) return;
        var memory = IDVBuff.Diagnostics.ProcessMemoryReader.Read();
        var profile = _draft?.Recognition.GetFloor(_activeFloorKey);
        journal.Record("editor-memory", new
        {
            stage, mapId = _draft?.Id, mapClass = _draft?.Class, title = _draft?.Title,
            floorKey = _activeFloorKey, imagePath,
            memory.PrivateWorkingSetBytes, memory.PrivateCommitBytes,
            managedLiveBytes = GC.GetTotalMemory(false),
            gcCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
            bitmaps = _modernFloorBitmaps.Count, annotations = profile?.Annotations.Count,
            visuals = _modernCanvas?.Children.Count, renderCount = _modernRenderCount
        });
    }

    // The editor renders one floor. Retaining every previously visited floor
    // keeps decoded native surfaces alive although no consumer uses them.
    private void ReleaseModernFloorBitmaps(string? keepFloorKey = null)
    {
        foreach (var key in _modernFloorBitmaps.Keys.Where(key =>
            !string.Equals(key, keepFloorKey, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var entry = _modernFloorBitmaps[key];
            if (ReferenceEquals(_modernImage?.Source, entry.Source)) _modernImage!.Source = null;
            if (ReferenceEquals(_modernBitmap, entry)) _modernBitmap = null;
            entry.Dispose();
            _modernFloorBitmaps.Remove(key);
        }
    }

    private async Task LoadModernFloorBitmapAsync(ModernFloorBitmap entry, string imagePath,
        string floorKey, bool fitWhenLoaded, long viewRevision)
    {
        var token = entry.GetToken();
        var entered = false;
        try
        {
            // A cancelled decode drains before the replacement allocates any
            // pixels. Queued obsolete floors never enter the native decoder.
            await _modernImageLoadGate.WaitAsync(token);
            entered = true;
            var pixels = await Task.Run(() => MapEditorBitmapDecoder.Decode(imagePath, ModernEditorDecodePixelWidth, token), token);
            var transferred = false;
            try
            {
                token.ThrowIfCancellationRequested();
                entry.TransferPixels(pixels);
                transferred = true;
                await entry.Source.SetBitmapAsync(pixels);
                token.ThrowIfCancellationRequested();
                entry.IsReady = true;
                MemoryJournal.Current?.Record("editor-image-opened", new
                {
                    mapId = _draft?.Id, floorKey, imagePath, entry.SourceWidth, entry.SourceHeight,
                    decodedWidth = pixels.PixelWidth, decodedHeight = pixels.PixelHeight,
                    isCurrent = ReferenceEquals(_modernBitmap, entry),
                    meaning = "WIC interfaces close after decode; display source retains recovery pixels and closes them on Dispose, deferred if upload is pending."
                });
                ApplyModernFloorBitmap(entry, entry.SourceWidth, entry.SourceHeight, fitWhenLoaded, viewRevision);
                RecordModernEditorMemory("image-applied", imagePath);
            }
            finally { if (!transferred) MapEditorBitmapDecoder.Release(pixels); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (entry.IsDisposed) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_modernBitmap, entry) && ReferenceEquals(_modernImage?.Source, entry.Source))
                _modernImage!.Source = null;
            entry.Dispose();
            IDVBuff.Diagnostics.OutputLog.Write("ERROR", "MAP/EDITOR", $"图片加载失败：{imagePath}; {ex}");
            SetModernStatus("地图图片加载失败。", true);
        }
        finally { if (entered) _modernImageLoadGate.Release(); }
    }
}
