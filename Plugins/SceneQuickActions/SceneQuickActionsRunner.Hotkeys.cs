using System.Diagnostics;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.SceneQuickActions;

internal sealed partial class SceneQuickActionsRunner
{
    // ==================== 拖拽热键 ====================

    /// <summary>接入宿主的插件输入通道：热键的按键监听、注入过滤都由宿主负责。</summary>
    public void AttachHotkeys(IPluginInputService? input, string pluginId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_hotkeyService, input)
                && string.Equals(_pluginId, pluginId, StringComparison.Ordinal))
                return;

            StopHotkeys();
            _hotkeyService = input;
            _pluginId = pluginId;
            if (_cancellation is not null)
                StartHotkeys();
        }
    }

    private string _pluginId = "scene-quick-actions";
    private long _hotkeyBindingsUpdatedAt;

    private void OnHotkeyInvoked(object? sender, PluginInputEventArgs args)
    {
        lock (_sync)
        {
            if (_disposed || _cancellation is null
                || !ReferenceEquals(sender, _hotkeyService)
                || args.Timestamp < _hotkeyBindingsUpdatedAt)
                return;

            HandleHotkeyInvoked(args, _cancellation.Token);
        }
    }

    private void HandleHotkeyInvoked(PluginInputEventArgs args, CancellationToken token)
    {
        if (!string.Equals(args.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase))
            return;

        var options = Volatile.Read(ref _options);

        // 「开背包 / 切出鼠标」的按键：只用来判断背包是不是开着。
        // 这一路不受总开关影响 —— 停用期间照样记着，恢复时才不会数错奇偶。
        if (string.Equals(args.BindingKey, SceneQuickActionsOptions.BackpackKeyKey, StringComparison.Ordinal)
            && options.BackpackKey.IsConfigured)
        {
            if (!args.IsDown)
                return;
            // 本插件自己注入的那次 Tab 不计入（否则开 / 关背包的奇偶会被自己打乱）。
            if (args.Timestamp < Interlocked.Read(ref _suppressBackpackKeyUntilTimestamp))
                return;
            _backpack.PressBackpackKey(DateTime.UtcNow);
            return;
        }

        if (!args.IsDown)
            return;

        // 总开关热键：启用 ⇄ 停用（优先级高于其它热键，停用后还能按它回来）。
        if (options.MasterHotkey.IsConfigured
            && string.Equals(args.BindingKey, SceneQuickActionsOptions.MasterHotkeyKey, StringComparison.Ordinal))
        {
            var masterPressedAt = DateTime.UtcNow;
            if (masterPressedAt < _lastMasterHotkeyAt.AddMilliseconds(HotkeyDebounceMilliseconds))
                return;
            _lastMasterHotkeyAt = masterPressedAt;
            var enabled = ToggleAutomatic();
            AutomaticStateChanged?.Invoke(enabled);
            return;
        }

        if (!AutomaticEnabled)
        {
            LogThrottled("插件已停用（总开关），本次不响应拖拽热键；按总开关热键再按一次即可恢复。");
            return;
        }

        var isBag = options.DropBagHotkey.IsConfigured
            && string.Equals(args.BindingKey, SceneQuickActionsOptions.DropBagHotkeyKey, StringComparison.Ordinal);
        var isHotbar = options.DropHotbarHotkey.IsConfigured
            && string.Equals(args.BindingKey, SceneQuickActionsOptions.DropHotbarHotkeyKey, StringComparison.Ordinal);
        if (!isBag && !isHotbar)
            return;

        // 防抖：按住不放时只认第一次
        var now = DateTime.UtcNow;
        if (now < _lastHotkeyAt.AddMilliseconds(HotkeyDebounceMilliseconds))
            return;
        _lastHotkeyAt = now;

        if (Interlocked.CompareExchange(ref _hotkeyBusy, 1, 0) != 0)
            return;

        var scheduled = false;
        try
        {
            if (!_input.TryGetForegroundGameWindow(out var window))
            {
                LogThrottled("热键已按下，但当前前台窗口不是游戏，已忽略。");
                return;
            }

            // 暂停「拾取相关」：默认 30 秒，免得把刚丢出来的东西又捡回去。
            var silence = Math.Clamp(
                options.DropSilenceSeconds,
                SceneQuickActionsOptions.DropSilenceMinimum,
                SceneQuickActionsOptions.DropSilenceMaximum);
            Interlocked.Exchange(ref _dropSilenceUntilTicks, DateTime.UtcNow.AddSeconds(silence).Ticks);
            // 手动操作优先：自动动作取消后仍持有操作锁，先释放鼠标、恢复背包，再交接。
            _automaticOperationCancellation?.Cancel();

            string[] work;
            var hotbarSlot = 0;
            if (isBag)
            {
                work = BuildBagDropWork();
                _logger.Info(
                    $"热键「丢光背包」触发：切出鼠标（背包已经开着就直接用，不重复按 Tab），"
                    + $"再依次拖 {work.Length} 格到屏幕中心；「拾取相关」暂停 {silence} 秒。");
            }
            else
            {
                var cursor = Volatile.Read(ref _hotbarCursor);
                hotbarSlot = cursor % HotbarSlotCount;
                work = BuildHotbarDropWork(ref cursor);
                Volatile.Write(ref _hotbarCursor, cursor);
                _logger.Info(
                    $"热键「逐个丢道具栏」触发：{work[0]}（切出鼠标，背包已经开着就直接用）；"
                    + $"「拾取相关」暂停 {silence} 秒。");
            }

            _hotkeyTask = Task.Run(() =>
            {
                var ownsInput = false;
                try
                {
                    _inputOperationGate.Wait(token);
                    ownsInput = true;
                    EnsureCanInject(window, token);
                    RunDropWork(work, window, isBag, hotbarSlot, options, token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _logger.Warning($"拖拽失败：{exception.Message}");
                }
                finally
                {
                    if (ownsInput)
                        _inputOperationGate.Release();
                    Interlocked.Exchange(ref _hotkeyBusy, 0);
                }
            });
            scheduled = true;
        }
        finally
        {
            if (!scheduled)
                Interlocked.Exchange(ref _hotkeyBusy, 0);
        }
    }

    /// <summary>热键改动后重新注册（设置页改键位时调用）。</summary>
    public void RefreshHotkeyBindings() => RegisterHotkeys();

    /// <summary>把热键绑定注册到宿主（键位改动后要重新注册）。</summary>
    private void RegisterHotkeys()
    {
        lock (_sync)
        {
            if (_disposed || _cancellation is null || _hotkeyService is null)
                return;

            var options = Volatile.Read(ref _options);
            _hotkeyService.SetBinding(_pluginId, SceneQuickActionsOptions.MasterHotkeyKey, options.MasterHotkey);
            _hotkeyService.SetBinding(_pluginId, SceneQuickActionsOptions.BackpackKeyKey, options.BackpackKey);
            _hotkeyService.SetBinding(_pluginId, SceneQuickActionsOptions.DropBagHotkeyKey, options.DropBagHotkey);
            _hotkeyService.SetBinding(_pluginId, SceneQuickActionsOptions.DropHotbarHotkeyKey, options.DropHotbarHotkey);
            // 宿主事件使用 Stopwatch 时间戳，可能仍在 UI 队列中等待分发。
            _hotkeyBindingsUpdatedAt = Stopwatch.GetTimestamp();
            _logger.Info(
                $"热键已注册：丢光背包 {options.DropBagHotkey.DisplayName}，"
                + $"逐个丢道具栏 {options.DropHotbarHotkey.DisplayName}。");
            _logger.Info(options.MasterHotkey.IsConfigured
                ? $"总开关热键已注册：{options.MasterHotkey.DisplayName}（按一下停用整个插件，再按一下恢复）。"
                : "总开关热键尚未设置（去插件设置里录一个键，就能随时停用/启用整个插件）。");
            if (options.BackpackMode != SceneQuickActionsBackpackMode.Off)
            {
                _logger.Info(
                    $"背包判断：{SceneQuickActionsOptions.ToBackpackModeOption(options.BackpackMode)}"
                    + $"（「{options.BackpackKey.DisplayName}」开背包时视为背包打开；"
                    + $"最多保持 {options.BackpackMaxHoldSeconds} 秒）。");
            }
        }
    }

    private void StartHotkeys()
    {
        if (_hotkeyService is null)
            return;

        _hotkeyService.BindingInvoked -= OnHotkeyInvoked;
        _hotkeyService.BindingInvoked += OnHotkeyInvoked;
        RegisterHotkeys();
    }

    private void StopHotkeys()
    {
        if (_hotkeyService is null)
            return;

        _hotkeyService.BindingInvoked -= OnHotkeyInvoked;
        _hotkeyService.ClearBindings(_pluginId);
    }

    /// <summary>丢光背包：上排 3 格 + 中排 3 格，逐个拖到屏幕正中心松手。</summary>
    private static string[] BuildBagDropWork() =>
    [
        .. Enumerable.Range(1, BagSlotCount).Select(index => DescribeSlot("背包", index))
    ];

    /// <summary>逐个丢道具栏：每按一次取下一个格子（1 → 2 → 3 → 4 循环）。</summary>
    private static string[] BuildHotbarDropWork(ref int cursor)
    {
        var slot = cursor % HotbarSlotCount;
        cursor = (slot + 1) % HotbarSlotCount;
        return [DescribeSlot("道具栏", slot + 1)];
    }

    private static string DescribeSlot(string kind, int index) => $"{kind}第 {index} 格";

    /// <summary>后台执行：切出鼠标 → 逐个拖到屏幕中心 → 恢复。</summary>
    private void RunDropWork(string[] labels, IntPtr window, bool isBag, int hotbarSlot,
        SceneQuickActionsOptions options, CancellationToken token)
    {
        var wokeMouse = false;
        try
        {
            token.ThrowIfCancellationRequested();
            EnsureCanInject(window, token);
            if (!_input.TryGetClientBounds(window, out var bounds))
                return;

            if (!SceneQuickActionsDropPlan.TryGetSlots(
                    bounds.Width, bounds.Height, isBag, hotbarSlot, out var source))
            {
                LogThrottled($"拖拽未执行：游戏客户区 {bounds.Width}×{bounds.Height} 不是 16:9 或 16:10。");
                return;
            }

            // 两个功能都少了这一步：不切出鼠标，游戏里根本没有可拖的指针。
            // 功能一要先开背包（格子才在），功能二只要指针切出来。
            WakeMouseForDrop(window, options, token, ref wokeMouse);

            var originX = bounds.X;
            var originY = bounds.Y;
            var targetX = originX + (int)Math.Round(DropTargetX * bounds.Width);
            var targetY = originY + (int)Math.Round(DropTargetY * bounds.Height);

            // 拖拽快慢由设置页的「拖拽速度」档位决定。
            var timing = SceneQuickActionsOptions.GetDropTiming(
                options.DropSpeedIndex);

            for (var index = 0; index < source.Length; index++)
            {
                EnsureCanInject(window, token);

                var fromX = originX + (int)Math.Round(source[index].X * bounds.Width);
                var fromY = originY + (int)Math.Round(source[index].Y * bounds.Height);
                DragOnce(window, fromX, fromY, targetX, targetY, timing, options,
                    index < source.Length - 1 ? timing.Interval : 0, token);
                _logger.Info($"已把 {labels[index]} 拖到屏幕中心（{fromX},{fromY} → {targetX},{targetY}）。");

            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Warning($"拖拽失败：{exception.Message}");
        }
        finally
        {
            // 再把背包关回去，恢复游戏手势（再按一次 Tab 是开/关切换）。
            if (wokeMouse)
                CloseBackpack(window, options, token);
        }
    }

    /// <summary>拖拽前切出鼠标：背包已经开着就不重复按 Tab（再按会把它关掉），否则按一次 Tab 开背包。</summary>
    private void WakeMouseForDrop(IntPtr window, SceneQuickActionsOptions options,
        CancellationToken token, ref bool wokeMouse) =>
        EnsureMouseOut(options, window, token, ref wokeMouse);

    /// <summary>按一次左键拖拽：移动 → 按下 → 缓动到目标 → 松手。快慢由 timing 决定。</summary>
    private void DragOnce(
        IntPtr window,
        int fromX,
        int fromY,
        int targetX,
        int targetY,
        (int Move, int Settle, int Interval) timing,
        SceneQuickActionsOptions options,
        int afterReleaseMilliseconds,
        CancellationToken token)
    {
        EnsureCanInject(window, token);
        _input.MoveCursorTo(fromX, fromY);
        SleepAfterStep(options, timing.Settle, token);

        EnsureCanInject(window, token);
        _input.SetLeftButton(down: true);
        try
        {
            SleepAfterStep(options, timing.Settle, token);
            const int steps = 16;
            var perStep = Math.Max(1, timing.Move / steps);
            for (var step = 1; step <= steps; step++)
            {
                EnsureCanInject(window, token);
                var progress = step / (double)steps;
                var eased = progress * progress * (3 - (2 * progress));
                _input.MoveCursorTo(
                    (int)Math.Round(fromX + ((targetX - fromX) * eased)),
                    (int)Math.Round(fromY + ((targetY - fromY) * eased)));
                NativeInput.Sleep(perStep, token);
            }
            SleepAfterStep(options, 0, token);
        }
        finally
        {
            _input.SetLeftButton(down: false);
        }
        SleepAfterStep(options, afterReleaseMilliseconds, token);
    }

}
