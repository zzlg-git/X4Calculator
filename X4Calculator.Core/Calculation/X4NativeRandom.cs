using System.Numerics;

namespace X4Calculator.Core.Calculation;

/// <summary>一次 native 随机索引抽取的状态与结果。</summary>
public readonly record struct X4NativeRandomIndexDraw(ulong NextState, ulong Index);

/// <summary>一次 native 浮点抽取的状态与结果。</summary>
public readonly record struct X4NativeRandomFloatDraw(ulong NextState, float Value);

/// <summary>
/// 假定调用前 64 位状态均匀时，索引抽取的精确状态计数。
/// <see cref="BaseStateCount"/> 是每个索引至少对应的状态数；前
/// <see cref="ExtraStateIndexCount"/> 个索引各额外对应一个状态。
/// </summary>
public readonly record struct X4NativeRandomIndexDistribution(
    ulong CandidateCount,
    UInt128 BaseStateCount,
    ulong ExtraStateIndexCount)
{
    /// <summary>返回指定索引在均匀 64 位输入状态下对应的精确状态数。</summary>
    public UInt128 GetStateCount(ulong index)
    {
        if (index >= CandidateCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return BaseStateCount + (index < ExtraStateIndexCount ? 1u : 0u);
    }
}

/// <summary>
/// X4 9.00 已验证的 64 位 native RNG 内核。
/// 此类型只重现单次状态递推与输出映射；不推断游戏 TLS 状态的种子，也不宣称单一状态轨道的周期。
/// </summary>
public static class X4NativeRandom
{
    private const ulong Multiplier = 0x5851f42d4c957f2d;
    private const ulong Increment = 0x14057b7ef767814f;
    private const float InverseUInt32Range = 1f / 4_294_967_296f;

    /// <summary>重现 native 的一次 64 位状态更新。</summary>
    public static ulong NextState(ulong state)
    {
        var advanced = unchecked(state * Multiplier + Increment);
        return BitOperations.RotateRight(advanced, 30);
    }

    /// <summary>
    /// 使用下一状态对候选数取模，返回保留下一状态的抽取结果。
    /// </summary>
    public static X4NativeRandomIndexDraw DrawIndex(ulong state, ulong candidateCount)
    {
        if (candidateCount == 0)
            throw new ArgumentOutOfRangeException(nameof(candidateCount));

        var nextState = NextState(state);
        return new X4NativeRandomIndexDraw(nextState, nextState % candidateCount);
    }

    /// <summary>
    /// 按 native float32 运算顺序，将下一状态的低 32 位缩放到指定范围。
    /// </summary>
    public static X4NativeRandomFloatDraw DrawFloat(ulong state, float extent = 2f)
    {
        var nextState = NextState(state);
        var unit = (float)(uint)nextState * InverseUInt32Range;
        var value = unit * extent;
        return new X4NativeRandomFloatDraw(nextState, value);
    }

    /// <summary>
    /// 描述均匀 64 位输入状态下各索引的精确权重。
    /// 该统计描述不要求或推断游戏可恢复的 TLS seed。
    /// </summary>
    public static X4NativeRandomIndexDistribution DescribeUniformIndexDistribution(ulong candidateCount)
    {
        if (candidateCount == 0)
            throw new ArgumentOutOfRangeException(nameof(candidateCount));

        var stateCount = UInt128.One << 64;
        return new X4NativeRandomIndexDistribution(
            candidateCount,
            stateCount / candidateCount,
            (ulong)(stateCount % candidateCount));
    }
}
