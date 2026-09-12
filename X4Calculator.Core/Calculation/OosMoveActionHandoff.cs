namespace X4Calculator.Core.Calculation;

public enum OosMoveActionCandidate { NormalCompletion, Timer }
public enum OosMoveActionAcceptance { NotPending, NotDue, NestedTimerConsumed, Continued }

/// <summary>
/// 普通 MoveTo 的脚本完成/计时器竞争。Accept 表示上游已确定实际消费项，不按 timestamp 推断赢家。
/// 物理 FlightPoint 独立保留；此类不执行 stop、不删除点、不增加动作尾时。
/// </summary>
public sealed class OosMoveActionHandoff
{
    public double? TimerDeadline { get; private set; }
    public bool NormalCompletionPending { get; private set; }
    public bool Continued { get; private set; }
    public OosMoveActionCandidate? Winner { get; private set; }
    private long _lastSequence = -1;
    private double _lastTime;

    public OosMoveActionHandoff(double now, double? timerDeadline, bool normalCompletionPending)
    {
        if (!double.IsFinite(now) || (timerDeadline is { } d && !double.IsFinite(d)))
            throw new ArgumentOutOfRangeException(nameof(now));
        _lastTime = now;
        TimerDeadline = timerDeadline;
        NormalCompletionPending = normalCompletionPending;
    }

    public void DeliverNormalCompletion(long sequence, double now)
    {
        Check(sequence, now);
        if (Continued) throw new InvalidOperationException("Completion belongs to an action generation that already continued.");
        NormalCompletionPending = true;
        Commit(sequence, now);
    }

    /// <summary>now 为 AI update/TLS action clock；reason8 非叶节点只消费 timer，不向子节点传播或自动续行。</summary>
    public OosMoveActionAcceptance Accept(long sequence, double now, OosMoveActionCandidate candidate,
        bool? isDirectLeaf)
    {
        Check(sequence, now);
        if (isDirectLeaf is null || !Enum.IsDefined(candidate))
            throw new NotSupportedException("UNKNOWN: actual consumer kind and action-node leaf condition are required.");
        if (Continued) throw new InvalidOperationException("A new action generation is required after continuation.");
        if (candidate == OosMoveActionCandidate.NormalCompletion && !NormalCompletionPending ||
            candidate == OosMoveActionCandidate.Timer && TimerDeadline is null)
        {
            Commit(sequence, now);
            return OosMoveActionAcceptance.NotPending;
        }
        if (candidate == OosMoveActionCandidate.Timer)
        {
            if (TimerDeadline > now)
            {
                Commit(sequence, now);
                return OosMoveActionAcceptance.NotDue;
            }
            if (!isDirectLeaf.Value)
            {
                TimerDeadline = null;
                Commit(sequence, now);
                return OosMoveActionAcceptance.NestedTimerConsumed;
            }
        }
        else if (!isDirectLeaf.Value)
            throw new NotSupportedException("UNKNOWN: nested non-timer continuation is outside the ordinary MoveTo leaf contract.");
        TimerDeadline = null;
        NormalCompletionPending = false;
        Winner = candidate;
        Continued = true;
        Commit(sequence, now);
        return OosMoveActionAcceptance.Continued;
    }

    private void Check(long sequence, double now)
    {
        if (sequence <= _lastSequence || !double.IsFinite(now) || now < _lastTime)
            throw new ArgumentException("Explicit increasing consumption order and nondecreasing time are required.");
    }
    private void Commit(long sequence, double now) { _lastSequence = sequence; _lastTime = now; }
}
