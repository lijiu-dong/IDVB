using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using IDVBuff.Features.Maps;

namespace IDVBuff.Cli;
/// <summary>
/// Interactive and scripted front end for the same SessionOrchestrator used
/// by the WinUI application.  This class contains presentation and protocol
/// code only; identification, alignment, capture and overlay rendering stay
/// in the production runtime.
/// </summary>
internal sealed partial class RealCliHost : IAsyncDisposable
{
#if IDVB_DEVELOPMENT_BUILD
    private static async Task<CliCommandResult> ExecuteDevelopmentDataReplayAsync(string input, IReadOnlyList<string> tokens,
        Stopwatch stopwatch, CancellationToken cancellation)
    {
        var mapClass = ParseOption(tokens, "--class") ?? throw new ArgumentException("需要 --class 地图类名称。");
        var inspectionPause = ParseOptionInt(tokens, "--inspection-pause-seconds") ?? 0;
        var copyOnly = bool.Parse(ParseOption(tokens, "--copy-only") ?? "false");
        var exerciseEditor = bool.Parse(ParseOption(tokens, "--exercise-editor") ?? "false");
        if (exerciseEditor && (!AppDataPaths.IsMemoryTest || copyOnly))
            throw new ArgumentException("Editor action replay requires isolated storage and the complete guarded data workflow.");
        if (inspectionPause is < 0 or > 60) throw new ArgumentOutOfRangeException("检查暂停必须为 0 到 60 秒。");
        if (Microsoft.UI.Xaml.Application.Current is not App application
            || application.MainWindow.Content is not Microsoft.UI.Xaml.Controls.Frame { Content: IDVBuff.Views.MainPage main })
            throw new InvalidOperationException("数据回放必须连接完整开发客户端。");
        var result = await main.ReplayDevelopmentDataAsync(mapClass, cancellation, inspectionPause, copyOnly, exerciseEditor);
        return CliCommandResult.Ok(input, copyOnly ? "已通过正常界面复制并登记精确副本，等待回执校验清理。"
            : "复制、最大压缩导出、导入及限定副本删除回放结束。", stopwatch.Elapsed.TotalMilliseconds, result);
    }

    private static async Task<CliCommandResult> ExecuteDevelopmentCleanupAsync(string input, IReadOnlyList<string> tokens,
        Stopwatch stopwatch, CancellationToken cancellation)
    {
        var directory = ParseOption(tokens, "--receipt") ?? throw new ArgumentException("需要 --receipt 创建回执目录。");
        if (Microsoft.UI.Xaml.Application.Current is not App application
            || application.MainWindow.Content is not Microsoft.UI.Xaml.Controls.Frame { Content: IDVBuff.Views.MainPage main })
            throw new InvalidOperationException("副本清理必须连接完整开发客户端。");
        var result = await main.CleanupDevelopmentCopyAsync(directory, cancellation);
        return CliCommandResult.Ok(input, "已按创建回执清理精确副本并核对原始资源。", stopwatch.Elapsed.TotalMilliseconds, result);
    }
#endif


    private async Task<CliCommandResult> GameCommandAsync(
        string input,
        IReadOnlyList<string> tokens,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (tokens.Count < 2)
            return CliCommandResult.Failed(input, "用法：game start|stop|status|map|map-only|floor|image|next|previous|clear", stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());

        var operation = tokens[1].ToLowerInvariant();
        switch (operation)
        {
            case "start":
                return await GameStateResultAsync(
                    input,
                    await EnsureOverlayGameAsync(cancellationToken),
                    stopwatch);
            case "stop":
                if (_overlayGame is not null)
                {
                    var ownsProcess = _overlayGame.OwnsProcess;
                    if (ownsProcess)
                        await _overlayGame.StopAsync(cancellationToken);
                    else
                        _overlayGame.Disconnect();
                    await _overlayGame.DisposeAsync();
                    _overlayGame = null;
                    if (ownsProcess)
                        _session.SynchronizeExternalGameMapState(false);
                }
                return CliCommandResult.Ok(input, "overlay_game 已断开或关闭。", stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
            case "status":
                return await GameStateResultAsync(input, await GetOverlayStateAsync(cancellationToken), stopwatch);
            case "map":
                return await GameStateResultAsync(
                    input,
                    await SetMapStateAsync(tokens, cancellationToken),
                    stopwatch);
            case "map-only":
                return await GameStateResultAsync(
                    input,
                    await SetMapOnlyStateAsync(tokens, cancellationToken),
                    stopwatch);
            case "floor":
                await EnsureOverlayGameAsync(cancellationToken);
                var floorState = await _overlayGame!.SelectFloorAsync(
                    ParseRequiredIndex(tokens, 2) - 1,
                    cancellationToken);
                _session.SelectFloorPosition(floorState.FloorIndex + 1);
                return await GameStateResultAsync(
                    input,
                    floorState,
                    stopwatch);
            case "image":
                await EnsureOverlayGameAsync(cancellationToken);
                return await GameStateResultAsync(
                    input,
                    await _overlayGame!.SelectImageAsync(ParseRequiredIndex(tokens, 2) - 1, cancellationToken),
                    stopwatch);
            case "next":
                await EnsureOverlayGameAsync(cancellationToken);
                return await GameStateResultAsync(input, await _overlayGame!.NextImageAsync(cancellationToken), stopwatch);
            case "previous":
            case "prev":
                await EnsureOverlayGameAsync(cancellationToken);
                return await GameStateResultAsync(input, await _overlayGame!.PreviousImageAsync(cancellationToken), stopwatch);
            case "clear":
                await EnsureOverlayGameAsync(cancellationToken);
                var cleared = await _overlayGame!.ClearImagesAsync(cancellationToken);
                _session.SynchronizeExternalGameMapState(cleared.MapOpen);
                return await GameStateResultAsync(input, cleared, stopwatch);
            default:
                return CliCommandResult.Failed(input, $"未知 game 命令：{tokens[1]}", stopwatch.Elapsed.TotalMilliseconds, BuildSnapshot());
        }
    }

    private async Task<OverlayGameState> SetMapStateAsync(
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        await EnsureOverlayGameAsync(cancellationToken);
        if (tokens.Count < 3)
            throw new ArgumentException("用法：game map open|close");
        var value = tokens[2].ToLowerInvariant();
        var open = value is "open" or "on" or "true";
        if (!open && value is not ("close" or "off" or "false"))
            throw new ArgumentException("game map 只能使用 open 或 close。");
        var state = await _overlayGame!.SetMapOpenAsync(open, cancellationToken);
        await Task.Delay(150, cancellationToken);
        _session.SynchronizeExternalGameMapState(state.MapOpen);
        if (open && !await _overlayGame.ActivateWindowAsync(cancellationToken))
            throw new InvalidOperationException("overlay_game 窗口无法置为前台，无法执行真实捕获。");
        return state;
    }

    private async Task<OverlayGameState> SetMapOnlyStateAsync(
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        await EnsureOverlayGameAsync(cancellationToken);
        if (tokens.Count < 3)
            throw new ArgumentException("用法：game map-only on|off");
        var value = tokens[2].ToLowerInvariant();
        var mapOnly = value is "on" or "true";
        if (!mapOnly && value is not ("off" or "false"))
            throw new ArgumentException("game map-only 只能使用 on 或 off。");
        return await _overlayGame!.SetMapOnlyAsync(mapOnly, cancellationToken);
    }

    private async Task<CliCommandResult> GameStateResultAsync(
        string input,
        OverlayGameState state,
        Stopwatch stopwatch) =>
        CliCommandResult.Ok(
            input,
            "overlay_game 状态已更新。",
            stopwatch.Elapsed.TotalMilliseconds,
            BuildSnapshot(state),
            state);

    private async Task<OverlayGameState> GetOverlayStateAsync(CancellationToken cancellationToken)
    {
        await EnsureOverlayGameAsync(cancellationToken);
        return await _overlayGame!.GetStateAsync(cancellationToken);
    }

    private async Task<OverlayGameState> EnsureOverlayGameAsync(CancellationToken cancellationToken)
    {
        _overlayGame ??= new OverlayGameController(
            _options.OverlayGamePath,
            _options.OverlayGamePipeName);
        var state = await _overlayGame.StartAsync(cancellationToken);
#if IDVB_DEVELOPMENT_BUILD
        if (IDVBuff.Lifecycle.BackgroundTestRuntime.Enabled)
            IDVBuff.Lifecycle.BackgroundTestRuntime.Bind(new nint(state.WindowHandle), state.ProcessId, state.BackgroundTesting, state.ControlVersion);
#endif
        if (_options.UseXButton1ForGameMap)
            _session.UseCliGameMapXButton1Binding();
        _session.SynchronizeExternalGameMapState(state.MapOpen);
        return state;
    }

    private async Task<bool> PrepareCaptureTargetAsync(
        CancellationToken cancellationToken = default)
    {
        if (_overlayGame is null || !_overlayGame.IsConnected)
            return true;

        try
        {
            if (!await _overlayGame.ActivateWindowAsync(cancellationToken))
            {
                if (!_session.TryValidateCliCaptureTarget())
                    return false;
                _session.ReportCliCaptureFailure(
                    "overlay_game 窗口无法置为前台，无法执行真实捕获。");
                return false;
            }
            // Activation is acknowledged above. The shared scan/alignment pipeline
            // captures and validates actual viewport readiness; a fixed sleep adds
            // latency without establishing that any frame is ready.
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception exception)
        {
            _session.ReportCliCaptureFailure(exception.Message);
            return false;
        }
    }

    private async Task<OverlayGameState?> TryGetOverlayStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_overlayGame is null || !_overlayGame.IsConnected)
            return null;
        return await _overlayGame.GetStateAsync(cancellationToken);
    }

    private Dictionary<string, object?> BuildSnapshot(
        OverlayGameState? overlayState = null)
    {
        var recognition = _session.LastRecognition;
        var map = recognition?.Map;
        var result = recognition?.Result;
        return new Dictionary<string, object?>
        {
            ["statusMessage"] = _session.StatusMessage,
            ["match"] = _session.MatchSnapshot,
            ["session"] = _session.SessionSnapshot,
            ["mapLocked"] = recognition is not null,
            ["map"] = map is null ? null : new
            {
                id = map.Id,
                displayName = map.DisplayName,
                floor = result?.Floor,
                confidence = result?.Confidence,
                source = result?.Source,
                hasTransform = result?.OverlayTransform is not null
            },
            ["candidates"] = _session.LastCandidateChoices
                .Select((choice, index) => new
                {
                    position = index + 1,
                    mapId = choice.Recognition.Map.Id,
                    mapDisplayName = choice.Recognition.Map.DisplayName,
                    floor = choice.Recognition.Result.Floor,
                    confidence = choice.RawConfidence,
                    vectorError = choice.VectorError
                })
                .ToArray(),
            ["gameMapOpen"] = _session.IsGameMapOpen,
            ["overlayVisible"] = _session.IsOverlayVisible,
#if IDVB_DEVELOPMENT_BUILD
            ["postScanTracking"] = _session.CaptureDevelopmentTrackingSnapshot(),
#endif
            ["readyMapCount"] = _session.ReadyMapCount,
            ["totalMapCount"] = _session.TotalMapCount,
            ["scanPhaseTimings"] = _session.LastScanPhaseTimings,
            ["alignmentPhaseTimings"] = _session.LastAlignmentPhaseTimings,
            ["scanOperationTrace"] = _session.LastScanOperationTrace,
            ["alignmentOperationTrace"] = _session.LastAlignmentOperationTrace,
            ["candidateOperationTrace"] = _session.LastCandidateOperationTrace,
            ["diagnostics"] = _session.LastDiagnostics,
            ["logSessionPath"] = _session.LogCollector.CurrentSessionPath,
            ["integrity"] = _session.IntegrityStatus,
            ["overlayGame"] = _overlayGame is null
                ? null
                : new
                {
                    connected = _overlayGame.IsConnected,
                    ownsProcess = _overlayGame.OwnsProcess,
                    pipeName = _overlayGame.PipeName,
                    lastStopResult = _overlayGame.LastStopResult,
                    state = overlayState ?? _overlayGame.LastState
                }
        };
    }

    private static IReadOnlyList<string> Tokenize(string input) =>
        TokenRegex.Matches(input)
            .Select(match => match.Value.Trim('"'))
            .ToArray();

    private static string? ParseOption(IReadOnlyList<string> tokens, string option)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], option, StringComparison.OrdinalIgnoreCase))
                return tokens[i + 1];
        }
        return null;
    }

    private static int? ParseOptionInt(IReadOnlyList<string> tokens, string option)
    {
        var value = ParseOption(tokens, option);
        return int.TryParse(value, out var number) ? number : null;
    }

    private static int ParseRequiredIndex(IReadOnlyList<string> tokens, int index)
    {
        if (tokens.Count <= index || !int.TryParse(tokens[index], out var value) || value < 1)
            throw new ArgumentException("楼层和图片索引从 1 开始，必须是正整数。");
        return value;
    }

    private void Emit(CliCommandResult result, bool machineReadable)
    {
        _outputResults.Add(result);
        if (machineReadable || _options.JsonOutput || _options.JsonLines)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, CliJson.Options));
            return;
        }

        Console.WriteLine(result.Success ? "[OK]" : $"[FAIL {result.ExitCode}]");
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
            Console.WriteLine(result.StatusMessage);
        if (result.Exception is not null)
            Console.WriteLine(result.Exception.StackTrace);
    }

    private async Task RenderTuiAsync(CancellationToken cancellationToken)
    {
        if (!_options.NoClear && !Console.IsOutputRedirected)
        {
            try { Console.Clear(); } catch { }
        }

        var state = _session.LastRecognition;
        Console.WriteLine("Identity Vision Bridge / IDVB RealCLI");
        Console.WriteLine(new string('=', 44));
        Console.WriteLine($"对局：{(_session.MatchSnapshot.IsStarted ? "进行中" : "未开始")}");
        Console.WriteLine($"识别/对齐：{(_session.IsScanning ? "进行中" : "空闲")}");
        Console.WriteLine($"地图：{(state is null ? "未锁定" : state.Map.DisplayName)}");
        Console.WriteLine($"楼层：{_session.CurrentFloorKey ?? state?.Result.Floor ?? "-"}");
        Console.WriteLine($"游戏地图：{(_session.IsGameMapOpen ? "打开" : "关闭")}");
        Console.WriteLine($"IDVB Overlay：{(_session.IsOverlayVisible ? "显示" : "隐藏")}");
        Console.WriteLine($"overlay_game：{(_overlayGame?.IsConnected == true ? "已连接" : "未连接")}");
        Console.WriteLine($"状态：{_session.StatusMessage}");
        Console.WriteLine($"日志：{_session.LogCollector.EntryCount} 条，待写入 {_session.LogCollector.BufferedEntryCount} 条");
        var recentEntries = await _session.LogCollector
            .GetCompleteEntriesAsync(cancellationToken);
        foreach (var entry in recentEntries.TakeLast(3))
            Console.WriteLine($"  {entry.Level}/{entry.Category}: {entry.Message}");
        if (_session.LastScanPhaseTimings is { } scanTimings)
            Console.WriteLine($"扫描耗时：{string.Join(", ", scanTimings.Select(pair => $"{pair.Key}={pair.Value:0.##}ms"))}");
        if (_session.LastAlignmentPhaseTimings is { } alignmentTimings)
            Console.WriteLine($"对齐耗时：{string.Join(", ", alignmentTimings.Select(pair => $"{pair.Key}={pair.Value:0.##}ms"))}");
        if (_session.LastScanOperationTrace is { } scanTrace)
            Console.WriteLine(
                $"扫描黑洞：wall={scanTrace.WallClockMs:0.##}ms "
                    + $"covered={scanTrace.CoveredTopLevelMs:0.##}ms "
                    + $"unaccounted={scanTrace.UnaccountedMs:0.##}ms "
                    + $"outcome={scanTrace.Outcome}");
        if (_session.LastAlignmentOperationTrace is { } alignmentTrace)
            Console.WriteLine(
                $"对齐黑洞：wall={alignmentTrace.WallClockMs:0.##}ms "
                    + $"covered={alignmentTrace.CoveredTopLevelMs:0.##}ms "
                    + $"unaccounted={alignmentTrace.UnaccountedMs:0.##}ms "
                    + $"outcome={alignmentTrace.Outcome}");
        if (_session.LastCandidateOperationTrace is { } candidateTrace)
            Console.WriteLine(
                $"候选黑洞：wall={candidateTrace.WallClockMs:0.##}ms "
                    + $"covered={candidateTrace.CoveredTopLevelMs:0.##}ms "
                    + $"unaccounted={candidateTrace.UnaccountedMs:0.##}ms "
                    + $"outcome={candidateTrace.Outcome}");
        Console.WriteLine();
        Console.WriteLine("begin --class S1 | scan [--automatic | --candidate N] | align | end | status | logs | timings");
        Console.WriteLine("game start/status/map open|close/map-only on|off/floor N/image N/next/previous/clear");
        Console.WriteLine("quit");
    }

    private static readonly IReadOnlyDictionary<string, string> CommandHelp =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["begin"] = "begin --class <class>",
            ["scan"] = "scan [--automatic | --candidate <1-N>]；--automatic 仅接受自动身份验证；默认候选选择只用于回放，不代表自动识别",
            ["align"] = "只对已锁定地图执行 IDVB 的仅对齐入口",
            ["end"] = "结束当前对局并释放锁定地图",
            ["status"] = "输出完整运行状态快照",
            ["logs"] = "输出 MapLogCollector 日志与持久化路径",
            ["timings"] = "输出扫描/对齐阶段耗时",
            ["game"] = "控制 overlay_game Named Pipe 状态",
            ["overlay"] = "使用 overlay show|hide|toggle 控制 IDVB Overlay",
            ["quit"] = "退出并清理子进程"
        };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_overlayGame is not null)
        {
            await _overlayGame.StopAsync();
            await _overlayGame.DisposeAsync();
            _overlayGame = null;
        }
    }
}
