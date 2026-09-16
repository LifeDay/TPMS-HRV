using System.Numerics;
using PicoGK;

namespace TpmsHrv;

/// <summary>
/// Crude, un-normalized trig implicit for a gyroid. Wall thickness is wrong
/// and uneven at this stage - expected, and not this session's problem
/// (real gradient normalization is Session 2).
/// d(vec) &gt; 0 and d(vec) &lt; 0 mark opposite sides of the gyroid surface;
/// these are used as stand-ins for two flow streams that the sealing slabs
/// seal off at each port.
/// </summary>
sealed class GyroidRawImplicit
{
    readonly float fK;
    public GyroidRawImplicit(float fCellSizeMM) => fK = 2f * MathF.PI / fCellSizeMM;

    public float d(in Vector3 vec) =>
        MathF.Sin(fK * vec.X) * MathF.Cos(fK * vec.Y) +
        MathF.Sin(fK * vec.Y) * MathF.Cos(fK * vec.Z) +
        MathF.Sin(fK * vec.Z) * MathF.Cos(fK * vec.X);
}

/// <summary>IImplicit wrapper: solid where |d| &lt;= halfThickness (the membrane sheet).</summary>
sealed class GyroidSheetImplicit : IImplicit
{
    readonly GyroidRawImplicit oRaw;
    readonly float fHalfThicknessMM;

    public GyroidSheetImplicit(GyroidRawImplicit oRaw, float fWallThicknessMM)
    {
        this.oRaw = oRaw;
        fHalfThicknessMM = 0.5f * fWallThicknessMM;
    }

    public float fSignedDistance(in Vector3 vec) => MathF.Abs(oRaw.d(vec)) - fHalfThicknessMM;
}

/// <summary>IImplicit wrapper: solid where d &gt; +halfThickness ("stream A" side).</summary>
sealed class GyroidStreamAImplicit : IImplicit
{
    readonly GyroidRawImplicit oRaw;
    readonly float fHalfThicknessMM;

    public GyroidStreamAImplicit(GyroidRawImplicit oRaw, float fWallThicknessMM)
    {
        this.oRaw = oRaw;
        fHalfThicknessMM = 0.5f * fWallThicknessMM;
    }

    public float fSignedDistance(in Vector3 vec) => fHalfThicknessMM - oRaw.d(vec);
}

/// <summary>IImplicit wrapper: solid where d &lt; -halfThickness ("stream B" side).</summary>
sealed class GyroidStreamBImplicit : IImplicit
{
    readonly GyroidRawImplicit oRaw;
    readonly float fHalfThicknessMM;

    public GyroidStreamBImplicit(GyroidRawImplicit oRaw, float fWallThicknessMM)
    {
        this.oRaw = oRaw;
        fHalfThicknessMM = 0.5f * fWallThicknessMM;
    }

    public float fSignedDistance(in Vector3 vec) => oRaw.d(vec) + fHalfThicknessMM;
}
