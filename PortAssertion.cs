using System.Numerics;
using PicoGK;

namespace TpmsHrv;

public enum EFace { PlusX, MinusX, PlusY, MinusY, PlusZ, MinusZ }

/// <summary>
/// Classifies each port group's centroid against the volume's bounding box
/// and throws if the colored role doesn't match the face it actually sits
/// on. Catches a plug assigned to the wrong stream - looks perfect in the
/// viewer, prints fine over 60 hours, and is a dead short between supply
/// and exhaust.
/// </summary>
public static class PortAssertion
{
    static readonly Dictionary<EFace, EPort> oExpectedRoleForFace = new()
    {
        [EFace.PlusX]  = EPort.SupplyIn,
        [EFace.MinusX] = EPort.SupplyOut,
        [EFace.PlusZ]  = EPort.ExhaustIn,
        [EFace.MinusZ] = EPort.ExhaustOut,
        // +Y / -Y intentionally absent: exterior skin, no port ever expected there.
    };

    public static EFace eClassifyFace(Vector3 vecCentroid, BBox3 oBBox)
    {
        Vector3 vecCenter = oBBox.vecCenter();
        Vector3 vecHalfExtent = oBBox.vecSize() * 0.5f;
        Vector3 vecRel = vecCentroid - vecCenter;

        float fFracX = MathF.Abs(vecRel.X) / vecHalfExtent.X;
        float fFracY = MathF.Abs(vecRel.Y) / vecHalfExtent.Y;
        float fFracZ = MathF.Abs(vecRel.Z) / vecHalfExtent.Z;

        if (fFracX >= fFracY && fFracX >= fFracZ)
            return vecRel.X >= 0 ? EFace.PlusX : EFace.MinusX;

        if (fFracY >= fFracX && fFracY >= fFracZ)
            return vecRel.Y >= 0 ? EFace.PlusY : EFace.MinusY;

        return vecRel.Z >= 0 ? EFace.PlusZ : EFace.MinusZ;
    }

    /// <summary>Throws if a colored port group does not sit on its expected face.</summary>
    public static void AssertPlacement(string strGroupName, EPort ePortFromColor, EFace eFace)
    {
        if (ePortFromColor == EPort.None)
            return; // unpainted / exterior skin - no placement contract

        if (!oExpectedRoleForFace.TryGetValue(eFace, out EPort eExpected) || eExpected != ePortFromColor)
        {
            throw new Exception(
                $"{strGroupName}: colored {ePortFromColor}, sits on {eFace}");
        }
    }
}
