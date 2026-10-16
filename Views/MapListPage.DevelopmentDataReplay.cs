#if IDVB_DEVELOPMENT_BUILD
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using IDVBuff.Core.Diagnostics;
using IDVBuff.Diagnostics;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private string? _developmentExportDestination, _developmentImportPackage;
    private static readonly JsonSerializerOptions DevelopmentMetadataOptions = CreateDevelopmentMetadataOptions();

    private static JsonSerializerOptions CreateDevelopmentMetadataOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(MapRecord)) return;
            var navigation = type.Properties.Single(property => property.Name == nameof(MapRecord.Navigation));
            // Hash the already persisted route file below. Invoking this lazy
            // getter loads every route into the test's retained baseline.
            navigation.Get = _ => null;
            navigation.ShouldSerialize = (_, _) => false;
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }

    // Only the OS picker is substituted. UI prompts, compression, inspection,
    // import, catalog refresh and deletion run in this normal client.
    internal async Task<object> ReplayDevelopmentDataAsync(string sourceClass, CancellationToken cancellation, int inspectionPauseSeconds = 0, bool copyOnly = false, bool exerciseEditor = false)
    {
        await InitialListReady.WaitAsync(cancellation);
        var run = Guid.NewGuid();
        var directory = Path.Combine(DevelopmentReplayArtifactRoot(), run.ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "current-class-maximum.idvm");
        var baseline = await _repository.GetTagCatalogSnapshotAsync();
        var protectedIds = baseline.Maps.Select(map => map.Id).ToHashSet();
        var protectedClasses = baseline.Classes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fingerprints = await FingerprintClassesAsync(protectedClasses, protectedIds, cancellation);
        var protectedProperties = baseline.ClassProperties.ToDictionary(pair => pair.Key,
            pair => JsonSerializer.Serialize(pair.Value), StringComparer.OrdinalIgnoreCase);
        var protectedGroups = baseline.VariantGroups.ToDictionary(group => group.Id, group => JsonSerializer.Serialize(group));
        var source = (await _repository.GetClassCatalogSnapshotAsync(sourceClass)).Maps.ToArray();
        if (source.Length == 0) throw new InvalidOperationException("测试来源地图类为空。");
        var sourceCount = source.Length;
        // The immutable IDs/hashes above are the deletion guard. Do not retain
        // unrelated editable records across export/import just for verification.
        baseline = new MapCatalogSnapshot([], []);
        var permitted = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        void Receipt(string stage, object detail)
        {
            var record = new { run, stage, detail, pid = Environment.ProcessId, build = BuildVersionInfo.BuildVersion,
                reading = ProcessMemoryReader.Read(), pickerSubstitution = true };
            var path = Path.Combine(directory, stage + ".json");
            using (var stream = File.Create(path)) JsonSerializer.Serialize(stream, record);
            // The large integrity manifest belongs in the receipt, not in a
            // second diagnostic queue retaining a serialized copy of it.
            MemoryJournal.Current?.Record("full-client-data-replay", new { run, stage, path,
                pid = record.pid, build = record.build, reading = record.reading }, durable: true);
        }
        async Task VerifyProtectedAsync()
        {
            var current = await _repository.GetTagCatalogSnapshotAsync();
            if (!protectedIds.IsSubsetOf(current.Maps.Select(map => map.Id).ToHashSet())
                || !protectedClasses.IsSubsetOf(current.Classes.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("原始地图或地图类集合发生变化，拒绝继续删除。");
            if (protectedProperties.Any(pair => !current.ClassProperties.TryGetValue(pair.Key, out var value)
                    || JsonSerializer.Serialize(value) != pair.Value)
                || protectedGroups.Any(pair => current.VariantGroups.SingleOrDefault(group => group.Id == pair.Key) is not { } value
                    || JsonSerializer.Serialize(value) != pair.Value))
                throw new InvalidOperationException("原始地图类属性或变体组合发生变化，拒绝继续删除。");
            var now = await FingerprintClassesAsync(protectedClasses, protectedIds, cancellation);
            if (now.Count != fingerprints.Count || fingerprints.Any(pair => !now.TryGetValue(pair.Key, out var value) || value != pair.Value))
                throw new InvalidOperationException("原始地图元数据或资源哈希发生变化，拒绝继续删除。");
        }
        async Task<MapRecord[]> VerifyDisposableAsync(string name)
        {
            await VerifyProtectedAsync();
            if (protectedClasses.Contains(name) || !permitted.TryGetValue(name, out var allowed))
                throw new InvalidOperationException("删除目标不是本轮已登记副本。");
            var current = await _repository.GetClassCatalogSnapshotAsync(name);
            var maps = current.Maps.Where(map => map.Class == name).ToArray();
            if (!maps.Select(map => map.Id).ToHashSet().SetEquals(allowed) || allowed.Overlaps(protectedIds))
                throw new InvalidOperationException("副本 ID 或数量不一致，拒绝删除。");
            Receipt("delete-check-" + allowed.Count + "-" + name.GetHashCode().ToString("X"), new { name, ids = allowed, originalResourcesUnchanged = true });
            return maps;
        }
        async Task<string> RegisterAddedAsync(IReadOnlySet<string> before, int expectedCount, string stage)
        {
            var current = await _repository.GetTagCatalogSnapshotAsync();
            var added = current.Classes.Where(name => !before.Contains(name)).ToArray();
            if (added.Length != 1 || protectedClasses.Contains(added[0])) throw new InvalidOperationException("新增地图类不唯一，保留现场。");
            var maps = current.Maps.Where(map => map.Class == added[0]).ToArray();
            var ids = maps.Select(map => map.Id).ToHashSet();
            if (maps.Length != expectedCount || ids.Count != expectedCount || ids.Overlaps(protectedIds)
                || permitted.Values.Any(existing => existing.Overlaps(ids)))
                throw new InvalidOperationException("新增地图 ID、来源或数量无法确认，保留现场。");
            permitted.Add(added[0], ids);
            Receipt(stage, new { sourceClass, name = added[0], ids, expectedCount });
            await VerifyProtectedAsync();
            return added[0];
        }
        Receipt("baseline", new { sourceClass, protectedIds, protectedClasses, fingerprints });
        try
        {
            SelectReplayClass(sourceClass);
            await Task.Delay(300, cancellation);
            InvokeReplay(_classEditButton!);
            await WaitReplayAsync(() => _classEditButton!.Flyout is MenuFlyout, cancellation);
            var menu = (MenuFlyout)_classEditButton!.Flyout!;
            var copy = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "复制");
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(copy).GetPattern(PatternInterface.Invoke)).Invoke();
            // Copy commits the new class before its refresh and progress popup
            // finish. Wait for the same controls a user needs to click export;
            // a changed class alone must not bypass that operation's lifetime.
            await WaitReplayAsync(() => _selectedClass != sourceClass && !_isPackageOperation
                && _classComboBox is { IsEnabled: true }
                && _classComboBox.SelectedItem is ComboBoxItem selected && Equals(selected.Tag, _selectedClass)
                && _classEditButton is { IsEnabled: true } && _importButton is { IsEnabled: true }, cancellation);
            MemoryJournal.Current?.Record("full-client-data-copy-ui-ready", new { _selectedClass,
                classSelectorEnabled = _classComboBox!.IsEnabled, classEditorEnabled = _classEditButton!.IsEnabled,
                packageEntryEnabled = _importButton!.IsEnabled,
                meaning = "Copy refresh and progress popup completed; normal clickable controls are enabled." });
            var copiedClass = await RegisterAddedAsync(protectedClasses, sourceCount, "copied");
            if (_selectedClass != copiedClass) throw new InvalidOperationException("复制后的当前地图类不一致。");
            var copies = (await _repository.GetClassCatalogSnapshotAsync(copiedClass)).Maps.ToArray();
            foreach (var original in source)
            {
                var copied = copies.Single(map => map.SequenceNumber == original.SequenceNumber);
                if (!DevelopmentCopyMatches(original, copied, copiedClass))
                    throw new InvalidOperationException("副本内容与来源地图不一致，拒绝继续。");
            }
            if (exerciseEditor)
            {
                if (!AppDataPaths.IsMemoryTest || copyOnly) throw new InvalidOperationException("An isolated complete workflow is required.");
                var edited = copies.FirstOrDefault(map => !map.IsCoCreationTemplate)
                    ?? throw new InvalidOperationException("The copied class has no ordinary map eligible for downsampling.");
                Receipt("editor-actions", await ReplayCopiedEditorActionsAsync(edited, cancellation));
                await VerifyProtectedAsync();
                SelectReplayClass(copiedClass);
            }
            source = []; copies = [];
            if (copyOnly)
            {
                Receipt("copy-only-completed", new { originalsUnchanged = true, copiedClass, cleanup = "Exact baseline/copied receipt required; no automatic deletion." });
                return new { run, directory, success = true, scope = "Copy only; export/import and deletion have not run." };
            }
            _developmentExportDestination = output;
            // The same handler reached by the export choice shows the real range/format dialog.
            var exporting = ShowExportIdvmDialogAsync(_importButton!, _publishButton!);
            var dialog = await WaitDataDialogAsync("选择导出范围", cancellation, exporting);
            var compression = ReplayDescendants(dialog).OfType<ComboBox>().Single(box => Equals(box.Header, "地图包格式"));
            compression.SelectedItem = compression.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, IdvmCompressionMode.Maximum));
            InvokeDataDialog(dialog, "PrimaryButton");
            await DismissDataCompletionAsync(exporting, cancellation);
            if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new InvalidOperationException("实际导出没有生成数据包。");
            await using (var plan = await _idvmPackageService.InspectAsync(output, cancellation))
                if (plan.ClassCount != 1 || plan.MapCount != sourceCount) throw new InvalidOperationException("导出的地图类或地图数量不一致。");
            Receipt("exported", new { output, bytes = new FileInfo(output).Length, compression = "Maximum", Length = sourceCount });

            var beforeImport = (await _repository.GetTagCatalogSnapshotAsync()).Classes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _developmentImportPackage = output;
            if (inspectionPauseSeconds > 0)
                IdvmPackageService.DevelopmentImportInspectionPause = async token =>
                {
                    Receipt("import-inspected", new { inspectionPauseSeconds, purpose = "Read-only heap ownership investigation; not a memory/performance acceptance run." });
                    await Task.Delay(TimeSpan.FromSeconds(inspectionPauseSeconds), token);
                };
            var importing = ImportIdvmPackageAsync(_importButton!, _publishButton!);
            await DismissDataCompletionAsync(importing, cancellation);
            var importedClass = await RegisterAddedAsync(beforeImport, sourceCount, "imported");

            var disposable = await VerifyDisposableAsync(copiedClass);
            SelectReplayClass(copiedClass);
            var deleted = disposable.OrderBy(map => map.SequenceNumber).First();
            disposable = [];
            var deletingMap = DeleteSelectedMapAsync(deleted);
            dialog = await WaitDataDialogAsync("删除地图？", cancellation, deletingMap);
            if (!Equals(dialog.Content, $"将永久删除 {deleted.DisplayName} 及其两张图片和识别标记数据。"))
                throw new InvalidOperationException("实际删除地图确认内容与已验证副本不一致。");
            InvokeDataDialog(dialog, "PrimaryButton");
            await DismissDataCompletionAsync(deletingMap, cancellation);
            permitted[copiedClass].Remove(deleted.Id);
            if ((await _repository.GetTagCatalogSnapshotAsync()).Maps.Any(map => map.Id == deleted.Id))
                throw new InvalidOperationException("副本地图未实际删除。");
            Receipt("map-deleted", new { copiedClass, deleted.Id });
            foreach (var name in new[] { copiedClass, importedClass })
            {
                await VerifyDisposableAsync(name);
                SelectReplayClass(name);
                var deleting = ConfirmDeleteClassAsync(name, permitted[name]);
                dialog = await WaitDataDialogAsync($"删除地图类“{name}”？", cancellation, deleting);
                InvokeDataDialog(dialog, "PrimaryButton");
                await DismissDataCompletionAsync(deleting, cancellation);
                var current = await _repository.GetTagCatalogSnapshotAsync();
                if (current.Classes.Contains(name, StringComparer.OrdinalIgnoreCase) || current.Maps.Any(map => permitted[name].Contains(map.Id)))
                    throw new InvalidOperationException("测试副本未完整删除。");
                current = new MapCatalogSnapshot([], []);
                await VerifyProtectedAsync();
            }
            SelectReplayClass(sourceClass);
            Receipt("completed", new { originalsUnchanged = true, disposableCopiesRemoved = true });
            return new { run, directory, output, success = true, pickerSubstitution = true };
        }
        catch
        {
            Receipt("failed-preserved", new { permitted, output, cleanup = "No automatic deletion after failure." });
            throw;
        }
        finally
        {
            _developmentExportDestination = _developmentImportPackage = null;
            IdvmPackageService.DevelopmentImportInspectionPause = null;
        }
    }

    private static bool DevelopmentCopyMatches(MapRecord original, MapRecord copied, string copiedClass)
    {
        var expected = original.Clone(); expected.Id = copied.Id; expected.Class = copiedClass;
        return DevelopmentMetadataFingerprint(expected) == DevelopmentMetadataFingerprint(copied);
    }

    private void SelectReplayClass(string name) => _classComboBox!.SelectedItem = _classComboBox.Items
        .OfType<ComboBoxItem>().Single(item => Equals(item.Tag, name));

    internal async Task<object> CleanupDevelopmentCopyAsync(string receiptDirectory, CancellationToken cancellation)
    {
        await InitialListReady.WaitAsync(cancellation);
        var directory = Path.GetFullPath(receiptDirectory);
        if (!string.Equals(Path.GetDirectoryName(directory), DevelopmentReplayArtifactRoot(), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(directory), "N", out var run))
            throw new InvalidOperationException("清理必须指定已登记的开发回放目录。");
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("清理回执路径不存在或经过目录链接。");
        foreach (var file in new[] { "baseline.json", "copied.json" })
            if ((File.GetAttributes(Path.Combine(directory, file)) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("清理回执文件不能是链接。");
        using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "baseline.json"), cancellation));
        using var copied = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "copied.json"), cancellation));
        if (baseline.RootElement.GetProperty("run").GetGuid() != run || copied.RootElement.GetProperty("run").GetGuid() != run
            || baseline.RootElement.GetProperty("stage").GetString() != "baseline" || copied.RootElement.GetProperty("stage").GetString() != "copied")
            throw new InvalidOperationException("回执身份不一致，拒绝清理。");
        var original = baseline.RootElement.GetProperty("detail");
        var protectedIds = original.GetProperty("protectedIds").EnumerateArray().Select(item => item.GetGuid()).ToHashSet();
        var protectedClasses = original.GetProperty("protectedClasses").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fingerprints = original.GetProperty("fingerprints").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!);
        var target = copied.RootElement.GetProperty("detail");
        var name = target.GetProperty("name").GetString()!;
        var allowed = target.GetProperty("ids").EnumerateArray().Select(item => item.GetGuid()).ToHashSet();
        if (protectedClasses.Contains(name) || allowed.Count == 0 || allowed.Count != target.GetProperty("expectedCount").GetInt32()
            || allowed.Overlaps(protectedIds)) throw new InvalidOperationException("删除目标不是回执登记的独立副本。");
        async Task VerifyOriginalsAsync()
        {
            var current = await _repository.GetCatalogSnapshotAsync();
            if (!protectedIds.IsSubsetOf(current.Maps.Select(map => map.Id).ToHashSet())
                || !protectedClasses.IsSubsetOf(current.Classes.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("受保护原始集合发生变化，拒绝清理。");
            var now = await FingerprintsAsync(current.Maps.Where(map => protectedIds.Contains(map.Id)));
            if (now.Count != fingerprints.Count || fingerprints.Any(pair => !now.TryGetValue(pair.Key, out var value) || value != pair.Value))
                throw new InvalidOperationException("受保护原始资源或元数据哈希不一致，拒绝清理。");
        }
        await VerifyOriginalsAsync();
        var before = await _repository.GetCatalogSnapshotAsync();
        if (!before.Maps.Where(map => string.Equals(map.Class, name, StringComparison.OrdinalIgnoreCase))
            .Select(map => map.Id).ToHashSet().SetEquals(allowed))
            throw new InvalidOperationException("副本 GUID 集合发生变化，拒绝清理。");
        var groups = JsonSerializer.Serialize(before.VariantGroups.Where(group => !string.Equals(group.Class, name, StringComparison.OrdinalIgnoreCase)));
        SelectReplayClass(name);
        await Task.Delay(300, cancellation);
        var deleting = ConfirmDeleteClassAsync(name, allowed);
        var dialog = await WaitDataDialogAsync($"删除地图类“{name}”？", cancellation, deleting);
        if (!Equals(dialog.Content, $"将永久删除当前展示的地图类及其 {allowed.Count} 张地图，此操作无法撤销。"))
            throw new InvalidOperationException("删除确认内容不一致，拒绝清理。");
        InvokeDataDialog(dialog, "PrimaryButton");
        await DismissDataCompletionAsync(deleting, cancellation);
        await VerifyOriginalsAsync();
        var after = await _repository.GetCatalogSnapshotAsync();
        if (after.Classes.Contains(name, StringComparer.OrdinalIgnoreCase) || after.Maps.Any(map => allowed.Contains(map.Id))
            || !after.Maps.Select(map => map.Id).ToHashSet().SetEquals(before.Maps.Select(map => map.Id).Where(id => !allowed.Contains(id)))
            || JsonSerializer.Serialize(after.VariantGroups) != groups)
            throw new InvalidOperationException("清理后的集合不一致，保留现场。");
        var expectedProperties = before.ClassProperties.Where(pair => !string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (JsonSerializer.Serialize(after.ClassProperties) != JsonSerializer.Serialize(expectedProperties))
            throw new InvalidOperationException("其他地图类属性发生变化，保留现场。");
        var result = new { run, name, removedIds = allowed, originalsUnchanged = true, pid = Environment.ProcessId, build = BuildVersionInfo.BuildVersion };
        using (var output = File.Create(Path.Combine(directory, "cleanup-" + Guid.NewGuid().ToString("N") + ".json"))) JsonSerializer.Serialize(output, result);
        MemoryJournal.Current?.Record("full-client-data-cleanup", result, durable: true);
        return result;
    }

    private static string DevelopmentReplayArtifactRoot()
    {
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.Parent?.FullName
            ?? throw new InvalidOperationException("无法解析开发构建根目录。");
        return Path.GetFullPath(Path.Combine(root, "artifacts", "memory", "data-replay"));
    }

    private static string ValidateDevelopmentPackagePath(string path, bool mustExist)
    {
        var full = Path.GetFullPath(path);
        var root = DevelopmentReplayArtifactRoot();
        if (!Program.IsDevelopmentInstance || App.IsSafeMode
            || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !full.EndsWith(".idvm", StringComparison.OrdinalIgnoreCase) || File.Exists(full) != mustExist)
            throw new InvalidOperationException("测试文件必须是本轮开发回放目录中的独立 IDVM 文件。");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(full)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("测试文件路径不能经过目录链接。");
        return full;
    }

    // Keep the complete integrity checks, but own editable records for only one
    // class at a time. The guard must not add a second whole editable catalog
    // to the real client's working set before invoking its UI operations.
    private async Task<Dictionary<string, string>> FingerprintClassesAsync(
        IEnumerable<string> classes, IReadOnlySet<Guid> protectedIds, CancellationToken cancellation)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in classes)
        {
            cancellation.ThrowIfCancellationRequested();
            var snapshot = await _repository.GetClassCatalogSnapshotAsync(name);
            var fingerprints = await FingerprintsAsync(snapshot.Maps.Where(map => protectedIds.Contains(map.Id)));
            foreach (var pair in fingerprints) result.Add(pair.Key, pair.Value);
            snapshot = new MapCatalogSnapshot([], []);
        }
        return result;
    }

    private async Task<Dictionary<string, string>> FingerprintsAsync(IEnumerable<MapRecord> maps) => await Task.Run(() =>
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var map in maps)
        {
            result.Add(map.Id + "/metadata", DevelopmentMetadataFingerprint(map));
            var root = Path.GetDirectoryName(_repository.GetFloorOnePath(map))!;
            if (!string.Equals(Path.GetFileName(root), map.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法确认地图资源根目录，拒绝测试删除。");
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("资源目录中存在链接，拒绝测试删除。");
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("资源中存在链接，拒绝测试删除。");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else
                    {
                        using var input = File.OpenRead(entry);
                        result.Add(map.Id + "/" + Path.GetRelativePath(root, entry), Convert.ToHexString(SHA256.HashData(input)));
                    }
                }
            }
        }
        return result;
    });

    private static string DevelopmentMetadataFingerprint(MapRecord map)
    {
        // Hash the same complete JSON bytes as before while they are written,
        // without retaining another whole byte array/string for every map.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output = new DevelopmentHashStream(hash);
        JsonSerializer.Serialize(output, map, DevelopmentMetadataOptions);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Hash bytes directly. CryptoStream also copies each hash input to an
    // output buffer, which is unnecessary when the only consumer is SHA-256.
    private sealed class DevelopmentHashStream(IncrementalHash hash) : Stream
    {
        private bool _disposed;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            hash.AppendData(buffer);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
    }

    private ContentDialog? CurrentDataDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
        .SelectMany(popup => ReplayDescendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();

    private async Task<ContentDialog> WaitDataDialogAsync(string title, CancellationToken cancellation, Task? operation = null)
    {
        await WaitReplayAsync(() =>
        {
            if (Equals(CurrentDataDialog()?.Title, title)) return true;
            if (operation?.IsCompleted == true)
            {
                // Surface the actual entry failure instead of hiding it behind
                // a later wait timeout; success without this dialog also fails.
                operation.GetAwaiter().GetResult();
                throw new InvalidOperationException($"操作已结束，但未显示预期对话框：{title}");
            }
            return false;
        }, cancellation);
        return CurrentDataDialog()!;
    }

    private static void InvokeDataDialog(ContentDialog dialog, string name) => InvokeReplay(ReplayDescendants(dialog)
        .OfType<Button>().Single(button => button.Name == name));

    private async Task DismissDataCompletionAsync(Task operation, CancellationToken cancellation)
    {
        while (!operation.IsCompleted)
        {
            cancellation.ThrowIfCancellationRequested();
            MemoryBudget.Process.ThrowIfExceeded();
            if (CurrentDataDialog() is { } dialog)
            {
                if (dialog.PrimaryButtonText.Length != 0 || dialog.SecondaryButtonText.Length != 0)
                    throw new InvalidOperationException("出现未预期的操作确认，保留现场。");
                InvokeDataDialog(dialog, "CloseButton");
            }
            await Task.Delay(100, cancellation);
        }
        await operation;
    }
}
#endif
