using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

public sealed record OosSafeTarget(Vec3 Position, bool Collided);
public sealed record OosGenericTarget(Vec3 Position, Vec3 RandomizedBase, ulong NextState,
    int DrawCount, bool Collided, string GeometryBranch);

/// <summary>普通单站点 OBB 条件下的原生目标生成。调用方负责排除其他障碍、预约与区域优先级。</summary>
public static class OosTransportArrivalGeometry
{
    /// <summary>EB33EF..EB342B：末航点的 approach 事件；使用 chord，不使用曲线弧长。</summary>
    public static bool IsLastWaypointApproached(float advancedU, float chordLength,
        float pointRadius, float cachedBrakingDistance, int remainingPointCount,
        bool ordinaryLinearBranch, bool alreadyApproaching = false, bool suppressApproach = false)
    {
        if (!ordinaryLinearBranch) throw new NotSupportedException("Ordinary Linear FCM branch is required.");
        if (!float.IsFinite(advancedU) || advancedU < 0 || advancedU > 1 ||
            !float.IsFinite(chordLength) || chordLength < 0 || !float.IsFinite(pointRadius) ||
            !float.IsFinite(cachedBrakingDistance) || cachedBrakingDistance < 0 || remainingPointCount < 0)
            throw new ArgumentOutOfRangeException(nameof(advancedU));
        var radius = pointRadius >= 0 ? pointRadius : MathF.Min(cachedBrakingDistance, 5000f);
        var threshold = 1f - radius / MathF.Max(chordLength, .1f);
        return !alreadyApproaching && !suppressApproach && remainingPointCount == 1 && advancedU >= threshold;
    }

    public static Vec3 DockQuadrant(Vec3 position)
    {
        var p = V(position);
        if (MathF.Abs(p[1]) > MathF.Abs(p[0]) && MathF.Abs(p[1]) > MathF.Abs(p[2]))
            return new(0, p[1] >= 0 ? 1 : -1, 0);
        if (MathF.Abs(p[2]) > MathF.Abs(p[0]) && MathF.Abs(p[2]) > MathF.Abs(p[1]))
            return new(0, 0, p[2] > 0 ? 1 : -1);
        return new(p[0] > 0 ? 1 : -1, 0, 0);
    }

    public static OosSafeTarget SafePoint(Vec3 position, Vec3 center, Vec3 halfExtents,
        Vec3 direction, float radius, X4RotationMatrix rotation, bool noOtherConstraints)
    {
        if (!noOtherConstraints) throw new NotSupportedException("Other constraints must be excluded.");
        var p = V(position); var c = V(center); var h = V(halfExtents); var q = V(direction);
        var m = Matrix(rotation);
        if (!float.IsFinite(radius) || radius < 0 || h.Any(x => x < 0) || q.All(x => x == 0))
            throw new ArgumentOutOfRangeException(nameof(radius));
        var d = Enumerable.Range(0, 3).Select(i => p[i] - c[i]).ToArray();
        var local = Enumerable.Range(0, 3).Select(i => Sum3(m[0][i]*d[0], m[1][i]*d[1], m[2][i]*d[2])).ToArray();
        var overlap = Enumerable.Range(0, 3).All(i => -h[i] < local[i] + radius && local[i] - radius < h[i]);
        if (!overlap) return new(ToVec(p), false);
        var world = WorldHalf(h, m);
        var inflation = radius * 1.01f;
        var enter = float.NegativeInfinity; var leave = float.PositiveInfinity;
        for (var i = 0; i < 3; i++)
        {
            var expanded = world[i] + inflation;
            var lo = c[i] - expanded; var hi = c[i] + expanded;
            if (q[i] == 0)
            {
                if (p[i] < lo || p[i] > hi) throw new NotSupportedException("Ray misses expanded bounds.");
            }
            else
            {
                var t0 = (lo - p[i]) / q[i]; var t1 = (hi - p[i]) / q[i];
                enter = MathF.Max(enter, MathF.Min(t0, t1));
                leave = MathF.Min(leave, MathF.Max(t0, t1));
            }
        }
        if (leave < MathF.Max(enter, 0) || !float.IsFinite(leave))
            throw new NotSupportedException("No finite forward ray exit.");
        return new(ToVec(Enumerable.Range(0, 3).Select(i => p[i] + leave*q[i]).ToArray()), true);
    }

    public static OosGenericTarget GenerateGenericTarget(Vec3 dockPosition, Vec3 center,
        Vec3 halfExtents, float shipSize, float shipSafeSize, ulong seed,
        X4RotationMatrix rotation, bool baseInTargetZone, bool noOtherConstraints)
    {
        if (!baseInTargetZone || !noOtherConstraints)
            throw new NotSupportedException("Unique target zone and excluded other constraints are required.");
        if (!float.IsFinite(shipSize) || shipSize <= 0 || !float.IsFinite(shipSafeSize) || shipSafeSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(shipSize));
        var dock = V(dockPosition); var c = V(center); var h = V(halfExtents); var m = Matrix(rotation);
        if (h.Any(x => x <= 0)) throw new ArgumentOutOfRangeException(nameof(halfExtents));
        var state = seed; var count = 0;
        float Draw(float extent)
        {
            var draw = X4NativeRandom.DrawFloat(state, extent);
            state = draw.NextState; count++; return draw.Value;
        }
        float[] Sphere()
        {
            var azimuth = Draw((float)(2*Math.PI)); var z = Draw(2) - 1f;
            var radial = (float)Math.Sin((float)Math.Acos(z));
            return [(float)Math.Cos(azimuth)*radial, (float)Math.Sin(azimuth)*radial, z];
        }
        var distance = Draw(shipSize * 3f);
        var p = dock.ToArray();
        if (distance > 0)
        {
            var dir = Sphere();
            for (var i = 0; i < 3; i++) p[i] = dock[i] + distance*dir[i];
        }
        var safeDirection = Sphere(); var radius = shipSafeSize / 2f;
        var world = WorldHalf(h, m);
        var cachedRadius = OosTransportTerminalPhaseCalculator.CalculateSourceBoundingSizes(Vec3.Zero, ToVec(world)).Radius;
        if (cachedRadius <= 25000f)
        {
            var result = SafePoint(ToVec(p), ToVec(c), ToVec(world), ToVec(safeDirection), radius,
                X4RotationMatrix.Identity, true);
            return new(result.Position, ToVec(p), state, count, result.Collided,
                result.Collided ? "direction-ray-exit" : "unchanged-clear");
        }
        var collided = Enumerable.Range(0, 3).All(i => c[i]-world[i] < p[i]+radius && p[i]-radius < c[i]+world[i]);
        if (!collided) return new(ToVec(p), ToVec(p), state, count, false, "unchanged-clear");
        var expanded = world.Select(x => x + radius).ToArray();
        var delta = Enumerable.Range(0, 3).Select(i => p[i]-c[i]).ToArray();
        var depth = Enumerable.Range(0, 3).Select(i => expanded[i]-MathF.Abs(delta[i])).ToArray();
        var axis = depth[1] < depth[0] && depth[1] < depth[2] ? 1 : depth[0] < depth[2] ? 0 : 2;
        var corrected = p.ToArray(); corrected[axis] = c[axis] + (delta[axis] < 0 ? -expanded[axis] : expanded[axis]);
        return new(ToVec(corrected), ToVec(p), state, count, true, "nearest-axis");
    }

    private static float Sum3(float a, float b, float c) => (float)((double)a + b + c);
    private static float[] WorldHalf(float[] h, float[][] m) => Enumerable.Range(0, 3)
        .Select(i => Sum3(MathF.Abs(m[i][0])*h[0], MathF.Abs(m[i][1])*h[1], MathF.Abs(m[i][2])*h[2])).ToArray();
    private static float[] V(Vec3 v)
    {
        float[] result = [(float)v.X, (float)v.Y, (float)v.Z];
        if (result.Any(x => !float.IsFinite(x))) throw new ArgumentOutOfRangeException(nameof(v));
        return result;
    }
    private static Vec3 ToVec(float[] v) => new(v[0], v[1], v[2]);
    private static float[][] Matrix(X4RotationMatrix matrix)
    {
        float[][] m = [V(matrix.Row0), V(matrix.Row1), V(matrix.Row2)];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                if (Math.Abs((double)m[0][i]*m[0][j] + (double)m[1][i]*m[1][j] + (double)m[2][i]*m[2][j] - (i == j ? 1 : 0)) > 1e-5)
                    throw new ArgumentException("Rotation must be orthonormal.", nameof(matrix));
        return m;
    }
}
