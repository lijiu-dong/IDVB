namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>「背包是不是开着」的判断方式（设置页「背包检测」里的下拉框）。</summary>
public enum SceneQuickActionsBackpackMode
{
    /// <summary>只认「开背包键」：按一次视为打开、再按一次视为关闭（默认）。</summary>
    KeyOnly = 0,

    /// <summary>按键 + 指针双向：游戏切出了鼠标指针也算「有面板占着鼠标」（更保守）。</summary>
    KeyAndCursor = 1,

    /// <summary>关：不判断背包（等同旧行为）。</summary>
    Off = 2
}

/// <summary>
/// 「背包（鼠标切出）状态」的判定。两路证据合成一个结论：
///
///   ① 开背包键状态机 —— 游戏里打开 / 收起背包只有这一颗键（默认 Tab），
///      按奇数次视为打开、偶数次视为关闭。用户自己按的边沿由宿主钩子送来；
///      本插件自己注入的 Tab 由 runner 用时间窗屏蔽掉，不参与计数。
///   ② 指针可见性 —— 正常游玩时游戏把指针藏起来，一旦背包（或别的点选界面）
///      打开就把指针切出来。这一路只当**安全解除**用：指针先切出过、又收回，
///      说明背包不是被 Tab 关掉的（例如被攻击 / 倒下时游戏自己收的），
///      这时不能一直等下一次按 Tab，否则「拾取相关」会一直停着。
///
/// 兜底：状态一直是「打开」且**超过设置里的最长保持秒数没有任何新证据**时自动解除，
/// 免得漏掉一次边沿就永久不拾取。
///
/// 所有方法都接受调用方给的 <c>now</c>，因此离线探针可以把时间往前拨，完整验证状态迁移。
/// </summary>
internal sealed class SceneQuickActionsBackpackState
{
    /// <summary>连续几帧看到指针可见才算「游戏切出了指针」（防闪烁误判）。</summary>
    internal const int CursorVisibleSamples = 2;

    /// <summary>连续几帧看不到指针才算「指针被收回去了」。</summary>
    internal const int CursorHiddenSamples = 3;

    private readonly object _sync = new();
    private readonly Queue<string> _transitions = new();

    private SceneQuickActionsBackpackMode _mode = SceneQuickActionsBackpackMode.KeyOnly;
    private int _maxHoldSeconds = 120;
    private bool _keyOpen;
    private bool _cursorOpen;
    private bool _open;
    private bool _cursorVisible;
    private bool _sawCursorWhileOpen;
    private int _cursorVisibleStreak;
    private int _cursorHiddenStreak;
    private DateTime _lastEvidenceAt = DateTime.MinValue;
    private string _reason = "插件启动，从「背包已关闭」重新开始";

    /// <summary>状态翻转（true = 打开）时触发，供 runner 挂「刚关背包后别急着捡」之类的反应。</summary>
    internal event Action<bool>? OpenChanged;

    internal bool IsOpen
    {
        get
        {
            lock (_sync)
                return _open;
        }
    }

    internal bool IsCursorVisible
    {
        get
        {
            lock (_sync)
                return _cursorVisible;
        }
    }

    internal SceneQuickActionsBackpackMode Mode
    {
        get
        {
            lock (_sync)
                return _mode;
        }
    }

    internal string Reason
    {
        get
        {
            lock (_sync)
                return _reason;
        }
    }

    /// <summary>插件（重新）启用：从「背包已关闭」重新开始，不继承上一次的状态。</summary>
    internal void Reset(DateTime now)
    {
        lock (_sync)
        {
            _keyOpen = false;
            _cursorOpen = false;
            _cursorVisible = false;
            _sawCursorWhileOpen = false;
            _cursorVisibleStreak = 0;
            _cursorHiddenStreak = 0;
            _lastEvidenceAt = now;
            _reason = "插件启动，从「背包已关闭」重新开始";
            if (_open)
            {
                _open = false;
                _transitions.Enqueue("背包状态已重置为「已关闭」：恢复「拾取相关」。");
            }
        }
    }

    /// <summary>
    /// 接入当前设置。判断方式**发生变化**时清空全部证据重新开始（避免带着上一种方式
    /// 的残留状态继续算）；同一种方式下每轮重新接入只做一次兜底检查。
    /// </summary>
    internal void Configure(SceneQuickActionsBackpackMode mode, int maxHoldSeconds, DateTime now)
    {
        bool? flipped;
        lock (_sync)
        {
            if (mode != _mode)
            {
                _mode = mode;
                _keyOpen = false;
                _cursorOpen = false;
                _sawCursorWhileOpen = false;
                _cursorVisibleStreak = 0;
                _cursorHiddenStreak = 0;
                _lastEvidenceAt = now;
                _reason = mode == SceneQuickActionsBackpackMode.Off
                    ? "已关闭背包判断"
                    : $"{SceneQuickActionsOptions.ToBackpackModeOption(mode)}";
                _transitions.Enqueue(mode == SceneQuickActionsBackpackMode.Off
                    ? "背包判断已关闭：一直按「能拾取就拾取」处理。"
                    : $"背包判断方式已改为「{SceneQuickActionsOptions.ToBackpackModeOption(mode)}」，状态重新计算。");
            }

            _maxHoldSeconds = Math.Max(0, maxHoldSeconds);
            flipped = Recompute(now);
        }

        RaiseIfFlipped(flipped);
    }

    /// <summary>「开背包键」（默认 Tab）被按下：打开 / 关闭交替。</summary>
    internal void PressBackpackKey(DateTime now)
    {
        bool? flipped;
        lock (_sync)
        {
            _keyOpen = !_keyOpen;
            _lastEvidenceAt = now;
            _reason = _keyOpen ? "按下了「开背包键」" : "又按了一次「开背包键」";
            flipped = Recompute(now);
        }

        RaiseIfFlipped(flipped);
    }

    /// <summary>本插件自己按了 Tab 开背包（切出鼠标）——直接记为打开，不参与奇偶计数。</summary>
    internal void MarkOpenedByPlugin(DateTime now, string reason)
    {
        bool? flipped;
        lock (_sync)
        {
            _keyOpen = true;
            _lastEvidenceAt = now;
            _reason = reason;
            flipped = Recompute(now);
        }

        RaiseIfFlipped(flipped);
    }

    /// <summary>本插件自己按了 Tab 关背包——直接记为关闭。</summary>
    internal void MarkClosedByPlugin(DateTime now, string reason)
    {
        bool? flipped;
        lock (_sync)
        {
            _keyOpen = false;
            _cursorOpen = false;
            _lastEvidenceAt = now;
            _reason = reason;
            flipped = Recompute(now);
        }

        RaiseIfFlipped(flipped);
    }

    /// <summary>采样一次「指针可见性」。只在游戏处于前台时才有意义（切出去时桌面指针当然可见）。</summary>
    internal void ObserveCursor(bool visible, DateTime now)
    {
        bool? flipped;
        lock (_sync)
        {
            _cursorVisible = visible;

            if (visible)
            {
                _cursorVisibleStreak++;
                _cursorHiddenStreak = 0;
                if (_cursorVisibleStreak >= CursorVisibleSamples)
                {
                    _cursorOpen = true;
                    if (_open)
                        _sawCursorWhileOpen = true;
                    // 背包开着的时候指针一直是可见的：每次采样都算一次新证据，
                    // 这样「最长保持」不会把真正开着的背包误判成兜底释放。
                    _lastEvidenceAt = now;
                    _reason = "游戏切出了鼠标指针";
                }
            }
            else
            {
                _cursorHiddenStreak++;
                _cursorVisibleStreak = 0;
                if (_cursorHiddenStreak == CursorHiddenSamples)
                {
                    if (_cursorOpen)
                    {
                        _cursorOpen = false;
                        _lastEvidenceAt = now;
                        _reason = "游戏已把指针收回";
                    }

                    if (_keyOpen && _sawCursorWhileOpen)
                    {
                        // 指针先切出过、现在收回了 → 背包不是被 Tab 关掉的，
                        // 多半是游戏自己收的（被攻击 / 倒下）。这时必须跟着放开，
                        // 否则「拾取相关」会一直停到下一次按 Tab 或兜底超时。
                        _keyOpen = false;
                        _lastEvidenceAt = now;
                        _reason = "指针已收回（背包被游戏自动收起）";
                    }
                }
            }

            flipped = Recompute(now);
        }

        RaiseIfFlipped(flipped);
    }

    private void RaiseIfFlipped(bool? flipped)
    {
        if (flipped is { } open)
            OpenChanged?.Invoke(open);
    }

    /// <summary>状态翻转时排队一条人话日志（由 runner 在工作线程里统一取走打印）。</summary>
    internal bool TryTakeTransition(out string message)
    {
        lock (_sync)
        {
            if (_transitions.Count == 0)
            {
                message = string.Empty;
                return false;
            }

            message = _transitions.Dequeue();
            return true;
        }
    }

    /// <summary>把「原始证据」按当前判断方式折算成「背包是否打开」，并记录翻转。</summary>
    /// <returns>状态翻转时返回新的状态；没有翻转返回 <c>null</c>（调用方据此决定要不要发事件）。</returns>
    private bool? Recompute(DateTime now)
    {
        var open = _mode switch
        {
            SceneQuickActionsBackpackMode.Off => false,
            SceneQuickActionsBackpackMode.KeyAndCursor => _keyOpen || _cursorOpen,
            _ => _keyOpen
        };

        if (open
            && _maxHoldSeconds > 0
            && _lastEvidenceAt != DateTime.MinValue
            && (now - _lastEvidenceAt).TotalSeconds > _maxHoldSeconds)
        {
            _keyOpen = false;
            _cursorOpen = false;
            _reason = $"状态一直是「打开」，超过 {_maxHoldSeconds} 秒没有任何新证据";
            open = false;
        }

        if (open == _open)
            return null;

        _open = open;
        if (open)
        {
            _sawCursorWhileOpen = _cursorVisible;
            _transitions.Enqueue($"背包已打开（依据：{_reason}）：这期间「拾取相关」不动作。");
        }
        else
        {
            _transitions.Enqueue($"背包已关闭（依据：{_reason}）：恢复「拾取相关」。");
        }

        return open;
    }
}
