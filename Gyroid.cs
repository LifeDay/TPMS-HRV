using System.Numerics;
using PicoGK;

namespace TpmsHrv;

/// <summary>
/// Gyroid level-set field, gradient-normalized so its value approximates the
/// signed Euclidean distance (mm) to the gyroid mid-surface. That's what makes
/// "|field| &lt;= wall/2" a uniform-thickness sheet.
///
/// The raw trig implicit d(p) has |grad d| between sqrt(2)k and sqrt(3)k on the
/// surface, so thresholding d directly gives a wall that swings ~0.60-0.73mm
/// for a nominal 0.8mm. First-order normalization d/|grad d| makes it uniform
/// but ~3.5% thin (third-order curvature error, same everywhere). One extra
/// Newton step toward the surface removes that bias: measured 0.799 +/- 0.0005mm
/// along the surface normal for nominal 0.8mm at lambda = 8mm.
///
/// Sign is preserved everywhere: fDistance &gt; 0 is stream A's side, &lt; 0 is
/// stream B's. The only zeros of grad d are the critical points at
/// d = +/-sqrt(2) and +/-1.5, far outside any wall band, so the clamped
/// denominator only ever inflates the magnitude of points already deep inside
/// a channel.
/// </summary>
sealed class GyroidField
{
    readonly float fK;
    readonly float fMinGradient;

    // Beyond this distance the Newton refinement is skipped: its only job is
    // wall-band accuracy, and first-order is sign-correct everywhere.
    const float fRefineBandMM = 1.5f;

    public GyroidField(float fCellSizeMM)
    {
        fK = 2f * MathF.PI / fCellSizeMM;
        fMinGradient = 1e-3f * fK;
    }

    public float fRaw(in Vector3 vec) =>
        MathF.Sin(fK * vec.X) * MathF.Cos(fK * vec.Y) +
        MathF.Sin(fK * vec.Y) * MathF.Cos(fK * vec.Z) +
        MathF.Sin(fK * vec.Z) * MathF.Cos(fK * vec.X);

    public Vector3 vecRawGradient(in Vector3 vec)
    {
        float fSx = MathF.Sin(fK * vec.X), fCx = MathF.Cos(fK * vec.X);
        float fSy = MathF.Sin(fK * vec.Y), fCy = MathF.Cos(fK * vec.Y);
        float fSz = MathF.Sin(fK * vec.Z), fCz = MathF.Cos(fK * vec.Z);
        return fK * new Vector3(
            fCx * fCy - fSz * fSx,
            fCy * fCz - fSx * fSy,
            fCz * fCx - fSy * fSz);
    }

    public float fDistance(in Vector3 vec)
    {
        Vector3 vecGrad = vecRawGradient(vec);
        float fGradLen = MathF.Max(vecGrad.Length(), fMinGradient);
        float fStep = fRaw(vec) / fGradLen;
        if (MathF.Abs(fStep) >= fRefineBandMM)
            return fStep;

        Vector3 vecNext = vec - (fStep / fGradLen) * vecGrad;
        float fGradLenNext = MathF.Max(vecRawGradient(vecNext).Length(), fMinGradient);
        return fStep + fRaw(vecNext) / fGradLenNext;
    }

    /// <summary>Unit normal of the level set through vec, pointing toward stream A.</summary>
    public Vector3 vecNormal(in Vector3 vec) => Vector3.Normalize(vecRawGradient(vec));

    /// <summary>
    /// Newton-projects vec onto the mid-surface. Returns false if it didn't
    /// converge (start point too close to a critical point).
    /// </summary>
    public bool bProjectToSurface(Vector3 vec, out Vector3 vecSurface)
    {
        for (int i = 0; i < 20; i++)
        {
            Vector3 vecGrad = vecRawGradient(vec);
            float fGradLenSq = vecGrad.LengthSquared();
            if (fGradLenSq < fMinGradient * fMinGradient)
                break;

            vec -= (fRaw(vec) / fGradLenSq) * vecGrad;
        }

        vecSurface = vec;
        return MathF.Abs(fRaw(vec)) < 1e-5f;
    }
}

/// <summary>IImplicit wrapper: solid where |distance| &lt;= wall/2 (the membrane sheet).</summary>
sealed class GyroidSheetImplicit : IImplicit
{
    readonly GyroidField oField;
    readonly float fHalfThicknessMM;

    public GyroidSheetImplicit(GyroidField oField, float fWallThicknessMM)
    {
        this.oField = oField;
        fHalfThicknessMM = 0.5f * fWallThicknessMM;
    }

    public float fSignedDistance(in Vector3 vec) => MathF.Abs(oField.fDistance(vec)) - fHalfThicknessMM;
}

/// <summary>IImplicit wrapper: solid where distance &gt; +wall/2 ("stream A" side).</summary>
sealed class GyroidStreamAImplicit : IImplicit
{
    readonly GyroidField oField;
    readonly float fHalfThicknessMM;

    public GyroidStreamAImplicit(GyroidField oField, float fWallThicknessMM)
    {
        this.oField = oField;
        fHalfThicknessMM = 0.5f * fWallThicknessMM;
    }

    public float fSignedDistance(in Vector3 vec) => fHalfThicknessMM - oField.fDistance(vec);
}

/// <summary>IImplicit wrapper: solid where distance &lt; -wall/2 ("stream B" side).</summary>
sealed class GyroidStreamBImplicit : IImplicit
{
    readonly GyroidField oField;
    readonly float fHalfThicknessMM;

    public GyroidStreamBImplicit(GyroidField oField, float fWallThicknessMM)
    {
        this.oField = oField;
        fHalfThicknessMM = 0.5f * fWallThicknessMM;
    }

    public float fSignedDistance(in Vector3 vec) => oField.fDistance(vec) + fHalfThicknessMM;
}
