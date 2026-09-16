using System.Numerics;

namespace TpmsHrv;

public enum EPort
{
    None,
    SupplyIn,
    SupplyOut,
    ExhaustIn,
    ExhaustOut,
}

public static class PortClassifier
{
    // Fixture palette (see fixture spec table). Onshape assigns unpainted
    // parts one of eight rotating default colors, which is NOT predictable -
    // so "unpainted" is never matched directly; anything outside tolerance
    // of these four falls through to EPort.None.
    static readonly (EPort ePort, Vector3 vecKd)[] aPalette =
    {
        (EPort.SupplyIn,    new Vector3(1.000000f, 0.000000f, 0.000000f)), // Red
        (EPort.SupplyOut,   new Vector3(0.972549f, 0.529412f, 0.003922f)), // Orange
        (EPort.ExhaustIn,   new Vector3(0.231373f, 0.380392f, 0.705882f)), // Blue
        (EPort.ExhaustOut,  new Vector3(0.615686f, 0.811765f, 0.929412f)), // Cyan
    };

    public static EPort ePortFromKd(Vector3 vecKd, float fTol)
    {
        EPort eBest = EPort.None;
        float fBestDist = float.MaxValue;

        foreach ((EPort ePort, Vector3 vecPalette) in aPalette)
        {
            float fDist = Vector3.Distance(vecKd, vecPalette);
            if (fDist < fBestDist)
            {
                fBestDist = fDist;
                eBest = ePort;
            }
        }

        return fBestDist <= fTol ? eBest : EPort.None;
    }
}
