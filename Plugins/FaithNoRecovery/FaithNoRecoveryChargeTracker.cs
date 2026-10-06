namespace IDVBuff.Plugins.FaithNoRecovery;

/// <summary>一次检测所处的阶段。</summary>
internal enum FaithChargePhase
{
    /// <summary>没在蓄力。</summary>
    Idle,

    /// <summary>两个标志都在：信仰正在蓄力。</summary>
    Charging,

    /// <summary>一个标志已经没了、另一个还在，按设置再等一会儿。</summary>
    WaitingOtherCue
}

/// <summary>
/// 一次检测的结论。<see cref="ShouldSwitch"/> 为 true 时，调用方应在
/// <see cref="DelayMilliseconds"/> 毫秒后执行「道具轮切」。
/// </summary>
internal readonly record struct FaithChargeDecision(
    FaithChargePhase Phase,
    bool ShouldSwitch,
    int DelayMilliseconds,
    string? Note)
{
    public static FaithChargeDecision Idle { get; } = new(FaithChargePhase.Idle, false, 0, null);

    public static FaithChargeDecision Charging { get; } = new(FaithChargePhase.Charging, false, 0, null);

    public static FaithChargeDecision Waiting { get; } = new(FaithChargePhase.WaitingOtherCue, false, 0, null);
}

/// <summary>
/// 「蓄力什么时候结束」的状态机（纯逻辑，不抓帧、不注入输入，因此可以离线单测）。
///
/// 判定规则：
///   ① 只有「蓄力环 + 『点击ESC，取消操作』」**两个标志同时在场**才算在蓄力；
///   ② 两个标志一起出现的时长不足 <c>MinimumChargeMilliseconds</c> 的，按一闪而过忽略；
///   ③ 结束的判定是「他没有按 ESC 取消，但这两个标志都已经消失了」——
///      默认要求两个都消失（<c>RequireBothGone</c>）；只剩一个时最多再等
///      <c>OtherCueGraceMilliseconds</c> 毫秒，等不到也按结束处理；
///   ④ 只要**这次蓄力开始之后**按过 ESC，就认定是玩家自己取消的，本次绝不动作；
///   ⑤ 动作后进入 <c>CooldownMilliseconds</c> 冷却，避免连续轮切。
/// </summary>
internal sealed class FaithNoRecoveryChargeTracker
{
    private long _chargeStartTicks;
    private long _lastBothTicks;
    private long _cooldownUntilTicks;

    /// <summary>当前是否处在「两个标志都看见」的蓄力态（探针用来断言状态机）。</summary>
    public bool IsCharging => _chargeStartTicks != 0;

    /// <summary>这一次蓄力已经持续了多久；没在蓄力时返回 0。</summary>
    public TimeSpan CurrentChargeDuration =>
        _chargeStartTicks == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - _chargeStartTicks));

    public FaithChargeDecision Observe(
        FaithNoRecoveryOptions options,
        bool ringPresent,
        bool hintPresent,
        long lastEscapeDownTicks,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        var now = nowUtc.Ticks;

        if (ringPresent && hintPresent)
        {
            if (_chargeStartTicks == 0)
            {
                _chargeStartTicks = now;
                _lastBothTicks = now;
                return new FaithChargeDecision(
                    FaithChargePhase.Charging, false, 0, "识别到蓄力标志（蓄力环 + 取消提示），开始计时。");
            }

            _lastBothTicks = now;
            return FaithChargeDecision.Charging;
        }

        if (_chargeStartTicks == 0)
            return FaithChargeDecision.Idle;

        var chargeMilliseconds = (now - _chargeStartTicks) / (double)TimeSpan.TicksPerMillisecond;
        var minimumCharge = Math.Max(0, options.MinimumChargeMilliseconds);
        if (chargeMilliseconds < minimumCharge)
        {
            Reset();
            return new FaithChargeDecision(
                FaithChargePhase.Idle,
                false,
                0,
                $"两个标志只一起出现了 {chargeMilliseconds:F0}ms（门槛 {minimumCharge}ms），按一闪而过忽略。");
        }

        // 还剩一个标志在场：默认再等一小会儿，等两个都消失。
        var lingeringCue = ringPresent ? "蓄力环还在" : hintPresent ? "取消提示还在" : string.Empty;
        if (options.RequireBothGone && lingeringCue.Length > 0)
        {
            var sinceBoth = (now - _lastBothTicks) / (double)TimeSpan.TicksPerMillisecond;
            var grace = Math.Max(0, options.OtherCueGraceMilliseconds);
            if (sinceBoth < grace)
                return FaithChargeDecision.Waiting;
        }

        var endedByEscape = lastEscapeDownTicks >= _chargeStartTicks;
        Reset();

        if (endedByEscape)
        {
            return new FaithChargeDecision(
                FaithChargePhase.Idle,
                false,
                0,
                $"蓄力 {chargeMilliseconds:F0}ms 后标志消失，但这期间按过 ESC —— 是你自己取消的，本次不轮切。");
        }

        if (now < _cooldownUntilTicks)
        {
            var remaining = (_cooldownUntilTicks - now) / (double)TimeSpan.TicksPerMillisecond;
            return new FaithChargeDecision(
                FaithChargePhase.Idle, false, 0, $"还在冷却（剩 {remaining:F0}ms），忽略这一次。");
        }

        _cooldownUntilTicks = now + MillisecondsToTicks(options.CooldownMilliseconds);
        var delay = Math.Clamp(
            options.ActionDelayMilliseconds,
            FaithNoRecoveryOptions.ActionDelayMinimum,
            FaithNoRecoveryOptions.ActionDelayMaximum);
        var lingeringNote = lingeringCue.Length > 0
            ? $"，但{lingeringCue}（已超过 {Math.Max(0, options.OtherCueGraceMilliseconds)}ms 宽限）"
            : string.Empty;
        return new FaithChargeDecision(
            FaithChargePhase.Idle,
            true,
            delay,
            $"蓄力结束（持续 {chargeMilliseconds:F0}ms，两个标志都已消失{lingeringNote}），"
            + $"{delay}ms 后执行道具轮切。");
    }

    /// <summary>插件停用/参数重置时回到初始状态。</summary>
    public void Reset()
    {
        _chargeStartTicks = 0;
        _lastBothTicks = 0;
    }

    /// <summary>连同冷却一起清空（重新启用时调用，避免上一局的冷却拖到这一局）。</summary>
    public void ResetAll()
    {
        Reset();
        _cooldownUntilTicks = 0;
    }

    private static long MillisecondsToTicks(int milliseconds) =>
        (long)Math.Max(0, milliseconds) * TimeSpan.TicksPerMillisecond;
}
